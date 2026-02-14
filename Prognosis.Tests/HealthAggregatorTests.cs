using Prognosis;

namespace Prognosis.Tests;

public class HealthAggregatorTests
{
    // ── Aggregate ────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_NoDependencies_ReturnsIntrinsic()
    {
        var result = HealthAggregator.Aggregate(
            new HealthEvaluation(HealthStatus.Degraded, "slow"),
            Array.Empty<ServiceDependency>());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("slow", result.Reason);
    }

    [Theory]
    [InlineData(HealthStatus.Unhealthy, ServiceImportance.Required, HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded, ServiceImportance.Required, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unknown, ServiceImportance.Required, HealthStatus.Unknown)]
    [InlineData(HealthStatus.Healthy, ServiceImportance.Required, HealthStatus.Healthy)]
    [InlineData(HealthStatus.Unhealthy, ServiceImportance.Important, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Degraded, ServiceImportance.Important, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unknown, ServiceImportance.Important, HealthStatus.Unknown)]
    [InlineData(HealthStatus.Unhealthy, ServiceImportance.Optional, HealthStatus.Healthy)]
    [InlineData(HealthStatus.Degraded, ServiceImportance.Optional, HealthStatus.Healthy)]
    public void Aggregate_PropagatesAccordingToImportance(
        HealthStatus depStatus, ServiceImportance importance, HealthStatus expected)
    {
        var dep = new DelegatingServiceHealth("Dep", () => depStatus);
        var result = HealthAggregator.Aggregate(
            HealthStatus.Healthy,
            new[] { new ServiceDependency(dep, importance) });

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Aggregate_PicksWorstAcrossMultipleDependencies()
    {
        var healthy = new DelegatingServiceHealth("A");
        var degraded = new DelegatingServiceHealth("B", () => HealthStatus.Degraded);
        var unhealthy = new DelegatingServiceHealth("C",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var result = HealthAggregator.Aggregate(HealthStatus.Healthy, new[]
        {
            new ServiceDependency(healthy, ServiceImportance.Required),
            new ServiceDependency(degraded, ServiceImportance.Required),
            new ServiceDependency(unhealthy, ServiceImportance.Required),
        });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("C", result.Reason!);
    }

    [Fact]
    public void Aggregate_IntrinsicWorseThanDeps_IntrinsicWins()
    {
        var healthy = new DelegatingServiceHealth("Dep");
        var result = HealthAggregator.Aggregate(
            new HealthEvaluation(HealthStatus.Unhealthy, "self broken"),
            new[] { new ServiceDependency(healthy, ServiceImportance.Required) });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("self broken", result.Reason);
    }

    // ── EvaluateAll ──────────────────────────────────────────────────

    [Fact]
    public void EvaluateAll_ReturnsPostOrder_LeavesBeforeParents()
    {
        var leaf = new DelegatingServiceHealth("Leaf");
        var parent = new DelegatingServiceHealth("Parent")
            .DependsOn(leaf, ServiceImportance.Required);

        var snapshots = HealthAggregator.EvaluateAll(parent);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal("Leaf", snapshots[0].Name);
        Assert.Equal("Parent", snapshots[1].Name);
    }

    [Fact]
    public void EvaluateAll_SharedDependency_AppearsOnce()
    {
        var shared = new DelegatingServiceHealth("Shared");
        var a = new DelegatingServiceHealth("A").DependsOn(shared, ServiceImportance.Required);
        var b = new DelegatingServiceHealth("B").DependsOn(shared, ServiceImportance.Required);
        var root = new DelegatingServiceHealth("Root")
            .DependsOn(a, ServiceImportance.Required)
            .DependsOn(b, ServiceImportance.Required);

        var snapshots = HealthAggregator.EvaluateAll(root);
        var names = snapshots.Select(s => s.Name).ToList();

        Assert.Single(names, n => n == "Shared");
    }

    // ── CreateReport ─────────────────────────────────────────────────

    [Fact]
    public void CreateReport_OverallStatus_IsWorstAcrossRoots()
    {
        var healthy = new DelegatingServiceHealth("A");
        var unhealthy = new DelegatingServiceHealth("B",
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
            new ServiceSnapshot("Svc", HealthStatus.Healthy, 0),
        });
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Unhealthy, new[]
        {
            new ServiceSnapshot("Svc", HealthStatus.Unhealthy, 0, "down"),
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
        var snapshot = new ServiceSnapshot("Svc", HealthStatus.Healthy, 0);
        var report = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[] { snapshot });

        var changes = HealthAggregator.Diff(report, report);

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_NewServiceAppears_ReportsUnknownToCurrent()
    {
        var before = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<ServiceSnapshot>());
        var after = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new ServiceSnapshot("New", HealthStatus.Healthy, 0),
        });

        var changes = HealthAggregator.Diff(before, after);

        Assert.Single(changes);
        Assert.Equal(HealthStatus.Unknown, changes[0].Previous);
        Assert.Equal(HealthStatus.Healthy, changes[0].Current);
    }

    // ── DetectCycles ─────────────────────────────────────────────────

    [Fact]
    public void DetectCycles_AcyclicGraph_ReturnsEmpty()
    {
        var leaf = new DelegatingServiceHealth("Leaf");
        var root = new DelegatingServiceHealth("Root")
            .DependsOn(leaf, ServiceImportance.Required);

        var cycles = HealthAggregator.DetectCycles(root);

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_DirectCycle_Detected()
    {
        var a = new DelegatingServiceHealth("A");
        var b = new DelegatingServiceHealth("B").DependsOn(a, ServiceImportance.Required);
        a.DependsOn(b, ServiceImportance.Required);

        var cycles = HealthAggregator.DetectCycles(a);

        Assert.Single(cycles);
        Assert.Contains("A", cycles[0]);
        Assert.Contains("B", cycles[0]);
    }

    // ── NotifyGraph ──────────────────────────────────────────────────

    [Fact]
    public void NotifyGraph_CallsNotifyChangedOnAllObservableNodes()
    {
        var leaf = new DelegatingServiceHealth("Leaf",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var root = new DelegatingServiceHealth("Root")
            .DependsOn(leaf, ServiceImportance.Required);

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
