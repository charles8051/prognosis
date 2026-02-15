using Prognosis;

namespace Prognosis.Tests;

public class AggregateWithRedundancyTests
{
    [Fact]
    public void OneUnhealthy_OneHealthy_Required_ReturnsDegraded()
    {
        var healthy = new DelegatingServiceHealth("A");
        var unhealthy = new DelegatingServiceHealth("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(healthy, ServiceImportance.Required),
            new ServiceDependency(unhealthy, ServiceImportance.Required),
        });

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void AllUnhealthy_Required_ReturnsUnhealthy()
    {
        var a = new DelegatingServiceHealth("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var b = new DelegatingServiceHealth("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(a, ServiceImportance.Required),
            new ServiceDependency(b, ServiceImportance.Required),
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void AllHealthy_ReturnsHealthy()
    {
        var a = new DelegatingServiceHealth("A");
        var b = new DelegatingServiceHealth("B");

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(a, ServiceImportance.Required),
            new ServiceDependency(b, ServiceImportance.Required),
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void SingleUnhealthy_Required_NoSiblings_ReturnsUnhealthy()
    {
        var unhealthy = new DelegatingServiceHealth("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(unhealthy, ServiceImportance.Required),
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void Important_Unhealthy_StillCappedAtDegraded()
    {
        var unhealthy = new DelegatingServiceHealth("A",
            () => HealthStatus.Unhealthy);

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(unhealthy, ServiceImportance.Important),
        });

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void Optional_Unhealthy_Ignored()
    {
        var unhealthy = new DelegatingServiceHealth("A",
            () => HealthStatus.Unhealthy);

        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(unhealthy, ServiceImportance.Optional),
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void CompositeServiceHealth_UsesInjectedStrategy()
    {
        var healthy = new DelegatingServiceHealth("Primary");
        var unhealthy = new DelegatingServiceHealth("Secondary",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var composite = new CompositeServiceHealth("Root", new[]
        {
            new ServiceDependency(healthy, ServiceImportance.Required),
            new ServiceDependency(unhealthy, ServiceImportance.Required),
        }, HealthAggregator.AggregateWithRedundancy);

        Assert.Equal(HealthStatus.Degraded, composite.Evaluate().Status);
    }

    [Fact]
    public void IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = new DelegatingServiceHealth("A");

        var result = HealthAggregator.AggregateWithRedundancy(
            new HealthEvaluation(HealthStatus.Unhealthy, "self broken"), new[]
            {
                new ServiceDependency(healthy, ServiceImportance.Required),
            });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void OnlyOptionalHealthy_UnhealthyRequired_NotCapped()
    {
        var unhealthy = new DelegatingServiceHealth("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var optionalHealthy = new DelegatingServiceHealth("B");

        // The optional dep is healthy, but only non-optional healthy siblings count.
        var result = HealthAggregator.AggregateWithRedundancy(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(unhealthy, ServiceImportance.Required),
            new ServiceDependency(optionalHealthy, ServiceImportance.Optional),
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
