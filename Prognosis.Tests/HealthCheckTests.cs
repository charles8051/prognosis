namespace Prognosis.Tests;

public class HealthCheckTests
{
    [Fact]
    public void Constructor_SetsName()
    {
        var svc = new HealthCheck("MyService");

        Assert.Equal("MyService", svc.Name);
    }

    [Fact]
    public void Constructor_NameOnly_EvaluatesHealthy()
    {
        var svc = new HealthCheck("MyService");

        Assert.Equal(HealthStatus.Healthy, svc.Evaluate().Status);
    }

    [Fact]
    public void Constructor_WithHealthCheck_DelegatesEvaluation()
    {
        var svc = new HealthCheck("Svc",
            () => new HealthEvaluation(HealthStatus.Degraded, "slow"));

        var eval = svc.Evaluate();

        Assert.Equal(HealthStatus.Degraded, eval.Status);
        Assert.Equal("slow", eval.Reason);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForFluentChaining()
    {
        var dep = new HealthCheck("Dep");
        var svc = new HealthCheck("Svc");

        var returned = svc.DependsOn(dep, Importance.Required);

        Assert.Same(svc, returned);
    }

    [Fact]
    public void DependsOn_WiresEdge_AffectsEvaluation()
    {
        var dep = new HealthCheck("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var svc = new HealthCheck("Svc")
            .DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, svc.Evaluate().Status);
    }

    [Fact]
    public void DependsOn_ImportantCapsUnhealthyAtDegraded()
    {
        var dep = new HealthCheck("Dep",
            () => HealthStatus.Unhealthy);
        var svc = new HealthCheck("Svc")
            .DependsOn(dep, Importance.Important);

        Assert.Equal(HealthStatus.Degraded, svc.Evaluate().Status);
    }

    [Fact]
    public void DependsOn_Optional_DoesNotAffectParent()
    {
        var dep = new HealthCheck("Dep",
            () => HealthStatus.Unhealthy);
        var svc = new HealthCheck("Svc")
            .DependsOn(dep, Importance.Optional);

        Assert.Equal(HealthStatus.Healthy, svc.Evaluate().Status);
    }

    [Fact]
    public void StatusChanged_EmitsAfterNotifyChanged()
    {
        var svc = new HealthCheck("Svc",
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
        var svc = new HealthCheck("DB");
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
