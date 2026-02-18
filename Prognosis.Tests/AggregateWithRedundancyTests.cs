using Prognosis;

namespace Prognosis.Tests;

public class AggregateWithRedundancyTests
{
    [Fact]
    public void OneUnhealthy_OneHealthy_Required_ReturnsDegraded()
    {
        var healthy = new HealthCheck("A");
        var unhealthy = new HealthCheck("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new HealthDependency(healthy, Importance.Required),
            new HealthDependency(unhealthy, Importance.Required),
        });

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void AllUnhealthy_Required_ReturnsUnhealthy()
    {
        var a = new HealthCheck("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var b = new HealthCheck("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new HealthDependency(a, Importance.Required),
            new HealthDependency(b, Importance.Required),
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void AllHealthy_ReturnsHealthy()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B");

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new HealthDependency(a, Importance.Required),
            new HealthDependency(b, Importance.Required),
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void SingleUnhealthy_Required_NoSiblings_ReturnsUnhealthy()
    {
        var unhealthy = new HealthCheck("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new HealthDependency(unhealthy, Importance.Required),
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void Important_Unhealthy_StillCappedAtDegraded()
    {
        var unhealthy = new HealthCheck("A",
            () => HealthStatus.Unhealthy);

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new HealthDependency(unhealthy, Importance.Important),
        });

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void Optional_Unhealthy_Ignored()
    {
        var unhealthy = new HealthCheck("A",
            () => HealthStatus.Unhealthy);

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new HealthDependency(unhealthy, Importance.Optional),
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void HealthGroup_UsesInjectedStrategy()
    {
        var healthy = new HealthCheck("Primary");
        var unhealthy = new HealthCheck("Secondary",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var composite = new HealthGroup("Root", new[]
        {
            new HealthDependency(healthy, Importance.Required),
            new HealthDependency(unhealthy, Importance.Required),
        }, HealthAggregator.AggregateWithRedundancy);

        Assert.Equal(HealthStatus.Degraded, composite.Evaluate().Status);
    }

    [Fact]
    public void IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = new HealthCheck("A");

        var result = HealthAggregator.AggregateWithRedundancy(
            new HealthEvaluation(HealthStatus.Unhealthy, "self broken"), new[]
            {
                new HealthDependency(healthy, Importance.Required),
            });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void OnlyOptionalHealthy_UnhealthyRequired_NotCapped()
    {
        var unhealthy = new HealthCheck("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var optionalHealthy = new HealthCheck("B");

        // The optional dep is healthy, but only non-optional healthy siblings count.
        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new HealthDependency(unhealthy, Importance.Required),
            new HealthDependency(optionalHealthy, Importance.Optional),
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
