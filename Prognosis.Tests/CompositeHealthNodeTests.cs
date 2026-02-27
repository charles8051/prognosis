namespace Prognosis.Tests;

public class CompositeHealthNodeTests
{
    [Fact]
    public void Evaluate_AllHealthy_ReturnsHealthy()
    {
        var dep = HealthNode.CreateDelegate("Dep");
        var composite = HealthNode.CreateComposite("Comp")
            .DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Healthy, composite.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_UnhealthyRequired_ReturnsUnhealthy()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("down"));
        var composite = HealthNode.CreateComposite("Comp")
            .DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, composite.Evaluate().Status);
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

        // Important+Unhealthy → Degraded. Optional ignored. Intrinsic = Unknown.
        // Degraded > Unknown, so Degraded wins.
        Assert.Equal(HealthStatus.Degraded, composite.Evaluate().Status);
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

    [Fact]
    public void BubbleChange_EmitsOnStatusChange()
    {
        var isUnhealthy = true;
        var dep = HealthNode.CreateDelegate("Dep",
            () => isUnhealthy
                ? HealthEvaluation.Unhealthy("down")
                : HealthStatus.Healthy);
        var composite = HealthNode.CreateComposite("Comp")
            .DependsOn(dep, Importance.Required);
        // DependsOn propagates immediately, so _lastEmitted
        // is already Unhealthy. Subscribe and verify a status change emits.
        var emitted = new List<HealthStatus>();
        composite.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        isUnhealthy = false;
        composite.BubbleChange();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Healthy, emitted[0]);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
