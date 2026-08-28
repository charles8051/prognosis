namespace Prognosis.Tests;

public class HealthGraphTests
{
    // ── Create ───────────────────────────────────────────────────────

    [Fact]
    public void Create_SingleRoot_IndexesAllReachableNodes()
    {
        var leaf = HealthNode.Create("Leaf");
        var root = HealthNode.Create("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(2, graph.Nodes.Count());
        Assert.Same(root, graph["Root"]);
        Assert.Same(leaf, graph["Leaf"]);
    }

    [Fact]
    public void Create_MultipleChildren_IndexesAll()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(3, graph.Nodes.Count());
    }

    [Fact]
    public void Create_SharedDependency_IndexedOnce()
    {
        var shared = HealthNode.Create("Shared");
        var a = HealthNode.Create("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(shared, Importance.Required);
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Equal(4, graph.Nodes.Count());
        Assert.Same(shared, graph["Shared"]);
    }

    [Fact]
    public void Create_DeepGraph_IndexesAllLevels()
    {
        var c = HealthNode.Create("C");
        var b = HealthNode.Create("B").DependsOn(c, Importance.Required);
        var a = HealthNode.Create("A").DependsOn(b, Importance.Required);

        var graph = HealthGraph.Create(a);

        Assert.Equal(3, graph.Nodes.Count());
        Assert.Same(c, graph["C"]);
    }

    // ── Roots ────────────────────────────────────────────────────────

    [Fact]
    public void Root_ReturnsExactNodePassed()
    {
        var leaf = HealthNode.Create("Leaf");
        var root = HealthNode.Create("Root")
            .DependsOn(leaf, Importance.Required);

        var graph = HealthGraph.Create(root);

        Assert.Same(root, graph.Root);
    }

    [Fact]
    public void Root_WithMultipleChildren_ReturnsRoot()
    {
        var shared = HealthNode.Create("Shared");
        var a = HealthNode.Create("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(shared, Importance.Required);
        var root = HealthNode.Create("Root")
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
        var node = HealthNode.Create("MyNode");
        var graph = HealthGraph.Create(node);

        Assert.Same(node, graph["MyNode"]);
    }

    [Fact]
    public void Indexer_UnknownName_ThrowsKeyNotFound()
    {
        var graph = HealthGraph.Create(HealthNode.Create("A"));

        Assert.Throws<KeyNotFoundException>(() => graph["Missing"]);
    }

    // ── TryGetService ────────────────────────────────────────────────

    [Fact]
    public void TryGetService_Found_ReturnsTrue()
    {
        var node = HealthNode.Create("DB");
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetNode("DB", out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetService_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(HealthNode.Create("A"));

        Assert.False(graph.TryGetNode("Missing", out _));
    }

    [Fact]
    public void TryGetServiceGeneric_Found_ReturnsTrue()
    {
        // The generic overload uses typeof(T).Name as the key.
        var node = HealthNode.Create(typeof(StubHealthAware).Name);
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetNode<StubHealthAware>(out var found));
        Assert.Same(node, found);
    }

    [Fact]
    public void TryGetServiceGeneric_NotFound_ReturnsFalse()
    {
        var graph = HealthGraph.Create(HealthNode.Create("Unrelated"));

        Assert.False(graph.TryGetNode<StubHealthAware>(out _));
    }

    // ── Services ─────────────────────────────────────────────────────

    [Fact]
    public void Services_ReturnsAllIndexedNodes()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");
        var root = HealthNode.Create("Root")
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
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        Assert.False(graph.TryGetNode("NewChild", out _));

        var child = HealthNode.Create("NewChild");
        root.DependsOn(child, Importance.Required);

        Assert.True(graph.TryGetNode("NewChild", out var found));
        Assert.Same(child, found);
    }

    [Fact]
    public void Nodes_AfterDependsOn_IncludesNewNode()
    {
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        Assert.Single(graph.Nodes);

        root.DependsOn(HealthNode.Create("Added"), Importance.Required);

        Assert.Equal(2, graph.Nodes.Count());
    }

    [Fact]
    public void Nodes_AfterRemoveDependency_ExcludesOrphanedNode()
    {
        var child = HealthNode.Create("Child");
        var root = HealthNode.Create("Root")
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
        var root = HealthNode.Create("Root");
        var mid = HealthNode.Create("Mid");
        var graph = HealthGraph.Create(root);

        root.DependsOn(mid, Importance.Required);
        Assert.True(graph.TryGetNode("Mid", out _));

        var leaf = HealthNode.Create("Leaf");
        mid.DependsOn(leaf, Importance.Required);

        Assert.True(graph.TryGetNode("Leaf", out var found));
        Assert.Same(leaf, found);
    }

    // ── GetReport ───────────────────────────────────────────────────

    [Fact]
    public void GetReport_ReturnsReportFromRoots()
    {
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.Create("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.GetReport();

        Assert.True(report.Nodes.Count > 0);
        Assert.Equal(HealthStatus.Unhealthy, report.Nodes.First(n => n.Name == "Child").Status);
    }

    [Fact]
    public void GetReport_SingleHealthyNode_ReturnsHealthy()
    {
        var graph = HealthGraph.Create(HealthNode.Create("Only"));

        var report = graph.GetReport();

        var node = Assert.Single(report.Nodes);
        Assert.Equal(HealthStatus.Healthy, node.Status);
    }

    // ── CreateTreeSnapshot ───────────────────────────────────────────

    [Fact]
    public void CreateTreeSnapshot_SingleNode_ReturnsLeafWithNoDependencies()
    {
        var graph = HealthGraph.Create(HealthNode.Create("Only"));

        var tree = graph.CreateTreeSnapshot();

        Assert.Equal("Only", tree.Name);
        Assert.Equal(HealthStatus.Healthy, tree.Status);
        Assert.Empty(tree.Dependencies);
    }

    [Fact]
    public void CreateTreeSnapshot_PreservesHierarchyAndImportance()
    {
        var db = HealthNode.Create("Database");
        var cache = HealthNode.Create("Cache");
        var auth = HealthNode.Create("Auth")
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
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.Create("Root")
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
        var shared = HealthNode.Create("Shared");
        var a = HealthNode.Create("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(shared, Importance.Required);
        var root = HealthNode.Create("Root")
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

    // ── TopologyChanged ──────────────────────────────────────────────

    [Fact]
    public void TopologyChanged_DependsOn_EmitsAddedNode()
    {
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        var child = HealthNode.Create("Child");
        root.DependsOn(child, Importance.Required);

        Assert.Single(emitted);
        Assert.Single(emitted[0].Added);
        Assert.Same(child, emitted[0].Added[0]);
        Assert.Empty(emitted[0].Removed);
    }

    [Fact]
    public void TopologyChanged_RemoveDependency_EmitsRemovedNode()
    {
        var child = HealthNode.Create("Child");
        var root = HealthNode.Create("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        root.RemoveDependency(child);

        Assert.Single(emitted);
        Assert.Empty(emitted[0].Added);
        Assert.Single(emitted[0].Removed);
        Assert.Same(child, emitted[0].Removed[0]);
    }

    [Fact]
    public void TopologyChanged_NoStructuralChange_DoesNotEmit()
    {
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.Create("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        // Refresh fires but topology is unchanged — no notification expected.
        child.Refresh();

        Assert.Empty(emitted);
    }

    [Fact]
    public void TopologyChanged_TransitiveDependsOn_EmitsAllNewNodes()
    {
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        var leaf = HealthNode.Create("Leaf");
        var mid = HealthNode.Create("Mid")
            .DependsOn(leaf, Importance.Required);
        root.DependsOn(mid, Importance.Required);

        // mid and leaf should both appear in Added across the emitted changes.
        var allAdded = emitted.SelectMany(c => c.Added).ToList();
        Assert.Contains(mid, allAdded);
        Assert.Contains(leaf, allAdded);
    }

    [Fact]
    public void TopologyChanged_Unsubscribe_StopsNotifications()
    {
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        var subscription = graph.TopologyChanged
            .Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        subscription.Dispose();

        root.DependsOn(HealthNode.Create("Child"), Importance.Required);

        Assert.Empty(emitted);
    }
    // ── Shared nodes across multiple graphs ────────────────────────────

    [Fact]
    public void SharedNode_TwoGraphs_BothReceiveStatusChanged()
    {
        var isHealthy = true;
        var shared = HealthNode.Create("Shared").WithHealthProbe(
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);

        var root1 = HealthNode.Create("Root1")
            .DependsOn(shared, Importance.Required);
        var root2 = HealthNode.Create("Root2")
            .DependsOn(shared, Importance.Required);

        var graph1 = HealthGraph.Create(root1);
        var graph2 = HealthGraph.Create(root2);

        var reports1 = new List<HealthReport>();
        var reports2 = new List<HealthReport>();
        graph1.StatusChanged.Subscribe(new TestObserver<HealthReport>(reports1.Add));
        graph2.StatusChanged.Subscribe(new TestObserver<HealthReport>(reports2.Add));

        isHealthy = false;
        shared.Refresh();

        Assert.Single(reports1);
        Assert.Single(reports2);
        Assert.Equal(HealthStatus.Unhealthy,
            reports1[0].Nodes.First(n => n.Name == "Shared").Status);
        Assert.Equal(HealthStatus.Unhealthy,
            reports2[0].Nodes.First(n => n.Name == "Shared").Status);
    }

    [Fact]
    public void SharedNode_DependsOn_BothGraphsNotified()
    {
        var shared = HealthNode.Create("Shared");
        var root1 = HealthNode.Create("Root1")
            .DependsOn(shared, Importance.Required);
        var root2 = HealthNode.Create("Root2")
            .DependsOn(shared, Importance.Required);

        var graph1 = HealthGraph.Create(root1);
        var graph2 = HealthGraph.Create(root2);

        var topo1 = new List<TopologyChange>();
        var topo2 = new List<TopologyChange>();
        graph1.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(topo1.Add));
        graph2.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(topo2.Add));

        // Add a new child to the shared node — both graphs should detect
        // the topology change.
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        shared.DependsOn(child, Importance.Required);

        Assert.Single(topo1);
        Assert.Contains(topo1[0].Added, n => n.Name == "Child");
        Assert.Single(topo2);
        Assert.Contains(topo2[0].Added, n => n.Name == "Child");
    }

    [Fact]
    public void SharedNode_RemovedFromOneGraph_OtherGraphStillWorks()
    {
        var isHealthy = true;
        var shared = HealthNode.Create("Shared").WithHealthProbe(
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);

        var root1 = HealthNode.Create("Root1")
            .DependsOn(shared, Importance.Required);
        var root2 = HealthNode.Create("Root2")
            .DependsOn(shared, Importance.Required);

        var graph1 = HealthGraph.Create(root1);
        var graph2 = HealthGraph.Create(root2);

        // Detach shared from graph1's root.
        root1.RemoveDependency(shared);

        // Graph2 should still receive updates from the shared node.
        var reports2 = new List<HealthReport>();
        graph2.StatusChanged.Subscribe(new TestObserver<HealthReport>(reports2.Add));

        isHealthy = false;
        shared.Refresh();

        Assert.Single(reports2);
        Assert.Equal(HealthStatus.Unhealthy,
            reports2[0].Nodes.First(n => n.Name == "Shared").Status);
    }

    // ── RefreshAll direct tests ──────────────────────────────────────

    [Fact]
    public void RefreshAll_EmitsStatusChanged_OnFirstCall()
    {
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var graph = HealthGraph.Create(node);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        var report = graph.RefreshAll();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0].Nodes[0].Status);
        Assert.Same(emitted[0], report);
    }

    [Fact]
    public void RefreshAll_NoChange_DoesNotEmitDuplicate()
    {
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        graph.RefreshAll();
        graph.RefreshAll();

        Assert.Single(emitted);
    }

    [Fact]
    public void RefreshAll_StateChange_EmitsNewReport()
    {
        var isHealthy = true;
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var graph = HealthGraph.Create(node);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        var report1 = graph.RefreshAll();

        isHealthy = false;
        var report2 = graph.RefreshAll();

        Assert.Equal(2, emitted.Count);
        Assert.Equal(HealthStatus.Healthy, report1.Root.Status);
        Assert.Equal(HealthStatus.Unhealthy, report2.Root.Status);
    }

    // ── StatusChanged unsubscribe ────────────────────────────────────

    [Fact]
    public void StatusChanged_Unsubscribe_StopsNotifications()
    {
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var graph = HealthGraph.Create(node);

        var emitted = new List<HealthReport>();
        var subscription = graph.StatusChanged
            .Subscribe(new TestObserver<HealthReport>(emitted.Add));

        subscription.Dispose();

        node.Refresh();

        Assert.Empty(emitted);
    }

    // ── Duplicate node names ─────────────────────────────────────────

    [Fact]
    public void Create_DuplicateNodeNames_ThrowsArgumentException()
    {
        var a = HealthNode.Create("Dup");
        var b = HealthNode.Create("Dup");
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        Assert.Throws<ArgumentException>(() => HealthGraph.Create(root));
    }

    // ── Dispose ──────────────────────────────────────────────────────

    [Fact]
    public void Dispose_StopsStatusChangedNotifications()
    {
        var isHealthy = true;
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var graph = HealthGraph.Create(node);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        graph.Dispose();

        isHealthy = false;
        node.Refresh();

        Assert.Empty(emitted);
    }

    [Fact]
    public void Dispose_CompletesObservers()
    {
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);

        var completed = false;
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(
            _ => { }, () => completed = true));

        graph.Dispose();

        Assert.True(completed);
    }

