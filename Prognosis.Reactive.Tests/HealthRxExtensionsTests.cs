using System.Reactive.Linq;
using System.Reactive.Subjects;
using Prognosis;
using Prognosis.Reactive;

namespace Prognosis.Reactive.Tests;

public class HealthRxExtensionsTests
{
    // ── PollHealthReport (IReadOnlyList<HealthNode>) ────────────────

    [Fact]
    public async Task PollHealthReport_Roots_EmitsReportOnInterval()
    {
        var node = new HealthCheck("Svc");
        var roots = new HealthNode[] { node };

        HealthReport? received = null;
        using var sub = roots
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => received = r);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.NotNull(received);
        Assert.Equal(HealthStatus.Healthy, received!.OverallStatus);
    }

    [Fact]
    public async Task PollHealthReport_Roots_SameState_SuppressesDuplicate()
    {
        var node = new HealthCheck("Svc");
        var roots = new HealthNode[] { node };

        var count = 0;
        using var sub = roots
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(_ => Interlocked.Increment(ref count));

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // DistinctUntilChanged — only one emission despite multiple ticks.
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PollHealthReport_Roots_EmitsOnStateChange()
    {
        var isHealthy = true;
        var node = new HealthCheck("Svc",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var roots = new HealthNode[] { node };

        var reports = new List<HealthReport>();
        using var sub = roots
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => reports.Add(r));

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        isHealthy = false;
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        Assert.True(reports.Count >= 2);
        Assert.Equal(HealthStatus.Healthy, reports[0].OverallStatus);
        Assert.Contains(reports, r => r.OverallStatus == HealthStatus.Unhealthy);
    }

    // ── PollHealthReport (HealthGraph) ──────────────────────────────

    [Fact]
    public async Task PollHealthReport_Graph_EmitsReportOnInterval()
    {
        var node = new HealthCheck("Svc");
        var graph = HealthGraph.Create(node);

        HealthReport? received = null;
        using var sub = graph
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => received = r);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.NotNull(received);
        Assert.Equal(HealthStatus.Healthy, received!.OverallStatus);
    }

    // ── ObserveHealthReport (HealthGraph) ────────────────────────────

    [Fact]
    public async Task ObserveHealthReport_Graph_EmitsOnStatusChange()
    {
        var isHealthy = true;
        var leaf = new HealthCheck("Leaf",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var root = new HealthCheck("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var reports = new List<HealthReport>();
        using var sub = graph
            .ObserveHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => reports.Add(r));

        // Trigger a status change on the leaf.
        isHealthy = false;
        leaf.NotifyChanged();

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.OverallStatus == HealthStatus.Unhealthy);
    }

    // ── ObserveHealthReport (IReadOnlyList<HealthNode>) ─────────────

    [Fact]
    public async Task ObserveHealthReport_Roots_EmitsOnLeafChange()
    {
        var isHealthy = true;
        var leaf = new HealthCheck("Leaf",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var root = new HealthCheck("Root")
            .DependsOn(leaf, Importance.Required);
        var roots = new HealthNode[] { root };

        var reports = new List<HealthReport>();
        using var sub = roots
            .ObserveHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => reports.Add(r));

        isHealthy = false;
        leaf.NotifyChanged();

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.OverallStatus == HealthStatus.Unhealthy);
    }

    // ── SelectServiceChanges ────────────────────────────────────────

    [Fact]
    public void SelectServiceChanges_EmitsStatusChanges()
    {
        var subject = new Subject<HealthReport>();

        var changes = new List<StatusChange>();
        using var sub = subject
            .SelectServiceChanges()
            .Subscribe(c => changes.Add(c));

        var report1 = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy, 0),
        });
        var report2 = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Unhealthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Unhealthy, 0, "down"),
        });

        subject.OnNext(report1);
        subject.OnNext(report2);

        Assert.Single(changes);
        Assert.Equal("Svc", changes[0].Name);
        Assert.Equal(HealthStatus.Healthy, changes[0].Previous);
        Assert.Equal(HealthStatus.Unhealthy, changes[0].Current);
    }

    [Fact]
    public void SelectServiceChanges_NoChange_NoEmission()
    {
        var subject = new Subject<HealthReport>();

        var changes = new List<StatusChange>();
        using var sub = subject
            .SelectServiceChanges()
            .Subscribe(c => changes.Add(c));

        var report = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy, 0),
        });

        subject.OnNext(report);
        subject.OnNext(report);

        Assert.Empty(changes);
    }

    [Fact]
    public void SelectServiceChanges_NewServiceAppears_EmitsChange()
    {
        var subject = new Subject<HealthReport>();

        var changes = new List<StatusChange>();
        using var sub = subject
            .SelectServiceChanges()
            .Subscribe(c => changes.Add(c));

        var report1 = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy,
            Array.Empty<HealthSnapshot>());
        var report2 = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("New", HealthStatus.Healthy, 0),
        });

        subject.OnNext(report1);
        subject.OnNext(report2);

        Assert.Single(changes);
        Assert.Equal("New", changes[0].Name);
        Assert.Equal(HealthStatus.Unknown, changes[0].Previous);
    }

    [Fact]
    public void SelectServiceChanges_FirstReport_NoEmission()
    {
        var subject = new Subject<HealthReport>();

        var changes = new List<StatusChange>();
        using var sub = subject
            .SelectServiceChanges()
            .Subscribe(c => changes.Add(c));

        var report = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Healthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy, 0),
        });

        subject.OnNext(report);

        Assert.Empty(changes);
    }
}
