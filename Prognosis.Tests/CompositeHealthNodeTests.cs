namespace Prognosis.Tests;

public class CompositeHealthNodeTests
{
    [Fact]
    public void Evaluate_AllHealthy_ReturnsHealthy()
    {
        var dep = new DelegateHealthNode("Dep");
        var composite = new CompositeHealthNode("Comp")
            .DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Healthy, composite.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_UnhealthyRequired_ReturnsUnhealthy()
    {
        var dep = new DelegateHealthNode("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var composite = new CompositeHealthNode("Comp")
            .DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, composite.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_MixedImportance_PropagatesCorrectly()
    {
        var required = new DelegateHealthNode("Req");
        var important = new DelegateHealthNode("Imp",
            () => HealthStatus.Unhealthy);
        var optional = new DelegateHealthNode("Opt",
            () => HealthStatus.Unhealthy);

        var composite = new CompositeHealthNode("Comp")
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
        var composite = new CompositeHealthNode("MyComposite");

        Assert.Equal("MyComposite", composite.Name);
    }

    [Fact]
    public void Dependencies_ReflectsDependsOnCalls()
    {
        var a = new DelegateHealthNode("A");
        var b = new DelegateHealthNode("B");
        var composite = new CompositeHealthNode("Comp")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Optional);

        Assert.Equal(2, composite.Dependencies.Count);
    }

    [Fact]
    public void BubbleChange_EmitsOnStatusChange()
    {
        var isUnhealthy = true;
        var dep = new DelegateHealthNode("Dep",
            () => isUnhealthy
                ? new HealthEvaluation(HealthStatus.Unhealthy, "down")
                : HealthStatus.Healthy);
        var composite = new CompositeHealthNode("Comp")
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
