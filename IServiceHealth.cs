namespace Prognosis;

/// <summary>
/// Common contract for anything that can report its health.
/// Implementations may represent real services, composite aggregations, or both.
/// </summary>
public interface IServiceHealth
{
    string Name { get; }

    /// <summary>
    /// Zero or more services this service depends on, each tagged with an importance level.
    /// </summary>
    IReadOnlyList<ServiceDependency> Dependencies { get; }

    /// <summary>
    /// The effective health of this service, taking its own state and all
    /// dependency statuses (weighted by importance) into account.
    /// </summary>
    HealthStatus Evaluate();
}
