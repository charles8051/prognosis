using System.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// The single-deadline reconciliation (ADR-010 OQ3 / ADR-011 OQ5): the graph exposes ONE
/// <see cref="HealthGraph.NextTemporalDeadline"/> — a min over BOTH policy pending-deadlines
/// (wave TimeSpan timebase) AND leased nodes' next-decay instants (lease Stopwatch-tick
/// timebase), reconciled into wave time. These are graph-level, deterministic (fake clock,
/// manual <see cref="HealthGraph.RefreshAll"/>) — no monitor timing.
/// </summary>
public class HealthLeaseDeadlineTests
{
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    // The tick→wave-time conversion divides by Stopwatch.Frequency, so compare within a
    // small tolerance rather than exact ticks.
    private static void AssertClose(TimeSpan expected, TimeSpan? actual)
    {
        Assert.NotNull(actual);
        Assert.True(
            Math.Abs((actual.Value - expected).TotalMilliseconds) < 2,
            $"expected ~{expected}, got {actual}");
    }

    [Fact]
    public void LeaseDecay_SurfacesInNextTemporalDeadline_InWaveTime()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);   // constructedAt = t0 = 0
        lease.Affirm(HealthEvaluation.Healthy);              // affirmedAt = 0
        graph.RefreshAll();

        // Next decay (expiry to Unknown) at affirmedAt + Ttl = 90, in wave time.
        AssertClose(Sec(90), graph.NextTemporalDeadline);
    }

    [Fact]
    public void LeaseReaffirm_MovesTheDeadlineLater()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);
        graph.RefreshAll();
        AssertClose(Sec(90), graph.NextTemporalDeadline);

        clock.AdvanceSeconds(30);
        lease.Affirm(HealthEvaluation.Healthy);   // affirmedAt = 30 → expiry at 120
        graph.RefreshAll();
        AssertClose(Sec(120), graph.NextTemporalDeadline);
    }

    [Fact]
    public void FullyEscalatedLease_ContributesNoDeadline()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), EscalateAfter: Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);
        graph.RefreshAll();

        // Past ttl + escalateAfter = 180: escalated, stable verdict, no further deadline.
        clock.AdvanceSeconds(200);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Root.Status);
        Assert.Null(graph.NextTemporalDeadline);
    }

    [Fact]
    public void SingleMin_FoldsLeaseAndPolicy_OverOneGraph()
    {
        var clock = new FakeClock();
        var leaseNode = HealthNode.Create("Lease");
        var lease = leaseNode.Lease(new HealthLeaseOptions(Sec(90), Clock: clock.Read));

        var probe = new Probe(HealthEvaluation.Healthy);
        var peripheralNode = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read)
            .WithDebounce(new DebounceOptions(Sec(10)));

        var root = HealthNode.Create("Root")
            .DependsOn(leaseNode, Importance.Required)
            .DependsOn(peripheralNode, Importance.Required);

        var graph = HealthGraph.Create(root, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);   // lease decay at 90
        graph.RefreshAll();

        // Peripheral drops at t=5 (sub-threshold): installs a policy deadline at 5+10=15. The
        // single min folds both → 15 (the nearer, policy).
        clock.AdvanceSeconds(5);
        probe.Set(HealthEvaluation.Unhealthy("gone"));
        graph.RefreshAll();
        AssertClose(Sec(15), graph.NextTemporalDeadline);

        // Past the debounce window (t=25): the peripheral gates, its deadline clears; the lease (90) is
        // now the min — the SAME single surface serviced both.
        clock.AdvanceSeconds(20);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
        AssertClose(Sec(90), graph.NextTemporalDeadline);
    }

    [Fact]
    public void DetachedLease_ContributesNoDeadline()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);
        graph.RefreshAll();
        AssertClose(Sec(90), graph.NextTemporalDeadline);

        // A later probe install detaches the lease; its deadline must vanish.
        node.ReplaceHealthProbe(() => HealthEvaluation.Healthy);
        graph.RefreshAll();
        Assert.Null(graph.NextTemporalDeadline);
    }

    [Fact]
    public void LeaseDeadline_AtExactExpiry_SchedulesTheUnknownStage_NotEscalation()
    {
        // Decay keeps the verdict authoritative while `age <= ttl`, so at now == expireAt
        // the node is still fresh and the NEXT deadline is the Unknown-stage boundary
        // (~90), NOT escalation (~180). The pre-fix strict `<` skipped the Unknown stage
        // here — falsification: it surfaces ~180.
        var clock = new FakeClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), EscalateAfter: Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);

        clock.AdvanceSeconds(90);   // now == expireAt exactly
        graph.RefreshAll();

        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status); // age == ttl -> still fresh
        AssertClose(Sec(90), graph.NextTemporalDeadline);
    }

    [Fact]
    public void LeaseDeadline_AtExactEscalation_SchedulesEscalation_NotNull()
    {
        // Decay keeps Unknown while `age <= ttl + escalateAfter`, so at now == escalateAt
        // the node is still Unknown and the escalation deadline (~180) must remain
        // scheduled — the pre-fix strict `<` dropped it to null, potentially stranding the
        // node at Unknown forever. Falsification: it surfaces null.
        var clock = new FakeClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), EscalateAfter: Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);

        clock.AdvanceSeconds(180);  // now == escalateAt exactly (ttl + escalateAfter)
        graph.RefreshAll();

        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status); // age == ttl+esc -> still Unknown
        AssertClose(Sec(180), graph.NextTemporalDeadline);
    }

    [Fact]
    public void LeaseDeadline_IsEpochIndependent_WhenLeaseClockDiffersFromGraphClock()
    {
        // The lease clock and graph clock share a RATE (both Stopwatch-frequency) but a
        // wildly different EPOCH. The duration-based reconciliation (now + TimeUntil)
        // must still yield ~90s of wave time — the absolute-tick approach would produce
        // garbage (~1e6 s, or a clamp-to-zero "wake now" busy-loop).
        var graphClock = new FakeClock();                 // epoch 0
        var leaseClock = new FakeClock();
        leaseClock.AdvanceSeconds(1_000_000);             // epoch far from the graph's

        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), Clock: leaseClock.Read));
        var graph = HealthGraph.Create(node, graphClock.Read);
        lease.Affirm(HealthEvaluation.Healthy);
        graph.RefreshAll();

        AssertClose(Sec(90), graph.NextTemporalDeadline);
    }

    // ── HasTemporalNodes + the undriven-temporal-graph warning ──────────

    [Fact]
    public void HasTemporalNodes_ReflectsARuntimeLease_NotJustConstruction()
    {
        // ADR-010 §1: a lease is installable at runtime, so HasTemporalNodes must be
        // live, not frozen at construction (otherwise the warning is falsely silent for
        // a node leased after the graph was built).
        var node = HealthNode.Create("N");
        var graph = HealthGraph.Create(node);
        Assert.False(graph.HasTemporalNodes);
        string? before = null;
        graph.WarnIfTemporalWithoutWaveSource(m => before = m);
        Assert.Null(before);

        node.Lease(new HealthLeaseOptions(Sec(90)));   // runtime lease, after construction

        Assert.True(graph.HasTemporalNodes);
        string? after = null;
        graph.WarnIfTemporalWithoutWaveSource(m => after = m);
        Assert.NotNull(after);
    }


    [Fact]
    public void WarnIfTemporalWithoutWaveSource_Warns_WhenTemporalAndNoMonitor()
    {
        var node = HealthNode.Create("Leased");
        node.Lease(new HealthLeaseOptions(Sec(90)));
        var graph = HealthGraph.Create(node);

        Assert.True(graph.HasTemporalNodes);
        string? message = null;
        graph.WarnIfTemporalWithoutWaveSource(m => message = m);
        Assert.NotNull(message);
        Assert.Contains("wave source", message);
    }

    [Fact]
    public void WarnIfTemporalWithoutWaveSource_Silent_WhenMonitorAttached()
    {
        var node = HealthNode.Create("Leased");
        node.Lease(new HealthLeaseOptions(Sec(90)));
        var graph = HealthGraph.Create(node);

        using var monitor = new HealthMonitor(graph);   // attaches the wave source

        string? message = null;
        graph.WarnIfTemporalWithoutWaveSource(m => message = m);
        Assert.Null(message);
    }

    [Fact]
    public void WarnIfTemporalWithoutWaveSource_Silent_WhenNoTemporalNodes()
    {
        var graph = HealthGraph.Create(HealthNode.Create("Plain"));
        Assert.False(graph.HasTemporalNodes);

        string? message = null;
        graph.WarnIfTemporalWithoutWaveSource(m => message = m);
        Assert.Null(message);
    }

    [Fact]
    public void RunMonitor_StartsAndAttachesWaveSource()
    {
        var node = HealthNode.Create("Leased");
        node.Lease(new HealthLeaseOptions(Sec(90)));
        var graph = HealthGraph.Create(node);

        using var monitor = graph.RunMonitor();

        Assert.Same(graph, monitor.Graph);
        string? message = null;
        graph.WarnIfTemporalWithoutWaveSource(m => message = m);
        Assert.Null(message);
    }

    private sealed class Probe(HealthEvaluation initial)
    {
        private HealthEvaluation _value = initial;
        public HealthEvaluation Read() => Volatile.Read(ref _value);
        public void Set(HealthEvaluation value) => Volatile.Write(ref _value, value);
    }

    private sealed class FakeClock
    {
        private long _ticks;
        public long Read() => Volatile.Read(ref _ticks);
        public void AdvanceSeconds(double seconds)
            => Interlocked.Add(ref _ticks, (long)(seconds * Stopwatch.Frequency));
    }
}
