using Prognosis;

namespace Prognosis.Tests;

public class AggregationTests
{
    // ── Aggregate ────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_NoDependencies_ReturnsIntrinsic()
    {
        var check = HealthNode.CreateDelegate("Svc",
            () => HealthEvaluation.Degraded("slow"));
        var graph = HealthGraph.Create(check);

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Svc");

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("slow", result.Reason);
    }

    [Theory]
    [InlineData(HealthStatus.Unhealthy, Importance.Required, HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded, Importance.Required, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unknown, Importance.Required, HealthStatus.Unknown)]
    [InlineData(HealthStatus.Healthy, Importance.Required, HealthStatus.Healthy)]
    [InlineData(HealthStatus.Unhealthy, Importance.Important, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Degraded, Importance.Important, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unknown, Importance.Important, HealthStatus.Unknown)]
    [InlineData(HealthStatus.Unhealthy, Importance.Optional, HealthStatus.Healthy)]
    [InlineData(HealthStatus.Degraded, Importance.Optional, HealthStatus.Healthy)]
    public void Aggregate_PropagatesAccordingToImportance(
        HealthStatus depStatus, Importance importance, HealthStatus expected)
    {
        var dep = HealthNode.CreateDelegate("Dep", () => depStatus);
        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(dep, importance);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(expected, graph.CreateReport().Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void Aggregate_PicksWorstAcrossMultipleDependencies()
    {
        var healthy = HealthNode.CreateDelegate("A");
        var degraded = HealthNode.CreateDelegate("B", () => HealthStatus.Degraded);
        var unhealthy = HealthNode.CreateDelegate("C",
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(healthy, Importance.Required)
            .DependsOn(degraded, Importance.Required)
            .DependsOn(unhealthy, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Parent");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("C", result.Reason!);
    }

    [Fact]
    public void Aggregate_IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = HealthNode.CreateDelegate("Dep");
        var parent = HealthNode.CreateDelegate("Parent",
            () => HealthEvaluation.Unhealthy("self broken"))
            .DependsOn(healthy, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var result = graph.CreateReport().Nodes.First(n => n.Name == "Parent");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("self broken", result.Reason);
    }

    // ── RefreshAll (snapshot content) ───────────────────────────────

    [Fact]
    public void RefreshAll_ReturnsAllNodes()
    {
        var leaf = HealthNode.CreateDelegate("Leaf");
        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var report = graph.RefreshAll();

        Assert.Equal(2, report.Nodes.Count);
        Assert.Contains(report.Nodes, n => n.Name == "Leaf");
        Assert.Contains(report.Nodes, n => n.Name == "Parent");
    }

    [Fact]
    public void RefreshAll_SharedDependency_AppearsOnce()
    {
        var shared = HealthNode.CreateDelegate("Shared");
        var a = HealthNode.CreateDelegate("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.CreateDelegate("B").DependsOn(shared, Importance.Required);
        var root = HealthNode.CreateDelegate("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.RefreshAll();
        var names = report.Nodes.Select(s => s.Name).ToList();

        Assert.Single(names, n => n == "Shared");
    }

    // ── CreateReport ─────────────────────────────────────────────────

    [Fact]
    public void CreateReport_IncludesAllNodes()
    {
        var healthy = HealthNode.CreateDelegate("A");
        var unhealthy = HealthNode.CreateDelegate("B",
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.CreateComposite("Root")
            .DependsOn(healthy, Importance.Required)
            .DependsOn(unhealthy, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.CreateReport();

        Assert.Equal(3, report.Nodes.Count);
        Assert.Equal(HealthStatus.Unhealthy, report.Nodes.First(n => n.Name == "B").Status);
    }

    [Fact]
    public void CreateReport_SingleHealthyNode_ReturnsSingleNode()
    {
        var graph = HealthGraph.Create(HealthNode.CreateDelegate("Only"));

        var report = graph.CreateReport();

        var node = Assert.Single(report.Nodes);
        Assert.Equal(HealthStatus.Healthy, node.Status);
    }

    // ── Diff ─────────────────────────────────────────────────────────

    [Fact]
    public void Diff_DetectsStatusChange()
    {
        var before = new HealthReport(
            new HealthSnapshot("Svc", HealthStatus.Healthy),
            new[] { new HealthSnapshot("Svc", HealthStatus.Healthy) });
        var after = new HealthReport(
            new HealthSnapshot("Svc", HealthStatus.Unhealthy, "down"),
            new[] { new HealthSnapshot("Svc", HealthStatus.Unhealthy, "down") });

        var changes = before.DiffTo(after);

        Assert.Single(changes);
        Assert.Equal("Svc", changes[0].Name);
        Assert.Equal(HealthStatus.Healthy, changes[0].Previous);
        Assert.Equal(HealthStatus.Unhealthy, changes[0].Current);
    }

    [Fact]
    public void Diff_NoChanges_ReturnsEmpty()
    {
        var snapshot = new HealthSnapshot("Svc", HealthStatus.Healthy);
        var report = new HealthReport(snapshot, new[] { snapshot });

        var changes = report.DiffTo(report);

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_NewServiceAppears_ReportsUnknownToCurrent()
    {
        var before = new HealthReport(
            new HealthSnapshot("Root", HealthStatus.Healthy),
            Array.Empty<HealthSnapshot>());
        var after = new HealthReport(
            new HealthSnapshot("Root", HealthStatus.Healthy),
            new[] { new HealthSnapshot("New", HealthStatus.Healthy) });

        var changes = before.DiffTo(after);

        Assert.Single(changes);
        Assert.Equal(HealthStatus.Unknown, changes[0].Previous);
        Assert.Equal(HealthStatus.Healthy, changes[0].Current);
    }

    [Fact]
    public void Diff_ServiceDisappears_ReportsCurrentToUnknown()
    {
        var before = new HealthReport(
            new HealthSnapshot("Root", HealthStatus.Healthy),
            new[] { new HealthSnapshot("Old", HealthStatus.Healthy) });
        var after = new HealthReport(
            new HealthSnapshot("Root", HealthStatus.Healthy),
            Array.Empty<HealthSnapshot>());

        var changes = before.DiffTo(after);

        Assert.Single(changes);
        Assert.Equal("Old", changes[0].Name);
        Assert.Equal(HealthStatus.Healthy, changes[0].Previous);
        Assert.Equal(HealthStatus.Unknown, changes[0].Current);
    }

    // ── DetectCycles ─────────────────────────────────────────────────

    [Fact]
    public void DetectCycles_AcyclicGraph_ReturnsEmpty()
    {
        var leaf = HealthNode.CreateDelegate("Leaf");
        var root = HealthNode.CreateDelegate("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var cycles = graph.DetectCycles();

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_DirectCycle_Detected()
    {
        var a = HealthNode.CreateDelegate("A");
        var b = HealthNode.CreateDelegate("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(a);

        var cycles = graph.DetectCycles();

        Assert.Single(cycles);
        Assert.Contains("A", cycles[0]);
        Assert.Contains("B", cycles[0]);
    }

    // ── NotifyAll ────────────────────────────────────────────────────

    [Fact]
    public void RefreshAll_ReevaluatesAllNodes()
    {
        var leaf = HealthNode.CreateDelegate("Leaf",
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.CreateDelegate("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var reports = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(reports.Add));

        graph.RefreshAll();

        Assert.Single(reports);
        Assert.Equal(HealthStatus.Unhealthy,
            reports[0].Nodes.First(n => n.Name == "Leaf").Status);
    }
}

/// <summary>Minimal IObserver for testing.</summary>
file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
