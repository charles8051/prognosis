namespace Prognosis.Tests;

public class HealthAdapterTests
{
    [Fact]
    public void Constructor_SetsName()
    {
        var svc = new HealthAdapter("MyService");

        Assert.Equal("MyService", svc.Name);
    }

    [Fact]
    public void Constructor_NameOnly_EvaluatesHealthy()
    {
        var svc = new HealthAdapter("MyService");

        Assert.Equal(HealthStatus.Healthy, svc.Evaluate().Status);
    }

    [Fact]
    public void Constructor_WithHealthAdapter_DelegatesEvaluation()
    {
        var svc = new HealthAdapter("Svc",
            () => new HealthEvaluation(HealthStatus.Degraded, "slow"));

        var eval = svc.Evaluate();

        Assert.Equal(HealthStatus.Degraded, eval.Status);
        Assert.Equal("slow", eval.Reason);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForFluentChaining()
    {
        var dep = new HealthAdapter("Dep");
        var svc = new HealthAdapter("Svc");

        var returned = svc.DependsOn(dep, Importance.Required);

        Assert.Same(svc, returned);
    }

    [Fact]
    public void DependsOn_WiresEdge_AffectsEvaluation()
    {
        var dep = new HealthAdapter("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var svc = new HealthAdapter("Svc")
            .DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, svc.Evaluate().Status);
    }

    [Fact]
    public void DependsOn_ImportantCapsUnhealthyAtDegraded()
    {
        var dep = new HealthAdapter("Dep",
            () => HealthStatus.Unhealthy);
        var svc = new HealthAdapter("Svc")
            .DependsOn(dep, Importance.Important);

        Assert.Equal(HealthStatus.Degraded, svc.Evaluate().Status);
    }

    [Fact]
    public void DependsOn_Optional_DoesNotAffectParent()
    {
        var dep = new HealthAdapter("Dep",
            () => HealthStatus.Unhealthy);
        var svc = new HealthAdapter("Svc")
            .DependsOn(dep, Importance.Optional);

        Assert.Equal(HealthStatus.Healthy, svc.Evaluate().Status);
    }

    [Fact]
    public void StatusChanged_EmitsAfterBubbleChange()
    {
        var svc = new HealthAdapter("Svc",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var emitted = new List<HealthStatus>();
        svc.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        svc.BubbleChange();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void ToString_IncludesNameAndStatus()
    {
        var svc = new HealthAdapter("DB");
        var str = svc.ToString();

        Assert.Contains("DB", str);
        Assert.Contains("Healthy", str);
    }

    // ── Parent tracking ──────────────────────────────────────────────

    [Fact]
    public void DependsOn_SetsParentOnChild()
    {
        var child = new HealthAdapter("Child");
        var parent = new HealthAdapter("Parent")
            .DependsOn(child, Importance.Required);

        Assert.True(child.HasParents);
        Assert.Single(child.Parents);
        Assert.Same(parent, child.Parents[0]);
    }

    [Fact]
    public void DependsOn_MultipleParents_TracksAll()
    {
        var child = new HealthAdapter("Child");
        var p1 = new HealthAdapter("P1").DependsOn(child, Importance.Required);
        var p2 = new HealthAdapter("P2").DependsOn(child, Importance.Important);

        Assert.Equal(2, child.Parents.Count);
    }

    [Fact]
    public void HasParents_FalseForOrphanedNode()
    {
        var orphan = new HealthAdapter("Orphan");

        Assert.False(orphan.HasParents);
        Assert.Empty(orphan.Parents);
    }

    // ── RemoveDependency ─────────────────────────────────────────────

    [Fact]
    public void RemoveDependency_RemovesEdge()
    {
        var child = new HealthAdapter("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var parent = new HealthAdapter("Parent")
            .DependsOn(child, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, parent.Evaluate().Status);

        var removed = parent.RemoveDependency(child);

        Assert.True(removed);
        Assert.Empty(parent.Dependencies);
        Assert.Equal(HealthStatus.Healthy, parent.Evaluate().Status);
    }

    [Fact]
    public void RemoveDependency_ClearsParentOnChild()
    {
        var child = new HealthAdapter("Child");
        var parent = new HealthAdapter("Parent")
            .DependsOn(child, Importance.Required);

        Assert.True(child.HasParents);

        parent.RemoveDependency(child);

        Assert.False(child.HasParents);
        Assert.Empty(child.Parents);
    }

    [Fact]
    public void RemoveDependency_UnknownNode_ReturnsFalse()
    {
        var parent = new HealthAdapter("Parent");
        var unknown = new HealthAdapter("Unknown");

        Assert.False(parent.RemoveDependency(unknown));
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
