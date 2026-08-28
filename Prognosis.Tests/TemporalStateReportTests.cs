using System.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// ADR-013: the sparse structured <see cref="TemporalState"/> field on
/// <see cref="HealthSnapshot"/>, its population at report-build time, and — the
/// load-bearing constraint — its exclusion from report-change detection.
/// Falsification noted in-line; the exclusion tests fail if the comparer regresses
/// to record equality.
/// </summary>
public class TemporalStateReportTests
{
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(10);
    private static readonly GraceOptions Grace100 = new(TimeSpan.FromSeconds(100));

    // ───────────────── The mandated exclusion proof ─────────────────

    [Fact]
    public void Comparer_TreatsTemporalOnlyDifference_AsEqual()
    {
        // Two snapshots identical on (Name, Status, Reason) but differing ONLY in
        // Temporal must compare EQUAL and hash EQUAL. Falsification: a comparer using
        // record `==` (which folds Temporal in) fails both asserts.
        var a = new HealthSnapshot("N", HealthStatus.Healthy, null, null,
            new TemporalState(FlapCount: 1, PendingDeadline: TimeSpan.FromSeconds(9)));
        var b = new HealthSnapshot("N", HealthStatus.Healthy, null, null,
            new TemporalState(FlapCount: 7, PendingDeadline: TimeSpan.FromSeconds(3)));

        var ra = new HealthReport(a, new[] { a });
        var rb = new HealthReport(b, new[] { b });

        Assert.True(HealthReportComparer.Instance.Equals(ra, rb));
        Assert.Equal(
            HealthReportComparer.Instance.GetHashCode(ra),
            HealthReportComparer.Instance.GetHashCode(rb));
    }

    [Fact]
    public void Comparer_StillDistinguishes_RealStatusChange()
    {
        // The exclusion must not blind the comparer to a genuine status change.
        var a = new HealthSnapshot("N", HealthStatus.Healthy, null, null,
            new TemporalState(FlapCount: 1));
        var b = new HealthSnapshot("N", HealthStatus.Unhealthy, "down", null,
            new TemporalState(FlapCount: 1));

        Assert.False(HealthReportComparer.Instance.Equals(
            new HealthReport(a, new[] { a }), new HealthReport(b, new[] { b })));
    }

    [Fact]
    public void DebounceHold_TemporalDeadlineCountsDown_ButReportStreamStaysSilent()
    {
        // End-to-end: during a debounce hold the effective (Name, Status, Reason) is
        // unchanged wave to wave, but Temporal.PendingDeadline counts down. The report
        // stream must NOT churn on those Temporal-only ticks (ADR-012 §3 / ADR-013 §3),
        // while a genuine gate still emits.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);
        graph.GetReport();

        var emissions = 0;
        graph.StatusChanged.Subscribe(new Obs<HealthReport>(_ => emissions++));

        // Enter the hold.
        probe.Set(HealthEvaluation.Unhealthy("gone"));
        clock.AdvanceSeconds(2);
        graph.RefreshAll();
        var d1 = graph.GetReport().Nodes[0].Temporal!.PendingDeadline;

        // Tick within the window: Temporal deadline shrinks, status held Healthy.
        clock.AdvanceSeconds(3);
        graph.RefreshAll();
        var d2 = graph.GetReport().Nodes[0].Temporal!.PendingDeadline;

        Assert.True(clock.ElapsedSeconds >= 5);
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);   // still held
        Assert.True(d2 < d1, "the relative pending deadline must count down");
        Assert.Equal(0, emissions);   // Temporal-only change did not churn the stream

