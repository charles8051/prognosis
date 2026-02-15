namespace Prognosis.Tests;

public class CompositeServiceHealthTests
{
    [Fact]
    public void Evaluate_AllHealthy_ReturnsHealthy()
    {
        var dep = new DelegatingServiceHealth("Dep");
        var composite = new CompositeServiceHealth("Comp", new[]
        {
            new ServiceDependency(dep, ServiceImportance.Required),
        });

        Assert.Equal(HealthStatus.Healthy, composite.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_UnhealthyRequired_ReturnsUnhealthy()
    {
        var dep = new DelegatingServiceHealth("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var composite = new CompositeServiceHealth("Comp", new[]
        {
            new ServiceDependency(dep, ServiceImportance.Required),
        });

        Assert.Equal(HealthStatus.Unhealthy, composite.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_MixedImportance_PropagatesCorrectly()
    {
        var required = new DelegatingServiceHealth("Req");
        var important = new DelegatingServiceHealth("Imp",
            () => HealthStatus.Unhealthy);
        var optional = new DelegatingServiceHealth("Opt",
            () => HealthStatus.Unhealthy);

        var composite = new CompositeServiceHealth("Comp", new[]
        {
            new ServiceDependency(required, ServiceImportance.Required),
            new ServiceDependency(important, ServiceImportance.Important),
            new ServiceDependency(optional, ServiceImportance.Optional),
        });

        // Important+Unhealthy → Degraded. Optional ignored. Intrinsic = Unknown.
        // Degraded > Unknown, so Degraded wins.
        Assert.Equal(HealthStatus.Degraded, composite.Evaluate().Status);
    }

    [Fact]
    public void Name_ReturnsConstructorValue()
    {
        var composite = new CompositeServiceHealth("MyComposite",
            Array.Empty<ServiceDependency>());

        Assert.Equal("MyComposite", composite.Name);
    }

    [Fact]
    public void Dependencies_ReflectsConstructorDeps()
    {
        var a = new DelegatingServiceHealth("A");
        var b = new DelegatingServiceHealth("B");
        var composite = new CompositeServiceHealth("Comp", new[]
        {
            new ServiceDependency(a, ServiceImportance.Required),
            new ServiceDependency(b, ServiceImportance.Optional),
        });

        Assert.Equal(2, composite.Dependencies.Count);
    }

    [Fact]
    public void NotifyChanged_EmitsOnStatusChange()
    {
        var dep = new DelegatingServiceHealth("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var composite = new CompositeServiceHealth("Comp", new[]
        {
            new ServiceDependency(dep, ServiceImportance.Required),
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
