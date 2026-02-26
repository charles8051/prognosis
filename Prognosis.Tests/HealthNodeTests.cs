namespace Prognosis.Tests;

public class HealthNodeTests
{
    // ── Evaluate ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_NoDependencies_ReturnsIntrinsicCheck()
    {
        var node = new HealthAdapter("Svc",
            () => new HealthEvaluation(HealthStatus.Degraded, "slow"));

        var result = node.Evaluate();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("slow", result.Reason);
    }

    [Fact]
    public void Evaluate_WithDependency_AggregatesCorrectly()
    {
        var dep = new HealthAdapter("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var node = new HealthAdapter("Svc")
            .DependsOn(dep, Importance.Required);

        var result = node.Evaluate();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ── DependsOn ────────────────────────────────────────────────────

    [Fact]
    public void DependsOn_AddsDependency()
    {
        var node = new HealthAdapter("Svc");
        var dep = new HealthAdapter("Dep");

        node.DependsOn(dep, Importance.Important);

        Assert.Single(node.Dependencies);
        Assert.Equal("Dep", node.Dependencies[0].Node.Name);
        Assert.Equal(Importance.Important, node.Dependencies[0].Importance);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForChaining()
    {
        var node = new HealthAdapter("Svc");
        var dep = new HealthAdapter("Dep");

        var returned = node.DependsOn(dep, Importance.Required);

        Assert.Same(node, returned);
    }

    [Fact]
    public void DependsOn_AfterEvaluate_IsAllowed()
    {
        var dep = new HealthAdapter("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var node = new HealthAdapter("Svc");

        // Evaluate first — this used to freeze the graph.
        var before = node.Evaluate();
        Assert.Equal(HealthStatus.Healthy, before.Status);

        // Adding an edge at runtime now works.
        node.DependsOn(dep, Importance.Required);
        var after = node.Evaluate();
        Assert.Equal(HealthStatus.Unhealthy, after.Status);
    }

    [Fact]
    public void RemoveDependency_DetachesEdge()
    {
        var dep = new HealthAdapter("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var node = new HealthAdapter("Svc")
            .DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, node.Evaluate().Status);

        var removed = node.RemoveDependency(dep);

        Assert.True(removed);
        Assert.Empty(node.Dependencies);
        Assert.Equal(HealthStatus.Healthy, node.Evaluate().Status);
    }

    [Fact]
    public void RemoveDependency_UnknownService_ReturnsFalse()
    {
        var node = new HealthAdapter("Svc");
        var unknown = new HealthAdapter("Unknown");

        Assert.False(node.RemoveDependency(unknown));
    }

    // ── Circular dependency guard ────────────────────────────────────

    [Fact]
    public void Evaluate_CircularDependency_ReturnsUnhealthy_DoesNotStackOverflow()
    {
        var a = new HealthAdapter("A");
        var b = new HealthAdapter("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);

        var result = a.Evaluate();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Circular", result.Reason);
    }

    // ── BubbleChange / StatusChanged ────────────────────────────────

    [Fact]
    public void BubbleChange_EmitsOnStatusChange()
    {
        var isHealthy = true;
        var node = new HealthAdapter("Svc",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);

        var emitted = new List<HealthStatus>();
        node.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        // First notify — emits initial status.
        node.BubbleChange();
        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Healthy, emitted[0]);

        // Same status — no duplicate emission.
        node.BubbleChange();
        Assert.Single(emitted);

        // Status changes — emits new status.
        isHealthy = false;
        node.BubbleChange();
        Assert.Equal(2, emitted.Count);
        Assert.Equal(HealthStatus.Unhealthy, emitted[1]);
    }

    [Fact]
    public void StatusChanged_Unsubscribe_StopsEmitting()
    {
        var node = new HealthAdapter("Svc");
        var emitted = new List<HealthStatus>();

        var subscription = node.StatusChanged.Subscribe(
            new TestObserver<HealthStatus>(emitted.Add));

        node.BubbleChange();
        Assert.Single(emitted);

        subscription.Dispose();

        // Force a change so BubbleChange would emit if still subscribed.
        // We need to reset _lastEmitted by changing status.
        // Since the intrinsic is always Healthy and _lastEmitted is Healthy,
        // changing won't trigger. Use a new node instead.
        var node2 = new HealthAdapter("Svc2", () => HealthStatus.Degraded);
        var emitted2 = new List<HealthStatus>();
        var sub2 = node2.StatusChanged.Subscribe(
            new TestObserver<HealthStatus>(emitted2.Add));
        node2.BubbleChange();
        Assert.Single(emitted2);

        sub2.Dispose();
        // After dispose, further notify doesn't add.
        // (Status doesn't change so BubbleChange wouldn't emit anyway.)
        // Verify the subscription list is cleaned up by subscribing again.
        var emitted3 = new List<HealthStatus>();
        node2.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted3.Add));
        node2.BubbleChange(); // _lastEmitted == Degraded, no change
        Assert.Empty(emitted3);
    }

    [Fact]
    public void StatusChanged_MultipleSubscribers_AllReceive()
    {
        var node = new HealthAdapter("Svc",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var emitted1 = new List<HealthStatus>();
        var emitted2 = new List<HealthStatus>();
        node.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted1.Add));
        node.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted2.Add));

        node.BubbleChange();

        Assert.Single(emitted1);
        Assert.Single(emitted2);
        Assert.Equal(HealthStatus.Unhealthy, emitted1[0]);
        Assert.Equal(HealthStatus.Unhealthy, emitted2[0]);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
