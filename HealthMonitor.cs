namespace Prognosis;

/// <summary>
/// The consumer-started shell that drives waves on a <see cref="HealthGraph"/>
/// (ADR-010 §6 / ADR-011 §6). It wakes on the earlier of an <em>optional</em> fixed
/// cadence and the graph's <see cref="HealthGraph.NextTemporalDeadline"/> — the single
/// min over policy pending-deadlines AND lease next-decay-instants — and calls
/// <see cref="HealthGraph.RefreshAll"/> on each wake so time-based transitions (a lease
/// decaying, a debounce hold gating) are actually evaluated. It re-arms whenever the
/// deadline moves (subscribing to <see cref="HealthGraph.TemporalDeadlineChanged"/>),
/// and when nothing is pending and no cadence is set it sleeps until signalled rather
/// than spinning.
/// <para>
/// This resolves ADR-010 open question 3 / ADR-011 open question 5 (monitor-assisted
/// deadlines): the library now offers the blessed pump, so temporal features no longer
/// require a hand-rolled deadline loop. The no-timers-in-the-core doctrine is intact —
/// the timer lives HERE, in the consumer-started shell, exactly where ADR-010 §6 said a
/// wave source belongs; the graph and nodes still schedule nothing.
/// </para>
/// <para>
/// <b>Cadence is optional.</b> A graph whose temporal nodes are all deadline-driven
/// (edges install the deadline; the monitor wakes to apply it) needs no cadence. A graph
/// with drifting pull-probes — a delegate reading a live flag or queue depth whose change
/// has no computable deadline — still needs periodic polling to observe the change at all;
/// pass a cadence for that shape. Both coexist: cadence wakes AND deadline wakes.
/// </para>
/// Subscribe to <see cref="ReportChanged"/> (which delegates to
/// <see cref="HealthGraph.StatusChanged"/>) to receive notifications when the graph's
/// effective health changes.
/// </summary>
public sealed class HealthMonitor : IAsyncDisposable, IDisposable
{
    private const long NoDeadline = long.MinValue;

    private readonly HealthGraph _graph;
    private readonly TimeSpan? _cadence;
    private readonly IMonitorDelay _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    // Latest next-deadline from the subscription, in the graph's wave timebase, as
    // ticks; NoDeadline means "nothing pending". A plain long, written/read via
    // Interlocked, so no boxing and no torn read.
    private long _deadlineTicks = NoDeadline;

    // Wave count, for deterministic test synchronization only (never a hot-path read).
    private int _waves;

    private IDisposable? _deadlineSubscription;
    private Task? _loopTask;

    /// <summary>
    /// Emits a new <see cref="HealthReport"/> whenever the graph's health state
    /// changes between waves. Delegates to <see cref="HealthGraph.StatusChanged"/>.
    /// </summary>
    public IObservable<HealthReport> ReportChanged => _graph.StatusChanged;

    /// <summary>
    /// The underlying <see cref="HealthGraph"/> being driven by this monitor.
    /// </summary>
    public HealthGraph Graph => _graph;

    /// <summary>
    /// The fixed poll cadence, or <see langword="null"/> when this monitor is purely
    /// deadline-driven (wakes only on temporal deadlines and re-arms).
    /// </summary>
    public TimeSpan? Cadence => _cadence;

    /// <summary>
    /// Creates a monitor for the given <see cref="HealthGraph"/>. When
    /// <paramref name="cadence"/> is <see langword="null"/> (the default) the monitor is
    /// purely deadline-driven; when set, it also waves at least that often (for drifting
    /// pull-probes whose change has no computable deadline). Call <see cref="Start"/> to
    /// begin the background loop.
    /// </summary>
    /// <param name="graph">The graph to drive.</param>
    /// <param name="cadence">
    /// Optional fixed poll interval. Must be positive when supplied.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cadence"/> is non-positive.</exception>
    public HealthMonitor(HealthGraph graph, TimeSpan? cadence = null)
        : this(graph, cadence, delay: null) { }