        // The real gate does emit.
        clock.AdvanceSeconds(10);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
        Assert.Equal(1, emissions);
    }

    // ───────────────── Population ─────────────────

    [Fact]
    public void Unconfigured_QuiescentNode_HasNullTemporal()
    {
        var node = HealthNode.Create("Plain").WithHealthProbe(() => HealthEvaluation.Healthy);
        var graph = HealthGraph.Create(node);
        Assert.Null(graph.GetReport().Root.Temporal);
    }

    [Fact]
    public void LeasedNode_Fresh_ThenExpired_ThenEscalated_ReportsStalenessAndBand()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node, clock.Read);
        var lease = node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(90), Clock: clock.Read));

        lease.Affirm(HealthEvaluation.Healthy);
        graph.RefreshAll();
        var fresh = graph.GetReport().Root.Temporal;
        Assert.NotNull(fresh);
        Assert.Equal(StalenessMarker.Fresh, fresh!.Staleness);
        Assert.Equal(0, fresh.TtlBand);

        clock.AdvanceSeconds(135);   // 1.5 * ttl -> stage one, band 1
        graph.RefreshAll();
        var expired = graph.GetReport().Root.Temporal!;
        Assert.Equal(StalenessMarker.Expired, expired.Staleness);
        Assert.Equal(1, expired.TtlBand);

        clock.AdvanceSeconds(100);   // > 2 * ttl -> escalated
        graph.RefreshAll();
        var escalated = graph.GetReport().Root.Temporal!;
        Assert.True(clock.ElapsedSeconds >= 235);
        Assert.Equal(StalenessMarker.Escalated, escalated.Staleness);
        Assert.Null(escalated.TtlBand);   // banding applies to the Expired stage only
    }

    [Fact]
    public void GraceNode_InWindow_ReportsInGraceAndPendingDeadline_NoStaleness()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Unhealthy("no device"));
        var node = HealthNode.Create("Dev").WithHealthProbe(probe.Read).WithGrace(Grace100);
        var graph = HealthGraph.Create(node, clock.Read);

        var t = graph.GetReport().Root.Temporal;
        Assert.NotNull(t);
        Assert.True(t!.InGraceWindow);
        Assert.False(t.InDebounceHold);
        Assert.Null(t.Staleness);                    // not leased
        Assert.NotNull(t.PendingDeadline);           // grace deadline pending
        Assert.True(t.PendingDeadline <= TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void DebounceNode_InHold_ReportsInDebounceHold()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);

        probe.Set(HealthEvaluation.Unhealthy("blip"));
        clock.AdvanceSeconds(2);
        graph.RefreshAll();

        var t = graph.GetReport().Root.Temporal!;
        Assert.True(t.InDebounceHold);
        Assert.False(t.InGraceWindow);
    }

    [Fact]
    public void DebounceNode_InHold_ReportsInDebounceHold_EvenWhenHeldValueEqualsRaw()
    {
        // HeldAs == the raw fault status: during the window the effective equals LastRaw,
        // so the pre-fix `eff.Status != history.LastRaw` heuristic read InDebounceHold
        // FALSE for a genuinely active hold. Deriving from the pending deadline reports it
        // true. Falsification: the old heuristic asserts false here.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read)
            .WithDebounce(new DebounceOptions(Min, HeldAs: HealthStatus.Unhealthy));
        var graph = HealthGraph.Create(node, clock.Read);

        probe.Set(HealthEvaluation.Unhealthy("blip"));
        clock.AdvanceSeconds(2);   // sub-threshold (< Min) -> holding, HeldAs = Unhealthy = raw
        graph.RefreshAll();

        var t = graph.GetReport().Root.Temporal!;
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status); // held value == raw
        Assert.True(t.InDebounceHold);                                        // still an active hold
        Assert.False(t.InGraceWindow);
        Assert.NotNull(t.PendingDeadline);
    }

    [Fact]
    public void DebounceNode_AfterWindowElapses_ClearsInDebounceHold()
    {
        // The load-bearing invariant behind deriving InDebounceHold from the pending
        // deadline: once the fault persists past the window (the node GATES), DebounceCore
        // returns a null deadline and the chain clears history.PendingDeadline, so
        // InDebounceHold reads false again — no false-positive hold alongside a gated
        // status. Falsification: if the deadline were left set (even to a past value),
        // this would read true.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);

        probe.Set(HealthEvaluation.Unhealthy("blip"));
        clock.AdvanceSeconds(2);    // inside the window: holding
        graph.RefreshAll();
        Assert.True(graph.GetReport().Root.Temporal!.InDebounceHold);

        clock.AdvanceSeconds(20);   // t = 22 > Min: the fault persisted, the node gates
        graph.RefreshAll();
        var t = graph.GetReport().Root.Temporal!;
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status); // gated
        Assert.False(t.InDebounceHold);                                      // hold is over
        Assert.Null(t.PendingDeadline);                                      // deadline cleared on gate
    }

    [Fact]
    public void LeasedNode_DoesNotReportInDebounceHold()
    {
        // A lease-only node (no debounce) must never report InDebounceHold, before or
        // after decay: inHold is guarded by hasDebounce, and a lease does not write
        // history.PendingDeadline. Guards the cross-cutting case the derivation relies on.
        var clock = new FakeClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Unhealthy("down"));
        graph.RefreshAll();

        Assert.False(graph.GetReport().Root.Temporal!.InDebounceHold);

        clock.AdvanceSeconds(100);  // decayed past ttl
        graph.RefreshAll();
        Assert.False(graph.GetReport().Root.Temporal!.InDebounceHold);
    }

    [Fact]
    public void FlappingNode_ReportsFlapCount_EvenWhenUnconfigured()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Twitchy").WithHealthProbe(probe.Read);
        var graph = HealthGraph.Create(node, clock.Read);

        for (var i = 0; i < 3; i++)
        {
            clock.AdvanceSeconds(1);
            probe.Set(HealthEvaluation.Unhealthy("blip"));
            graph.RefreshAll();
            clock.AdvanceSeconds(1);
            probe.Set(HealthEvaluation.Healthy);
            graph.RefreshAll();
        }

        var t = graph.GetReport().Root.Temporal;
        Assert.NotNull(t);   // flap alone populates Temporal even with no lease/policy
        Assert.Equal(6, t!.FlapCount);
    }

    // ───────────────── helpers ─────────────────

    private sealed class Probe(HealthEvaluation initial)
    {
        private volatile HealthEvaluation _eval = initial;
        public HealthEvaluation Read() => _eval;
        public void Set(HealthEvaluation e) => _eval = e;
    }

    private sealed class Obs<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class FakeClock
    {
        private long _ticks;
        public long Read() => Volatile.Read(ref _ticks);
        public void AdvanceSeconds(double seconds)
            => Interlocked.Add(ref _ticks, (long)(seconds * Stopwatch.Frequency));
        public double ElapsedSeconds => Volatile.Read(ref _ticks) / (double)Stopwatch.Frequency;
    }
}
