using Prognosis;

namespace Prognosis.Tests;

public class AggregationTests
{
    // ── Aggregate ────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_NoDependencies_ReturnsIntrinsic()
    {
        var check = HealthNode.Create("Svc").WithHealthProbe(
            () => HealthEvaluation.Degraded("slow"));
        var graph = HealthGraph.Create(check);

        var result = graph.GetReport().Nodes.First(n => n.Name == "Svc");

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
        var dep = HealthNode.Create("Dep").WithHealthProbe(() => depStatus);
        var parent = HealthNode.Create("Parent")
            .DependsOn(dep, importance);
        var graph = HealthGraph.Create(parent);

        Assert.Equal(expected, graph.GetReport().Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void Aggregate_PicksWorstAcrossMultipleDependencies()
    {
        var healthy = HealthNode.Create("A");
        var degraded = HealthNode.Create("B").WithHealthProbe(() => HealthStatus.Degraded);
        var unhealthy = HealthNode.Create("C").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));

        var parent = HealthNode.Create("Parent")
            .DependsOn(healthy, Importance.Required)
            .DependsOn(degraded, Importance.Required)
            .DependsOn(unhealthy, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var result = graph.GetReport().Nodes.First(n => n.Name == "Parent");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("C", result.Reason!);
    }

    [Fact]
    public void Aggregate_IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = HealthNode.Create("Dep");
        var parent = HealthNode.Create("Parent").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("self broken"))
            .DependsOn(healthy, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var result = graph.GetReport().Nodes.First(n => n.Name == "Parent");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("self broken", result.Reason);
    }

    // ── RefreshAll (snapshot content) ───────────────────────────────

    [Fact]
    public void RefreshAll_ReturnsAllNodes()
    {
        var leaf = HealthNode.Create("Leaf");
        var parent = HealthNode.Create("Parent")
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
        var shared = HealthNode.Create("Shared");
        var a = HealthNode.Create("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(shared, Importance.Required);
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.RefreshAll();
        var names = report.Nodes.Select(s => s.Name).ToList();

        Assert.Single(names, n => n == "Shared");
    }

    // ── GetReport ────────────────────────────────────────────────────

    [Fact]
    public void GetReport_IncludesAllNodes()
    {
        var healthy = HealthNode.Create("A");
        var unhealthy = HealthNode.Create("B").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.Create("Root")
            .DependsOn(healthy, Importance.Required)
            .DependsOn(unhealthy, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.GetReport();

        Assert.Equal(3, report.Nodes.Count);
        Assert.Equal(HealthStatus.Unhealthy, report.Nodes.First(n => n.Name == "B").Status);
    }

    [Fact]
    public void GetReport_SingleHealthyNode_ReturnsSingleNode()
    {
        var graph = HealthGraph.Create(HealthNode.Create("Only"));

        var report = graph.GetReport();

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
        var leaf = HealthNode.Create("Leaf");
        var root = HealthNode.Create("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var cycles = graph.DetectCycles();

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_DirectCycle_Detected()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B").DependsOn(a, Importance.Required);
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
        var leaf = HealthNode.Create("Leaf").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.Create("Root")
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
