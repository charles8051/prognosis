namespace Prognosis;

/// <summary>
/// Resolves the effective <see cref="HealthStatus"/> for a service given its
/// intrinsic evaluation and a set of weighted dependencies.
/// </summary>
public static class HealthAggregator
{
    /// <summary>
    /// Computes the worst-case health across the intrinsic evaluation and every
    /// dependency, with the propagation rules driven by <see cref="ServiceImportance"/>.
    /// </summary>
    public static HealthEvaluation Aggregate(
        HealthEvaluation intrinsic,
        IReadOnlyList<ServiceDependency> dependencies)
    {
        var effective = intrinsic.Status;
        string? reason = intrinsic.Reason;

        foreach (var dep in dependencies)
        {
            var depEval = dep.Service.Evaluate();

            var contribution = dep.Importance switch
            {
                // Required: the dependency's status passes through as-is.
                ServiceImportance.Required => depEval.Status,

                // Important: unhealthy is capped at degraded; unknown and degraded pass through.
                ServiceImportance.Important => depEval.Status switch
                {
                    HealthStatus.Unhealthy => HealthStatus.Degraded,
                    _ => depEval.Status,
                },

                // Optional: never affects the parent.
                ServiceImportance.Optional => HealthStatus.Healthy,

                _ => HealthStatus.Healthy,
            };

            if (contribution > effective)
            {
                effective = contribution;
                reason = depEval.Reason is not null
                    ? $"{dep.Service.Name}: {depEval.Reason}"
                    : $"{dep.Service.Name} is {depEval.Status}";
            }
        }

        return new HealthEvaluation(effective, reason);
    }

    /// <summary>
    /// Aggregation strategy for services with redundant dependencies.
    /// When at least one non-optional dependency is healthy, a required dependency's
    /// <see cref="HealthStatus.Unhealthy"/> is capped at <see cref="HealthStatus.Degraded"/>
    /// instead of propagating through. If <em>all</em> non-optional dependencies are
    /// unhealthy the parent becomes unhealthy as usual.
    /// </summary>
    /// <remarks>
    /// Use this when a parent has multiple paths to the same capability (e.g. primary +
    /// secondary database) and losing one should degrade — not kill — the parent.
    /// </remarks>
    public static HealthEvaluation AggregateWithRedundancy(
        HealthEvaluation intrinsic,
        IReadOnlyList<ServiceDependency> dependencies)
    {
        // First pass: evaluate all dependencies and determine whether any
        // non-optional dependency is healthy.
        var depCount = dependencies.Count;
        var evals = new (ServiceDependency dep, HealthEvaluation eval)[depCount];
        var hasHealthyNonOptional = false;

        for (var i = 0; i < depCount; i++)
        {
            var dep = dependencies[i];
            var eval = dep.Service.Evaluate();
            evals[i] = (dep, eval);

            if (dep.Importance != ServiceImportance.Optional && eval.Status == HealthStatus.Healthy)
                hasHealthyNonOptional = true;
        }

        // Second pass: compute effective status using the redundancy rule.
        var effective = intrinsic.Status;
        string? reason = intrinsic.Reason;

        for (var i = 0; i < depCount; i++)
        {
            var (dep, depEval) = evals[i];

            var contribution = dep.Importance switch
            {
                ServiceImportance.Required when depEval.Status == HealthStatus.Unhealthy && hasHealthyNonOptional
                    => HealthStatus.Degraded,
                ServiceImportance.Required
                    => depEval.Status,

                ServiceImportance.Important => depEval.Status switch
                {
                    HealthStatus.Unhealthy => HealthStatus.Degraded,
                    _ => depEval.Status,
                },

                ServiceImportance.Optional => HealthStatus.Healthy,

                _ => HealthStatus.Healthy,
            };

            if (contribution > effective)
            {
                effective = contribution;
                reason = depEval.Reason is not null
                    ? $"{dep.Service.Name}: {depEval.Reason}"
                    : $"{dep.Service.Name} is {depEval.Status}";
            }
        }

        return new HealthEvaluation(effective, reason);
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
    /// Compares two reports and returns a change record for every service
    /// whose <see cref="HealthStatus"/> differs between them.
    /// </summary>
    public static IReadOnlyList<ServiceStatusChange> Diff(
        HealthReport previous,
        HealthReport current)
    {
        var previousByName = new Dictionary<string, ServiceSnapshot>(previous.Services.Count);
        foreach (var snapshot in previous.Services)
        {
            previousByName[snapshot.Name] = snapshot;
        }

        var changes = new List<ServiceStatusChange>();

        foreach (var curr in current.Services)
        {
            if (previousByName.TryGetValue(curr.Name, out var prev))
            {
                if (prev.Status != curr.Status)
                {
                    changes.Add(new ServiceStatusChange(
                        curr.Name, prev.Status, curr.Status, curr.Reason));
                }
            }
            else
            {
                // New service appeared in the graph.
                changes.Add(new ServiceStatusChange(
                    curr.Name, HealthStatus.Unknown, curr.Status, curr.Reason));
            }
        }

        return changes;
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

    /// <summary>
    /// Walks the dependency graph depth-first from the given roots and calls
    /// <see cref="IObservableServiceHealth.NotifyChanged"/> on every observable
    /// service encountered. Leaves are notified before their parents.
    /// </summary>
    public static void NotifyGraph(params IServiceHealth[] roots)
    {
        var visited = new HashSet<IServiceHealth>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
        {
            NotifyGraphDfs(root, visited);
        }
    }

    private static void NotifyGraphDfs(IServiceHealth service, HashSet<IServiceHealth> visited)
    {
        if (!visited.Add(service))
            return;

        // Depth-first: notify leaves before parents.
        foreach (var dep in service.Dependencies)
        {
            NotifyGraphDfs(dep.Service, visited);
        }

        if (service is IObservableServiceHealth observable)
        {
            observable.NotifyChanged();
        }
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

        var eval = service.Evaluate();
        results.Add(new ServiceSnapshot(service.Name, eval.Status, service.Dependencies.Count, eval.Reason));
    }
}