    [Fact]
    public void Dispose_TopologyChanged_CompletesObservers()
    {
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);

        var completed = false;
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(
            _ => { }, () => completed = true));

        graph.Dispose();

        Assert.True(completed);
    }

    [Fact]
    public void Dispose_SharedNode_OtherGraphStillWorks()
    {
        var isHealthy = true;
        var shared = HealthNode.Create("Shared").WithHealthProbe(
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);

        var root1 = HealthNode.Create("Root1")
            .DependsOn(shared, Importance.Required);
        var root2 = HealthNode.Create("Root2")
            .DependsOn(shared, Importance.Required);

        var graph1 = HealthGraph.Create(root1);
        var graph2 = HealthGraph.Create(root2);

        graph1.Dispose();

        var reports2 = new List<HealthReport>();
        graph2.StatusChanged.Subscribe(new TestObserver<HealthReport>(reports2.Add));

        isHealthy = false;
        shared.Refresh();

        Assert.Single(reports2);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_IsIdempotent()
    {
        var graph = HealthGraph.Create(HealthNode.Create("Svc"));

        graph.Dispose();
        graph.Dispose();
    }
}

/// <summary>Stub for generic TryGetNode tests.</summary>
file class StubHealthAware
{
    public HealthNode HealthNode { get; } = HealthNode.Create(typeof(StubHealthAware).Name);
}

file class TestObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;
    private readonly Action _onCompleted;

    public TestObserver(Action<T> onNext, Action? onCompleted = null)
    {
        _onNext = onNext;
        _onCompleted = onCompleted ?? (() => { });
    }

    public void OnNext(T value) => _onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() => _onCompleted();
}
