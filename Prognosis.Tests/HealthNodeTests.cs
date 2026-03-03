namespace Prognosis.Tests;

public class HealthNodeTests
{
    // ── Evaluation (via HealthGraph.GetReport) ─────────────────────────

    [Fact]
    public void Evaluate_NoDependencies_ReturnsIntrinsicCheck()
    {
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthEvaluation.Degraded("slow"));
        var graph = HealthGraph.Create(node);

        var result = graph.GetReport().Nodes.First(n => n.Name == "Svc");

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("slow", result.Reason);
    }

    [Fact]
    public void Evaluate_WithDependency_AggregatesCorrectly()
    {
        var dep = HealthNode.Create("Dep").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.Create("Svc")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        var result = graph.GetReport().Nodes.First(n => n.Name == "Svc");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ── DependsOn ────────────────────────────────────────────────────

    [Fact]
    public void DependsOn_AddsDependency()
    {
        var node = HealthNode.Create("Svc");
        var dep = HealthNode.Create("Dep");

        node.DependsOn(dep, Importance.Important);

        Assert.Single(node.Dependencies);
        Assert.Equal("Dep", node.Dependencies[0].Node.Name);
        Assert.Equal(Importance.Important, node.Dependencies[0].Importance);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForChaining()
    {
        var node = HealthNode.Create("Svc");
        var dep = HealthNode.Create("Dep");

        var returned = node.DependsOn(dep, Importance.Required);

        Assert.Same(node, returned);
    }

    [Fact]
    public void DependsOn_AfterGraphCreation_IsAllowed()
    {
        var dep = HealthNode.Create("Dep").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.Create("Svc");
        var graph = HealthGraph.Create(node);

        // Check initial state.
        var before = graph.GetReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Healthy, before.Status);

        // Adding an edge at runtime now works.
        node.DependsOn(dep, Importance.Required);
        var after = graph.GetReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, after.Status);
    }

    [Fact]
    public void RemoveDependency_DetachesEdge()
    {
        var dep = HealthNode.Create("Dep").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.Create("Svc")
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);

        var removed = node.RemoveDependency(dep);

        Assert.True(removed);
        Assert.Empty(node.Dependencies);
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void RemoveDependency_UnknownService_ReturnsFalse()
    {
        var node = HealthNode.Create("Svc");
        var unknown = HealthNode.Create("Unknown");

        Assert.False(node.RemoveDependency(unknown));
    }

    // ── Circular dependency guard ────────────────────────────────────

    [Fact]
    public void Evaluate_CircularDependency_DoesNotStackOverflow()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(a);

        a.Refresh();
        var result = graph.GetReport().Nodes.First(n => n.Name == "A");

        // Cycle is handled by propagation guard — the node evaluates
        // without stack overflow and returns a cached result.
        Assert.True(result.Status is HealthStatus.Healthy or HealthStatus.Unhealthy);
    }

    // ── Factory validation ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NullOrEmptyName_ThrowsArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(() => HealthNode.Create(name!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithCheck_NullOrEmptyName_ThrowsArgumentException(string? name)
    {
        Assert.Throws<ArgumentException>(
            () => HealthNode.Create(name!).WithHealthProbe(() => HealthStatus.Healthy));
    }

    // ── Duplicate edge guard ─────────────────────────────────────────

    [Fact]
    public void DependsOn_SameNodeTwice_ThrowsInvalidOperationException()
    {
        var dep = HealthNode.Create("Dep");
        var node = HealthNode.Create("Svc")
            .DependsOn(dep, Importance.Required);

        Assert.Throws<InvalidOperationException>(
            () => node.DependsOn(dep, Importance.Optional));
    }

    // ── ReplaceCheck ─────────────────────────────────────────────────

    [Fact]
    public void ReplaceCheck_SwapsIntrinsicCheck()
    {
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthStatus.Healthy);
        var graph = HealthGraph.Create(node);

        Assert.Equal(HealthStatus.Healthy,
            graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);

        node.ReplaceCheck(() => HealthEvaluation.Unhealthy("mock down"));

        Assert.Equal(HealthStatus.Unhealthy,
            graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void ReplaceCheck_PropagatesUpThroughParent()
    {
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => HealthStatus.Healthy);
        var parent = HealthNode.Create("Parent")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        child.ReplaceCheck(() => HealthEvaluation.Unhealthy("swapped"));

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy,
            emitted[0].Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void ReplaceCheck_PreservesEdges()
    {
        var dep = HealthNode.Create("Dep").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthStatus.Healthy)
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        // Svc is unhealthy because of its Required dep.
        Assert.Equal(HealthStatus.Unhealthy,
            graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);

        // Swap the intrinsic check — edges remain intact.
        node.ReplaceCheck(() => HealthEvaluation.Degraded("slow"));

        var result = graph.GetReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Single(node.Dependencies);
    }

    [Fact]
    public void ReplaceCheck_NullDelegate_ThrowsArgumentNullException()
    {
        var node = HealthNode.Create("Svc");

        Assert.Throws<ArgumentNullException>(
            () => node.ReplaceCheck(null!));
    }

    [Fact]
    public void DependsOn_DifferentNodes_Allowed()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");
        var node = HealthNode.Create("Svc")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        Assert.Equal(2, node.Dependencies.Count);
    }

    // ── ReportStatus ─────────────────────────────────────────────────

    [Fact]
    public void ReportStatus_OverridesCachedEvaluation()
    {
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthStatus.Healthy);
        var graph = HealthGraph.Create(node);

        node.ReportStatus(HealthEvaluation.Unhealthy("externally reported"));

        var result = graph.GetReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("externally reported", result.Reason);
    }

    [Fact]
    public void ReportStatus_PropagatesToParent()
    {
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => HealthStatus.Healthy);
        var parent = HealthNode.Create("Parent")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        child.ReportStatus(HealthEvaluation.Unhealthy("connectivity lost"));

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy,
            emitted[0].Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void ReportStatus_ExpiredByNextRefresh()
    {
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthStatus.Healthy);
        var graph = HealthGraph.Create(node);

        node.ReportStatus(HealthEvaluation.Unhealthy("transient failure"));
        Assert.Equal(HealthStatus.Unhealthy,
            graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);

        graph.RefreshAll();

        Assert.Equal(HealthStatus.Healthy,
            graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void ReportStatus_AggregatesWithDependencies()
    {
        var dep = HealthNode.Create("Dep").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("dep down"));
        var node = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthStatus.Healthy)
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        node.ReportStatus(HealthEvaluation.Degraded("slow"));

        var result = graph.GetReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void ReportStatus_NullEvaluation_ThrowsArgumentNullException()
    {
        var node = HealthNode.Create("Svc");

        Assert.Throws<ArgumentNullException>(
            () => node.ReportStatus(null!));
    }

    [Fact]
    public void ReportStatus_CrossNodeAttribution_PropagatesCorrectly()
    {
        var internet = HealthNode.Create("Internet").WithHealthProbe(
            () => HealthStatus.Healthy);
        var api = HealthNode.Create("API").WithHealthProbe(
            () => HealthStatus.Healthy)
            .DependsOn(internet, Importance.Required);
        var cache = HealthNode.Create("Cache").WithHealthProbe(
            () => HealthStatus.Healthy)
            .DependsOn(internet, Importance.Required);
        var app = HealthNode.Create("App")
            .DependsOn(api, Importance.Required)
            .DependsOn(cache, Importance.Required);
        var graph = HealthGraph.Create(app);

        internet.ReportStatus(HealthEvaluation.Unhealthy("connectivity lost"));

        var report = graph.GetReport();
        Assert.Equal(HealthStatus.Unhealthy,
            report.Nodes.First(n => n.Name == "API").Status);
        Assert.Equal(HealthStatus.Unhealthy,
            report.Nodes.First(n => n.Name == "Cache").Status);
        Assert.Equal(HealthStatus.Unhealthy,
            report.Nodes.First(n => n.Name == "App").Status);
    }

    // ── ReplaceDependencies ──────────────────────────────────────────

    [Fact]
    public void ReplaceDependencies_SwapsDependencySet()
    {
        var realDb = HealthNode.Create("RealDb").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var mockDb = HealthNode.Create("MockDb");
        var node = HealthNode.Create("Svc")
            .DependsOn(realDb, Importance.Required);
        var graph = HealthGraph.Create(node);

        Assert.Equal(HealthStatus.Unhealthy,
            graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);

        node.ReplaceDependencies([(mockDb, Importance.Required)]);

        Assert.Single(node.Dependencies);
        Assert.Same(mockDb, node.Dependencies[0].Node);
        Assert.Equal(HealthStatus.Healthy,
            graph.GetReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void ReplaceDependencies_ClearsParentBackReferences()
    {
        var dep = HealthNode.Create("Dep");
        var node = HealthNode.Create("Svc")
            .DependsOn(dep, Importance.Required);

        Assert.Single(dep.Parents);

        node.ReplaceDependencies([]);

        Assert.Empty(dep.Parents);
    }

    [Fact]
    public void ReplaceDependencies_AddsParentBackReferences()
    {
        var node = HealthNode.Create("Svc");
        var dep = HealthNode.Create("Dep");

        node.ReplaceDependencies([(dep, Importance.Important)]);

        Assert.Single(dep.Parents);
        Assert.Same(node, dep.Parents[0]);
    }

    [Fact]
    public void ReplaceDependencies_RetainedNodeKeepsParentBackReference()
    {
        var kept = HealthNode.Create("Kept");
        var removed = HealthNode.Create("Removed");
        var added = HealthNode.Create("Added");
        var node = HealthNode.Create("Svc")
            .DependsOn(kept, Importance.Required)
            .DependsOn(removed, Importance.Required);

        node.ReplaceDependencies([
            (kept, Importance.Important),
            (added, Importance.Required)
        ]);

        Assert.Single(kept.Parents);
        Assert.Empty(removed.Parents);
        Assert.Single(added.Parents);
        Assert.Equal(2, node.Dependencies.Count);
    }

    [Fact]
    public void ReplaceDependencies_EmptyList_RemovesAll()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");
        var node = HealthNode.Create("Svc")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        node.ReplaceDependencies([]);

        Assert.Empty(node.Dependencies);
        Assert.Empty(a.Parents);
        Assert.Empty(b.Parents);
    }

    [Fact]
    public void ReplaceDependencies_PropagatesUpThroughParent()
    {
        var realDep = HealthNode.Create("Real").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var mockDep = HealthNode.Create("Mock");
        var child = HealthNode.Create("Child")
            .DependsOn(realDep, Importance.Required);
        var parent = HealthNode.Create("Parent")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(HealthStatus.Unhealthy,
            graph.GetReport().Nodes.First(n => n.Name == "Parent").Status);

        child.ReplaceDependencies([(mockDep, Importance.Required)]);

        Assert.Equal(HealthStatus.Healthy,
            graph.GetReport().Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void ReplaceDependencies_UpdatesGraphTopology()
    {
        var realDep = HealthNode.Create("Real");
        var mockDep = HealthNode.Create("Mock");
        var node = HealthNode.Create("Svc")
            .DependsOn(realDep, Importance.Required);
        var graph = HealthGraph.Create(node);

        Assert.True(graph.TryGetNode("Real", out _));
        Assert.False(graph.TryGetNode("Mock", out _));

        node.ReplaceDependencies([(mockDep, Importance.Required)]);

        Assert.False(graph.TryGetNode("Real", out _));
        Assert.True(graph.TryGetNode("Mock", out _));
    }

    [Fact]
    public void ReplaceDependencies_EmitsTopologyChanged()
    {
        var realDep = HealthNode.Create("Real");
        var mockDep = HealthNode.Create("Mock");
        var node = HealthNode.Create("Svc")
            .DependsOn(realDep, Importance.Required);
        var graph = HealthGraph.Create(node);

        var changes = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(
            new TestObserver<TopologyChange>(changes.Add));

        node.ReplaceDependencies([(mockDep, Importance.Required)]);

        Assert.Single(changes);
        Assert.Contains(changes[0].Added, n => n.Name == "Mock");
        Assert.Contains(changes[0].Removed, n => n.Name == "Real");
    }

    [Fact]
    public void ReplaceDependencies_NullArgument_ThrowsArgumentNullException()
    {
        var node = HealthNode.Create("Svc");

        Assert.Throws<ArgumentNullException>(
            () => node.ReplaceDependencies(null!));
    }

    [Fact]
    public void ReplaceDependencies_DuplicateNodes_ThrowsArgumentException()
    {
        var dep = HealthNode.Create("Dep");
        var node = HealthNode.Create("Svc");

        Assert.Throws<ArgumentException>(
            () => node.ReplaceDependencies([
                (dep, Importance.Required),
                (dep, Importance.Optional)
            ]));
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
