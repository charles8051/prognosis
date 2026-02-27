namespace Prognosis.Tests;

public class HealthGraphTests
{
    // ── Create ───────────────────────────────────────────────────────

    [Fact]
    public void Create_SingleRoot_IndexesAllReachableNodes()
    {
        var leaf = new DelegateHealthNode("Leaf");
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(2, graph.Nodes.Count());
        Assert.Same(root, graph["Root"]);
        Assert.Same(leaf, graph["Leaf"]);
    }

    [Fact]
    public void Create_MultipleChildren_IndexesAll()
    {
        var a = new DelegateHealthNode("A");
        var b = new DelegateHealthNode("B");
        var root = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(3, graph.Nodes.Count());
    }

    [Fact]
    public void Create_SharedDependency_IndexedOnce()
    {
        var shared = new DelegateHealthNode("Shared");
        var a = new DelegateHealthNode("A").DependsOn(shared, Importance.Required);
        var b = new DelegateHealthNode("B").DependsOn(shared, Importance.Required);
        var root = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(4, graph.Nodes.Count());
        Assert.Same(shared, graph["Shared"]);
    }

    [Fact]
    public void Create_DeepGraph_IndexesAllLevels()
    {
        var c = new DelegateHealthNode("C");
        var b = new DelegateHealthNode("B").DependsOn(c, Importance.Required);
        var a = new DelegateHealthNode("A").DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(a);

        Assert.Equal(3, graph.Nodes.Count());
        Assert.Same(c, graph["C"]);
    }

    // ── Roots ────────────────────────────────────────────────────────

    [Fact]
    public void Root_ReturnsExactNodePassed()
    {
        var leaf = new DelegateHealthNode("Leaf");
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Same(root, graph.Root);
    }

    [Fact]
    public void Root_WithMultipleChildren_ReturnsRoot()
    {
        var shared = new DelegateHealthNode("Shared");
        var a = new DelegateHealthNode("A").DependsOn(shared, Importance.Required);
        var b = new DelegateHealthNode("B").DependsOn(shared, Importance.Required);
        var root = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Same(root, graph.Root);
        Assert.Equal(2, root.Dependencies.Count);
    }

    // ── Indexer ──────────────────────────────────────────────────────

    [Fact]
    public void Indexer_ReturnsNodeByName()
    {
        var node = new DelegateHealthNode("MyNode");
        var graph = HealthGraph.Create(node);

        Assert.Same(node, graph["MyNode"]);
    }

    [Fact]
    public void Indexer_UnknownName_ThrowsKeyNotFound()
    {
        var graph = HealthGraph.Create(new DelegateHealthNode("A"));

        Assert.Throws<KeyNotFoundException>(() => graph["Missing"]);
    }

    // ── TryGetService ────────────────────────────────────────────────

