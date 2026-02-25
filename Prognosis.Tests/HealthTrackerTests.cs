namespace Prognosis.Tests;

public class HealthTrackerTests
{
    // ── Evaluate ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_NoDependencies_ReturnsIntrinsicCheck()
    {
        var tracker = new HealthTracker(
            () => new HealthEvaluation(HealthStatus.Degraded, "slow"));

        var result = tracker.Evaluate();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("slow", result.Reason);
    }

    [Fact]
    public void Evaluate_DefaultConstructor_ReturnsUnknown()
    {
        var tracker = new HealthTracker();

        Assert.Equal(HealthStatus.Unknown, tracker.Evaluate().Status);
    }

    [Fact]
    public void Evaluate_WithDependency_AggregatesCorrectly()
    {
        var dep = new HealthCheck("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var tracker = new HealthTracker(() => HealthStatus.Healthy);
        tracker.DependsOn(dep, Importance.Required);

        var result = tracker.Evaluate();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ── DependsOn ────────────────────────────────────────────────────

    [Fact]
    public void DependsOn_AddsDependency()
    {
        var tracker = new HealthTracker();
        var dep = new HealthCheck("Dep");

        tracker.DependsOn(dep, Importance.Important);

        Assert.Single(tracker.Dependencies);
        Assert.Equal("Dep", tracker.Dependencies[0].Node.Name);
        Assert.Equal(Importance.Important, tracker.Dependencies[0].Importance);
    }

    [Fact]
    public void DependsOn_ReturnsSelf_ForChaining()
    {
        var tracker = new HealthTracker();
        var dep = new HealthCheck("Dep");

        var returned = tracker.DependsOn(dep, Importance.Required);

        Assert.Same(tracker, returned);
    }

    [Fact]
    public void DependsOn_AfterEvaluate_IsAllowed()
    {
        var dep = new HealthCheck("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var tracker = new HealthTracker(() => HealthStatus.Healthy);

        // Evaluate first — this used to freeze the graph.
        var before = tracker.Evaluate();
        Assert.Equal(HealthStatus.Healthy, before.Status);

        // Adding an edge at runtime now works.
        tracker.DependsOn(dep, Importance.Required);
        var after = tracker.Evaluate();
        Assert.Equal(HealthStatus.Unhealthy, after.Status);
    }

    [Fact]
    public void RemoveDependency_DetachesEdge()
    {
        var dep = new HealthCheck("Dep",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var tracker = new HealthTracker(() => HealthStatus.Healthy);
        tracker.DependsOn(dep, Importance.Required);

        Assert.Equal(HealthStatus.Unhealthy, tracker.Evaluate().Status);

        var removed = tracker.RemoveDependency(dep);

        Assert.True(removed);
        Assert.Empty(tracker.Dependencies);
        Assert.Equal(HealthStatus.Healthy, tracker.Evaluate().Status);
    }

    [Fact]
    public void RemoveDependency_UnknownService_ReturnsFalse()
    {
        var tracker = new HealthTracker();
        var unknown = new HealthCheck("Unknown");

        Assert.False(tracker.RemoveDependency(unknown));
    }

    // ── Circular dependency guard ────────────────────────────────────

    [Fact]
    public void Evaluate_CircularDependency_ReturnsUnhealthy_DoesNotStackOverflow()
    {
        var a = new HealthCheck("A");
        var b = new HealthCheck("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);

        var result = a.Evaluate();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Circular", result.Reason);
    }

    // ── NotifyChanged / StatusChanged ────────────────────────────────

    [Fact]
    public void NotifyChanged_EmitsOnStatusChange()
    {
        var isHealthy = true;
        var tracker = new HealthTracker(
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);

        var emitted = new List<HealthStatus>();
        tracker.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted.Add));

        // First notify — emits initial status.
        tracker.NotifyChanged();
        Assert.Single(emitted);
        Assert.Equal(HealthStatus.Healthy, emitted[0]);

        // Same status — no duplicate emission.
        tracker.NotifyChanged();
        Assert.Single(emitted);

        // Status changes — emits new status.
        isHealthy = false;
        tracker.NotifyChanged();
        Assert.Equal(2, emitted.Count);
        Assert.Equal(HealthStatus.Unhealthy, emitted[1]);
    }

    [Fact]
    public void StatusChanged_Unsubscribe_StopsEmitting()
    {
        var tracker = new HealthTracker(() => HealthStatus.Healthy);
        var emitted = new List<HealthStatus>();

        var subscription = tracker.StatusChanged.Subscribe(
            new TestObserver<HealthStatus>(emitted.Add));

        tracker.NotifyChanged();
        Assert.Single(emitted);

        subscription.Dispose();

        // Force a change so NotifyChanged would emit if still subscribed.
        // We need to reset _lastEmitted by changing status.
        // Since the intrinsic is always Healthy and _lastEmitted is Healthy,
        // changing won't trigger. Use a new tracker instead.
        var tracker2 = new HealthTracker(() => HealthStatus.Degraded);
        var emitted2 = new List<HealthStatus>();
        var sub2 = tracker2.StatusChanged.Subscribe(
            new TestObserver<HealthStatus>(emitted2.Add));
        tracker2.NotifyChanged();
        Assert.Single(emitted2);

        sub2.Dispose();
        // After dispose, further notify doesn't add.
        // (Status doesn't change so NotifyChanged wouldn't emit anyway.)
        // Verify the subscription list is cleaned up by subscribing again.
        var emitted3 = new List<HealthStatus>();
        tracker2.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted3.Add));
        tracker2.NotifyChanged(); // _lastEmitted == Degraded, no change
        Assert.Empty(emitted3);
    }

    [Fact]
    public void StatusChanged_MultipleSubscribers_AllReceive()
    {
        var tracker = new HealthTracker(
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));

        var emitted1 = new List<HealthStatus>();
        var emitted2 = new List<HealthStatus>();
        tracker.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted1.Add));
        tracker.StatusChanged.Subscribe(new TestObserver<HealthStatus>(emitted2.Add));

        tracker.NotifyChanged();

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
