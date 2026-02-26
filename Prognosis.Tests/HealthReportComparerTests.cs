namespace Prognosis.Tests;

public class HealthReportComparerTests
{
    private static readonly HealthReportComparer Comparer = HealthReportComparer.Instance;

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        var report = new HealthReport(Array.Empty<HealthSnapshot>());

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
        var report = new HealthReport(Array.Empty<HealthSnapshot>());

        Assert.False(Comparer.Equals(report, null));
        Assert.False(Comparer.Equals(null, report));
    }

    [Fact]
    public void Equals_DifferentOverallStatus_ReturnsFalse()
    {
        var a = new HealthReport(new[] { new HealthSnapshot("Svc", HealthStatus.Healthy) });
        var b = new HealthReport(new[] { new HealthSnapshot("Svc", HealthStatus.Unhealthy) });

        Assert.False(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_DifferentNodeCount_ReturnsFalse()
    {
        var a = new HealthReport(Array.Empty<HealthSnapshot>());
        var b = new HealthReport(new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });

        Assert.False(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_SameNodes_ReturnsTrue()
    {
        var nodes = new[] { new HealthSnapshot("Svc", HealthStatus.Healthy) };
        var a = new HealthReport(nodes);
        var b = new HealthReport(nodes);

        Assert.True(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_SameData_DifferentInstances_ReturnsTrue()
    {
        var snapshot = new HealthSnapshot("Svc", HealthStatus.Degraded, "slow");
        var a = new HealthReport(new[] { snapshot });
        var b = new HealthReport(new[] { snapshot });

        Assert.True(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_DifferentNodeStatus_ReturnsFalse()
    {
        var a = new HealthReport(new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });
        var b = new HealthReport(new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Degraded),
        });

        Assert.False(Comparer.Equals(a, b));
    }
}
