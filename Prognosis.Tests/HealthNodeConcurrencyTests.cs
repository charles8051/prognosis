using System.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// Concurrency hardening for the choke point (ADR-011 §4). ADR-011 §4
/// claims the single-CAS swap covers the real multi-writer paths, but two
/// evaluation-path fields — the wave-time baseline (§5) and the one-shot
/// <see cref="HealthNode.ReportStatus"/> bypass — used to live OUTSIDE that CAS.
/// These tests drive genuinely concurrent propagation through ONE node shared by
/// TWO graphs (independent propagation locks, so their waves overlap in
/// <c>NotifyChangedCore</c>) and assert integrity properties the pre-fix
/// outside-CAS layout violates.
/// </summary>
public class HealthNodeConcurrencyTests
{
    /// <summary>
    /// The one-shot bypass must be consumed by AT MOST one wave per arm. A node in
    /// two graphs is armed by a stream of <see cref="HealthNode.ReportStatus"/> calls
    /// while both graphs are waved concurrently; the intrinsic probe counts its own
    /// (non-bypassed) invocations. Every wave either bypasses (consuming one armed
    /// one-shot) or invokes the probe, so:
    /// <code>
    ///   bypasses ≤ arms   ⇔   probeCalls ≥ totalWaves − arms
    /// </code>
    /// Folded into the CAS, an arm is read-and-cleared atomically, so a given arm is
    /// consumed exactly once — the floor holds under every interleaving. With the
    /// pre-fix non-atomic check-then-clear, two concurrent waves can both read the
    /// flag <see langword="true"/> and both bypass (double-consume), driving
    /// <c>bypasses &gt; arms</c> and the probe below its floor. Falsification: revert
    /// the fold (a plain <c>volatile bool</c> read-then-clear) and this fails.
    /// </summary>
    [Fact]
    public void ConcurrentPropagation_SharedNode_BypassConsumedAtMostOncePerArm()
    {
        var probeCalls = 0;
        var probe = new Func<HealthEvaluation>(() =>
        {
            Interlocked.Increment(ref probeCalls);
            return HealthEvaluation.Unhealthy("probe");
        });

        var node = HealthNode.Create("Shared").WithHealthProbe(probe);

        // Two graphs over the SAME root node: independent propagation locks, so a wave on
        // graph A overlaps a wave on graph B inside NotifyChangedCore. The wave-count
        // arithmetic below is derived from this graph count via the node's multicast
        // propagation contract (`_bubbleStrategy` fires ONE callback per attached graph,
        // pinned by HealthGraphTests.SharedNode_TwoGraphs_*): each constructor waves the
        // node once (GraphCount construction waves), and each ReportStatus's own Refresh
        // multicasts one wave per graph (GraphCount waves per arm). Named so the
        // dependency is explicit — add a graph and you bump this.
        const int GraphCount = 2;
        using var graphA = HealthGraph.Create(node);
        using var graphB = HealthGraph.Create(node);

        const int refreshIters = 40_000;
        const int armIters = 40_000;
        var refreshA = 0;
        var refreshB = 0;
        var arms = 0;

        var start = new ManualResetEventSlim(false);

        var waveA = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < refreshIters; i++)
            {
                graphA.RefreshAll();
                Interlocked.Increment(ref refreshA);
            }
        });
        var waveB = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < refreshIters; i++)
            {
                graphB.RefreshAll();
                Interlocked.Increment(ref refreshB);
            }
        });
        var armer = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < armIters; i++)
            {
                // Each ReportStatus arms one one-shot and drives its own Refresh, which
                // waves the node once per attached graph (multicast → GraphCount waves per arm).
                node.ReportStatus(HealthEvaluation.Degraded("push"));
                Interlocked.Increment(ref arms);
            }
        });

        waveA.Start();
        waveB.Start();
        armer.Start();
        start.Set();
        waveA.Join();
        waveB.Join();
        armer.Join();

        // Total waves through the node = A refreshes + B refreshes + GraphCount per arm
        // (multicast) + GraphCount construction waves.
        long totalWaves = (long)refreshA + refreshB + ((long)GraphCount * arms) + GraphCount;
        long floor = totalWaves - arms; // bypasses ≤ arms

        Assert.True(
            probeCalls >= floor,
            $"probe invoked {probeCalls} times but the one-shot floor is {floor} "
            + $"(totalWaves={totalWaves}, arms={arms}); bypasses exceeded arms, so an "
            + "arm was double-consumed — the one-shot is not folded into the CAS.");
    }

    /// <summary>
    /// Timebase monotonicity: the CAS'd wave-time baseline
    /// (<see cref="HealthNode.LastWaveTimeForTest"/>) never regresses under concurrent
    /// multi-graph waves, even when the two graphs' clocks run at different rates so
    /// their per-wave <c>now</c> values interleave. The baseline is advanced as
    /// <c>max(observed, now)</c> inside the CAS, so the sequence of published values is
    /// non-decreasing and a sampler thread never observes a decrease. This directly
    /// falsifies the plain <c>nextLastWave = now</c> overwrite (the pre-monotonic
    /// draft): there, a slower graph's smaller-<c>now</c> wave winning a later CAS
    /// publishes a baseline below one already published, which the sampler catches as a
    /// regression. It is the fallback path (§5, <c>chainNow = observed.LastWaveTime</c>)
    /// that a regressed baseline would corrupt.
    /// </summary>
    [Fact]
    public void ConcurrentPropagation_SharedNode_WaveTimeBaselineNeverRegresses()
    {
        // Two INDEPENDENT clocks at different rates, so graph A's now and graph B's now
        // interleave: whenever a slower wave wins a CAS after a faster one, a plain
        // overwrite would regress the baseline. A flip each wave forces a real change,
        // so every wave swaps and advances the baseline (not the steady-state no-op).
        var clockA = new FakeTickClock();
        var clockB = new FakeTickClock();
        var flip = 0;
        var probe = new Func<HealthEvaluation>(() =>
            (Volatile.Read(ref flip) & 1) == 0
                ? HealthEvaluation.Healthy
                : HealthEvaluation.Unhealthy("down"));

        var node = HealthNode.Create("Shared").WithHealthProbe(probe);
        using var graphA = HealthGraph.Create(node, clockA.Read);
        using var graphB = HealthGraph.Create(node, clockB.Read);

        const int iters = 30_000;
        var start = new ManualResetEventSlim(false);
        var regressions = 0;
        var stop = false;

        var waveA = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < iters; i++)
            {
                clockA.AdvanceTicks(7);          // faster clock
                Interlocked.Increment(ref flip);
                graphA.RefreshAll();
            }
        });
        var waveB = new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < iters; i++)
            {
                clockB.AdvanceTicks(3);          // slower clock — its now trails A's
                Interlocked.Increment(ref flip);
                graphB.RefreshAll();
            }
        });
        // Sampler: read the published baseline continuously; any decrease is a regression.
        var sampler = new Thread(() =>
        {
            start.Wait();
            var prev = TimeSpan.MinValue;
            while (!Volatile.Read(ref stop))
            {
                var cur = node.LastWaveTimeForTest;
                if (cur is TimeSpan t)
                {
                    if (t < prev)
                        Interlocked.Increment(ref regressions);
                    prev = t;
                }
            }
        });

        waveA.Start();
        waveB.Start();
        sampler.Start();
        start.Set();
        waveA.Join();
        waveB.Join();
        Volatile.Write(ref stop, true);
        sampler.Join();

        Assert.Equal(0, regressions);
    }

    /// <summary>
    /// A monotonic, thread-safe fake clock in <see cref="Stopwatch"/>-tick units.
    /// <see cref="AdvanceTicks"/> adds raw ticks (no wall-clock sleep — this is an
    /// in-memory counter), so the storm runs at full speed.
    /// </summary>
    private sealed class FakeTickClock
    {
        private long _ticks;
        public long Read() => Interlocked.Read(ref _ticks);
        public void AdvanceTicks(long ticks) => Interlocked.Add(ref _ticks, ticks);
    }
}
