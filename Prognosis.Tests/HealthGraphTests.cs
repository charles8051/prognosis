namespace Prognosis.Tests;

public class HealthGraphTests
{
    // ── Create ───────────────────────────────────────────────────────

    [Fact]
    public void Create_SingleRoot_IndexesAllReachableNodes()
    {
        var leaf = new HealthAdapter("Leaf");
        var root = new HealthAdapter("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(2, graph.Nodes.Count());
        Assert.Same(root, graph["Root"]);
        Assert.Same(leaf, graph["Leaf"]);
    }

    [Fact]
    public void Create_MultipleEntryPoints_IndexesAll()
    {
        var a = new HealthAdapter("A");
        var b = new HealthAdapter("B");

        var graph = HealthGraph.Create(a, b);

        Assert.Equal(2, graph.Nodes.Count());
    }

    [Fact]
    public void Create_SharedDependency_IndexedOnce()
    {
        var shared = new HealthAdapter("Shared");
        var a = new HealthAdapter("A").DependsOn(shared, Importance.Required);
        var b = new HealthAdapter("B").DependsOn(shared, Importance.Required);

        var graph = HealthGraph.Create(a, b);

        Assert.Equal(3, graph.Nodes.Count());
        Assert.Same(shared, graph["Shared"]);
    }

    [Fact]
    public void Create_DeepGraph_IndexesAllLevels()
    {
        var c = new HealthAdapter("C");
        var b = new HealthAdapter("B").DependsOn(c, Importance.Required);
        var a = new HealthAdapter("A").DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(a);

        Assert.Equal(3, graph.Nodes.Count());
        Assert.Same(c, graph["C"]);
    }

    // ── Roots ────────────────────────────────────────────────────────

    [Fact]
    public void Roots_ReturnsExactNodesPassed()
    {
        var leaf = new HealthAdapter("Leaf");
        var root = new HealthAdapter("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        var roots = graph.Roots;
        Assert.Single(roots);
        Assert.Same(root, roots[0]);
    }

    [Fact]
    public void Roots_MultipleRoots_ReturnsAll()
    {
        var shared = new HealthAdapter("Shared");
        var a = new HealthAdapter("A").DependsOn(shared, Importance.Required);
        var b = new HealthAdapter("B").DependsOn(shared, Importance.Required);

        var graph = HealthGraph.Create(a, b);

        var roots = graph.Roots;
        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, r => r.Name == "A");
        Assert.Contains(roots, r => r.Name == "B");
    }

    // ── Indexer ──────────────────────────────────────────────────────

    [Fact]
    public void Indexer_ReturnsNodeByName()
    {
        var node = new HealthAdapter("MyNode");
        var graph = HealthGraph.Create(node);

        Assert.Same(node, graph["MyNode"]);
    }

    [Fact]
    public void Indexer_UnknownName_ThrowsKeyNotFound()
    {
        var graph = HealthGraph.Create(new HealthAdapter("A"));

        Assert.Throws<KeyNotFoundException>(() => graph["Missing"]);
    }

    // ── TryGetService ────────────────────────────────────────────────

    [Fact]
    public void TryGetService_Found_ReturnsTrue()
    {
        var node = new HealthAdapter("DB");
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetNode("DB", out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetService_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(new HealthAdapter("A"));

        Assert.False(graph.TryGetNode("Missing", out _));
    }

    [Fact]
    public void TryGetServiceGeneric_Found_ReturnsTrue()
    {
        // The generic overload uses typeof(T).Name as the key.
        var node = new HealthAdapter(typeof(StubHealthAware).Name);
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetNode<StubHealthAware>(out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetServiceGeneric_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(new HealthAdapter("Unrelated"));

        Assert.False(graph.TryGetNode<StubHealthAware>(out _));
    }

    // ── Services ─────────────────────────────────────────────────────

    [Fact]
    public void Services_ReturnsAllIndexedNodes()
    {
        var a = new HealthAdapter("A");
        var b = new HealthAdapter("B");
        var graph = HealthGraph.Create(a, b);

        var names = graph.Nodes.Select(n => n.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "A", "B" }, names);
    }

    // ── CreateReport ─────────────────────────────────────────────────

    [Fact]
    public void CreateReport_ReturnsReportFromRoots()
    {
        var child = new HealthAdapter("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new HealthAdapter("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.CreateReport();

        Assert.Equal(HealthStatus.Unhealthy, report.OverallStatus);
        Assert.True(report.Services.Count > 0);
    }

    [Fact]
    public void CreateReport_EmptyGraph_ReturnsHealthy()
    {
        var graph = HealthGraph.Create();

        var report = graph.CreateReport();

        Assert.Equal(HealthStatus.Healthy, report.OverallStatus);
        Assert.Empty(report.Services);
    }
}

/// <summary>Minimal IHealthAware stub for generic TryGetService tests.</summary>
file class StubHealthAware : IHealthAware
{
    public HealthNode HealthNode { get; } = new HealthAdapter(typeof(StubHealthAware).Name);
}