    [Fact]
    public void TryGetService_Found_ReturnsTrue()
    {
        var node = new DelegateHealthNode("DB");
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetNode("DB", out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetService_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(new DelegateHealthNode("A"));

        Assert.False(graph.TryGetNode("Missing", out _));
    }

    [Fact]
    public void TryGetServiceGeneric_Found_ReturnsTrue()
    {
        // The generic overload uses typeof(T).Name as the key.
        var node = new DelegateHealthNode(typeof(StubHealthAware).Name);
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetNode<StubHealthAware>(out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetServiceGeneric_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(new DelegateHealthNode("Unrelated"));

        Assert.False(graph.TryGetNode<StubHealthAware>(out _));
    }

    // ── Services ─────────────────────────────────────────────────────

    [Fact]
    public void Services_ReturnsAllIndexedNodes()
    {
        var a = new DelegateHealthNode("A");
        var b = new DelegateHealthNode("B");
        var root = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var names = graph.Nodes.Select(n => n.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "A", "B", "Root" }, names);
    }

    // ── Dynamic topology refresh ─────────────────────────────────────

    [Fact]
    public void TryGetNode_AfterDependsOn_FindsNewNode()
    {
        var root = new DelegateHealthNode("Root");
        var graph = HealthGraph.Create(root);

        Assert.False(graph.TryGetNode("NewChild", out _));

        var child = new DelegateHealthNode("NewChild");
        root.DependsOn(child, Importance.Required);

        Assert.True(graph.TryGetNode("NewChild", out var found));
        Assert.Same(child, found);
    }

    [Fact]
    public void Nodes_AfterDependsOn_IncludesNewNode()
    {
        var root = new DelegateHealthNode("Root");
        var graph = HealthGraph.Create(root);

        Assert.Single(graph.Nodes);

        root.DependsOn(new DelegateHealthNode("Added"), Importance.Required);

        Assert.Equal(2, graph.Nodes.Count());
    }

    [Fact]
    public void Nodes_AfterRemoveDependency_ExcludesOrphanedNode()
    {
        var child = new DelegateHealthNode("Child");
        var root = new DelegateHealthNode("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        Assert.Equal(2, graph.Nodes.Count());

        root.RemoveDependency(child);

        Assert.Single(graph.Nodes);
        Assert.False(graph.TryGetNode("Child", out _));
    }

    [Fact]
    public void TryGetNode_AfterDeepDependsOn_FindsTransitiveNode()
    {
        var root = new DelegateHealthNode("Root");
        var mid = new DelegateHealthNode("Mid");
        var graph = HealthGraph.Create(root);

        root.DependsOn(mid, Importance.Required);
        Assert.True(graph.TryGetNode("Mid", out _));

        var leaf = new DelegateHealthNode("Leaf");
        mid.DependsOn(leaf, Importance.Required);

        Assert.True(graph.TryGetNode("Leaf", out var found));
        Assert.Same(leaf, found);
    }

    // ── CreateReport ─────────────────────────────────────────────────

    [Fact]
    public void CreateReport_ReturnsReportFromRoots()
    {
        var child = new DelegateHealthNode("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new DelegateHealthNode("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.CreateReport();

        Assert.True(report.Nodes.Count > 0);
        Assert.Equal(HealthStatus.Unhealthy, report.Nodes.First(n => n.Name == "Child").Status);
    }

    [Fact]
    public void CreateReport_SingleHealthyNode_ReturnsHealthy()
    {
        var graph = HealthGraph.Create(new DelegateHealthNode("Only"));

        var report = graph.CreateReport();

        var node = Assert.Single(report.Nodes);
        Assert.Equal(HealthStatus.Healthy, node.Status);
    }

    // ── CreateTreeSnapshot ───────────────────────────────────────────

    [Fact]
    public void CreateTreeSnapshot_SingleNode_ReturnsLeafWithNoDependencies()
    {
        var graph = HealthGraph.Create(new DelegateHealthNode("Only"));

        var tree = graph.CreateTreeSnapshot();

        Assert.Equal("Only", tree.Name);
        Assert.Equal(HealthStatus.Healthy, tree.Status);
        Assert.Empty(tree.Dependencies);
    }

    [Fact]
    public void CreateTreeSnapshot_PreservesHierarchyAndImportance()
    {
        var db = new DelegateHealthNode("Database");
        var cache = new DelegateHealthNode("Cache");
        var auth = new DelegateHealthNode("Auth")
            .DependsOn(db, Importance.Required)
            .DependsOn(cache, Importance.Important);
        var graph = HealthGraph.Create(auth);

        var tree = graph.CreateTreeSnapshot();

        Assert.Equal("Auth", tree.Name);
        Assert.Equal(2, tree.Dependencies.Count);
        Assert.Equal("Database", tree.Dependencies[0].Node.Name);
        Assert.Equal(Importance.Required, tree.Dependencies[0].Importance);
        Assert.Equal("Cache", tree.Dependencies[1].Node.Name);
        Assert.Equal(Importance.Important, tree.Dependencies[1].Importance);
    }

    [Fact]
    public void CreateTreeSnapshot_PropagatesUnhealthyStatus()
    {
        var child = new DelegateHealthNode("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new DelegateHealthNode("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var tree = graph.CreateTreeSnapshot();

        Assert.Equal(HealthStatus.Unhealthy, tree.Status);
        Assert.Equal(HealthStatus.Unhealthy, tree.Dependencies[0].Node.Status);
        Assert.Equal("down", tree.Dependencies[0].Node.Reason);
    }

    [Fact]
    public void CreateTreeSnapshot_DiamondDependency_SecondOccurrenceIsLeaf()
    {
        var shared = new DelegateHealthNode("Shared");
        var a = new DelegateHealthNode("A").DependsOn(shared, Importance.Required);
        var b = new DelegateHealthNode("B").DependsOn(shared, Importance.Required);
        var root = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var tree = graph.CreateTreeSnapshot();

        // First branch includes Shared with children walked.
        var sharedUnderA = tree.Dependencies[0].Node.Dependencies[0].Node;
        Assert.Equal("Shared", sharedUnderA.Name);

        // Second branch: Shared was already visited — rendered as a leaf.
        var sharedUnderB = tree.Dependencies[1].Node.Dependencies[0].Node;
        Assert.Equal("Shared", sharedUnderB.Name);
        Assert.Empty(sharedUnderB.Dependencies);
    }
}

/// <summary>Minimal IHealthAware stub for generic TryGetService tests.</summary>
file class StubHealthAware : IHealthAware
{
    public HealthNode HealthNode { get; } = new DelegateHealthNode(typeof(StubHealthAware).Name);
}
