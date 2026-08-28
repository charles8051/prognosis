using System.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// ADR-011 temporal policy pipeline. Falsification discipline (per the task and the
/// peripheral-debounce PR): pure cores are table-tested with a fake clock asserting on
/// accumulated time; every graph-level claim exercises a real multi-writer or
/// deadline path; each test is written so a mutation of the production logic turns
/// it red (the mutation checked is noted in-line where non-obvious).
/// </summary>
public class TemporalPolicyTests
{
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GraceDeadline = TimeSpan.FromSeconds(100);

    // ─────────────────────────── DebounceCore (pure) ───────────────────────────

    [Fact]
    public void Debounce_HealthyRaw_PassesImmediately_NoDeadline()
    {
        var (eff, deadline) = DebounceCore.Apply(
            HealthEvaluation.Healthy, runStartedAt: TimeSpan.Zero, now: TimeSpan.FromSeconds(5),
            new DebounceOptions(Min), priorEffective: HealthEvaluation.Unhealthy("stale"));

        Assert.Equal(HealthStatus.Healthy, eff.Status);
        Assert.Null(deadline);
    }

    [Fact]
    public void Debounce_SubThresholdFault_HoldsPriorEffective_AndInstallsDeadline()
    {
        var prior = HealthEvaluation.Healthy;
        // run started at 5s, now 8s -> 3s < 10s threshold -> hold.
        var (eff, deadline) = DebounceCore.Apply(
            HealthEvaluation.Unhealthy("blip"), runStartedAt: TimeSpan.FromSeconds(5),
            now: TimeSpan.FromSeconds(8), new DebounceOptions(Min), prior);

        Assert.Same(prior, eff);                                   // held last-good
        Assert.Equal(TimeSpan.FromSeconds(15), deadline);          // runStart + min
    }

    [Fact]
    public void Debounce_AtThresholdBoundary_Gates()
    {
        // runDuration == MinimumFaultDuration is NOT held (the `<` boundary). This is
        // the inequality a falsification flips (`<=` would keep holding here).
        var (eff, deadline) = DebounceCore.Apply(
            HealthEvaluation.Unhealthy("down"), runStartedAt: TimeSpan.FromSeconds(5),
            now: TimeSpan.FromSeconds(15), new DebounceOptions(Min), HealthEvaluation.Healthy);

        Assert.Equal(HealthStatus.Unhealthy, eff.Status);
        Assert.Null(deadline);
    }

    [Fact]
    public void Debounce_HeldAs_ReportsConfiguredStatus_NotLastGood()
    {
        var (eff, _) = DebounceCore.Apply(
            HealthEvaluation.Unhealthy("blip"), runStartedAt: TimeSpan.Zero,
            now: TimeSpan.FromSeconds(1), new DebounceOptions(Min, HeldAs: HealthStatus.Degraded),
            priorEffective: HealthEvaluation.Healthy);

        Assert.Equal(HealthStatus.Degraded, eff.Status);
        Assert.Equal("blip", eff.Reason);   // carries the raw reason
    }

    // ─────────────────────────── GraceCore (pure) ───────────────────────────

    private static readonly GraceOptions Grace100 = new(GraceDeadline);

    [Fact]
    public void Grace_NeverLive_BeforeDeadline_SuppressesToUnknown()
    {
        var res = GraceCore.Apply(
            HealthEvaluation.Unhealthy("no device"), isLiveNow: false, default,
            now: TimeSpan.FromSeconds(30), Grace100);

        Assert.Equal(HealthStatus.Unknown, res.Effective.Status);
        Assert.Equal(GraceCore.GraceReason, res.Effective.Reason);
        Assert.False(res.Next.HasEverBeenLive);
    }

    [Fact]
    public void Grace_LiveNow_PassesRaw_AndLatches()
    {
        var res = GraceCore.Apply(
            HealthEvaluation.Unhealthy("down but live"), isLiveNow: true, default,
            now: TimeSpan.FromSeconds(30), Grace100);

        Assert.Equal(HealthStatus.Unhealthy, res.Effective.Status);   // raw passes
        Assert.True(res.Next.HasEverBeenLive);                        // latched
    }

