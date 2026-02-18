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
        var svc = new HealthCheck("Svc");
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
        var svc = new HealthCheck("Svc");
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
        var svc = new HealthCheck("Svc",
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
        var svc = new HealthCheck("Svc",
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
        var svc = new HealthCheck("Svc");
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
        var svc = new HealthCheck("Svc");
        _monitor = new HealthMonitor(new[] { svc }, TimeSpan.FromMilliseconds(50));

        await _monitor.DisposeAsync();
        _monitor = null; // prevent double dispose

        // If we get here without hanging, the test passes.
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
