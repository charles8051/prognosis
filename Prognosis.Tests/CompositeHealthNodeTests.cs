namespace Prognosis.Tests;

public class CompositeHealthNodeTests
{
    [Fact]
    public void Evaluate_AllHealthy_ReturnsHealthy()
    {
        var dep = HealthNode.CreateDelegate("Dep");
        var composite = HealthNode.CreateComposite("Comp")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(composite);

        Assert.Equal(HealthStatus.Healthy, graph.Evaluate("Comp").Status);
    }

    [Fact]
    public void Evaluate_UnhealthyRequired_ReturnsUnhealthy()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("down"));
        var composite = HealthNode.CreateComposite("Comp")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(composite);

        Assert.Equal(HealthStatus.Unhealthy, graph.Evaluate("Comp").Status);
    }

    [Fact]
    public void Evaluate_MixedImportance_PropagatesCorrectly()
    {
        var required = HealthNode.CreateDelegate("Req");
        var important = HealthNode.CreateDelegate("Imp",
            () => HealthStatus.Unhealthy);
        var optional = HealthNode.CreateDelegate("Opt",
            () => HealthStatus.Unhealthy);

        var composite = HealthNode.CreateComposite("Comp")
            .DependsOn(required, Importance.Required)
            .DependsOn(important, Importance.Important)
            .DependsOn(optional, Importance.Optional);
        var graph = HealthGraph.Create(composite);

        // Important+Unhealthy → Degraded. Optional ignored. Intrinsic = Healthy.
        // Degraded > Healthy, so Degraded wins.
        Assert.Equal(HealthStatus.Degraded, graph.Evaluate("Comp").Status);
    }

    [Fact]
    public void Name_ReturnsConstructorValue()
    {
        var composite = HealthNode.CreateComposite("MyComposite");

        Assert.Equal("MyComposite", composite.Name);
    }

    [Fact]
    public void Dependencies_ReflectsDependsOnCalls()
    {
        var a = HealthNode.CreateDelegate("A");
        var b = HealthNode.CreateDelegate("B");
        var composite = HealthNode.CreateComposite("Comp")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Optional);

        Assert.Equal(2, composite.Dependencies.Count);
    }
}