    /// <summary>
    /// Creates a monitor rooted at the given node (materializing a
    /// <see cref="HealthGraph"/> for it), with an optional cadence.
    /// </summary>
    public HealthMonitor(HealthNode root, TimeSpan? cadence = null)
        : this(HealthGraph.Create(root), cadence) { }

    // The real delay converts an absolute wave-time wake instant into a real wait using
    // the graph's own clock. Tests inject a virtual-time delay so the monitor's timing
    // is fully deterministic against the same fake clock the graph reads.
    internal HealthMonitor(HealthGraph graph, TimeSpan? cadence, IMonitorDelay? delay)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        if (cadence is TimeSpan c && c <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(cadence), cadence, "Cadence must be positive when supplied.");
        _cadence = cadence;
        _delay = delay ?? new RealMonitorDelay(graph);

        // Declaring a monitor for this graph is the wave-source signal — independent of
        // when (or whether) Start runs, so the undriven-temporal-graph warning
        // stays silent once a monitor exists.
        _graph.AttachWaveSource();
    }

    /// <summary>
    /// Starts the background loop. Safe to call multiple times — subsequent calls are
    /// no-ops. Subscribes to <see cref="HealthGraph.TemporalDeadlineChanged"/> first, so
    /// the current deadline (replayed on subscribe) is known before the first sleep.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_loopTask is not null)
                return;

            // Replay-latest on subscribe seeds _deadlineTicks before the loop sleeps.
            _deadlineSubscription =
                _graph.TemporalDeadlineChanged.Subscribe(new DeadlineObserver(this));
            _loopTask = RunLoopAsync(_cts.Token);
        }
    }

    /// <summary>
    /// Manually triggers a single wave. Useful for testing or getting the initial
    /// state before the first scheduled wake.
    /// </summary>
    public void Poll() => _graph.RefreshAll();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Cadence is measured from the last wave; seed it at loop entry so the first
        // cadence wake lands ~cadence later. The construction wave already established
        // the initial report/deadline.
        var lastWave = _graph.CurrentWaveTime;

        while (!ct.IsCancellationRequested)
        {
            var wakeAt = NextWake(lastWave);

            bool rearmed;
            try
            {
                rearmed = await _delay.WaitUntilAsync(wakeAt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            if (ct.IsCancellationRequested)
                break;

            if (rearmed)
                continue; // deadline moved: recompute the wake instant, do not necessarily wave

            // The scheduled wake instant was reached (cadence or deadline): drive the
            // wave. A wave counts as a poll, so it also resets the cadence clock.
            _graph.RefreshAll();
            lastWave = _graph.CurrentWaveTime;
            Interlocked.Increment(ref _waves);
        }
    }

    /// <summary>
    /// The next absolute wave-time instant to wake at: the earlier of the cadence wake
    /// (<c>lastWave + cadence</c>, when a cadence is set) and the graph's current
    /// next-deadline, or <see langword="null"/> when neither is set (sleep until
    /// signalled).
    /// </summary>
    private TimeSpan? NextWake(TimeSpan lastWave)
    {
        var cadenceWake = _cadence is TimeSpan c ? lastWave + c : (TimeSpan?)null;

        var ticks = Interlocked.Read(ref _deadlineTicks);
        var deadline = ticks == NoDeadline ? (TimeSpan?)null : TimeSpan.FromTicks(ticks);

        if (cadenceWake is null)
            return deadline;
        if (deadline is null)
            return cadenceWake;
        return cadenceWake.Value <= deadline.Value ? cadenceWake : deadline;
    }

    private void OnDeadlineChanged(TimeSpan? deadline)
    {
        Interlocked.Exchange(
            ref _deadlineTicks, deadline is TimeSpan d ? d.Ticks : NoDeadline);

        // Wake the loop to re-arm on the new deadline.
        _delay.Wake();
    }

    /// <summary>Wave count driven by the loop, for deterministic test synchronization.</summary>
    internal int WavesForTest => Volatile.Read(ref _waves);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _delay.Wake();
        _deadlineSubscription?.Dispose();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        (_delay as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Synchronous disposal. Prefer <see cref="DisposeAsync"/> in async contexts.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _delay.Wake();
        _deadlineSubscription?.Dispose();
        if (_loopTask is not null)
        {
            try { _loopTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        (_delay as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Bridges <see cref="HealthGraph.TemporalDeadlineChanged"/> to
    /// <see cref="OnDeadlineChanged"/>. On graph disposal (OnCompleted) the deadline is
    /// cleared so the loop parks until cancelled.
    /// </summary>
    private sealed class DeadlineObserver(HealthMonitor monitor) : IObserver<TimeSpan?>
    {
        public void OnNext(TimeSpan? value) => monitor.OnDeadlineChanged(value);

        // Treat an error on the deadline channel like completion: clear the deadline so
        // the loop parks (or falls back to cadence) rather than waiting on a stale value
        // with no further signal. TemporalDeadlineChanged does not error today; this is
        // defensive against a future channel change.
        public void OnError(Exception error) => monitor.OnDeadlineChanged(null);
        public void OnCompleted() => monitor.OnDeadlineChanged(null);
    }
}

/// <summary>
/// The monitor's wait primitive (a testable seam): block until an absolute wave-time
/// instant is reached, or until <see cref="Wake"/> re-arms, or until cancellation. The
/// production implementation (<see cref="RealMonitorDelay"/>) converts the instant to a
/// real wait using the graph clock; a test implementation drives a virtual clock so the
/// monitor's timing is deterministic. Kept internal — not public API surface.
/// </summary>
internal interface IMonitorDelay
{
    /// <summary>
    /// Completes with <see langword="false"/> when <paramref name="wakeAt"/> (an absolute
    /// wave-time instant) is reached, with <see langword="true"/> if <see cref="Wake"/>
    /// fires first, or throws <see cref="OperationCanceledException"/> on cancellation. A
    /// <see langword="null"/> <paramref name="wakeAt"/> waits indefinitely (until
    /// <see cref="Wake"/> or cancellation).
    /// </summary>
    Task<bool> WaitUntilAsync(TimeSpan? wakeAt, CancellationToken ct);

    /// <summary>Re-arm: wake any in-flight <see cref="WaitUntilAsync"/> with a "signalled" result.</summary>
    void Wake();
}

/// <summary>
/// Production <see cref="IMonitorDelay"/>: converts an absolute wave-time wake instant to
/// a real wait by reading the graph's own clock (<see cref="HealthGraph.CurrentWaveTime"/>),
/// interruptible by a bounded semaphore so a deadline move re-arms immediately.
/// </summary>
internal sealed class RealMonitorDelay(HealthGraph graph) : IMonitorDelay, IDisposable
{
    // SemaphoreSlim.WaitAsync caps its timeout at Int32.MaxValue ms; a farther deadline
    // waits in bounded chunks (waking to a no-op recompute), never busy.
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private readonly SemaphoreSlim _sem = new(0, 1);

    public Task<bool> WaitUntilAsync(TimeSpan? wakeAt, CancellationToken ct)
    {
        if (wakeAt is not TimeSpan target)
            return _sem.WaitAsync(Timeout.InfiniteTimeSpan, ct);

        var delay = target - graph.CurrentWaveTime;
        if (delay <= TimeSpan.Zero)
            return Task.FromResult(false); // already due — wave now
        return _sem.WaitAsync(delay < MaxWait ? delay : MaxWait, ct);
    }

    public void Wake()
    {
        try { _sem.Release(); }
        catch (SemaphoreFullException) { }
    }

    public void Dispose() => _sem.Dispose();
}
