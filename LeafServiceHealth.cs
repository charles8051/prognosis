namespace ServiceHealthModel;

/// <summary>
/// A service that has its own intrinsic health (e.g., a database, an HTTP endpoint)
/// and may optionally depend on other services.
/// </summary>
public sealed class LeafServiceHealth : IServiceHealth
{
    public string Name { get; }

    /// <summary>
    /// Gets or sets the intrinsic status of this service.
    /// Update this to simulate the real service becoming healthy/degraded/unhealthy.
    /// </summary>
    public HealthStatus IntrinsicStatus { get; set; }

    public IReadOnlyList<ServiceDependency> Dependencies { get; }

    public LeafServiceHealth(
        string name,
        HealthStatus intrinsicStatus = HealthStatus.Healthy,
        IReadOnlyList<ServiceDependency>? dependencies = null)
    {
        Name = name;
        IntrinsicStatus = intrinsicStatus;
        Dependencies = dependencies ?? [];
    }

    public HealthStatus Evaluate() =>
        HealthAggregator.Aggregate(IntrinsicStatus, Dependencies);

    public override string ToString() => $"{Name}: {Evaluate()}";
}
