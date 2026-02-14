namespace Prognosis;

/// <summary>
/// A composable helper that any existing service can embed to participate in the
/// health graph via delegation. The owning class implements <see cref="IServiceHealth"/>
/// and forwards <see cref="IServiceHealth.Dependencies"/> and <see cref="IServiceHealth.Evaluate"/>
/// to this tracker.
/// </summary>
/// <remarks>
/// Usage: embed a <see cref="ServiceHealthTracker"/> as a field, supply a
/// <c>Func&lt;HealthStatus&gt;</c> that returns the service's own intrinsic health,
/// then delegate the interface members.
/// </remarks>
public sealed class ServiceHealthTracker
{
    [ThreadStatic]
    private static HashSet<ServiceHealthTracker>? s_evaluating;

    private readonly Func<HealthEvaluation> _intrinsicCheck;
    private readonly List<ServiceDependency> _dependencies = new();
    private readonly object _lock = new();
    private readonly List<IObserver<HealthStatus>> _observers = new();
    private HealthStatus? _lastEmitted;

    /// <param name="intrinsicCheck">
    /// A callback that returns the owning service's intrinsic health
    /// (e.g., whether a connection is alive). Called on every <see cref="Evaluate"/>.
    /// </param>
    public ServiceHealthTracker(Func<HealthEvaluation> intrinsicCheck)
    {
        _intrinsicCheck = intrinsicCheck;
    }

    /// <summary>Shortcut: intrinsic status starts as <see cref="HealthStatus.Unknown"/> until first real check.</summary>
    public ServiceHealthTracker()
        : this(() => HealthStatus.Unknown) { }

    public IReadOnlyList<ServiceDependency> Dependencies => _dependencies;

    /// <summary>
    /// An observable that emits the new <see cref="HealthStatus"/> whenever
    /// <see cref="NotifyChanged"/> detects a status change.
    /// <see cref="IObservable{T}"/> is a BCL type — no System.Reactive dependency required.
    /// </summary>
    public IObservable<HealthStatus> StatusChanged => new StatusObservable(this);

    public ServiceHealthTracker DependsOn(IServiceHealth service, ServiceImportance importance)
    {
        _dependencies.Add(new ServiceDependency(service, importance));
        return this;
    }

    /// <summary>
    /// Re-evaluates the current health status and notifies observers if it has
    /// changed since the last notification.
    /// </summary>
    public void NotifyChanged()
    {
        var current = Evaluate().Status;

        List<IObserver<HealthStatus>>? snapshot = null;
        lock (_lock)
        {
            if (current == _lastEmitted)
                return;
            _lastEmitted = current;
            if (_observers.Count > 0)
                snapshot = new List<IObserver<HealthStatus>>(_observers);
        }

        if (snapshot is not null)
        {
            foreach (var observer in snapshot)
            {
                observer.OnNext(current);
            }
        }
    }

    public HealthEvaluation Evaluate()
    {
        s_evaluating ??= new(ReferenceEqualityComparer.Instance);

        if (!s_evaluating.Add(this))
            return new HealthEvaluation(HealthStatus.Unhealthy, "Circular dependency detected");

        try
        {
            return HealthAggregator.Aggregate(_intrinsicCheck(), _dependencies);
        }
        finally
        {
            s_evaluating.Remove(this);
        }
    }

    private void AddObserver(IObserver<HealthStatus> observer)
    {
        lock (_lock)
        {
            _observers.Add(observer);
        }
    }

    private void RemoveObserver(IObserver<HealthStatus> observer)
    {
        lock (_lock)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class StatusObservable(ServiceHealthTracker tracker) : IObservable<HealthStatus>
    {
        public IDisposable Subscribe(IObserver<HealthStatus> observer)
        {
            tracker.AddObserver(observer);
            return new Unsubscriber(tracker, observer);
        }
    }

    private sealed class Unsubscriber(ServiceHealthTracker tracker, IObserver<HealthStatus> observer) : IDisposable
    {
        public void Dispose() => tracker.RemoveObserver(observer);
    }
}
