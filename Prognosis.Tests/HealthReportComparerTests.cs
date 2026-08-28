namespace Prognosis.Tests;

public class HealthReportComparerTests
{
    private static readonly HealthReportComparer Comparer = HealthReportComparer.Instance;
    private static readonly HealthSnapshot DummyRoot = new("Root", HealthStatus.Healthy);

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        var report = new HealthReport(DummyRoot, Array.Empty<HealthSnapshot>());
        Assert.True(Comparer.Equals(report, report));
    }

    [Fact]
    public void Equals_NullBoth_ReturnsTrue()
    {
        Assert.True(Comparer.Equals(null, null));
    }

    [Fact]
    public void Equals_OneNull_ReturnsFalse()
    {
        var report = new HealthReport(DummyRoot, Array.Empty<HealthSnapshot>());
        Assert.False(Comparer.Equals(null, report));
    }

    [Fact]
    public void Equals_DifferentNodeCount_ReturnsFalse()
    {
        var a = new HealthReport(DummyRoot, Array.Empty<HealthSnapshot>());
        var b = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });

        Assert.False(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_SameNodes_ReturnsTrue()
    {
        var nodes = new[] { new HealthSnapshot("Svc", HealthStatus.Healthy) };
        var a = new HealthReport(DummyRoot, nodes);
        var b = new HealthReport(DummyRoot, nodes);

        Assert.True(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_SameData_DifferentInstances_ReturnsTrue()
    {
        var snapshot = new HealthSnapshot("Svc", HealthStatus.Degraded, "slow");
        var a = new HealthReport(DummyRoot, new[] { snapshot });
        var b = new HealthReport(DummyRoot, new[] { snapshot });

        Assert.True(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_DifferentNodeStatus_ReturnsFalse()
    {
        var a = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });
        var b = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded),
        });

        Assert.False(Comparer.Equals(a, b));
    }

    // ── ADR-012: report-equality key is (Name, Status, Reason); Tags excluded ──

    [Fact]
    public void Equals_ReasonOnlyChange_ReturnsFalse()
    {
        // Reason participates in the report-equality key (ADR-012 §1): a
        // same-status reason move is a report change.
        var a = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "queue depth 100"),
        });
        var b = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "backend refused"),
        });

        Assert.False(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_SameNameStatusReason_DifferentTags_ReturnsTrue()
    {
        // Tags are node identity, not a health signal, and are excluded from the
        // key (ADR-012 §3). Two snapshots differing only by a distinct Tags
        // reference now compare equal.
        var tagsA = new Dictionary<string, string> { ["env"] = "prod" };
        var tagsB = new Dictionary<string, string> { ["env"] = "staging" };
        var a = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "slow", tagsA),
        });
        var b = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "slow", tagsB),
        });

        Assert.True(Comparer.Equals(a, b));
    }

    [Fact]
    public void GetHashCode_EqualReports_ProduceEqualHashes()
    {
        var a = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "slow"),
        });
        var b = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "slow"),
        });

        Assert.Equal(Comparer.GetHashCode(a), Comparer.GetHashCode(b));
    }

    [Fact]
    public void GetHashCode_ReflectsReason_MatchingEquals()
    {
        // The hash now keys on Reason (ADR-012 §2): reports that Equals treats as
        // unequal because their reasons differ should (in practice) hash apart,
        // proving the hash is no longer Reason-blind.
        var a = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "queue depth 100"),
        });
        var b = new HealthReport(DummyRoot, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded, "backend refused"),
        });

        Assert.False(Comparer.Equals(a, b));
        Assert.NotEqual(Comparer.GetHashCode(a), Comparer.GetHashCode(b));
    }
}
