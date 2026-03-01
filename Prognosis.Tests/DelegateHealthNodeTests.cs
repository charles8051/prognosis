namespace Prognosis.Tests;

public class DelegateHealthNodeTests
{
    [Fact]
    public void Constructor_SetsName()
    {
        var svc = HealthNode.CreateDelegate("MyService");

        Assert.Equal("MyService", svc.Name);
    }

    [Fact]
    public void Constructor_NameOnly_EvaluatesHealthy()
    {
        var svc = HealthNode.CreateDelegate("MyService");
        var graph = HealthGraph.Create(svc);

        Assert.Equal(HealthStatus.Healthy, graph.CreateReport().Nodes.First(n => n.Name == "MyService").Status);
    }

    [Fact]
    public void Constructor_WithHealthAdapter_DelegatesEvaluation()
    {
        var svc = HealthNode.CreateDelegate("Svc",
            () => HealthEvaluation.Degraded("slow"));
        var graph = HealthGraph.Create(svc);

        var eval = graph.CreateReport().Nodes.First(n => n.Name == "Svc");

        Assert.Equal(HealthStatus.Degraded, eval.Status);
        Assert.Equal("slow", eval.Reason);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForFluentChaining()
    {
        var dep = HealthNode.CreateDelegate("Dep");
        var svc = HealthNode.CreateDelegate("Svc");

        var returned = svc.DependsOn(dep, Importance.Required);

        Assert.Same(svc, returned);
    }

    [Fact]
    public void DependsOn_WiresEdge_AffectsEvaluation()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("down"));
        var svc = HealthNode.CreateDelegate("Svc")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(svc);

        Assert.Equal(HealthStatus.Unhealthy, graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void DependsOn_ImportantCapsUnhealthyAtDegraded()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthStatus.Unhealthy);
        var svc = HealthNode.CreateDelegate("Svc")
            .DependsOn(dep, Importance.Important);
        var graph = HealthGraph.Create(svc);

        Assert.Equal(HealthStatus.Degraded, graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void DependsOn_Optional_DoesNotAffectParent()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthStatus.Unhealthy);
        var svc = HealthNode.CreateDelegate("Svc")
            .DependsOn(dep, Importance.Optional);
        var graph = HealthGraph.Create(svc);

        Assert.Equal(HealthStatus.Healthy, graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void ToString_IncludesNameAndStatus()
    {
        var svc = HealthNode.CreateDelegate("DB");
        var str = svc.ToString();

        Assert.Contains("DB", str);
        Assert.Contains("Healthy", str);
    }

    // ── Parent tracking ──────────────────────────────────────────────

    [Fact]
    public void DependsOn_SetsParentOnChild()
    {
        var child = HealthNode.CreateDelegate("Child");
        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(child, Importance.Required);

        Assert.True(child.HasParents);
        Assert.Single(child.Parents);
        Assert.Same(parent, child.Parents[0]);
    }

    [Fact]
    public void DependsOn_MultipleParents_TracksAll()
    {
        var child = HealthNode.CreateDelegate("Child");
        var p1 = HealthNode.CreateDelegate("P1").DependsOn(child, Importance.Required);
        var p2 = HealthNode.CreateDelegate("P2").DependsOn(child, Importance.Important);

        Assert.Equal(2, child.Parents.Count);
    }

    [Fact]
    public void HasParents_FalseForOrphanedNode()
    {
        var orphan = HealthNode.CreateDelegate("Orphan");

        Assert.False(orphan.HasParents);
        Assert.Empty(orphan.Parents);
    }

    // ── RemoveDependency ─────────────────────────────────────────────

    [Fact]
    public void RemoveDependency_RemovesEdge()
    {
        var child = HealthNode.CreateDelegate("Child",
            () => HealthEvaluation.Unhealthy("down"));
        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy, graph.CreateReport().Nodes.First(n => n.Name == "Parent").Status);

        var removed = parent.RemoveDependency(child);

        Assert.True(removed);
        Assert.Empty(parent.Dependencies);
        Assert.Equal(HealthStatus.Healthy, graph.CreateReport().Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void RemoveDependency_ClearsParentOnChild()
    {
        var child = HealthNode.CreateDelegate("Child");
        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(child, Importance.Required);

        Assert.True(child.HasParents);

        parent.RemoveDependency(child);

        Assert.False(child.HasParents);
        Assert.Empty(child.Parents);
    }

    [Fact]
    public void RemoveDependency_UnknownNode_ReturnsFalse()
    {
        var parent = HealthNode.CreateDelegate("Parent");
        var unknown = HealthNode.CreateDelegate("Unknown");

        Assert.False(parent.RemoveDependency(unknown));
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
