namespace ServiceHealthModel;

/// <summary>
/// Resolves the effective <see cref="HealthStatus"/> for a service given its
/// intrinsic status and a set of weighted dependencies.
/// </summary>
public static class HealthAggregator
{
    /// <summary>
    /// Computes the worst-case health across the intrinsic status and every
    /// dependency, with the propagation rules driven by <see cref="ServiceImportance"/>.
    /// </summary>
    public static HealthStatus Aggregate(
        HealthStatus intrinsicStatus,
        IReadOnlyList<ServiceDependency> dependencies)
    {
        var effective = intrinsicStatus;

        foreach (var dep in dependencies)
        {
            var depStatus = dep.Service.Evaluate();

            var contribution = dep.Importance switch
            {
                // Required: the dependency's status passes through as-is.
                ServiceImportance.Required => depStatus,

                // Important: unhealthy is capped at degraded; degraded passes through.
                ServiceImportance.Important => depStatus switch
                {
                    HealthStatus.Unhealthy => HealthStatus.Degraded,
                    _ => depStatus,
                },

                // Optional: never affects the parent.
                ServiceImportance.Optional => HealthStatus.Healthy,

                _ => HealthStatus.Healthy,
            };

            if (contribution > effective)
                effective = contribution;
        }

        return effective;
    }
}
