namespace Prognosis.Tests;

public class HealthGraphTests
{
    // ── Create ───────────────────────────────────────────────────────

    [Fact]
    public void Create_SingleRoot_IndexesAllReachableNodes()
    {
        var leaf = new HealthCheck("Leaf");
        var root = new HealthCheck("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(2, graph.Services.Count());
        Assert.Same(root, graph["Root"]);
        Assert.Same(leaf, graph["Leaf"]);
    }

    [Fact]
    public void Create_MultipleEntryPoints_IndexesAll()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B");

        var graph = HealthGraph.Create(a, b);

        Assert.Equal(2, graph.Services.Count());
    }

    [Fact]
    public void Create_SharedDependency_IndexedOnce()
    {
        var shared = new HealthCheck("Shared");
        var a = new HealthCheck("A").DependsOn(shared, Importance.Required);
        var b = new HealthCheck("B").DependsOn(shared, Importance.Required);

        var graph = HealthGraph.Create(a, b);

        Assert.Equal(3, graph.Services.Count());
        Assert.Same(shared, graph["Shared"]);
    }

    [Fact]
    public void Create_DeepGraph_IndexesAllLevels()
    {
        var c = new HealthCheck("C");
        var b = new HealthCheck("B").DependsOn(c, Importance.Required);
        var a = new HealthCheck("A").DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(a);

        Assert.Equal(3, graph.Services.Count());
        Assert.Same(c, graph["C"]);
    }

    // ── Roots ────────────────────────────────────────────────────────

    [Fact]
    public void Roots_ReturnsNodesWithoutParents()
    {
        var leaf = new HealthCheck("Leaf");
        var root = new HealthCheck("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        var roots = graph.Roots;
        Assert.Single(roots);
        Assert.Same(root, roots[0]);
    }

    [Fact]
    public void Roots_MultipleRoots_ReturnsAll()
    {
        var shared = new HealthCheck("Shared");
        var a = new HealthCheck("A").DependsOn(shared, Importance.Required);
        var b = new HealthCheck("B").DependsOn(shared, Importance.Required);

        var graph = HealthGraph.Create(a, b);

        var roots = graph.Roots;
        Assert.Equal(2, roots.Length);
        Assert.Contains(roots, r => r.Name == "A");
        Assert.Contains(roots, r => r.Name == "B");
    }

    [Fact]
    public void Roots_UpdatesDynamically_AfterAddEdge()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B");
        var graph = HealthGraph.Create(a, b);

        // Both are roots initially.
        Assert.Equal(2, graph.Roots.Length);

        // Wire A → B — B is now a child.
        a.DependsOn(b, Importance.Required);

        Assert.Single(graph.Roots);
        Assert.Same(a, graph.Roots[0]);
    }

    [Fact]
    public void Roots_UpdatesDynamically_AfterRemoveEdge()
    {
        var child = new HealthCheck("Child");
        var parent = new HealthCheck("Parent")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(parent);

        Assert.Single(graph.Roots);

        // Remove edge — child becomes a root.
        parent.RemoveDependency(child);

        Assert.Equal(2, graph.Roots.Length);
    }

    // ── Indexer ──────────────────────────────────────────────────────

    [Fact]
    public void Indexer_ReturnsNodeByName()
    {
        var node = new HealthCheck("MyNode");
        var graph = HealthGraph.Create(node);

        Assert.Same(node, graph["MyNode"]);
    }

    [Fact]
    public void Indexer_UnknownName_ThrowsKeyNotFound()
    {
        var graph = HealthGraph.Create(new HealthCheck("A"));

        Assert.Throws<KeyNotFoundException>(() => graph["Missing"]);
    }

    // ── TryGetService ────────────────────────────────────────────────

    [Fact]
    public void TryGetService_Found_ReturnsTrue()
    {
        var node = new HealthCheck("DB");
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetService("DB", out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetService_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(new HealthCheck("A"));

        Assert.False(graph.TryGetService("Missing", out _));
    }

    [Fact]
    public void TryGetServiceGeneric_Found_ReturnsTrue()
    {
        // The generic overload uses typeof(T).Name as the key.
        var node = new HealthCheck(typeof(StubHealthAware).Name);
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetService<StubHealthAware>(out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetServiceGeneric_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(new HealthCheck("Unrelated"));

        Assert.False(graph.TryGetService<StubHealthAware>(out _));
    }

    // ── Services ─────────────────────────────────────────────────────

    [Fact]
    public void Services_ReturnsAllIndexedNodes()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B");
        var graph = HealthGraph.Create(a, b);

        var names = graph.Services.Select(n => n.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "A", "B" }, names);
    }

    // ── CreateReport ─────────────────────────────────────────────────

    [Fact]
    public void CreateReport_ReturnsReportFromRoots()
    {
        var child = new HealthCheck("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new HealthCheck("Root")
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
    public HealthNode Health { get; } = new HealthCheck(typeof(StubHealthAware).Name);
}
