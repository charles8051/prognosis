namespace Prognosis;

/// <summary>
/// Polls the health graph on a configurable interval, calls
/// <see cref="HealthNode.NotifyChanged"/> on every node in the graph,
/// and emits a <see cref="HealthReport"/> when the overall state changes.
/// </summary>
public sealed class HealthMonitor : IAsyncDisposable, IDisposable
{
    private readonly HealthNode[] _roots;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollingTask;
    private readonly object _lock = new();
    private readonly List<IObserver<HealthReport>> _observers = new();
    private HealthReport? _lastReport;

    /// <summary>
    /// Emits a new <see cref="HealthReport"/> whenever the graph's health state
    /// changes between polling ticks.
    /// </summary>
    public IObservable<HealthReport> ReportChanged { get; }

    public HealthMonitor(IEnumerable<HealthNode> roots, TimeSpan interval)
    {
        _roots = roots.ToArray();
        _interval = interval;
        ReportChanged = new ReportObservable(this);
        _pollingTask = PollLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Manually triggers a single poll cycle. Useful for testing or getting
    /// the initial state before the first timer tick.
    /// </summary>
    public void Poll()
    {
        // Walk the graph depth-first and notify all observable services.
        HealthAggregator.NotifyGraph(_roots);

        // Build a report and emit if changed.
        var report = HealthAggregator.CreateReport(_roots);

        List<IObserver<HealthReport>>? snapshot = null;
        lock (_lock)
        {
            if (_lastReport is not null && HealthReportComparer.Instance.Equals(_lastReport, report))
                return;
            _lastReport = report;
            if (_observers.Count > 0)
                snapshot = new List<IObserver<HealthReport>>(_observers);
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
        _cts.Cancel();
        try { await _pollingTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    /// <summary>
    /// Synchronous disposal. Prefer <see cref="DisposeAsync"/> in async contexts.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        try { _pollingTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            Poll();
        }
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
