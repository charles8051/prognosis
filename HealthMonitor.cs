namespace Prognosis;

/// <summary>
/// Polls the health graph on a configurable interval, calling
/// <see cref="HealthGraph.RefreshAll"/> on every tick to re-evaluate
/// all intrinsic checks. Subscribe to <see cref="ReportChanged"/>
/// (which delegates to <see cref="HealthGraph.StatusChanged"/>) to
/// receive notifications when the graph's effective health changes.
/// </summary>
public sealed class HealthMonitor : IAsyncDisposable, IDisposable
{
    private readonly HealthGraph _graph;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private Task? _pollingTask;

    /// <summary>
    /// Emits a new <see cref="HealthReport"/> whenever the graph's health state
    /// changes between polling ticks. Delegates to
    /// <see cref="HealthGraph.StatusChanged"/>.
    /// </summary>
    public IObservable<HealthReport> ReportChanged => _graph.StatusChanged;

    /// <summary>
    /// The underlying <see cref="HealthGraph"/> being polled by this monitor.
    /// </summary>
    public HealthGraph Graph => _graph;

    /// <summary>
    /// Creates a monitor that polls the given <see cref="HealthGraph"/> on
    /// every tick. Call <see cref="Start"/> to begin the background polling loop.
    /// </summary>
    public HealthMonitor(HealthGraph graph, TimeSpan interval)
    {
        _graph = graph;
        _interval = interval;
    }

    public HealthMonitor(HealthNode root, TimeSpan interval)
        : this(HealthGraph.Create(root), interval) { }

    /// <summary>
    /// Starts the background polling loop. Safe to call multiple times —
    /// subsequent calls are no-ops.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            _pollingTask ??= PollLoopAsync(_cts.Token);
        }
    }

    /// <summary>
    /// Manually triggers a single poll cycle. Useful for testing or getting
    /// the initial state before the first timer tick.
    /// </summary>
    public void Poll()
    {
        _graph.RefreshAll();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pollingTask is not null)
        {
            try { await _pollingTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }

    /// <summary>
    /// Synchronous disposal. Prefer <see cref="DisposeAsync"/> in async contexts.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        if (_pollingTask is not null)
        {
            try { _pollingTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
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
}
