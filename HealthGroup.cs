namespace Prognosis;

/// <summary>
/// A virtual service whose health is derived entirely from its dependencies.
/// It has no underlying service of its own — it is a named aggregation point.
/// </summary>
public sealed class HealthGroup : HealthNode
{
    public override string Name { get; }

    /// <param name="name">Display name for this composite in the health graph.</param>
    public HealthGroup(string name)
        : base(() => HealthStatus.Healthy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A composite service must have a name.", nameof(name));

        Name = name;
    }

    /// <summary>Registers a dependency on another service.</summary>
    public new HealthGroup DependsOn(HealthNode node, Importance importance)
    {
        base.DependsOn(node, importance);
        return this;
    }

    public override string ToString() => $"{Name}: {Evaluate()}";
}