    [Fact]
    public void Grace_AlreadyLatched_PassesRaw_EvenWhenNotLiveNow()
    {
        var latched = new GraceState(HasEverBeenLive: true, DeadlineAt: TimeSpan.FromSeconds(100));
        var res = GraceCore.Apply(
            HealthEvaluation.Unhealthy("down"), isLiveNow: false, latched,
            now: TimeSpan.FromSeconds(10), Grace100);

        Assert.Equal(HealthStatus.Unhealthy, res.Effective.Status);
    }

    [Fact]
    public void Grace_NeverLive_PastDeadline_GatesOnRawMerits()
    {
        // ADR-008 resolution path: past the deadline a never-live node gates on raw.
        var res = GraceCore.Apply(
            HealthEvaluation.Unhealthy("no device"), isLiveNow: false,
            new GraceState(false, DeadlineAt: TimeSpan.FromSeconds(100)),
            now: TimeSpan.FromSeconds(100), Grace100);   // now == deadline (>=)

        Assert.Equal(HealthStatus.Unhealthy, res.Effective.Status);
    }

    [Fact]
    public void Grace_DeadlineIsAnchoredOnFirstFold_NotSliding()
    {
        // First fold at now=10 anchors deadline at 110. A later fold at now=60 must
        // still see 110, not 160 — otherwise the window slides forever and never fires.
        var first = GraceCore.Apply(
            HealthEvaluation.Unhealthy("x"), false, default, TimeSpan.FromSeconds(10), Grace100);
        Assert.Equal(TimeSpan.FromSeconds(110), first.Next.DeadlineAt);

        var second = GraceCore.Apply(
            HealthEvaluation.Unhealthy("x"), false, first.Next, TimeSpan.FromSeconds(60), Grace100);
        Assert.Equal(TimeSpan.FromSeconds(110), second.Next.DeadlineAt);
    }

    // ─────────────────────────── FlapWindow (pure) ───────────────────────────

    private static NodeObservationHistory HistoryWith(params double[] transitionSeconds)
    {
        var h = NodeObservationHistory.Seed(HealthStatus.Healthy);
        var status = HealthStatus.Healthy;
        foreach (var s in transitionSeconds)
        {
            status = status == HealthStatus.Healthy ? HealthStatus.Unhealthy : HealthStatus.Healthy;
            h = h.RecordTransition(status, TimeSpan.FromSeconds(s));
        }
        return h;
    }

