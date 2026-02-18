namespace Prognosis.Tests;

public class HealthGroupTests
{
    [Fact]
    public void Evaluate_AllHealthy_ReturnsHealthy()
    {
        var dep = new HealthCheck("Dep");
        var composite = new HealthGroup("Comp", new[]
        {
            new HealthDependency(dep, Importance.Required),
        });

        Assert.Equal(HealthStatus.Healthy, composite.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_UnhealthyRequired_ReturnsUnhealthy()
    {
        var dep = new HealthCheck("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var composite = new HealthGroup("Comp", new[]
        {
            new HealthDependency(dep, Importance.Required),
        });

        Assert.Equal(HealthStatus.Unhealthy, composite.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_MixedImportance_PropagatesCorrectly()
    {
        var required = new HealthCheck("Req");
        var important = new HealthCheck("Imp",
            () => HealthStatus.Unhealthy);
        var optional = new HealthCheck("Opt",
            () => HealthStatus.Unhealthy);

        var composite = new HealthGroup("Comp", new[]
        {
            new HealthDependency(required, Importance.Required),
            new HealthDependency(important, Importance.Important),
            new HealthDependency(optional, Importance.Optional),
        });

        // Important+Unhealthy → Degraded. Optional ignored. Intrinsic = Unknown.
        // Degraded > Unknown, so Degraded wins.
        Assert.Equal(HealthStatus.Degraded, composite.Evaluate().Status);
    }

    [Fact]
    public void Name_ReturnsConstructorValue()
    {
        var composite = new HealthGroup("MyComposite",
            Array.Empty<HealthDependency>());

        Assert.Equal("MyComposite", composite.Name);
    }

    [Fact]
    public void Dependencies_ReflectsConstructorDeps()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B");
        var composite = new HealthGroup("Comp", new[]
        {
            new HealthDependency(a, Importance.Required),
            new HealthDependency(b, Importance.Optional),
        });

        Assert.Equal(2, composite.Dependencies.Count);
    }

    [Fact]
    public void NotifyChanged_EmitsOnStatusChange()
    {
        var dep = new HealthCheck("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var composite = new HealthGroup("Comp", new[]
        {
            new HealthDependency(dep, Importance.Required),
        });

        var emitted = new List<HealthStatus>();
        composite.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        composite.NotifyChanged();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
