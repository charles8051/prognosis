using System.Reactive.Linq;
using System.Reactive.Subjects;
using Prognosis;
using Prognosis.Reactive;

namespace Prognosis.Reactive.Tests;

public class HealthRxExtensionsTests
{
    // ── SelectServiceChanges ────────────────────────────────────────

    [Fact]
    public void SelectServiceChanges_EmitsStatusChanges()
    {
        var subject = new Subject<HealthReport>();

        var changes = new List<StatusChange>();
        using var sub = subject
            .SelectHealthChanges()
            .Subscribe(c => changes.Add(c));

        var report1 = new HealthReport(new[]
        {
            new HealthSnapshot("Svc", HealthStatus.Healthy),
        });
        var report2 = new HealthReport(new[]
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
            .SelectHealthChanges()
            .Subscribe(c => changes.Add(c));

        var report = new HealthReport(new[]
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
            .SelectHealthChanges()
            .Subscribe(c => changes.Add(c));

        var report1 = new HealthReport(Array.Empty<HealthSnapshot>());
        var report2 = new HealthReport(new[]
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
            .SelectHealthChanges()
            .Subscribe(c => changes.Add(c));

        var report = new HealthReport(new[]
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
        Assert.All(received!.Nodes, n => Assert.Equal(HealthStatus.Healthy, n.Status));
        Assert.Equal(2, received.Nodes.Count);
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
        Assert.All(reports[0].Nodes, n => Assert.Equal(HealthStatus.Healthy, n.Status));
        Assert.Contains(reports, r => r.Nodes.Any(n => n.Status == HealthStatus.Unhealthy));
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
        graph.Refresh(leaf);

        Assert.Single(reports);
        Assert.Equal(HealthStatus.Unhealthy, reports[0].Nodes.First(n => n.Name == "Leaf").Status);
        Assert.Equal(2, reports[0].Nodes.Count);
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
        graph.Refresh(a);

        Assert.Single(reports);
        Assert.Equal(HealthStatus.Unhealthy, reports[0].Nodes.First(n => n.Name == "A").Status);
    }
}
