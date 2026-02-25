namespace Prognosis;

/// <summary>
/// Resolves the effective <see cref="HealthStatus"/> for a service given its
/// intrinsic evaluation and a set of weighted dependencies.
/// </summary>
public static class HealthAggregator
{
    /// <summary>
    /// Computes the worst-case health across the intrinsic evaluation and every
    /// dependency, with the propagation rules driven by <see cref="Importance"/>.
    /// </summary>
    public static HealthEvaluation Aggregate(
        HealthEvaluation intrinsic,
        IReadOnlyList<HealthDependency> dependencies)
    {
        var effective = intrinsic.Status;
        string? reason = intrinsic.Reason;

        foreach (var dep in dependencies)
        {
            var depEval = dep.Node.Evaluate();

            var contribution = dep.Importance switch
            {
                // Required: the dependency's status passes through as-is.
                Importance.Required => depEval.Status,

                // Important: unhealthy is capped at degraded; unknown and degraded pass through.
                Importance.Important => depEval.Status switch
                {
                    HealthStatus.Unhealthy => HealthStatus.Degraded,
                    _ => depEval.Status,
                },

                // Optional: never affects the parent.
                Importance.Optional => HealthStatus.Healthy,

                _ => HealthStatus.Healthy,
            };

            if (contribution > effective)
            {
                effective = contribution;
                reason = depEval.Reason is not null
                    ? $"{dep.Node.Name}: {depEval.Reason}"
                    : $"{dep.Node.Name} is {depEval.Status}";
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
        IReadOnlyList<HealthDependency> dependencies)
    {
        // First pass: evaluate all dependencies and determine whether any
        // non-optional dependency is healthy.
        var depCount = dependencies.Count;
        var evals = new (HealthDependency dep, HealthEvaluation eval)[depCount];
        var hasHealthyNonOptional = false;

        for (var i = 0; i < depCount; i++)
        {
            var dep = dependencies[i];
            var eval = dep.Node.Evaluate();
            evals[i] = (dep, eval);

            if (dep.Importance != Importance.Optional && eval.Status == HealthStatus.Healthy)
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
                Importance.Required when depEval.Status == HealthStatus.Unhealthy && hasHealthyNonOptional
                    => HealthStatus.Degraded,
                Importance.Required
                    => depEval.Status,

                Importance.Important => depEval.Status switch
                {
                    HealthStatus.Unhealthy => HealthStatus.Degraded,
                    _ => depEval.Status,
                },

                Importance.Optional => HealthStatus.Healthy,

                _ => HealthStatus.Healthy,
            };

            if (contribution > effective)
            {
                effective = contribution;
                reason = depEval.Reason is not null
                    ? $"{dep.Node.Name}: {depEval.Reason}"
                    : $"{dep.Node.Name} is {depEval.Status}";
            }
        }

        return new HealthEvaluation(effective, reason);
    }

    /// <summary>
    /// Evaluates the full graph and packages the result as a serialization-ready
    /// <see cref="HealthReport"/> with a timestamp and overall status.
    /// </summary>
    public static HealthReport CreateReport(params HealthNode[] roots)
    {
        var services = EvaluateAll(roots);
        var overall = services.Count > 0
            ? services.Max(s => s.Status)
            : HealthStatus.Healthy;

        return new HealthReport(DateTimeOffset.UtcNow, overall, services);
    }

    /// <summary>
    /// Compares two reports and returns a change record for every service
    /// whose <see cref="HealthStatus"/> differs between them, including
    /// services that appeared in or disappeared from the graph.
    /// </summary>
    public static IReadOnlyList<StatusChange> Diff(
        HealthReport previous,
        HealthReport current)
    {
        var previousByName = new Dictionary<string, HealthSnapshot>(previous.Services.Count);
        foreach (var snapshot in previous.Services)
        {
            previousByName[snapshot.Name] = snapshot;
        }

        var changes = new List<StatusChange>();

        foreach (var curr in current.Services)
        {
            if (previousByName.TryGetValue(curr.Name, out var prev))
            {
                if (prev.Status != curr.Status)
                {
                    changes.Add(new StatusChange(
                        curr.Name, prev.Status, curr.Status, curr.Reason));
                }

                previousByName.Remove(curr.Name);
            }
            else
            {
                // New service appeared in the graph.
                changes.Add(new StatusChange(
                    curr.Name, HealthStatus.Unknown, curr.Status, curr.Reason));
            }
        }

        // Services that were in the previous report but not the current one.
        foreach (var removed in previousByName.Values)
        {
            changes.Add(new StatusChange(
                removed.Name, removed.Status, HealthStatus.Unknown, "Service removed from graph"));
        }

        return changes;
    }

    /// <summary>
    /// Walks the full dependency graph from one or more roots and returns every
    /// service's evaluated status. Results are in depth-first post-order
    /// (leaves before their parents) and each service appears at most once.
    /// </summary>
    public static IReadOnlyList<HealthSnapshot> EvaluateAll(params HealthNode[] roots)
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var results = new List<HealthSnapshot>();

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
    public static IReadOnlyList<IReadOnlyList<string>> DetectCycles(params HealthNode[] roots)
    {
        // Gray = currently on the DFS stack, Black = fully explored.
        var gray = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var black = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var path = new List<HealthNode>();
        var cycles = new List<IReadOnlyList<string>>();

        foreach (var root in roots)
        {
            DetectCyclesDfs(root, gray, black, path, cycles);
        }

        return cycles;
    }

    private static void DetectCyclesDfs(
        HealthNode node,
        HashSet<HealthNode> gray,
        HashSet<HealthNode> black,
        List<HealthNode> path,
        List<IReadOnlyList<string>> cycles)
    {
        if (black.Contains(node))
            return;

        if (!gray.Add(node))
        {
            // Back-edge found — extract the cycle from the path.
            var cycleStart = path.IndexOf(node);
            var cycle = new List<string>(path.Count - cycleStart + 1);
            for (var i = cycleStart; i < path.Count; i++)
            {
                cycle.Add(path[i].Name);
            }
            cycle.Add(node.Name); // close the loop
            cycles.Add(cycle);
            return;
        }

        path.Add(node);

        foreach (var dep in node.Dependencies)
        {
            DetectCyclesDfs(dep.Node, gray, black, path, cycles);
        }

        path.RemoveAt(path.Count - 1);
        gray.Remove(node);
        black.Add(node);
    }

    /// <summary>
    /// Walks the dependency graph depth-first from the given roots and calls
    /// <see cref="HealthNode.NotifyChanged"/> on every node encountered.
    /// Leaves are notified before their parents.
    /// </summary>
    public static void NotifyGraph(params HealthNode[] roots)
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
        {
            NotifyGraphDfs(root, visited);
        }
    }

    private static void NotifyGraphDfs(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        // Depth-first: notify leaves before parents.
        foreach (var dep in node.Dependencies)
        {
            NotifyGraphDfs(dep.Node, visited);
        }

        node.NotifyChanged();
    }

    private static void Walk(
        HealthNode node,
        HashSet<HealthNode> visited,
        List<HealthSnapshot> results)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
        {
            Walk(dep.Node, visited, results);
        }

        var eval = node.Evaluate();
        results.Add(new HealthSnapshot(node.Name, eval.Status, node.Dependencies.Count, eval.Reason));
    }
}
