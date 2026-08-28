using Prognosis;

namespace Prognosis.Tests;

public class ResilientImportanceTests
{
    [Fact]
    public void OneUnhealthy_OneHealthy_Resilient_ReturnsDegraded()
    {
        var healthy = HealthNode.Create("A");
        var unhealthy = HealthNode.Create("B").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.Create("Root")
            .DependsOn(healthy, Importance.Resilient)
            .DependsOn(unhealthy, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void AllUnhealthy_Resilient_ReturnsUnhealthy()
    {
        var a = HealthNode.Create("A").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var b = HealthNode.Create("B").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.Create("Root")
            .DependsOn(a, Importance.Resilient)
            .DependsOn(b, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void AllHealthy_ReturnsHealthy()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");

        var parent = HealthNode.Create("Root")
            .DependsOn(a, Importance.Resilient)
            .DependsOn(b, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void SingleUnhealthy_Resilient_NoSiblings_ReturnsUnhealthy()
    {
        var unhealthy = HealthNode.Create("A").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.Create("Root")
            .DependsOn(unhealthy, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void Resilient_DoesNotCountNonResilientSiblings()
    {
        var unhealthy = HealthNode.Create("A").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var healthyRequired = HealthNode.Create("B");

        // B is healthy but Required, not Resilient — should not cap A's unhealthy.
        var parent = HealthNode.Create("Root")
            .DependsOn(unhealthy, Importance.Resilient)
            .DependsOn(healthyRequired, Importance.Required);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void Resilient_MixedWithOtherImportanceLevels()
    {
        var primaryDb = HealthNode.Create("PrimaryDb");
        var replicaDb = HealthNode.Create("ReplicaDb").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var cache = HealthNode.Create("Cache").WithHealthProbe(
            () => HealthStatus.Unhealthy);

        var parent = HealthNode.Create("Root")
            .DependsOn(primaryDb, Importance.Resilient)
            .DependsOn(replicaDb, Importance.Resilient)
            .DependsOn(cache, Importance.Important);
        var graph = HealthGraph.Create(parent);

        // ReplicaDb unhealthy but PrimaryDb healthy → capped at Degraded.
        // Cache unhealthy + Important → also capped at Degraded.
        // Worst = Degraded.
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = HealthNode.Create("A");

        var parent = HealthNode.Create("Root").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("self broken"))
            .DependsOn(healthy, Importance.Resilient);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Nodes.First(n => n.Name == "Root").Status);
    }
}
