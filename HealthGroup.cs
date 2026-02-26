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
    public HealthGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A composite service must have a name.", nameof(name));

        Name = name;
        _tracker = new HealthTracker(() => HealthStatus.Healthy);
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

    internal override void NotifyChangedCore() => _tracker.NotifyChanged();

    public override HealthEvaluation Evaluate() => _tracker.Evaluate();

    public override string ToString() => $"{Name}: {Evaluate()}";
}
