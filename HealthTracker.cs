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
    private readonly List<HealthDependency> _dependencies = new();
    private readonly object _lock = new();
    private readonly List<IObserver<HealthStatus>> _observers = new();
    private HealthStatus? _lastEmitted;
    private bool _frozen;

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
    /// Registers a dependency on another service. Must be called before the
    /// first <see cref="Evaluate"/> — the dependency list is frozen once
    /// evaluation begins.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if called after <see cref="Evaluate"/> has been invoked.
    /// </exception>
    public HealthTracker DependsOn(HealthNode service, Importance importance)
    {
        if (_frozen)
            throw new InvalidOperationException(
                "Dependencies cannot be modified after evaluation has started.");

        _dependencies.Add(new HealthDependency(service, importance));
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
        _frozen = true;

        s_evaluating ??= new(ReferenceEqualityComparer.Instance);

        if (!s_evaluating.Add(this))
            return new HealthEvaluation(HealthStatus.Unhealthy, "Circular dependency detected");

        try
        {
            return _aggregator(_intrinsicCheck(), _dependencies);
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
