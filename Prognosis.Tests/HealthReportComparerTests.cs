namespace Prognosis.Tests;

public class HealthReportComparerTests
{
    private static readonly HealthReportComparer Comparer = HealthReportComparer.Instance;

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        var report = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<ServiceSnapshot>());

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
        var report = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<ServiceSnapshot>());

        Assert.False(Comparer.Equals(report, null));
        Assert.False(Comparer.Equals(null, report));
    }

    [Fact]
    public void Equals_DifferentOverallStatus_ReturnsFalse()
    {
        var a = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<ServiceSnapshot>());
        var b = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Unhealthy,
            Array.Empty<ServiceSnapshot>());

        Assert.False(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_DifferentServiceCount_ReturnsFalse()
    {
        var a = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<ServiceSnapshot>());
        var b = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new ServiceSnapshot("Svc", HealthStatus.Healthy, 0),
        });

        Assert.False(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_SameServices_ReturnsTrue()
    {
        var services = new[] { new ServiceSnapshot("Svc", HealthStatus.Healthy, 0) };
        var a = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, services);
        var b = new HealthReport(DateTimeOffset.UtcNow.AddHours(1), HealthStatus.Healthy, services);

        Assert.True(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_DifferentTimestamps_SameData_ReturnsTrue()
    {
        var snapshot = new ServiceSnapshot("Svc", HealthStatus.Degraded, 2, "slow");
        var a = new HealthReport(DateTimeOffset.MinValue, HealthStatus.Degraded, new[] { snapshot });
        var b = new HealthReport(DateTimeOffset.MaxValue, HealthStatus.Degraded, new[] { snapshot });

        Assert.True(Comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_DifferentServiceStatus_ReturnsFalse()
    {
        var a = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new ServiceSnapshot("Svc", HealthStatus.Healthy, 0),
        });
        var b = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new ServiceSnapshot("Svc", HealthStatus.Degraded, 0),
        });

        Assert.False(Comparer.Equals(a, b));
    }
}
