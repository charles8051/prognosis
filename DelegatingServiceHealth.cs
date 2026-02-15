namespace Prognosis;

/// <summary>
/// Adapts any external or closed service into the health graph by wrapping a
/// <c>Func&lt;HealthEvaluation&gt;</c>. Use this when you cannot (or prefer not to)
/// modify the service class itself.
/// </summary>
public sealed class DelegatingServiceHealth : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _tracker;

    public string Name { get; }

    public IReadOnlyList<ServiceDependency> Dependencies => _tracker.Dependencies;

    public IObservable<HealthStatus> StatusChanged => _tracker.StatusChanged;

    /// <param name="name">Display name for the service.</param>
    /// <param name="healthCheck">
    /// A delegate that returns the service's intrinsic health evaluation.
    /// Called every time <see cref="Evaluate"/> is invoked.
    /// </param>
    /// <param name="aggregator">
    /// Strategy used to combine intrinsic health with dependency evaluations.
    /// Defaults to <see cref="HealthAggregator.Aggregate"/> when <see langword="null"/>.
    /// </param>
    public DelegatingServiceHealth(string name, Func<HealthEvaluation> healthCheck, AggregationStrategy? aggregator = null)
    {
        Name = name;
        _tracker = new ServiceHealthTracker(healthCheck, aggregator);
    }

    /// <summary>Shortcut: a service whose intrinsic status is always healthy.</summary>
    public DelegatingServiceHealth(string name)
        : this(name, () => HealthStatus.Healthy) { }

    /// <summary>Registers a dependency on another service.</summary>
    public DelegatingServiceHealth DependsOn(IServiceHealth service, ServiceImportance importance)
    {
        _tracker.DependsOn(service, importance);
        return this;
    }

    public void NotifyChanged() => _tracker.NotifyChanged();

    public HealthEvaluation Evaluate() => _tracker.Evaluate();

    public override string ToString() => $"{Name}: {Evaluate()}";
}
