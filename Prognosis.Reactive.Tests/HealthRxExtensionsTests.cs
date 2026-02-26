using System.Reactive.Linq;
using System.Reactive.Subjects;
using Prognosis;
using Prognosis.Reactive;

namespace Prognosis.Reactive.Tests;

public class HealthRxExtensionsTests
{
    // ── PollHealthReport (HealthNode) ──────────────────────────────────

    [Fact]
    public async Task PollHealthReport_EmitsReportOnInterval()
    {
        var node = new DelegateHealthNode("Svc");

        HealthReport? received = null;
        using var sub = node
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => received = r);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.NotNull(received);
        Assert.Equal(HealthStatus.Healthy, received!.OverallStatus);
    }

    [Fact]
    public async Task PollHealthReport_SameState_SuppressesDuplicate()
    {
        var node = new DelegateHealthNode("Svc");

        var count = 0;
        using var sub = node
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(_ => Interlocked.Increment(ref count));

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // DistinctUntilChanged — only one emission despite multiple ticks.
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PollHealthReport_EmitsOnStateChange()
    {
        var isHealthy = true;
        var leaf = new DelegateHealthNode("Leaf",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);

        var reports = new List<HealthReport>();
        using var sub = root
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => reports.Add(r));

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        isHealthy = false;
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        Assert.True(reports.Count >= 2);
        Assert.Equal(HealthStatus.Healthy, reports[0].OverallStatus);
        Assert.Contains(reports, r => r.OverallStatus == HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task PollHealthReport_IncludesFullSubtree()
    {
        var leaf = new DelegateHealthNode("Leaf");
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);

        HealthReport? received = null;
        using var sub = root
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => received = r);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.NotNull(received);
        Assert.Equal(2, received!.Services.Count);
    }

    // ── ObserveStatus (HealthNode) ──────────────────────────────────

    [Fact]
    public void ObserveStatus_EmitsEvaluationOnChange()
    {
        var isHealthy = true;
        var node = new DelegateHealthNode("Svc",
            () => isHealthy
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var evals = new List<HealthEvaluation>();
        using var sub = node
            .ObserveStatus()
            .Subscribe(e => evals.Add(e));

        // First notify — emits initial status.
        node.BubbleChange();
        Assert.Single(evals);
        Assert.Equal(HealthStatus.Healthy, evals[0].Status);

        // Status changes — emits new evaluation with reason.
        isHealthy = false;
        node.BubbleChange();
        Assert.Equal(2, evals.Count);
        Assert.Equal(HealthStatus.Unhealthy, evals[1].Status);
        Assert.Equal("down", evals[1].Reason);
    }

    // ── ObserveHealthReport (HealthNode) ─────────────────────────────

    [Fact]
    public void ObserveHealthReport_EmitsOnStatusChange()
    {
        var isHealthy = true;
        var leaf = new DelegateHealthNode("Leaf",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);

        var reports = new List<HealthReport>();
        using var sub = root
            .ObserveHealthReport()
            .Subscribe(r => reports.Add(r));

        // Trigger a status change on the leaf.
        // Propagation is synchronous — the report is emitted before
        // BubbleChange returns.
        isHealthy = false;
        leaf.BubbleChange();

        Assert.Single(reports);
        Assert.Equal(HealthStatus.Unhealthy, reports[0].OverallStatus);
        Assert.Equal(2, reports[0].Services.Count);
    }

    [Fact]
    public void ObserveHealthReport_ReportIncludesFullSubtree()
    {
        var isHealthy = true;
        var leaf = new DelegateHealthNode("Leaf",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var mid = new DelegateHealthNode("Mid").DependsOn(leaf, Importance.Required);
        var root = new DelegateHealthNode("Root").DependsOn(mid, Importance.Required);

        var reports = new List<HealthReport>();
        using var sub = root
            .ObserveHealthReport()
            .Subscribe(r => reports.Add(r));

        // Trigger an actual status change so StatusChanged fires.
        isHealthy = false;
        leaf.BubbleChange();

        Assert.Single(reports);
        var names = reports[0].Services.Select(s => s.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Leaf", "Mid", "Root" }, names);
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
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });
        var report2 = new HealthReport(DateTimeOffset.UtcNow, HealthStatus.Unhealthy, new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Unhealthy, "down"),
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
            new HealthSnapshot("Svc", HealthStatus.Healthy),
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
            new HealthSnapshot("New", HealthStatus.Healthy),
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
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });

        subject.OnNext(report);

        Assert.Empty(changes);
    }

    // ── PollHealthReport (HealthGraph) ─────────────────────────────────

    [Fact]
    public async Task PollHealthReport_Graph_EmitsReportOnInterval()
    {
        var leaf = new DelegateHealthNode("Leaf");
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        HealthReport? received = null;
        using var sub = graph
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => received = r);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.NotNull(received);
        Assert.Equal(HealthStatus.Healthy, received!.OverallStatus);
        Assert.Equal(2, received.Services.Count);
    }

    [Fact]
    public async Task PollHealthReport_Graph_EmitsOnStateChange()
    {
        var isHealthy = true;
        var leaf = new DelegateHealthNode("Leaf",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var reports = new List<HealthReport>();
        using var sub = graph
            .PollHealthReport(TimeSpan.FromMilliseconds(50))
            .Subscribe(r => reports.Add(r));

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        isHealthy = false;
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        Assert.True(reports.Count >= 2);
        Assert.Equal(HealthStatus.Healthy, reports[0].OverallStatus);
        Assert.Contains(reports, r => r.OverallStatus == HealthStatus.Unhealthy);
    }

    // ── ObserveHealthReport (HealthGraph) ──────────────────────────────

    [Fact]
    public void ObserveHealthReport_Graph_EmitsOnStatusChange()
    {
        var isHealthy = true;
        var leaf = new DelegateHealthNode("Leaf",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var root = new DelegateHealthNode("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var reports = new List<HealthReport>();
        using var sub = graph
            .ObserveHealthReport()
            .Subscribe(r => reports.Add(r));

        isHealthy = false;
        leaf.BubbleChange();

        Assert.Single(reports);
        Assert.Equal(HealthStatus.Unhealthy, reports[0].OverallStatus);
        Assert.Equal(2, reports[0].Services.Count);
    }

    [Fact]
    public void ObserveHealthReport_Graph_RootReflectsDescendantChange()
    {
        var isHealthy = true;
        var a = new DelegateHealthNode("A",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        var b = new DelegateHealthNode("B");
        var root = new CompositeHealthNode("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var reports = new List<HealthReport>();
        using var sub = graph
            .ObserveHealthReport()
            .Subscribe(r => reports.Add(r));

        isHealthy = false;
        a.BubbleChange();

        Assert.Single(reports);
        Assert.Equal(HealthStatus.Unhealthy, reports[0].OverallStatus);
    }
}
