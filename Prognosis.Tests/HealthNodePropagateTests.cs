namespace Prognosis.Tests;

public class HealthNodePropagateTests
{
    // ── StatusChanged basics ─────────────────────────────────────────

    [Fact]
    public void NotifyChange_EmitsReportOnStatusChange()
    {
        var node = HealthNode.CreateDelegate("Node",
            () => HealthEvaluation.Unhealthy("down"));
        var graph = HealthGraph.Create(node);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        graph.NotifyChange(node);

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0].Nodes[0].Status);
    }

    [Fact]
    public void NotifyChange_PropagatesFromChildToParent()
    {
        var isUnhealthy = false;
        var leaf = HealthNode.CreateDelegate("Leaf",
            () => isUnhealthy ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        // Leaf degrades after the edge was wired. NotifyChange bubbles it up.
        isUnhealthy = true;
        graph.NotifyChange(leaf);

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy,
            emitted[0].Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void NotifyChange_PropagatesThroughChain()
    {
        var isUnhealthy = false;
        var leaf = HealthNode.CreateDelegate("Leaf",
            () => isUnhealthy ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        var middle = HealthNode.CreateDelegate("Middle")
            .DependsOn(leaf, Importance.Required);
        var root = HealthNode.CreateDelegate("Root")
            .DependsOn(middle, Importance.Required);
        var graph = HealthGraph.Create(root);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        isUnhealthy = true;
        graph.NotifyChange(leaf);

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy,
            emitted[0].Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void NotifyChange_Diamond_EmitsExactlyOneReport()
    {
        // leaf → A and leaf → B, both → root (diamond shape)
        var isUnhealthy = false;
        var leaf = HealthNode.CreateDelegate("Leaf",
            () => isUnhealthy ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        var a = HealthNode.CreateDelegate("A")
            .DependsOn(leaf, Importance.Required);
        var b = HealthNode.CreateDelegate("B")
            .DependsOn(leaf, Importance.Required);
        var root = HealthNode.CreateComposite("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        isUnhealthy = true;
        graph.NotifyChange(leaf);

        // Root has two paths from the leaf but should emit exactly one report.
        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy,
            emitted[0].Nodes.First(n => n.Name == "Root").Status);
    }

    [Fact]
    public void NotifyChange_Cycle_DoesNotStackOverflow()
    {
        var a = HealthNode.CreateDelegate("A");
        var b = HealthNode.CreateDelegate("B")
            .DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(a);

        // Should not throw or hang.
        var exception = Record.Exception(() => graph.NotifyChange(a));
        Assert.Null(exception);
    }

    [Fact]
    public void NotifyChange_NoParents_EmitsReport()
    {
        var node = HealthNode.CreateDelegate("Lone",
            () => HealthEvaluation.Degraded("slow"));
        var graph = HealthGraph.Create(node);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        graph.NotifyChange(node);

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Degraded, emitted[0].Nodes[0].Status);
    }

    // ── DependsOn / RemoveDependency auto-propagation ─────────────────

    [Fact]
    public void DependsOn_ImmediatelyEmitsReport()
    {
        var child = HealthNode.CreateDelegate("Child",
            () => HealthEvaluation.Unhealthy("down"));
        var parent = HealthNode.CreateDelegate("Parent");
        var graph = HealthGraph.Create(parent);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        // The child is already Unhealthy. Wiring the edge surfaces that status
        // to the parent immediately without requiring a poll.
        parent.DependsOn(child, Importance.Required);

        Assert.True(emitted.Count >= 1);
        var lastReport = emitted.Last();
        Assert.Equal(HealthStatus.Unhealthy,
            lastReport.Nodes.First(n => n.Name == "Parent").Status);
    }

    [Fact]
    public void DependsOn_PropagatesUpThroughGrandparent()
    {
        var child = HealthNode.CreateDelegate("Child",
            () => HealthEvaluation.Unhealthy("down"));
        var parent = HealthNode.CreateDelegate("Parent");
        var grandparent = HealthNode.CreateDelegate("Grandparent")
            .DependsOn(parent, Importance.Required);
        var graph = HealthGraph.Create(grandparent);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        // Wiring parent → child propagates all the way up to grandparent.
        parent.DependsOn(child, Importance.Required);

        Assert.True(emitted.Count >= 1);
        var lastReport = emitted.Last();
        Assert.Equal(HealthStatus.Unhealthy,
            lastReport.Nodes.First(n => n.Name == "Grandparent").Status);
    }

    [Fact]
    public void RemoveDependency_ImmediatelyEmitsReport()
    {
        var child = HealthNode.CreateDelegate("Child",
            () => HealthEvaluation.Unhealthy("down"));
        var parent = HealthNode.CreateDelegate("Parent")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(parent);

        var emitted = new List<HealthReport>();
        graph.StatusChanged.Subscribe(new TestObserver<HealthReport>(emitted.Add));

        // Removing the edge immediately restores Healthy without polling.
        parent.RemoveDependency(child);

        Assert.True(emitted.Count >= 1);
        var lastReport = emitted.Last();
        Assert.Equal(HealthStatus.Healthy,
            lastReport.Nodes.First(n => n.Name == "Parent").Status);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