    [Fact]
    public void Flap_CountsOnlyTransitionsInsideWindow()
    {
        var h = HistoryWith(1, 2, 50, 90, 95);   // 5 transitions at those instants
        // window (100-20, 100] = (80,100]: instants 90 and 95 -> 2.
        Assert.Equal(2, FlapWindow.Count(h, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Flap_BoundaryTransition_ExactlyWindowOld_IsCounted()
    {
        var h = HistoryWith(80);   // one transition at 80s
        // now=100, window=20 -> cutoff exactly 80 -> counted (>= cutoff).
        Assert.Equal(1, FlapWindow.Count(h, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Flap_NonPositiveWindow_CountsNothing()
    {
        var h = HistoryWith(1, 2, 3);
        Assert.Equal(0, FlapWindow.Count(h, TimeSpan.FromSeconds(100), TimeSpan.Zero));
    }

    [Fact]
    public void Flap_ColdStart_EmptyHistory_IsZero()
    {
        var h = NodeObservationHistory.Seed(HealthStatus.Healthy);
        Assert.Equal(0, FlapWindow.Count(h, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void History_TransitionsAreBounded_DropOldest()
    {
        var h = NodeObservationHistory.Seed(HealthStatus.Healthy);
        for (var i = 1; i <= NodeObservationHistory.TransitionBound + 5; i++)
        {
            var status = i % 2 == 1 ? HealthStatus.Unhealthy : HealthStatus.Healthy;
            h = h.RecordTransition(status, TimeSpan.FromSeconds(i));
        }

        Assert.Equal(NodeObservationHistory.TransitionBound, h.Transitions.Count);
        // Oldest dropped: the first retained instant is (total - bound + 1).
        var total = NodeObservationHistory.TransitionBound + 5;
        Assert.Equal(TimeSpan.FromSeconds(total - NodeObservationHistory.TransitionBound + 1), h.Transitions[0]);
        Assert.Equal(TimeSpan.FromSeconds(total), h.Transitions[^1]);
    }

    // ─────────────────────── Debounce end-to-end (the worked example) ───────────────────────

    [Fact]
    public void Debounce_SubThresholdBlip_DoesNotGate_ButInstallsDeadline_ThenGates()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);
        graph.GetReport();   // populate the cached report so the first wave's equal report does not emit

        var statusEmissions = 0;
        graph.StatusChanged.Subscribe(new Obs<HealthReport>(_ => statusEmissions++));
        var deadlines = new List<TimeSpan?>();
        graph.TemporalDeadlineChanged.Subscribe(new Obs<TimeSpan?>(d => deadlines.Add(d)));
        deadlines.Clear();   // drop the replayed initial

        // Device drops at t=5 (sub-threshold).
        probe.Set(HealthEvaluation.Unhealthy("gone"));
        clock.AdvanceSeconds(5);
        graph.RefreshAll();

        Assert.True(clock.ElapsedSeconds >= 5, "fake clock must have advanced");
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);   // HELD, not gated
        Assert.Equal(0, statusEmissions);                                    // no gating emission
        Assert.Equal(TimeSpan.FromSeconds(15), graph.NextTemporalDeadline);  // 5 + 10
        // The deadline moved (null -> 15) WITHOUT a status change.
        Assert.Contains(TimeSpan.FromSeconds(15), deadlines);

        // Still absent past the window: now it gates.
        clock.AdvanceSeconds(20);   // t=25, run started at 5 -> 20s >= 10s
        graph.RefreshAll();

        Assert.True(clock.ElapsedSeconds >= 25);
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
        Assert.Equal(1, statusEmissions);                 // gated exactly once
        Assert.Null(graph.NextTemporalDeadline);          // deadline cleared
        Assert.Contains((TimeSpan?)null, deadlines);      // moved back to null
    }

    [Fact]
    public void Flap_ReadsRawTransitions_EvenWhenSuppressionHidesThem()
    {
        // A long debounce window holds every blip, so the effective status never
        // leaves Healthy — yet flap must still count the raw transitions (ADR-011 §8).
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Twitchy").WithHealthProbe(probe.Read)
            .WithDebounce(new DebounceOptions(TimeSpan.FromHours(1)));   // holds everything
        var graph = HealthGraph.Create(node, clock.Read);

        var gated = false;
        graph.StatusChanged.Subscribe(new Obs<HealthReport>(r =>
        {
            if (r.Root.Status != HealthStatus.Healthy) gated = true;
        }));

        for (var i = 0; i < 4; i++)
        {
            clock.AdvanceSeconds(1);
            probe.Set(HealthEvaluation.Unhealthy("blip"));
            graph.RefreshAll();
            clock.AdvanceSeconds(1);
            probe.Set(HealthEvaluation.Healthy);
            graph.RefreshAll();
        }

        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);   // never gated
        Assert.False(gated);
        var (_, history) = node.Observe();
        // 8 raw transitions (4 down + 4 up) all recorded despite full suppression.
        Assert.Equal(8, FlapWindow.Count(history, TimeSpan.FromSeconds(clock.ElapsedSeconds + 1), TimeSpan.FromHours(1)));
    }

    // ─────────────────────── Grace end-to-end (§3) ───────────────────────

    [Fact]
    public void Grace_NeverLive_SuppressesUntilDeadline_ThenGatesOnMerits()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Unhealthy("no device yet"));
        var node = HealthNode.Create("Dev").WithHealthProbe(probe.Read).WithGrace(Grace100);
        var graph = HealthGraph.Create(node, clock.Read);

        // At construction (t=0) grace already suppresses the determined Unhealthy.
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);
        Assert.Equal(GraceCore.GraceReason, graph.GetReport().Root.Reason);

        clock.AdvanceSeconds(50);
        graph.RefreshAll();
        Assert.True(clock.ElapsedSeconds >= 50);
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);   // still in grace

        clock.AdvanceSeconds(60);   // t=110 > 100 deadline
        graph.RefreshAll();
        Assert.True(clock.ElapsedSeconds >= 110);
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status); // gates on merits
    }

