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

    // ── ReplaceCheck ─────────────────────────────────────────────────

    [Fact]
    public void ReplaceCheck_SwapsIntrinsicCheck()
    {
        var node = HealthNode.CreateDelegate("Svc",
            () => HealthStatus.Healthy);
        var graph = HealthGraph.Create(node);

        Assert.Equal(HealthStatus.Healthy,
            graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);

        node.ReplaceCheck(() => HealthEvaluation.Unhealthy("mock down"));

        Assert.Equal(HealthStatus.Unhealthy,
            graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void ReplaceCheck_PropagatesUpThroughParent()
    {
        var child = HealthNode.CreateDelegate("Child",
            () => HealthStatus.Healthy);
        var parent = HealthNode.CreateDelegate("Parent")
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
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("down"));
        var node = HealthNode.CreateDelegate("Svc",
            () => HealthStatus.Healthy)
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        // Svc is unhealthy because of its Required dep.
        Assert.Equal(HealthStatus.Unhealthy,
            graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);

        // Swap the intrinsic check — edges remain intact.
        node.ReplaceCheck(() => HealthEvaluation.Degraded("slow"));

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Single(node.Dependencies);
    }

    [Fact]
    public void ReplaceCheck_NullDelegate_ThrowsArgumentNullException()
    {
        var node = HealthNode.CreateDelegate("Svc");

        Assert.Throws<ArgumentNullException>(
            () => node.ReplaceCheck(null!));
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

    // ── ReportStatus ─────────────────────────────────────────────────

    [Fact]
    public void ReportStatus_OverridesCachedEvaluation()
    {
        var node = HealthNode.CreateDelegate("Svc",
            () => HealthStatus.Healthy);
        var graph = HealthGraph.Create(node);

        node.ReportStatus(HealthEvaluation.Unhealthy("externally reported"));

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("externally reported", result.Reason);
    }

    [Fact]
    public void ReportStatus_PropagatesToParent()
    {
        var child = HealthNode.CreateDelegate("Child",
            () => HealthStatus.Healthy);
        var parent = HealthNode.CreateDelegate("Parent")
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
        var node = HealthNode.CreateDelegate("Svc",
            () => HealthStatus.Healthy);
        var graph = HealthGraph.Create(node);

        node.ReportStatus(HealthEvaluation.Unhealthy("transient failure"));
        Assert.Equal(HealthStatus.Unhealthy,
            graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);

        graph.RefreshAll();

        Assert.Equal(HealthStatus.Healthy,
            graph.CreateReport().Nodes.First(n => n.Name == "Svc").Status);
    }

    [Fact]
    public void ReportStatus_AggregatesWithDependencies()
    {
        var dep = HealthNode.CreateDelegate("Dep",
            () => HealthEvaluation.Unhealthy("dep down"));
        var node = HealthNode.CreateDelegate("Svc",
            () => HealthStatus.Healthy)
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(node);

        node.ReportStatus(HealthEvaluation.Degraded("slow"));

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Svc");
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void ReportStatus_NullEvaluation_ThrowsArgumentNullException()
    {
        var node = HealthNode.CreateDelegate("Svc");

        Assert.Throws<ArgumentNullException>(
            () => node.ReportStatus(null!));
    }

    [Fact]
    public void ReportStatus_CrossNodeAttribution_PropagatesCorrectly()
    {
        var internet = HealthNode.CreateDelegate("Internet",
            () => HealthStatus.Healthy);
        var api = HealthNode.CreateDelegate("API",
            () => HealthStatus.Healthy)
            .DependsOn(internet, Importance.Required);
        var cache = HealthNode.CreateDelegate("Cache",
            () => HealthStatus.Healthy)
            .DependsOn(internet, Importance.Required);
        var app = HealthNode.CreateComposite("App")
            .DependsOn(api, Importance.Required)
            .DependsOn(cache, Importance.Required);
        var graph = HealthGraph.Create(app);

        internet.ReportStatus(HealthEvaluation.Unhealthy("connectivity lost"));

        var report = graph.CreateReport();
        Assert.Equal(HealthStatus.Unhealthy,
            report.Nodes.First(n => n.Name == "API").Status);
        Assert.Equal(HealthStatus.Unhealthy,
            report.Nodes.First(n => n.Name == "Cache").Status);
        Assert.Equal(HealthStatus.Unhealthy,
            report.Nodes.First(n => n.Name == "App").Status);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
