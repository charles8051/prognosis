namespace ServiceHealthModel;

/// <summary>
/// A virtual service whose health is derived entirely from its dependencies.
/// It has no underlying service of its own — it is a named aggregation point.
/// </summary>
public sealed class CompositeServiceHealth : IServiceHealth
{
    public string Name { get; }

    public IReadOnlyList<ServiceDependency> Dependencies { get; }

    public CompositeServiceHealth(
        string name,
        IReadOnlyList<ServiceDependency> dependencies)
    {
        Name = name;
        Dependencies = dependencies;
    }

    public HealthStatus Evaluate() =>
        HealthAggregator.Aggregate(HealthStatus.Healthy, Dependencies);

    public override string ToString() => $"{Name}: {Evaluate()}";
}