    [Fact]
    public void MarkLive_ClearsGrace_OnNextWave_AndIsOneWay()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Unhealthy("down but the device is up"));
        var node = HealthNode.Create("Dev").WithHealthProbe(probe.Read).WithGrace(Grace100);
        var graph = HealthGraph.Create(node, clock.Read);

        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);   // grace

        clock.AdvanceSeconds(20);
        node.MarkLive();                 // schedules nothing; the next wave carries it
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);  // grace cleared

        // One-way: the latch does not un-set, so a later wave still gates on raw.
        clock.AdvanceSeconds(5);
        graph.RefreshAll();
        Assert.True(node.Observe().History.HasEverBeenLive);
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
    }

    [Fact]
    public void Grace_NeverWavedNodeWithNoGraph_ChainIsInert()
    {
        // ADR-011 §5: a policied node never evaluated in a wave has no timebase, so
        // the chain is inert (identity) — Refresh() with no graph must not suppress.
        var probe = new Probe(HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.Create("Lonely").WithHealthProbe(probe.Read).WithGrace(Grace100);

        node.Refresh();   // no graph attached -> BubbleChange(null), no clock

        Assert.Equal(HealthStatus.Unhealthy, node.Observe().Effective.Status);   // NOT suppressed
    }

    // ─────────────────────── Chain order: debounce then grace (§2) ───────────────────────

    [Fact]
    public void ChainOrder_GraceBeforeLive_DebounceAfterLive()
    {
        // The field-proven composition (ADR-011 §2): grace acts only before first-live,
        // debounce only after it, and debounce runs first so the grace latch keeps
        // advancing on the same observations. We assert both phases.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Unhealthy("no device yet"));
        var node = HealthNode.Create("Both").WithHealthProbe(probe.Read)
            .WithDebounce(new DebounceOptions(Min))       // 10s window
            .WithGrace(new GraceOptions(TimeSpan.FromSeconds(100)));
        var graph = HealthGraph.Create(node, clock.Read);

        // Phase 1 — before first-live: grace governs, the raw Unhealthy is suppressed.
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);
        Assert.Equal(GraceCore.GraceReason, graph.GetReport().Root.Reason);

        // Device becomes live and healthy; establish a live Healthy baseline.
        node.MarkLive();
        probe.Set(HealthEvaluation.Healthy);
        clock.AdvanceSeconds(5);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);

        // Phase 2 — after first-live: a sub-threshold fault is HELD by debounce (grace
        // is latched out of the way), effective stays Healthy, deadline at 8+10=18.
        probe.Set(HealthEvaluation.Unhealthy("blip"));
        clock.AdvanceSeconds(3);   // t=8
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);
        Assert.Equal(TimeSpan.FromSeconds(18), graph.NextTemporalDeadline);
    }

    // ─────────────────────── Lease/policy mutual exclusion (§7) ───────────────────────

    [Fact]
    public void Lease_OnPolicyNode_Throws()
    {
        var node = HealthNode.Create("N").WithDebounce(new DebounceOptions(Min));
        Assert.Throws<InvalidOperationException>(() => node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public void Lease_OnGraceNode_Throws()
    {
        var node = HealthNode.Create("N").WithGrace(Grace100);
        Assert.Throws<InvalidOperationException>(() => node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public void Debounce_OnLeasedNode_Throws()
    {
        var node = HealthNode.Create("N");
        node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30)));
        Assert.Throws<InvalidOperationException>(() => node.WithDebounce(new DebounceOptions(Min)));
    }

    [Fact]
    public void Grace_OnLeasedNode_Throws()
    {
        var node = HealthNode.Create("N");
        node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30)));
        Assert.Throws<InvalidOperationException>(() => node.WithGrace(Grace100));
    }

    // ─────────────────────── Choke-point invariants (§4) ───────────────────────

    [Fact]
    public void RawTransition_RecordedExactlyOnce_AcrossTwoGraphs()
    {
        // A node in two graphs propagates under two waves (multicast strategy). The
        // first wave records the raw transition; the second sees no raw change and
        // records nothing (ADR-011 §4/§5). Falsification: dropping the
        // `raw.Status != LastRaw` guard records twice.
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Shared").WithHealthProbe(probe.Read);
        using var a = HealthGraph.Create(node);
        using var b = HealthGraph.Create(node);

        probe.Set(HealthEvaluation.Unhealthy("down"));
        node.Refresh();   // one logical change -> both graphs wave

        Assert.Single(node.Observe().History.Transitions);
    }

    [Fact]
    public void ReportStatus_BypassesChainAndHistory()
    {
        // The one-shot interjection writes effective directly, bypassing the policy
        // chain (a grace node is NOT suppressed by the push) and records no transition.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Unhealthy("no device"));
        var node = HealthNode.Create("Push").WithHealthProbe(probe.Read).WithGrace(Grace100);
        var graph = HealthGraph.Create(node, clock.Read);

        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);   // grace-suppressed

        node.ReportStatus(HealthEvaluation.Degraded("operator override"));

        // The pushed value survives the wave unshaped by grace, and no transition was
        // recorded from the push.
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Root.Status);
        Assert.Empty(node.Observe().History.Transitions);
    }

    [Fact]
    public void WithHealthProbe_DirectWrite_LeavesPreChainValue_UntilNextWave()
    {
        // ADR-011 §4: WithHealthProbe writes the probe's immediate value directly and
        // does NOT run the chain; the pre-chain value is visible until the next wave.
        var clock = new FakeClock();
        var node = HealthNode.Create("N").WithGrace(Grace100);
        var graph = HealthGraph.Create(node, clock.Read);

        node.WithHealthProbe(() => HealthEvaluation.Unhealthy("down"));
        // No wave yet: the direct write is visible pre-chain (Unhealthy, not grace).
        Assert.Equal(HealthStatus.Unhealthy, node.Observe().Effective.Status);

        graph.RefreshAll();   // now the chain runs
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);  // grace-suppressed
    }

    [Fact]
    public void Unconfigured_Node_TracksFlap_ButBehavesIdentically()
    {
        // Additive: an unconfigured node's effective is identity, but it still records
        // raw transitions so flap is observable everywhere.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Plain").WithHealthProbe(probe.Read);
        var graph = HealthGraph.Create(node, clock.Read);

        clock.AdvanceSeconds(1);
        probe.Set(HealthEvaluation.Unhealthy("down"));
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);  // identity, gated

        clock.AdvanceSeconds(1);
        probe.Set(HealthEvaluation.Healthy);
        graph.RefreshAll();

        Assert.Equal(2, node.Observe().History.Transitions.Count);
    }

    // ─────────────────────── TemporalDeadlineChanged (§6a) ───────────────────────

    [Fact]
    public void DeadlineChanged_Silent_WhenNothingPendingChanges()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("N").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);

        var emissions = new List<TimeSpan?>();
        graph.TemporalDeadlineChanged.Subscribe(new Obs<TimeSpan?>(emissions.Add));
        emissions.Clear();   // drop replayed initial

        // Two healthy waves: nothing pending, deadline stays null -> no emission.
        clock.AdvanceSeconds(1);
        graph.RefreshAll();
        clock.AdvanceSeconds(1);
        graph.RefreshAll();

        Assert.Empty(emissions);
    }

    [Fact]
    public void DeadlineChanged_ReplaysCurrentMinimum_OnSubscribe()
    {
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("N").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);

        probe.Set(HealthEvaluation.Unhealthy("gone"));
        clock.AdvanceSeconds(2);
        graph.RefreshAll();   // installs a deadline at 12

        // A LATE subscriber must immediately receive the already-pending deadline.
        TimeSpan? replayed = TimeSpan.FromSeconds(-1);
        graph.TemporalDeadlineChanged.Subscribe(new Obs<TimeSpan?>(d => replayed = d));
        Assert.Equal(TimeSpan.FromSeconds(12), replayed);
    }

    [Fact]
    public void DeadlineChanged_FiresOutsidePropagationLock()
    {
        // If the emission ran under _propagationLock, a wave started from ANOTHER
        // thread inside the handler would block forever (the emitting thread would be
        // holding the lock while OnNext waits on the other thread). We prove it does
        // not by waving from a pool thread inside OnNext and requiring completion.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("N").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);

        var reentered = 0;
        var nestedCompleted = new ManualResetEventSlim(false);
        graph.TemporalDeadlineChanged.Subscribe(new Obs<TimeSpan?>(d =>
        {
            // Ignore the null replay-on-subscribe; re-enter only on the REAL deadline
            // change installed by the wave below, and only once.
            if (d is not null && Interlocked.Exchange(ref reentered, 1) == 0)
            {
                var t = Task.Run(() => graph.RefreshAll());
                if (t.Wait(TimeSpan.FromSeconds(5)))
                    nestedCompleted.Set();
            }
        }));

        probe.Set(HealthEvaluation.Unhealthy("gone"));
        clock.AdvanceSeconds(2);
        graph.RefreshAll();   // fires the deadline change -> handler waves from a pool thread

        Assert.True(nestedCompleted.Wait(TimeSpan.FromSeconds(5)),
            "a nested wave from another thread must not deadlock -> emission is outside the lock");
    }

    [Fact]
    public void BackwardsClock_DoesNotRegressWaveTime_SoAGatedNodeStaysGated()
    {
        // Defence in depth (ElapsedNow monotonic clamp): a contract-violating clock that
        // steps BACKWARDS (but stays positive, so the raw<0 floor does not mask it) must
        // not make wave time regress. Once a debounce fault has persisted past its window
        // and gated, a backwards clock step must NOT shrink the run duration back below
        // the threshold and un-gate. Falsification: without the clamp, now regresses and
        // the node flips back to a hold (Healthy).
        var clock = new SteppableClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("N").WithHealthProbe(probe.Read).WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node, clock.Read);

        probe.Set(HealthEvaluation.Unhealthy("gone"));
        clock.SetSeconds(2);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);   // held (run 0 < 10)

        clock.SetSeconds(15);   // run 13 >= 10 -> gates; no pending deadline
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
        Assert.Null(graph.NextTemporalDeadline);

        // Clock steps BACKWARDS to a positive-but-earlier instant. With the monotonic
        // clamp, wave time holds at 15, run duration stays 13, and the node stays gated
        // with NO pending deadline. Without the clamp, now regresses to 5, run duration
        // shrinks to 3 < 10, the debounce re-enters a hold and re-installs a pending
        // deadline — the stale re-arm the clamp exists to prevent.
        clock.SetSeconds(5);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
        Assert.Null(graph.NextTemporalDeadline);
    }

    [Fact]
    public void DeadlineChanged_SubscribeAfterDispose_CompletesImmediately_NotStranded()
    {
        var node = HealthNode.Create("N").WithHealthProbe(() => HealthEvaluation.Healthy)
            .WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node);
        graph.Dispose();   // completes the channel

        var completed = false;
        var got = 0;
        graph.TemporalDeadlineChanged.Subscribe(new CompletingObs(
            _ => got++, () => completed = true));

        Assert.True(completed, "a subscriber arriving after Complete must receive OnCompleted, not be stranded");
        Assert.Equal(0, got);
    }

    [Fact]
    public void NextTemporalDeadline_NullWhenNothingPending()
    {
        var node = HealthNode.Create("N").WithHealthProbe(() => HealthEvaluation.Healthy)
            .WithDebounce(new DebounceOptions(Min));
        var graph = HealthGraph.Create(node);
        Assert.Null(graph.NextTemporalDeadline);
    }

    // ─────────────────────── Exported grace core (§9) ───────────────────────

    [Fact]
    public void ApplyGrace_IsPure_AndNodeFree()
    {
        var res = Grace.ApplyGrace(
            HealthEvaluation.Unhealthy("x"), isLiveNow: false, default, Grace100,
            now: TimeSpan.FromSeconds(10));
        Assert.Equal(HealthStatus.Unknown, res.Effective.Status);

        var live = Grace.ApplyGrace(
            HealthEvaluation.Unhealthy("x"), isLiveNow: true, res.Next, Grace100,
            now: TimeSpan.FromSeconds(20));
        Assert.Equal(HealthStatus.Unhealthy, live.Effective.Status);
    }

    [Fact]
    public void ApplyGrace_DefaultNow_UsesLibraryMonotonicClock()
    {
        // With no `now`, a short deadline means the very first fold is already at/after
        // the process clock's reading (which is far past a 0-length window). A zero
        // deadline must gate immediately on the library clock, not suppress forever.
        var res = Grace.ApplyGrace(
            HealthEvaluation.Unhealthy("x"), isLiveNow: false, default, new GraceOptions(TimeSpan.Zero));
        Assert.Equal(HealthStatus.Unhealthy, res.Effective.Status);
    }

    [Fact]
    public void GraceMachine_OwnsState_AcrossCalls()
    {
        var machine = new GraceMachine(Grace100);
        Assert.Equal(HealthStatus.Unknown, machine.Update(HealthEvaluation.Unhealthy("x"), isLiveNow: false).Status);
        // Once live, latched internally; a later not-live call still passes raw.
        Assert.Equal(HealthStatus.Unhealthy, machine.Update(HealthEvaluation.Unhealthy("x"), isLiveNow: true).Status);
        Assert.Equal(HealthStatus.Unhealthy, machine.Update(HealthEvaluation.Unhealthy("x"), isLiveNow: false).Status);
    }

    [Fact]
    public void GraceMachine_NegativeDeadline_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new GraceMachine(new GraceOptions(TimeSpan.FromSeconds(-1))));

    [Fact]
    public void ApplyGrace_And_WithGracePolicy_ProduceIdenticalResults()
    {
        // §9 belt-and-suspenders: the in-graph policy and the exported fold both route
        // through the one GraceCore, so identical inputs give identical effective
        // verdicts. Drive a node WithGrace and mirror each wave with ApplyGrace.
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.Create("N").WithHealthProbe(probe.Read).WithGrace(Grace100);
        var graph = HealthGraph.Create(node, clock.Read);

        var state = default(GraceState);
        var mirror = Grace.ApplyGrace(HealthEvaluation.Unhealthy("down"), false, state, Grace100, TimeSpan.Zero);
        Assert.Equal(mirror.Effective.Status, graph.GetReport().Root.Status);   // both Unknown at t=0
        state = mirror.Next;

        foreach (var t in new[] { 50.0, 110.0 })
        {
            clock.AdvanceSeconds(t - clock.ElapsedSeconds);
            graph.RefreshAll();
            mirror = Grace.ApplyGrace(HealthEvaluation.Unhealthy("down"), false, state, Grace100, TimeSpan.FromSeconds(t));
            state = mirror.Next;
            Assert.Equal(mirror.Effective.Status, graph.GetReport().Root.Status);
        }
    }

    // ─────────────────────── Concurrency: CAS composes (§4) ───────────────────────

    [Fact]
    public void MarkLive_ComposesWithConcurrentWaves_LatchNeverLost()
    {
        // Many threads wave the graph while another thread MarkLive()s. The CAS pair
        // guarantees the live latch is not lost to a racing wave swap, and the pair is
        // never torn (Observe always returns a self-consistent snapshot).
        var clock = new FakeClock();
        var probe = new Probe(HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.Create("N").WithHealthProbe(probe.Read).WithGrace(Grace100);
        var graph = HealthGraph.Create(node, clock.Read);

        var stop = false;
        var wavers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                clock.AdvanceSeconds(0.001);
                graph.RefreshAll();
                var snapshot = node.Observe();   // must never throw / tear
                _ = snapshot;
            }
        })).ToArray();

        Thread.Sleep(20);
        node.MarkLive();
        Thread.Sleep(20);
        Volatile.Write(ref stop, true);
        Task.WaitAll(wavers, TimeSpan.FromSeconds(10));

        Assert.True(node.Observe().History.HasEverBeenLive);
        // Latched live + still Unhealthy raw before the deadline => raw passes now.
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
    }

    // ─────────────────────── helpers ───────────────────────

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

    private sealed class CompletingObs(Action<TimeSpan?> onNext, Action onCompleted) : IObserver<TimeSpan?>
    {
        public void OnNext(TimeSpan? value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() => onCompleted();
    }

    // A clock that can be set to an absolute (even negative) tick offset, for the
    // backwards-clock defence-in-depth test.
    private sealed class SteppableClock
    {
        private long _ticks;
        public long Read() => Volatile.Read(ref _ticks);
        public void SetSeconds(double seconds)
            => Volatile.Write(ref _ticks, (long)(seconds * Stopwatch.Frequency));
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
