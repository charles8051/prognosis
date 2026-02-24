namespace Prognosis.Tests;

public class HealthNodePropagateTests
{
    // ── PropagateChange basics ────────────────────────────────────────

    [Fact]
    public void PropagateChange_NotifiesSelf()
    {
        var node = new HealthCheck("Node",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var emitted = new List<HealthStatus>();
        node.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        node.PropagateChange();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void PropagateChange_NotifiesParent()
    {
        var isUnhealthy = false;
        var leaf = new HealthCheck("Leaf",
            () => isUnhealthy ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        var parent = new HealthCheck("Parent");
        parent.DependsOn(leaf, Importance.Required);
        // DependsOn propagates immediately: parent._lastEmitted = Healthy (leaf was Healthy at wire time).

        var emitted = new List<HealthStatus>();
        parent.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        // Leaf degrades after the edge was wired. PropagateChange bubbles it up.
        isUnhealthy = true;
        leaf.PropagateChange();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void PropagateChange_PropagatesThroughChain()
    {
        var isUnhealthy = false;
        var leaf = new HealthCheck("Leaf",
            () => isUnhealthy ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        var middle = new HealthCheck("Middle");
        var root = new HealthCheck("Root");
        middle.DependsOn(leaf, Importance.Required);
        root.DependsOn(middle, Importance.Required);
        // After wiring, root._lastEmitted = Healthy.

        var emitted = new List<HealthStatus>();
        root.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        isUnhealthy = true;
        leaf.PropagateChange();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void PropagateChange_Diamond_RootReceivesExactlyOneEmission()
    {
        // leaf → A and leaf → B, both → root (diamond shape)
        var isUnhealthy = false;
        var leaf = new HealthCheck("Leaf",
            () => isUnhealthy ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        var a = new HealthCheck("A");
        var b = new HealthCheck("B");
        var root = new HealthCheck("Root");

        a.DependsOn(leaf, Importance.Required);
        b.DependsOn(leaf, Importance.Required);
        root.DependsOn(a, Importance.Required);
        root.DependsOn(b, Importance.Required);
        // After wiring, root._lastEmitted = Healthy.

        var emitted = new List<HealthStatus>();
        root.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        isUnhealthy = true;
        leaf.PropagateChange();

        // Root has two paths from the leaf but should emit exactly once.
        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void PropagateChange_Cycle_DoesNotStackOverflow()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B");

        // Deliberately create a cycle: A → B → A.
        a.DependsOn(b, Importance.Required);
        b.DependsOn(a, Importance.Required);

        // Should not throw or hang.
        var exception = Record.Exception(() => a.PropagateChange());
        Assert.Null(exception);
    }

    [Fact]
    public void PropagateChange_NoParents_OnlyNotifiesSelf()
    {
        var node = new HealthCheck("Lone",
            () => new HealthEvaluation(HealthStatus.Degraded, "slow"));

        var emitted = new List<HealthStatus>();
        node.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        node.PropagateChange();

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Degraded, emitted[0]);
    }

    // ── DependsOn / RemoveDependency auto-propagation ─────────────────

    [Fact]
    public void DependsOn_ImmediatelyPropagatesToParent()
    {
        var child = new HealthCheck("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var parent = new HealthCheck("Parent");

        var emitted = new List<HealthStatus>();
        parent.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        // The child is already Unhealthy. Wiring the edge surfaces that status
        // to the parent immediately without requiring a poll.
        parent.DependsOn(child, Importance.Required);

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void DependsOn_PropagatesUpThroughGrandparent()
    {
        var child = new HealthCheck("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var parent = new HealthCheck("Parent");
        var grandparent = new HealthCheck("Grandparent");

        // Wire grandparent → parent. DependsOn propagates immediately,
        // setting grandparent._lastEmitted = Healthy (parent had no deps yet).
        grandparent.DependsOn(parent, Importance.Required);

        var emitted = new List<HealthStatus>();
        grandparent.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        // Wiring parent → child propagates all the way up to grandparent.
        parent.DependsOn(child, Importance.Required);

        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);
    }

    [Fact]
    public void RemoveDependency_ImmediatelyPropagatesToParent()
    {
        var child = new HealthCheck("Child",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var parent = new HealthCheck("Parent");

        var emitted = new List<HealthStatus>();
        parent.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        // Adding the edge immediately reflects the child's Unhealthy status.
        parent.DependsOn(child, Importance.Required);
        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Unhealthy, emitted[0]);

        // Removing the edge immediately restores Healthy without polling.
        parent.RemoveDependency(child);
        Assert.Equal(2, emitted.Count);
        Assert.Equal(HealthStatus.Healthy, emitted[1]);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
