namespace Prognosis;

/// <summary>
/// A virtual service whose health is derived entirely from its dependencies.
/// It has no underlying service of its own — it is a named aggregation point.
/// </summary>
public sealed class HealthGroup : HealthNode
{
    private readonly HealthTracker _tracker;

    public override string Name { get; }

    public override IReadOnlyList<HealthDependency> Dependencies => _tracker.Dependencies;

    public override IObservable<HealthStatus> StatusChanged => _tracker.StatusChanged;

    /// <param name="name">Display name for this composite in the health graph.</param>
    /// <param name="aggregator">
    /// Strategy used to combine dependency evaluations into an effective health.
    /// Defaults to <see cref="HealthAggregator.Aggregate"/> when <see langword="null"/>.
    /// </param>
    public HealthGroup(string name, AggregationStrategy? aggregator = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A composite service must have a name.", nameof(name));

        Name = name;
        _tracker = new HealthTracker(() => HealthStatus.Healthy, aggregator);
    }

    public HealthGroup(
        string name,
        IReadOnlyList<HealthDependency> dependencies,
        AggregationStrategy? aggregator = null)
        : this(name, aggregator)
    {
        foreach (var dep in dependencies)
        {
            DependsOn(dep.Service, dep.Importance);
        }
    }

    private protected override void AddDependency(HealthNode node, Importance importance)
        => _tracker.DependsOn(node, importance);

    private protected override bool RemoveDependencyCore(HealthNode node)
        => _tracker.RemoveDependency(node);

    /// <summary>Registers a dependency on another service.</summary>
    public new HealthGroup DependsOn(HealthNode node, Importance importance)
    {
        base.DependsOn(node, importance);
        return this;
    }

    public override void NotifyChanged() => _tracker.NotifyChanged();

    public override HealthEvaluation Evaluate() => _tracker.Evaluate();

    public override string ToString() => $"{Name}: {Evaluate()}";
}
