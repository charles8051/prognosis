namespace Prognosis.Tests;

public class CompositeHealthNodeTests
{
    [Fact]
    public void Evaluate_AllHealthy_ReturnsHealthy()
    {
        var dep = HealthNode.Create("Dep");
        var composite = HealthNode.Create("Comp")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(composite);

        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Nodes.First(n => n.Name == "Comp").Status);
    }

    [Fact]
    public void Evaluate_UnhealthyRequired_ReturnsUnhealthy()
    {
        var dep = HealthNode.Create("Dep").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var composite = HealthNode.Create("Comp")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(composite);

        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Nodes.First(n => n.Name == "Comp").Status);
    }

    [Fact]
    public void Evaluate_MixedImportance_PropagatesCorrectly()
    {
        var required = HealthNode.Create("Req");
        var important = HealthNode.Create("Imp").WithHealthProbe(
            () => HealthStatus.Unhealthy);
        var optional = HealthNode.Create("Opt").WithHealthProbe(
            () => HealthStatus.Unhealthy);

        var composite = HealthNode.Create("Comp")
            .DependsOn(required, Importance.Required)
            .DependsOn(important, Importance.Important)
            .DependsOn(optional, Importance.Optional);
        var graph = HealthGraph.Create(composite);

        // Important+Unhealthy → Degraded. Optional ignored. Intrinsic = Healthy.
        // Degraded > Healthy, so Degraded wins.
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Nodes.First(n => n.Name == "Comp").Status);
    }

    [Fact]
    public void Name_ReturnsConstructorValue()
    {
        var composite = HealthNode.Create("MyComposite");

        Assert.Equal("MyComposite", composite.Name);
    }

    [Fact]
    public void Dependencies_ReflectsDependsOnCalls()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");
        var composite = HealthNode.Create("Comp")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Optional);

        Assert.Equal(2, composite.Dependencies.Count);
    }
}
