namespace Prognosis;

/// <summary>
/// Polls the health graph on a configurable interval, calls
/// <see cref="IObservableServiceHealth.NotifyChanged"/> on every observable
/// service in the graph, and emits a <see cref="HealthReport"/> when the
/// overall state changes.
/// </summary>
public sealed class HealthMonitor : IAsyncDisposable
{
    private readonly IServiceHealth[] _roots;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollingTask;
    private readonly Lock _lock = new();
    private readonly List<IObserver<HealthReport>> _observers = [];
    private HealthReport? _lastReport;

    /// <summary>
    /// Emits a new <see cref="HealthReport"/> whenever the graph's health state
    /// changes between polling ticks.
    /// </summary>
    public IObservable<HealthReport> ReportChanged => new ReportObservable(this);

    public HealthMonitor(IEnumerable<IServiceHealth> roots, TimeSpan interval)
    {
        _roots = [.. roots];
        _timer = new PeriodicTimer(interval);
        _pollingTask = PollLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Manually triggers a single poll cycle. Useful for testing or getting
    /// the initial state before the first timer tick.
    /// </summary>
    public void Poll()
    {
        // Walk the graph depth-first and notify all observable services.
        var visited = new HashSet<IServiceHealth>(ReferenceEqualityComparer.Instance);
        foreach (var root in _roots)
        {
            NotifyGraph(root, visited);
        }

        // Build a report and emit if changed.
        var report = HealthAggregator.CreateReport(_roots);

        List<IObserver<HealthReport>>? snapshot = null;
        lock (_lock)
        {
            if (_lastReport is not null && !HasChanged(_lastReport, report))
                return;
            _lastReport = report;
            if (_observers.Count > 0)
                snapshot = [.. _observers];
        }

        if (snapshot is not null)
        {
            foreach (var observer in snapshot)
            {
                observer.OnNext(report);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _pollingTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _timer.Dispose();
        _cts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            Poll();
        }
    }

    private static void NotifyGraph(IServiceHealth service, HashSet<IServiceHealth> visited)
    {
        if (!visited.Add(service))
            return;

        // Depth-first: notify leaves before parents.
        foreach (var dep in service.Dependencies)
        {
            NotifyGraph(dep.Service, visited);
        }

        if (service is IObservableServiceHealth observable)
        {
            observable.NotifyChanged();
        }
    }

    private static bool HasChanged(HealthReport previous, HealthReport current)
    {
        if (previous.OverallStatus != current.OverallStatus)
            return true;

        if (previous.Services.Count != current.Services.Count)
            return true;

        for (var i = 0; i < current.Services.Count; i++)
        {
            if (previous.Services[i] != current.Services[i])
                return true;
        }

        return false;
    }

    private void AddObserver(IObserver<HealthReport> observer)
    {
        lock (_lock)
        {
            _observers.Add(observer);
        }
    }

    private void RemoveObserver(IObserver<HealthReport> observer)
    {
        lock (_lock)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class ReportObservable(HealthMonitor monitor) : IObservable<HealthReport>
    {
        public IDisposable Subscribe(IObserver<HealthReport> observer)
        {
            monitor.AddObserver(observer);
            return new Unsubscriber(monitor, observer);
        }
    }

    private sealed class Unsubscriber(HealthMonitor monitor, IObserver<HealthReport> observer) : IDisposable
    {
        public void Dispose() => monitor.RemoveObserver(observer);
    }
}
