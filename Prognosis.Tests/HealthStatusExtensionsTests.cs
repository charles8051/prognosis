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

    /// <summary>
    /// Every declared <see cref="HealthStatus"/> must be ranked explicitly (ADR-008 §4).
    /// A reflection sweep, not a fixed list, so that <b>adding a member to the enum fails
    /// this test until <c>Rank</c> is updated</b>.
    /// </summary>
    [Fact]
    public void Rank_HandlesEveryDeclaredStatus()
    {
        foreach (var status in Enum.GetValues<HealthStatus>())
        {
            var ex = Record.Exception(() => HealthStatusExtensions.Worst(status, HealthStatus.Healthy));
            Assert.True(
                ex is null,
                $"Rank is missing an arm for {status}. See ADR-008.");
        }
    }

    /// <summary>
    /// An undeclared status must throw rather than rank as <c>int.MaxValue</c>. That old
    /// fallback ranked an unrecognised member <em>worse than Unhealthy</em>, so a new status
    /// would have silently become the worst everywhere it was folded — inside the very
    /// function that enforces ADR-006's ordering guarantee.
    /// </summary>
    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public void Rank_ThrowsOnUndeclaredStatus_RatherThanRankingItWorst(int raw)
    {
        var undeclared = (HealthStatus)raw;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => HealthStatusExtensions.Worst(undeclared, HealthStatus.Healthy));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => undeclared.IsWorseThan(HealthStatus.Healthy));
    }
}
