using Prognosis;

namespace Prognosis.Tests;

public class ResilientImportanceTests
{
    [Fact]
    public void OneUnhealthy_OneHealthy_Resilient_ReturnsDegraded()
    {
        var healthy = new DelegateHealthNode("A");
        var unhealthy = new DelegateHealthNode("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var parent = new CompositeHealthNode("Root")
            .DependsOn(healthy, Importance.Resilient)
            .DependsOn(unhealthy, Importance.Resilient);

        Assert.Equal(HealthStatus.Degraded, parent.Evaluate().Status);
    }

    [Fact]
    public void AllUnhealthy_Resilient_ReturnsUnhealthy()
    {
        var a = new DelegateHealthNode("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var b = new DelegateHealthNode("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var parent = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Resilient)
            .DependsOn(b, Importance.Resilient);

        Assert.Equal(HealthStatus.Unhealthy, parent.Evaluate().Status);
    }

    [Fact]
    public void AllHealthy_ReturnsHealthy()
    {
        var a = new DelegateHealthNode("A");
        var b = new DelegateHealthNode("B");

        var parent = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Resilient)
            .DependsOn(b, Importance.Resilient);

        Assert.Equal(HealthStatus.Healthy, parent.Evaluate().Status);
    }

    [Fact]
    public void SingleUnhealthy_Resilient_NoSiblings_ReturnsUnhealthy()
    {
        var unhealthy = new DelegateHealthNode("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var parent = new CompositeHealthNode("Root")
            .DependsOn(unhealthy, Importance.Resilient);

        Assert.Equal(HealthStatus.Unhealthy, parent.Evaluate().Status);
    }

    [Fact]
    public void Resilient_DoesNotCountNonResilientSiblings()
    {
        var unhealthy = new DelegateHealthNode("A",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var healthyRequired = new DelegateHealthNode("B");

        // B is healthy but Required, not Resilient — should not cap A's unhealthy.
        var parent = new CompositeHealthNode("Root")
            .DependsOn(unhealthy, Importance.Resilient)
            .DependsOn(healthyRequired, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, parent.Evaluate().Status);
    }

    [Fact]
    public void Resilient_MixedWithOtherImportanceLevels()
    {
        var primaryDb = new DelegateHealthNode("PrimaryDb");
        var replicaDb = new DelegateHealthNode("ReplicaDb",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var cache = new DelegateHealthNode("Cache",
            () => HealthStatus.Unhealthy);

        var parent = new CompositeHealthNode("Root")
            .DependsOn(primaryDb, Importance.Resilient)
            .DependsOn(replicaDb, Importance.Resilient)
            .DependsOn(cache, Importance.Important);

        // ReplicaDb unhealthy but PrimaryDb healthy → capped at Degraded.
        // Cache unhealthy + Important → also capped at Degraded.
        // Worst = Degraded.
        Assert.Equal(HealthStatus.Degraded, parent.Evaluate().Status);
    }

    [Fact]
    public void IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = new DelegateHealthNode("A");

        var parent = new DelegateHealthNode("Root",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "self broken"))
            .DependsOn(healthy, Importance.Resilient);

        Assert.Equal(HealthStatus.Unhealthy, parent.Evaluate().Status);
    }
}
