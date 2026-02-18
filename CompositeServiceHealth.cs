namespace Prognosis;

/// <summary>
/// A virtual service whose health is derived entirely from its dependencies.
/// It has no underlying service of its own — it is a named aggregation point.
/// </summary>
public sealed class CompositeServiceHealth : ServiceHealth
{
    private readonly ServiceHealthTracker _tracker;

    public override string Name { get; }

    public override IReadOnlyList<ServiceDependency> Dependencies => _tracker.Dependencies;

    public override IObservable<HealthStatus> StatusChanged => _tracker.StatusChanged;

    /// <param name="name">Display name for this composite in the health graph.</param>
    /// <param name="aggregator">
    /// Strategy used to combine dependency evaluations into an effective health.
    /// Defaults to <see cref="HealthAggregator.Aggregate"/> when <see langword="null"/>.
    /// </param>
    public CompositeServiceHealth(string name, AggregationStrategy? aggregator = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A composite service must have a name.", nameof(name));

        Name = name;
        _tracker = new ServiceHealthTracker(() => HealthStatus.Healthy, aggregator);
    }

    public CompositeServiceHealth(
        string name,
        IReadOnlyList<ServiceDependency> dependencies,
        AggregationStrategy? aggregator = null)
        : this(name, aggregator)
    {
        foreach (var dep in dependencies)
        {
            _tracker.DependsOn(dep.Service, dep.Importance);
        }
    }

    private protected override void AddDependency(ServiceHealth service, ServiceImportance importance)
        => _tracker.DependsOn(service, importance);

    /// <summary>Registers a dependency on another service.</summary>
    public new CompositeServiceHealth DependsOn(ServiceHealth service, ServiceImportance importance)
    {
        AddDependency(service, importance);
        return this;
    }

    public override void NotifyChanged() => _tracker.NotifyChanged();

    public override HealthEvaluation Evaluate() => _tracker.Evaluate();

    public override string ToString() => $"{Name}: {Evaluate()}";
}
