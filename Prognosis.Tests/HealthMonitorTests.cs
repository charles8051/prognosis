namespace Prognosis.Tests;

public class HealthMonitorTests : IAsyncDisposable
{
    private HealthMonitor? _monitor;

    public async ValueTask DisposeAsync()
    {
        if (_monitor is not null)
            await _monitor.DisposeAsync();
    }

    [Fact]
    public void Poll_EmitsInitialReport()
    {
        var svc = new HealthAdapter("Svc");
        _monitor = new HealthMonitor(new[] { svc }, TimeSpan.FromHours(1));

        var reports = new List<HealthReport>();
        _monitor.ReportChanged.Subscribe(new TestObserver<HealthReport>(reports.Add));

        _monitor.Poll();

        Assert.Single(reports);
        Assert.Equal(HealthStatus.Healthy, reports[0].OverallStatus);
    }

    [Fact]
    public void Poll_SameState_SuppressesDuplicate()
    {
        var svc = new HealthAdapter("Svc");
        _monitor = new HealthMonitor(new[] { svc }, TimeSpan.FromHours(1));

        var reports = new List<HealthReport>();
        _monitor.ReportChanged.Subscribe(new TestObserver<HealthReport>(reports.Add));

        _monitor.Poll();
        _monitor.Poll();

        Assert.Single(reports);
    }

    [Fact]
    public void Poll_StateChanges_EmitsNewReport()
    {
        var isHealthy = true;
        var svc = new HealthAdapter("Svc",
            () => isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy);
        _monitor = new HealthMonitor(new[] { svc }, TimeSpan.FromHours(1));

        var reports = new List<HealthReport>();
        _monitor.ReportChanged.Subscribe(new TestObserver<HealthReport>(reports.Add));

        _monitor.Poll();
        isHealthy = false;
        _monitor.Poll();

        Assert.Equal(2, reports.Count);
        Assert.Equal(HealthStatus.Healthy, reports[0].OverallStatus);
        Assert.Equal(HealthStatus.Unhealthy, reports[1].OverallStatus);
    }

    [Fact]
    public void Poll_NotifiesObservableServices()
    {
        var svc = new HealthAdapter("Svc",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        _monitor = new HealthMonitor(new[] { svc }, TimeSpan.FromHours(1));

        var statuses = new List<HealthStatus>();
        svc.StatusChanged.Subscribe(new TestObserver<HealthStatus>(statuses.Add));

        _monitor.Poll();

        Assert.Single(statuses);
        Assert.Equal(HealthStatus.Unhealthy, statuses[0]);
    }

    [Fact]
    public void ReportChanged_MultipleSubscribers_AllReceive()
    {
        var svc = new HealthAdapter("Svc");
        _monitor = new HealthMonitor(new[] { svc }, TimeSpan.FromHours(1));

        var reports1 = new List<HealthReport>();
        var reports2 = new List<HealthReport>();
        _monitor.ReportChanged.Subscribe(new TestObserver<HealthReport>(reports1.Add));
        _monitor.ReportChanged.Subscribe(new TestObserver<HealthReport>(reports2.Add));

        _monitor.Poll();

        Assert.Single(reports1);
        Assert.Single(reports2);
    }

    [Fact]
    public async Task DisposeAsync_StopsPolling()
    {
        var svc = new HealthAdapter("Svc");
        _monitor = new HealthMonitor(new[] { svc }, TimeSpan.FromMilliseconds(50));
        _monitor.Start();

        await _monitor.DisposeAsync();
        _monitor = null; // prevent double dispose

        // If we get here without hanging, the test passes.
    }

    [Fact]
    public void Constructor_WithHealthGraph_Polls()
    {
        var child = new HealthAdapter("Child");
        var root = new HealthAdapter("Root")
            .DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        _monitor = new HealthMonitor(graph, TimeSpan.FromHours(1));

        var reports = new List<HealthReport>();
        _monitor.ReportChanged.Subscribe(new TestObserver<HealthReport>(reports.Add));

        _monitor.Poll();

        Assert.Single(reports);
        Assert.Equal(2, reports[0].Services.Count);
    }

    [Fact]
    public void Constructor_WithHealthGraph_ReflectsDynamicRootChanges()
    {
        var a = new HealthAdapter("A");
        var b = new HealthAdapter("B",
            () => new HealthEvaluation(HealthStatus.Unhealthy, "down"));
        var graph = HealthGraph.Create(a, b);

        _monitor = new HealthMonitor(graph, TimeSpan.FromHours(1));

        var reports = new List<HealthReport>();
        _monitor.ReportChanged.Subscribe(new TestObserver<HealthReport>(reports.Add));

        _monitor.Poll();
        Assert.Single(reports);
        Assert.Equal(HealthStatus.Unhealthy, reports[0].OverallStatus);

        // Wire A → B (Required) — A's status changes from Healthy to Unhealthy.
        a.DependsOn(b, Importance.Required);
        _monitor.Poll();

        // Report changed because A's effective status now reflects B's failure.
        Assert.Equal(2, reports.Count);
        Assert.Equal(HealthStatus.Unhealthy, reports[1].Services.First(s => s.Name == "A").Status);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
