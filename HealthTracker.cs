namespace Prognosis;

/// <summary>
/// A composable helper that any existing service can embed to participate in the
/// health graph via delegation. The owning class implements <see cref="HealthNode"/>
/// and forwards <see cref="HealthNode.Dependencies"/> and <see cref="HealthNode.Evaluate"/>
/// to this tracker.
/// </summary>
/// <remarks>
/// Usage: embed a <see cref="HealthTracker"/> as a field, supply a
/// <c>Func&lt;HealthStatus&gt;</c> that returns the service's own intrinsic health,
/// then delegate the interface members.
/// </remarks>
public sealed class HealthTracker
{
    [ThreadStatic]
    private static HashSet<HealthTracker>? s_evaluating;

    private readonly Func<HealthEvaluation> _intrinsicCheck;
    private readonly AggregationStrategy _aggregator;
    private readonly object _writeLock = new();
    private readonly object _observerLock = new();
    private readonly List<IObserver<HealthStatus>> _observers = new();
    private volatile IReadOnlyList<HealthDependency> _dependencies = Array.Empty<HealthDependency>();
    private HealthStatus? _lastEmitted;

    /// <param name="intrinsicCheck">
    /// A callback that returns the owning service's intrinsic health
    /// (e.g., whether a connection is alive). Called on every <see cref="Evaluate"/>.
    /// </param>
    /// <param name="aggregator">
    /// Strategy used to combine intrinsic health with dependency evaluations.
    /// Defaults to <see cref="HealthAggregator.Aggregate"/> when <see langword="null"/>.
    /// </param>
    public HealthTracker(Func<HealthEvaluation> intrinsicCheck, AggregationStrategy? aggregator = null)
    {
        _intrinsicCheck = intrinsicCheck;
        _aggregator = aggregator ?? HealthAggregator.Aggregate;
        StatusChanged = new StatusObservable(this);
    }

    /// <summary>Shortcut: intrinsic status starts as <see cref="HealthStatus.Unknown"/> until first real check.</summary>
    public HealthTracker()
        : this(() => HealthStatus.Unknown) { }

    public IReadOnlyList<HealthDependency> Dependencies => _dependencies;

    /// <summary>
    /// An observable that emits the new <see cref="HealthStatus"/> whenever
    /// <see cref="NotifyChanged"/> detects a status change.
    /// <see cref="IObservable{T}"/> is a BCL type — no System.Reactive dependency required.
    /// </summary>
    public IObservable<HealthStatus> StatusChanged { get; }

    /// <summary>
    /// Registers a dependency on another service. Thread-safe and may be
    /// called at any time, including after evaluation has started. The new
    /// edge is visible to the next <see cref="Evaluate"/> call.
    /// </summary>
    public HealthTracker DependsOn(HealthNode service, Importance importance)
    {
        lock (_writeLock)
        {
            var updated = new List<HealthDependency>(_dependencies)
            {
                new(service, importance)
            };
            _dependencies = updated;
        }
        return this;
    }

    /// <summary>
    /// Removes the first dependency that references <paramref name="service"/>.
    /// Returns <see langword="true"/> if a dependency was removed;
    /// otherwise <see langword="false"/>.
    /// </summary>
    public bool RemoveDependency(HealthNode service)
    {
        lock (_writeLock)
        {
            var current = _dependencies;
            var index = -1;
            for (var i = 0; i < current.Count; i++)
            {
                if (ReferenceEquals(current[i].Service, service))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return false;

            var updated = new List<HealthDependency>(current.Count - 1);
            for (var i = 0; i < current.Count; i++)
            {
                if (i != index)
                    updated.Add(current[i]);
            }
            _dependencies = updated;
            return true;
        }
    }

    /// <summary>
    /// Re-evaluates the current health status and notifies observers if it has
    /// changed since the last notification.
    /// </summary>
    public void NotifyChanged()
    {
        var current = Evaluate().Status;

        List<IObserver<HealthStatus>>? snapshot = null;
        lock (_observerLock)
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
            // Capture the volatile snapshot once — iteration is safe because
            // writers always replace the entire list (copy-on-write).
            var deps = _dependencies;
            return _aggregator(_intrinsicCheck(), deps);
        }
        finally
        {
            s_evaluating.Remove(this);
        }
    }

    private void AddObserver(IObserver<HealthStatus> observer)
    {
        lock (_observerLock)
        {
            _observers.Add(observer);
        }
    }

    private void RemoveObserver(IObserver<HealthStatus> observer)
    {
        lock (_observerLock)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class StatusObservable(HealthTracker tracker) : IObservable<HealthStatus>
    {
        public IDisposable Subscribe(IObserver<HealthStatus> observer)
        {
            tracker.AddObserver(observer);
            return new Unsubscriber(tracker, observer);
        }
    }

    private sealed class Unsubscriber(HealthTracker tracker, IObserver<HealthStatus> observer) : IDisposable
    {
        public void Dispose() => tracker.RemoveObserver(observer);
    }
}
