namespace Prognosis.Tests;

public class DelegatingServiceHealthTests
{
    [Fact]
    public void Constructor_SetsName()
    {
        var svc = new DelegatingServiceHealth("MyService");

        Assert.Equal("MyService", svc.Name);
    }

    [Fact]
    public void Constructor_NameOnly_EvaluatesHealthy()
    {
        var svc = new DelegatingServiceHealth("MyService");

        Assert.Equal(HealthStatus.Healthy, svc.Evaluate().Status);
    }

    [Fact]
    public void Constructor_WithHealthCheck_DelegatesEvaluation()
    {
        var svc = new DelegatingServiceHealth("Svc",
            () => new HealthEvaluation(HealthStatus.Degraded, "slow"));

        var eval = svc.Evaluate();

        Assert.Equal(HealthStatus.Degraded, eval.Status);
        Assert.Equal("slow", eval.Reason);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForFluentChaining()
    {
        var dep = new DelegatingServiceHealth("Dep");
        var svc = new DelegatingServiceHealth("Svc");

        var returned = svc.DependsOn(dep, ServiceImportance.Required);

        Assert.Same(svc, returned);
    }

    [Fact]
    public void DependsOn_WiresEdge_AffectsEvaluation()
    {
        var dep = new DelegatingServiceHealth("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var svc = new DelegatingServiceHealth("Svc")
            .DependsOn(dep, ServiceImportance.Required);

        Assert.Equal(HealthStatus.Unhealthy, svc.Evaluate().Status);
    }

    [Fact]
    public void DependsOn_ImportantCapsUnhealthyAtDegraded()
    {
        var dep = new DelegatingServiceHealth("Dep",
            () => HealthStatus.Unhealthy);
        var svc = new DelegatingServiceHealth("Svc")
            .DependsOn(dep, ServiceImportance.Important);

        Assert.Equal(HealthStatus.Degraded, svc.Evaluate().Status);
    }

    [Fact]
    public void DependsOn_Optional_DoesNotAffectParent()
    {
        var dep = new DelegatingServiceHealth("Dep",
            () => HealthStatus.Unhealthy);
        var svc = new DelegatingServiceHealth("Svc")
            .DependsOn(dep, ServiceImportance.Optional);

        Assert.Equal(HealthStatus.Healthy, svc.Evaluate().Status);
    }

    [Fact]
    public void StatusChanged_EmitsAfterNotifyChanged()
    {
        var svc = new DelegatingServiceHealth("Svc",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var emitted = new List<HealthStatus>();
        svc.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        svc.NotifyChanged();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void ToString_IncludesNameAndStatus()
    {
        var svc = new DelegatingServiceHealth("DB");
        var str = svc.ToString();

        Assert.Contains("DB", str);
        Assert.Contains("Healthy", str);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
