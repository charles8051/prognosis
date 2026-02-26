namespace Prognosis;

/// <summary>
/// Adapts any external or closed service into the health graph by wrapping a
/// <c>Func&lt;HealthEvaluation&gt;</c>. Use this when you cannot (or prefer not to)
/// modify the service class itself.
/// <para>
/// This is the recommended way for consumers to participate in the health graph.
/// Embed a <see cref="DelegateHealthNode"/> as a property on your service
/// class and pass it when composing the graph.
/// </para>
/// </summary>
public sealed class DelegateHealthNode : HealthNode
{
    public override string Name { get; }

    /// <param name="name">Display name for the service.</param>
    /// <param name="healthCheck">
    /// A delegate that returns the service's intrinsic health evaluation.
    /// Called every time <see cref="Evaluate"/> is invoked.
    /// </param>
    public DelegateHealthNode(string name, Func<HealthEvaluation> healthCheck)
        : base(healthCheck)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A service must have a name.", nameof(name));

        Name = name;
    }

    /// <summary>Shortcut: a service whose intrinsic status is always healthy.</summary>
    public DelegateHealthNode(string name)
        : this(name, () => HealthStatus.Healthy) { }

    /// <summary>Registers a dependency on another service.</summary>
    public new DelegateHealthNode DependsOn(HealthNode node, Importance importance)
    {
        base.DependsOn(node, importance);
        return this;
    }

    public override string ToString() => $"{Name}: {Evaluate()}";
}
