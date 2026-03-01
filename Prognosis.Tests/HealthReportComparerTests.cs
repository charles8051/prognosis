namespace Prognosis.Tests;

public class HealthReportComparerTests
{
    private static readonly HealthReportComparer Comparer = HealthReportComparer.Instance;
    private static readonly HealthSnapshot DummyRoot = new("Root", HealthStatus.Healthy);

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        var report = new HealthReport(DummyRoot, Array.Empty<HealthSnapshot>());
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
}
