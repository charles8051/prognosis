using Prognosis;

namespace Prognosis.Tests;

public class ResilientImportanceTests
{
    [Fact]
    public void OneUnhealthy_OneHealthy_Resilient_ReturnsDegraded()
    {
        var healthy = HealthNode.CreateDelegate("A");
        var unhealthy = HealthNode.CreateDelegate("B",
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.CreateComposite("Root")
            .DependsOn(healthy, Importance.Resilient)
            .DependsOn(unhealthy, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Degraded, graph.Evaluate("Root").Status);
    }

    [Fact]
    public void AllUnhealthy_Resilient_ReturnsUnhealthy()
    {
        var a = HealthNode.CreateDelegate("A",
            () => HealthEvaluation.Unhealthy("down"));
        var b = HealthNode.CreateDelegate("B",
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.CreateComposite("Root")
            .DependsOn(a, Importance.Resilient)
            .DependsOn(b, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.Evaluate("Root").Status);
    }

    [Fact]
    public void AllHealthy_ReturnsHealthy()
    {
        var a = HealthNode.CreateDelegate("A");
        var b = HealthNode.CreateDelegate("B");

        var parent = HealthNode.CreateComposite("Root")
            .DependsOn(a, Importance.Resilient)
            .DependsOn(b, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Healthy, graph.Evaluate("Root").Status);
    }

    [Fact]
    public void SingleUnhealthy_Resilient_NoSiblings_ReturnsUnhealthy()
    {
        var unhealthy = HealthNode.CreateDelegate("A",
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.CreateComposite("Root")
            .DependsOn(unhealthy, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.Evaluate("Root").Status);
    }

    [Fact]
    public void Resilient_DoesNotCountNonResilientSiblings()
    {
        var unhealthy = HealthNode.CreateDelegate("A",
            () => HealthEvaluation.Unhealthy("down"));
        var healthyRequired = HealthNode.CreateDelegate("B");

        // B is healthy but Required, not Resilient — should not cap A's unhealthy.
        var parent = HealthNode.CreateComposite("Root")
            .DependsOn(unhealthy, Importance.Resilient)
            .DependsOn(healthyRequired, Importance.Required);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.Evaluate("Root").Status);
    }

    [Fact]
    public void Resilient_MixedWithOtherImportanceLevels()
    {
        var primaryDb = HealthNode.CreateDelegate("PrimaryDb");
        var replicaDb = HealthNode.CreateDelegate("ReplicaDb",
            () => HealthEvaluation.Unhealthy("down"));
        var cache = HealthNode.CreateDelegate("Cache",
            () => HealthStatus.Unhealthy);

        var parent = HealthNode.CreateComposite("Root")
            .DependsOn(primaryDb, Importance.Resilient)
            .DependsOn(replicaDb, Importance.Resilient)
            .DependsOn(cache, Importance.Important);
        var graph = HealthGraph.Create(parent);

        // ReplicaDb unhealthy but PrimaryDb healthy → capped at Degraded.
        // Cache unhealthy + Important → also capped at Degraded.
        // Worst = Degraded.
        Assert.Equal(HealthStatus.Degraded, graph.Evaluate("Root").Status);
    }

    [Fact]
    public void IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = HealthNode.CreateDelegate("A");

        var parent = HealthNode.CreateDelegate("Root",
            () => HealthEvaluation.Unhealthy("self broken"))
            .DependsOn(healthy, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.Evaluate("Root").Status);
    }
}
