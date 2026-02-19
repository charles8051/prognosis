namespace Prognosis;

/// <summary>
/// Adapts any external or closed service into the health graph by wrapping a
/// <c>Func&lt;HealthEvaluation&gt;</c>. Use this when you cannot (or prefer not to)
/// modify the service class itself.
/// <para>
/// This is the recommended way for consumers to participate in the health graph.
/// Embed a <see cref="HealthCheck"/> as a property on your service
/// class and pass it when composing the graph.
/// </para>
/// </summary>
public sealed class HealthCheck : HealthNode
{
    private readonly HealthTracker _tracker;

    public override string Name { get; }

    public override IReadOnlyList<HealthDependency> Dependencies => _tracker.Dependencies;

    public override IObservable<HealthStatus> StatusChanged => _tracker.StatusChanged;

    /// <param name="name">Display name for the service.</param>
    /// <param name="healthCheck">
    /// A delegate that returns the service's intrinsic health evaluation.
    /// Called every time <see cref="Evaluate"/> is invoked.
    /// </param>
    /// <param name="aggregator">
    /// Strategy used to combine intrinsic health with dependency evaluations.
    /// Defaults to <see cref="HealthAggregator.Aggregate"/> when <see langword="null"/>.
    /// </param>
    public HealthCheck(string name, Func<HealthEvaluation> healthCheck, AggregationStrategy? aggregator = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A service must have a name.", nameof(name));

        Name = name;
        _tracker = new HealthTracker(healthCheck, aggregator);
    }

    /// <summary>Shortcut: a service whose intrinsic status is always healthy.</summary>
    public HealthCheck(string name)
        : this(name, () => HealthStatus.Healthy) { }

    private protected override void AddDependency(HealthNode node, Importance importance)
        => _tracker.DependsOn(node, importance);

    private protected override bool RemoveDependencyCore(HealthNode node)
        => _tracker.RemoveDependency(node);

    /// <summary>Registers a dependency on another service.</summary>
    public new HealthCheck DependsOn(HealthNode node, Importance importance)
    {
        base.DependsOn(node, importance);
        return this;
    }

    public override void NotifyChanged() => _tracker.NotifyChanged();

    public override HealthEvaluation Evaluate() => _tracker.Evaluate();

    public override string ToString() => $"{Name}: {Evaluate()}";
}
