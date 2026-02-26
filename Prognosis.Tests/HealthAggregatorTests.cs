using Prognosis;

namespace Prognosis.Tests;

public class AggregationTests
{
    // ── Aggregate ────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_NoDependencies_ReturnsIntrinsic()
    {
        var check = new DelegateHealthNode("Svc",
            () => new HealthEvaluation(HealthStatus.Degraded, "slow"));

        var result = check.Evaluate();

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
        var dep = new DelegateHealthNode("Dep", () => depStatus);
        var parent = new DelegateHealthNode("Parent")
            .DependsOn(dep, importance);

        Assert.Equal(expected, parent.Evaluate().Status);
    }

    [Fact]
    public void Aggregate_PicksWorstAcrossMultipleDependencies()
    {
        var healthy = new DelegateHealthNode("A");
        var degraded = new DelegateHealthNode("B", () => HealthStatus.Degraded);
        var unhealthy = new DelegateHealthNode("C",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var parent = new DelegateHealthNode("Parent")
            .DependsOn(healthy, Importance.Required)
            .DependsOn(degraded, Importance.Required)
            .DependsOn(unhealthy, Importance.Required);

        var result = parent.Evaluate();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("C", result.Reason!);
    }

    [Fact]
    public void Aggregate_IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = new DelegateHealthNode("Dep");
        var parent = new DelegateHealthNode("Parent",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "self broken"))
            .DependsOn(healthy, Importance.Required);

        var result = parent.Evaluate();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("self broken", result.Reason);
    }

    // ── EvaluateAll ──────────────────────────────────────────────────

    [Fact]
    public void EvaluateAll_ReturnsPostOrder_LeavesBeforeParents()
    {
        var leaf = new DelegateHealthNode("Leaf");
        var parent = new DelegateHealthNode("Parent")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var snapshots = graph.EvaluateAll();

        Assert.Equal(2, snapshots.Count);
        Assert.Equal("Leaf", snapshots[0].Name);
        Assert.Equal("Parent", snapshots[1].Name);
    }

    [Fact]
    public void EvaluateAll_SharedDependency_AppearsOnce()
    {
        var shared = new DelegateHealthNode("Shared");
        var a = new DelegateHealthNode("A").DependsOn(shared, Importance.Required);
        var b = new DelegateHealthNode("B").DependsOn(shared, Importance.Required);
        var root = new DelegateHealthNode("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var snapshots = graph.EvaluateAll();
        var names = snapshots.Select(s => s.Name).ToList();

        Assert.Single(names, n => n == "Shared");
    }

    // ── CreateReport ─────────────────────────────────────────────────

    [Fact]
    public void CreateReport_OverallStatus_IsWorstAcrossChildren()
    {
        var healthy = new DelegateHealthNode("A");
        var unhealthy = new DelegateHealthNode("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new CompositeHealthNode("Root")
            .DependsOn(healthy, Importance.Required)
            .DependsOn(unhealthy, Importance.Required);
        var graph = HealthGraph.Create(root);

        var report = graph.CreateReport();

        Assert.Equal(HealthStatus.Unhealthy, report.OverallStatus);
        Assert.Equal(3, report.Services.Count);
    }

    [Fact]
    public void CreateReport_SingleHealthyNode_ReturnsHealthy()
    {
        var graph = HealthGraph.Create(new DelegateHealthNode("Only"));

        var report = graph.CreateReport();

        Assert.Equal(HealthStatus.Healthy, report.OverallStatus);
        Assert.Single(report.Services);
    }

    // ── Diff ─────────────────────────────────────────────────────────

    [Fact]
    public void Diff_DetectsStatusChange()
    {
        var before = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Unhealthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Unhealthy, "down"),
        });

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
        var report = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[] { snapshot });

        var changes = report.DiffTo(report);

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_NewServiceAppears_ReportsUnknownToCurrent()
    {
        var before = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<HealthSnapshot>());
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("New", HealthStatus.Healthy),
        });

        var changes = before.DiffTo(after);

        Assert.Single(changes);
        Assert.Equal(HealthStatus.Unknown, changes[0].Previous);
        Assert.Equal(HealthStatus.Healthy, changes[0].Current);
    }

    [Fact]
    public void Diff_ServiceDisappears_ReportsCurrentToUnknown()
    {
        var before = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("Old", HealthStatus.Healthy),
        });
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
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
        var leaf = new DelegateHealthNode("Leaf");
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var cycles = graph.DetectCycles();

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_DirectCycle_Detected()
    {
        var a = new DelegateHealthNode("A");
        var b = new DelegateHealthNode("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(a);

        var cycles = graph.DetectCycles();

        Assert.Single(cycles);
        Assert.Contains("A", cycles[0]);
        Assert.Contains("B", cycles[0]);
    }

    // ── NotifyAll ────────────────────────────────────────────────────

    [Fact]
    public void NotifyAll_CallsBubbleChangeOnAllObservableNodes()
    {
        var leaf = new DelegateHealthNode("Leaf",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var statuses = new List<HealthStatus>();
        leaf.StatusChanged.Subscribe(new TestObserver<HealthStatus>(statuses.Add));

        graph.NotifyAll();

        Assert.Single(statuses);
        Assert.Equal(HealthStatus.Unhealthy, statuses[0]);
    }
}

/// <summary>Minimal IObserver for testing.</summary>
file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
