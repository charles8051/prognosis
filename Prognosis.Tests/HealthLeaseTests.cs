using System.Diagnostics;

namespace Prognosis.Tests;

public class HealthLeaseTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    [Fact]
    public void CreateLeased_ReturnsPendingNodeAndUsableLease()
    {
        var clock = new FakeClock();

        var (node, lease) = HealthNode.CreateLeased(
            "Svc",
            new HealthLeaseOptions(Ttl, Clock: clock.Read));
        var graph = HealthGraph.Create(node);

        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);

        lease.Affirm(new HealthEvaluation(HealthStatus.Healthy, "fresh"));

        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);
        Assert.Equal("fresh", graph.GetReport().Root.Reason);
    }

    // A pre-prefixed escalated verdict, matching what HealthLease hands to Decay.
    private static readonly HealthEvaluation Escalated =
        HealthEvaluation.Degraded(HealthLease.StaleReasonPrefix + "gone");

    private static readonly HealthEvaluation Affirmed =
        HealthEvaluation.Healthy;

    // ── Pure Decay core: table tests, no clock, no graph ──────────────

    [Fact]
    public void Decay_BelowTtl_ReturnsLastAffirmedUnchanged()
    {
        var result = HealthLease.Decay(Affirmed, TimeSpan.FromSeconds(30), Ttl, Ttl, Escalated);
        Assert.Same(Affirmed, result);
    }

    [Fact]
    public void Decay_AtTtlBoundary_StillReturnsLastAffirmed()
    {
        // age == ttl is inclusive of stage zero (age <= ttl). This is the
        // inequality the boundary falsification test flips.
        var result = HealthLease.Decay(Affirmed, Ttl, Ttl, Ttl, Escalated);
        Assert.Same(Affirmed, result);
    }

    [Fact]
    public void Decay_JustPastTtl_DecaysToUnknownStale()
    {
        var age = Ttl + TimeSpan.FromTicks(1);
        var result = HealthLease.Decay(Affirmed, age, Ttl, Ttl, Escalated);

        Assert.Equal(HealthStatus.Unknown, result.Status);
        Assert.NotNull(result.Reason);
        Assert.StartsWith(HealthLease.StaleReasonPrefix, result.Reason);
    }

    [Fact]
    public void Decay_StageOneReason_IsExactBandQuantizedString()
    {
        // age in (ttl, 2*ttl) -> band 1. ttl = 90s.
        var age = TimeSpan.FromSeconds(135);
        var result = HealthLease.Decay(Affirmed, age, Ttl, Ttl * 10, Escalated);

        Assert.Equal(
            "lease-expired: no affirmation for over 1 ttl (ttl 90s)",
            result.Reason);
    }

    [Fact]
    public void Decay_ReasonIsByteIdenticalWithinABand()
    {
        // Two different ages inside band 1 must produce the identical string,
        // or HealthReportComparer suppression is defeated.
        var escalateAfter = Ttl * 10;
        var a = HealthLease.Decay(Affirmed, TimeSpan.FromSeconds(120), Ttl, escalateAfter, Escalated);
        var b = HealthLease.Decay(Affirmed, TimeSpan.FromSeconds(170), Ttl, escalateAfter, Escalated);

        Assert.Equal(a.Reason, b.Reason);
        Assert.Same(a.Reason, a.Reason); // string identity within-instance sanity
    }

    [Fact]
    public void Decay_ReasonChangesOnlyAcrossABandBoundary()
    {
        var escalateAfter = Ttl * 10;
        var band1 = HealthLease.Decay(Affirmed, TimeSpan.FromSeconds(170), Ttl, escalateAfter, Escalated);
        var band2 = HealthLease.Decay(Affirmed, TimeSpan.FromSeconds(230), Ttl, escalateAfter, Escalated);

        Assert.NotEqual(band1.Reason, band2.Reason);
        Assert.Equal("lease-expired: no affirmation for over 1 ttl (ttl 90s)", band1.Reason);
        Assert.Equal("lease-expired: no affirmation for over 2 ttl (ttl 90s)", band2.Reason);
    }

    [Fact]
    public void Decay_AtEscalationDeadline_StillUnknown()
    {
        // age == ttl + escalateAfter is inclusive of stage one (age <= sum).
        var result = HealthLease.Decay(Affirmed, Ttl + Ttl, Ttl, Ttl, Escalated);
        Assert.Equal(HealthStatus.Unknown, result.Status);
    }

    [Fact]
    public void Decay_PastEscalationDeadline_ReturnsEscalated()
    {
        var age = Ttl + Ttl + TimeSpan.FromTicks(1);
        var result = HealthLease.Decay(Affirmed, age, Ttl, Ttl, Escalated);
        Assert.Same(Escalated, result);
    }

    [Fact]
    public void Decay_ZeroEscalateAfter_CollapsesUnknownStage()
    {
        // With escalateAfter == 0, stage one (ttl < age <= ttl) is empty, so any
        // age past ttl escalates immediately.
        var age = Ttl + TimeSpan.FromTicks(1);
        var result = HealthLease.Decay(Affirmed, age, Ttl, TimeSpan.Zero, Escalated);
        Assert.Same(Escalated, result);
    }

    [Fact]
    public void Decay_SubSecondTtl_ReasonShowsFractionalSeconds()
    {
        var ttl = TimeSpan.FromMilliseconds(500);
        var result = HealthLease.Decay(
            Affirmed, TimeSpan.FromMilliseconds(700), ttl, ttl * 10, Escalated);

        // Not the misleading "(ttl 0s)" a narrowing (int) cast would produce.
        Assert.Equal("lease-expired: no affirmation for over 1 ttl (ttl 0.5s)", result.Reason);
    }

    [Fact]
    public void Decay_HugeTtl_DoesNotOverflowTheReasonString()
    {
        // A TTL past ~68 years overflows a narrowing (int)ttl.TotalSeconds cast to a
        // negative/garbage number; the non-narrowing format must not.
        var ttl = TimeSpan.FromDays(40000);       // ~109 years
        var age = ttl + TimeSpan.FromDays(1);      // band 1, stage one
        var result = HealthLease.Decay(Affirmed, age, ttl, ttl, Escalated);

        // 40000 days * 86400 s = 3,456,000,000 s — past int.MaxValue (~2.147e9), so a
        // narrowing (int) cast would wrap to a negative number here.
        Assert.Equal(HealthStatus.Unknown, result.Status);
        Assert.Equal(
            "lease-expired: no affirmation for over 1 ttl (ttl 3456000000s)", result.Reason);
    }

    [Fact]
    public void Decay_ZeroTtl_GuardsAgainstDivByZeroAndBandsToOne()
    {
        var result = HealthLease.Decay(
            Affirmed, TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(10), Escalated);

        Assert.Equal(HealthStatus.Unknown, result.Status);
        Assert.Equal("lease-expired: no affirmation for over 1 ttl (ttl 0s)", result.Reason);
    }

    // ── Options validation: throws at Lease(), no silent clamp ─────────

    [Fact]
    public void Lease_EscalatedHealthy_Throws()
    {
        var node = HealthNode.Create("Svc");
        Assert.Throws<ArgumentException>(() =>
            node.Lease(new HealthLeaseOptions(Ttl, Escalated: HealthEvaluation.Healthy)));
    }

    [Fact]
    public void Lease_EscalatedUnknown_Throws()
    {
        var node = HealthNode.Create("Svc");
        Assert.Throws<ArgumentException>(() =>
            node.Lease(new HealthLeaseOptions(Ttl, Escalated: HealthEvaluation.Unknown("?"))));
    }

    [Fact]
    public void Lease_NegativeTtl_Throws()
    {
        var node = HealthNode.Create("Svc");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(-1))));
    }

    [Fact]
    public void Lease_NegativeEscalateAfter_Throws()
    {
        var node = HealthNode.Create("Svc");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            node.Lease(new HealthLeaseOptions(Ttl, EscalateAfter: TimeSpan.FromSeconds(-1))));
    }

    [Fact]
    public void Lease_SumOverflow_Throws()
    {
        var node = HealthNode.Create("Svc");
        var hugeTtl = TimeSpan.FromTicks(TimeSpan.MaxValue.Ticks - 5);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            node.Lease(new HealthLeaseOptions(hugeTtl, EscalateAfter: TimeSpan.FromTicks(10))));
    }

    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public void Lease_EscalatedInClosedSet_DoesNotThrow(HealthStatus status)
    {
        var node = HealthNode.Create("Svc");
        var escalated = new HealthEvaluation(status, "dead");
        var lease = node.Lease(new HealthLeaseOptions(Ttl, Escalated: escalated));
        Assert.NotNull(lease);
    }

    // ── Seeding: no Healthy-default window ─────────────────────────────

    [Fact]
    public void Lease_SeedsPendingUnknown_NotHealthyDefault()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);

        node.Lease(new HealthLeaseOptions(Ttl, Clock: clock.Read));

        var snap = graph.GetReport().Root;
        Assert.Equal(HealthStatus.Unknown, snap.Status);
        Assert.Equal("lease-pending: awaiting first affirmation", snap.Reason);
    }

    // ── Affirm + evaluation-time decay, asserting on accumulated time ──

    [Fact]
    public void Affirm_WithinTtl_ReportsAffirmedVerdict_AfterTimeAdvances()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        var lease = node.Lease(new HealthLeaseOptions(Ttl, Clock: clock.Read));

        lease.Affirm(HealthEvaluation.Degraded("slow but alive"));
        clock.AdvanceSeconds(60); // still < ttl (90s)
        graph.RefreshAll();

        Assert.True(clock.ElapsedSeconds >= 60, "fake clock must have advanced");
        var snap = graph.GetReport().Root;
        Assert.Equal(HealthStatus.Degraded, snap.Status);
        Assert.Equal("slow but alive", snap.Reason);
    }

    [Fact]
    public void Affirm_ThenExceedTtl_DecaysToUnknownStale()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        var lease = node.Lease(new HealthLeaseOptions(Ttl, Clock: clock.Read));

        lease.Affirm(HealthEvaluation.Healthy);
        clock.AdvanceSeconds(135); // 1.5 * ttl -> stage one, band 1
        graph.RefreshAll();

        Assert.True(clock.ElapsedSeconds >= 135);
        var snap = graph.GetReport().Root;
        Assert.Equal(HealthStatus.Unknown, snap.Status);
        Assert.Equal("lease-expired: no affirmation for over 1 ttl (ttl 90s)", snap.Reason);
    }

    [Fact]
    public void Affirm_ThenExceedEscalation_DecaysToEscalated()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        var lease = node.Lease(new HealthLeaseOptions(Ttl, Clock: clock.Read));

        lease.Affirm(HealthEvaluation.Healthy);
        clock.AdvanceSeconds(200); // > 2 * ttl (default escalateAfter == ttl)
        graph.RefreshAll();

        Assert.True(clock.ElapsedSeconds >= 200);
        var snap = graph.GetReport().Root;
        Assert.Equal(HealthStatus.Degraded, snap.Status);
        Assert.Equal(
            "lease-expired: escalated after ttl+escalateAfter with no affirmation",
            snap.Reason);
    }

    [Fact]
    public void ReAffirm_ResetsTheClock_EvenAfterEscalation()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        var lease = node.Lease(new HealthLeaseOptions(Ttl, Clock: clock.Read));

        lease.Affirm(HealthEvaluation.Healthy);
        clock.AdvanceSeconds(300); // well past escalation
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Root.Status);

        // Producer comes back to life: re-affirm resets AffirmedAt to now.
        lease.Affirm(HealthEvaluation.Healthy);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);

        // And it stays healthy for another ttl window despite the large elapsed time.
        clock.AdvanceSeconds(60);
        graph.RefreshAll();
        Assert.True(clock.ElapsedSeconds >= 360, "accumulated fake time must be large");
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);
    }

    [Fact]
    public void NeverAffirmed_EscalatesOnSchedule()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        node.Lease(new HealthLeaseOptions(Ttl, Clock: clock.Read));

        // Stage one first.
        clock.AdvanceSeconds(135);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);
        Assert.StartsWith(HealthLease.StaleReasonPrefix, graph.GetReport().Root.Reason);

        // Then escalation, without any Affirm ever.
        clock.AdvanceSeconds(100); // total 235 > 2 * ttl
        graph.RefreshAll();
        Assert.True(clock.ElapsedSeconds >= 235);
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Root.Status);
    }

    [Fact]
    public void CustomEscalated_Unhealthy_IsPrefixedAndReported()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        node.Lease(new HealthLeaseOptions(
            Ttl, Clock: clock.Read, Escalated: HealthEvaluation.Unhealthy("producer dead")));

        clock.AdvanceSeconds(200);
        graph.RefreshAll();

        var snap = graph.GetReport().Root;
        Assert.Equal(HealthStatus.Unhealthy, snap.Status);
        Assert.Equal("lease-expired: producer dead", snap.Reason);
    }

    // ── Detachment: a detached Affirm must throw ───────────────────────

    [Fact]
    public void Affirm_AfterReplaceHealthProbe_Throws()
    {
        var node = HealthNode.Create("Svc");
        var lease = node.Lease(new HealthLeaseOptions(Ttl));

        node.ReplaceHealthProbe(() => HealthEvaluation.Healthy);

        Assert.Throws<InvalidOperationException>(() => lease.Affirm(HealthEvaluation.Healthy));
    }

    [Fact]
    public void Affirm_AfterWithHealthProbe_Throws()
    {
        var node = HealthNode.Create("Svc");
        var lease = node.Lease(new HealthLeaseOptions(Ttl));

        node.WithHealthProbe(() => HealthEvaluation.Healthy);

        Assert.Throws<InvalidOperationException>(() => lease.Affirm(HealthEvaluation.Healthy));
    }

    [Fact]
    public void Affirm_OnSupersededLease_Throws_ButNewLeaseWorks()
    {
        var node = HealthNode.Create("Svc");
        var first = node.Lease(new HealthLeaseOptions(Ttl));
        var second = node.Lease(new HealthLeaseOptions(Ttl));

        Assert.Throws<InvalidOperationException>(() => first.Affirm(HealthEvaluation.Healthy));
        second.Affirm(HealthEvaluation.Healthy); // does not throw
    }

    [Fact]
    public void Affirm_Null_Throws()
    {
        var node = HealthNode.Create("Svc");
        var lease = node.Lease(new HealthLeaseOptions(Ttl));
        Assert.Throws<ArgumentNullException>(() => lease.Affirm(null!));
    }

    // ── ReportStatus keeps one-shot semantics on a leased node ─────────

    [Fact]
    public void ReportStatus_OnLeasedNode_IsOneShot_ThenLeaseResumes()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        var lease = node.Lease(new HealthLeaseOptions(Ttl, Clock: clock.Read));
        lease.Affirm(HealthEvaluation.Healthy);

        node.ReportStatus(HealthEvaluation.Degraded("transient blip"));
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Root.Status);

        // Next wave resumes the leased verdict (still within ttl).
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);
    }

    // ── Report-churn regression (ADR-010 §2 + ADR-012) ─────────────────

    [Fact]
    public void LeasedNode_HeldInOneBand_DoesNotChurnReportStream()
    {
        var clock = new FakeClock();
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);
        // Wide escalateAfter so several waves stay in the Unknown stage.
        var lease = node.Lease(new HealthLeaseOptions(Ttl, EscalateAfter: Ttl * 10, Clock: clock.Read));
        lease.Affirm(HealthEvaluation.Healthy);

        var emissions = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new CountingObserver(emissions.Add));

        // Cross into band 1 (Healthy -> Unknown): one legitimate emission.
        clock.AdvanceSeconds(135);
        var band1a = graph.RefreshAll();
        var emissionsAfterCrossing = emissions.Count;

        // More waves inside band 1: byte-identical reports, zero churn.
        clock.AdvanceSeconds(15);
        var band1b = graph.RefreshAll();
        clock.AdvanceSeconds(15);
        var band1c = graph.RefreshAll();

        Assert.True(HealthReportComparer.Instance.Equals(band1a, band1b));
        Assert.True(HealthReportComparer.Instance.Equals(band1b, band1c));
        Assert.Equal(emissionsAfterCrossing, emissions.Count); // no new emissions in-band

        // Cross into band 2: exactly one new emission.
        clock.AdvanceSeconds(90); // now ~ 2.5 * ttl
        var band2 = graph.RefreshAll();
        Assert.False(HealthReportComparer.Instance.Equals(band1c, band2));
        Assert.Equal(emissionsAfterCrossing + 1, emissions.Count);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>Stepable monotonic clock in Stopwatch-tick units.</summary>
    private sealed class FakeClock
    {
        private long _ticks;

        public long Read() => Volatile.Read(ref _ticks);

        public void AdvanceSeconds(double seconds)
            => _ticks += (long)(seconds * Stopwatch.Frequency);

        public double ElapsedSeconds => _ticks / (double)Stopwatch.Frequency;
    }

    private sealed class CountingObserver(Action<HealthReport> onNext) : IObserver<HealthReport>
    {
        public void OnNext(HealthReport value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
