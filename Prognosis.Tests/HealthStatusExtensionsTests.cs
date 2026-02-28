namespace Prognosis.Tests;

public class HealthStatusExtensionsTests
{
    [Theory]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Degraded, true)]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Unknown, true)]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Healthy, true)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Unknown, true)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Healthy, true)]
    [InlineData(HealthStatus.Unknown, HealthStatus.Healthy, true)]
    [InlineData(HealthStatus.Healthy, HealthStatus.Healthy, false)]
    [InlineData(HealthStatus.Healthy, HealthStatus.Unknown, false)]
    [InlineData(HealthStatus.Healthy, HealthStatus.Degraded, false)]
    [InlineData(HealthStatus.Healthy, HealthStatus.Unhealthy, false)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Degraded, false)]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Unhealthy, false)]
    public void IsWorseThan_ReturnsExpected(
        HealthStatus status, HealthStatus other, bool expected)
    {
        Assert.Equal(expected, status.IsWorseThan(other));
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HealthStatus.Healthy, HealthStatus.Healthy)]
    [InlineData(HealthStatus.Healthy, HealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Healthy, HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Unknown, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unknown, HealthStatus.Degraded, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Degraded, HealthStatus.Unhealthy)]
    public void Worst_ReturnsWorstOfTwo(
        HealthStatus a, HealthStatus b, HealthStatus expected)
    {
        Assert.Equal(expected, HealthStatusExtensions.Worst(a, b));
    }
}
