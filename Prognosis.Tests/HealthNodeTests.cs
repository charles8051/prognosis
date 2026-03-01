namespace Prognosis.Tests;

public class HealthNodeTests
{
    // ── Evaluation (via HealthGraph.CreateReport) ───────────────────────

    [Fact]
    public void Evaluate_NoDependencies_ReturnsIntrinsicCheck()
    {
        var node = HealthNode.CreateDelegate("Svc",
            () => HealthEvaluation.Degraded("slow"));
        var graph = HealthGraph.Create(node);

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Svc");

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("slow", result.Reason);
    }

    [Fact]
    public void Evaluate_WithDependency_AggregatesCorrectly()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.CreateDelegate("Svc")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Svc");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ── DependsOn ────────────────────────────────────────────────────

    [Fact]
    public void DependsOn_AddsDependency()
    {
        var node = HealthNode.CreateDelegate("Svc");
        var dep = HealthNode.CreateDelegate("Dep");

        node.DependsOn(dep, Importance.Important);

        Assert.Single(node.Dependencies);
        Assert.Equal("Dep", node.Dependencies[0].Node.Name);
        Assert.Equal(Importance.Important, node.Dependencies[0].Importance);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForChaining()
    {
        var node = HealthNode.CreateDelegate("Svc");
        var dep = HealthNode.CreateDelegate("Dep");

        var returned = node.DependsOn(dep, Importance.Required);

        Assert.Same(node, returned);
    }

    [Fact]
    public void DependsOn_AfterGraphCreation_IsAllowed()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.CreateDelegate("Svc");
        var graph = HealthGraph.Create(node);

        // Check initial state.
        var before = graph.CreateReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Healthy, before.Status);

        // Adding an edge at runtime now works.
        node.DependsOn(dep, Importance.Required);
        var after = graph.CreateReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, after.Status);
    }

    [Fact]
    public void RemoveDependency_DetachesEdge()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.CreateDelegate("Svc")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        Assert.Equal(HealthStatus.Unhealthy, graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);

        var removed = node.RemoveDependency(dep);

        Assert.True(removed);
        Assert.Empty(node.Dependencies);
        Assert.Equal(HealthStatus.Healthy, graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void RemoveDependency_UnknownService_ReturnsFalse()
    {
        var node = HealthNode.CreateDelegate("Svc");
        var unknown = HealthNode.CreateDelegate("Unknown");

        Assert.False(node.RemoveDependency(unknown));
    }

    // ── Circular dependency guard ────────────────────────────────────

    [Fact]
    public void Evaluate_CircularDependency_DoesNotStackOverflow()
    {
        var a = HealthNode.CreateDelegate("A");
        var b = HealthNode.CreateDelegate("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(a);

        a.Refresh();
        var result = graph.CreateReport().Nodes.First(n => n.Name == "A");

        // Cycle is handled by propagation guard — the node evaluates
        // without stack overflow and returns a cached result.
        Assert.True(result.Status is HealthStatus.Healthy or HealthStatus.Unhealthy);
    }

    // ── Factory validation ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateDelegate_NullOrEmptyName_ThrowsArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(() => HealthNode.CreateDelegate(name!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateDelegate_WithCheck_NullOrEmptyName_ThrowsArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(
            () => HealthNode.CreateDelegate(name!, () => HealthStatus.Healthy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateComposite_NullOrEmptyName_ThrowsArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(() => HealthNode.CreateComposite(name!));
    }

    // ── Duplicate edge guard ─────────────────────────────────────────

    [Fact]
    public void DependsOn_SameNodeTwice_ThrowsInvalidOperationException()
    {
        var dep = HealthNode.CreateDelegate("Dep");
        var node = HealthNode.CreateDelegate("Svc")
            .DependsOn(dep, Importance.Required);

        Assert.Throws<InvalidOperationException>(
            () => node.DependsOn(dep, Importance.Optional));
    }

    [Fact]
    public void DependsOn_DifferentNodes_Allowed()
    {
        var a = HealthNode.CreateDelegate("A");
        var b = HealthNode.CreateDelegate("B");
        var node = HealthNode.CreateDelegate("Svc")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        Assert.Equal(2, node.Dependencies.Count);
    }
}
