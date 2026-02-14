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

                // Important: unhealthy is capped at degraded; unknown and degraded pass through.
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

    /// <summary>
    /// Evaluates the full graph and packages the result as a serialization-ready
    /// <see cref="HealthReport"/> with a timestamp and overall status.
    /// </summary>
    public static HealthReport CreateReport(params IServiceHealth[] roots)
    {
        var services = EvaluateAll(roots);
        var overall = services.Count > 0
            ? services.Max(s => s.Status)
            : HealthStatus.Healthy;

        return new HealthReport(DateTimeOffset.UtcNow, overall, services);
    }

    /// <summary>
    /// Walks the full dependency graph from one or more roots and returns every
    /// service's evaluated status. Results are in depth-first post-order
    /// (leaves before their parents) and each service appears at most once.
    /// </summary>
    public static IReadOnlyList<ServiceSnapshot> EvaluateAll(params IServiceHealth[] roots)
    {
        var visited = new HashSet<IServiceHealth>(ReferenceEqualityComparer.Instance);
        var results = new List<ServiceSnapshot>();

        foreach (var root in roots)
        {
            Walk(root, visited, results);
        }

        return results;
    }

    /// <summary>
    /// Performs a DFS from the given roots and returns every cycle found as an
    /// ordered list of service names (e.g. ["A", "B", "A"]).
    /// Returns an empty list when the graph is acyclic.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> DetectCycles(params IServiceHealth[] roots)
    {
        // Gray = currently on the DFS stack, Black = fully explored.
        var gray = new HashSet<IServiceHealth>(ReferenceEqualityComparer.Instance);
        var black = new HashSet<IServiceHealth>(ReferenceEqualityComparer.Instance);
        var path = new List<IServiceHealth>();
        var cycles = new List<IReadOnlyList<string>>();

        foreach (var root in roots)
        {
            DetectCyclesDfs(root, gray, black, path, cycles);
        }

        return cycles;
    }

    private static void DetectCyclesDfs(
        IServiceHealth service,
        HashSet<IServiceHealth> gray,
        HashSet<IServiceHealth> black,
        List<IServiceHealth> path,
        List<IReadOnlyList<string>> cycles)
    {
        if (black.Contains(service))
            return;

        if (!gray.Add(service))
        {
            // Back-edge found — extract the cycle from the path.
            var cycleStart = path.IndexOf(service);
            var cycle = new List<string>(path.Count - cycleStart + 1);
            for (var i = cycleStart; i < path.Count; i++)
            {
                cycle.Add(path[i].Name);
            }
            cycle.Add(service.Name); // close the loop
            cycles.Add(cycle);
            return;
        }

        path.Add(service);

        foreach (var dep in service.Dependencies)
        {
            DetectCyclesDfs(dep.Service, gray, black, path, cycles);
        }

        path.RemoveAt(path.Count - 1);
        gray.Remove(service);
        black.Add(service);
    }

    private static void Walk(
        IServiceHealth service,
        HashSet<IServiceHealth> visited,
        List<ServiceSnapshot> results)
    {
        if (!visited.Add(service))
            return;

        foreach (var dep in service.Dependencies)
        {
            Walk(dep.Service, visited, results);
        }

        results.Add(new ServiceSnapshot(service.Name, service.Evaluate(), service.Dependencies.Count));
    }
}
