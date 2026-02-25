using Prognosis;

namespace Prognosis.Tests;

public class HealthAggregatorTests
{
    // ── Aggregate ────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_NoDependencies_ReturnsIntrinsic()
    {
        var check = new HealthCheck("Svc",
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
        var dep = new HealthCheck("Dep", () => depStatus);
        var parent = new HealthCheck("Parent")
            .DependsOn(dep, importance);

        Assert.Equal(expected, parent.Evaluate().Status);
    }

    [Fact]
    public void Aggregate_PicksWorstAcrossMultipleDependencies()
    {
        var healthy = new HealthCheck("A");
        var degraded = new HealthCheck("B", () => HealthStatus.Degraded);
        var unhealthy = new HealthCheck("C",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var parent = new HealthCheck("Parent")
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
        var healthy = new HealthCheck("Dep");
        var parent = new HealthCheck("Parent",
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
        var leaf = new HealthCheck("Leaf");
        var parent = new HealthCheck("Parent")
            .DependsOn(leaf, Importance.Required);

        var snapshots = HealthAggregator.EvaluateAll(parent);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal("Leaf", snapshots[0].Name);
        Assert.Equal("Parent", snapshots[1].Name);
    }

    [Fact]
    public void EvaluateAll_SharedDependency_AppearsOnce()
    {
        var shared = new HealthCheck("Shared");
        var a = new HealthCheck("A").DependsOn(shared, Importance.Required);
        var b = new HealthCheck("B").DependsOn(shared, Importance.Required);
        var root = new HealthCheck("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        var snapshots = HealthAggregator.EvaluateAll(root);
        var names = snapshots.Select(s => s.Name).ToList();

        Assert.Single(names, n => n == "Shared");
    }

    // ── CreateReport ─────────────────────────────────────────────────

    [Fact]
    public void CreateReport_OverallStatus_IsWorstAcrossRoots()
    {
        var healthy = new HealthCheck("A");
        var unhealthy = new HealthCheck("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var report = HealthAggregator.CreateReport(healthy, unhealthy);

        Assert.Equal(HealthStatus.Unhealthy, report.OverallStatus);
        Assert.Equal(2, report.Services.Count);
    }

    [Fact]
    public void CreateReport_EmptyRoots_ReturnsHealthy()
    {
        var report = HealthAggregator.CreateReport();

        Assert.Equal(HealthStatus.Healthy, report.OverallStatus);
        Assert.Empty(report.Services);
    }

    // ── Diff ─────────────────────────────────────────────────────────

    [Fact]
    public void Diff_DetectsStatusChange()
    {
        var before = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy, 0),
        });
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Unhealthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Unhealthy, 0, "down"),
        });

        var changes = HealthAggregator.Diff(before, after);

        Assert.Single(changes);
        Assert.Equal("Svc", changes[0].Name);
        Assert.Equal(HealthStatus.Healthy, changes[0].Previous);
        Assert.Equal(HealthStatus.Unhealthy, changes[0].Current);
    }

    [Fact]
    public void Diff_NoChanges_ReturnsEmpty()
    {
        var snapshot = new HealthSnapshot("Svc", HealthStatus.Healthy, 0);
        var report = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[] { snapshot });

        var changes = HealthAggregator.Diff(report, report);

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_NewServiceAppears_ReportsUnknownToCurrent()
    {
        var before = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<HealthSnapshot>());
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("New", HealthStatus.Healthy, 0),
        });

        var changes = HealthAggregator.Diff(before, after);

        Assert.Single(changes);
        Assert.Equal(HealthStatus.Unknown, changes[0].Previous);
        Assert.Equal(HealthStatus.Healthy, changes[0].Current);
    }

    [Fact]
    public void Diff_ServiceDisappears_ReportsCurrentToUnknown()
    {
        var before = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("Old", HealthStatus.Healthy, 0),
        });
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<HealthSnapshot>());

        var changes = HealthAggregator.Diff(before, after);

        Assert.Single(changes);
        Assert.Equal("Old", changes[0].Name);
        Assert.Equal(HealthStatus.Healthy, changes[0].Previous);
        Assert.Equal(HealthStatus.Unknown, changes[0].Current);
    }

    // ── DetectCycles ─────────────────────────────────────────────────

    [Fact]
    public void DetectCycles_AcyclicGraph_ReturnsEmpty()
    {
        var leaf = new HealthCheck("Leaf");
        var root = new HealthCheck("Root")
            .DependsOn(leaf, Importance.Required);

        var cycles = HealthAggregator.DetectCycles(root);

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_DirectCycle_Detected()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);

        var cycles = HealthAggregator.DetectCycles(a);

        Assert.Single(cycles);
        Assert.Contains("A", cycles[0]);
        Assert.Contains("B", cycles[0]);
    }

    // ── NotifyGraph ──────────────────────────────────────────────────

    [Fact]
    public void NotifyGraph_CallsNotifyChangedOnAllObservableNodes()
    {
        var leaf = new HealthCheck("Leaf",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new HealthCheck("Root")
            .DependsOn(leaf, Importance.Required);

        var statuses = new List<HealthStatus>();
        leaf.StatusChanged.Subscribe(new TestObserver<HealthStatus>(statuses.Add));

        HealthAggregator.NotifyGraph(root);

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
