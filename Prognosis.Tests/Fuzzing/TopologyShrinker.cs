namespace Prognosis.Tests.Fuzzing;

/// <summary>
/// Greedy delta-debugging over a <see cref="TopologySpec"/>. A raw counterexample from
/// the generator is typically 10–25 nodes of noise around a 3-node core; this walks it
/// down to something a human can read in one glance.
/// <para>
/// Every candidate edit strictly decreases <see cref="Score"/> (a lexicographic
/// node/edge/severity/importance tuple), so the search terminates regardless of how the
/// predicate behaves.
/// </para>
/// </summary>
public static class TopologyShrinker
{
    /// <summary>
    /// Returns the smallest spec reachable from <paramref name="spec"/> by node/edge
    /// removal and status/importance weakening for which <paramref name="stillFails"/>
    /// keeps returning <see langword="true"/>.
    /// </summary>
    /// <param name="spec">The original counterexample. Must already fail.</param>
    /// <param name="stillFails">
    /// Re-runs the property. Must be side-effect free and must return
    /// <see langword="true"/> only for the <em>same</em> failure — otherwise shrinking
    /// happily migrates to a different, less interesting bug.
    /// </param>
    /// <param name="budget">Maximum predicate evaluations.</param>
    public static TopologySpec Shrink(
        TopologySpec spec,
        Func<TopologySpec, bool> stillFails,
        int budget = 4000)
    {
        var best = spec;
        var spent = 0;
        var improved = true;

        while (improved && spent < budget)
        {
            improved = false;

            foreach (var candidate in Candidates(best))
            {
                if (spent++ >= budget)
                    break;

                if (Compare(Score(candidate), Score(best)) >= 0)
                    continue;

                if (!stillFails(candidate))
                    continue;

                best = candidate;
                improved = true;
                break; // Restart the pass from the new, smaller best.
            }
        }

        return best;
    }

    /// <summary>
    /// Candidate simplifications, cheapest-payoff-first: dropping a node can prune a
    /// whole orphaned subtree, so those come before edge and label edits.
    /// </summary>
    private static IEnumerable<TopologySpec> Candidates(TopologySpec spec)
    {
        for (var i = spec.Count - 1; i >= 1; i--)
            yield return spec.WithoutNode(i);

        for (var node = 0; node < spec.Count; node++)
        {
            for (var edge = spec.Nodes[node].Edges.Count - 1; edge >= 0; edge--)
                yield return spec.WithoutEdge(node, edge);
        }

        for (var node = 0; node < spec.Count; node++)
        {
            foreach (var status in new[]
                     { HealthStatus.Healthy, HealthStatus.Unknown, HealthStatus.Degraded })
            {
                if (spec.Nodes[node].Intrinsic != status)
                    yield return spec.WithIntrinsic(node, status);
            }
        }

        for (var node = 0; node < spec.Count; node++)
        {
            for (var edge = 0; edge < spec.Nodes[node].Edges.Count; edge++)
            {
                foreach (var importance in new[] { Importance.Optional, Importance.Required })
                {
                    if (spec.Nodes[node].Edges[edge].Importance != importance)
                        yield return spec.WithImportance(node, edge, importance);
                }
            }
        }
    }

    /// <summary>
    /// The lexicographic size of a spec: node count, then edge count, then total status
    /// severity, then total importance complexity. Strictly decreasing on every accepted
    /// edit, which is what makes the greedy loop terminate.
    /// </summary>
    private static (int Nodes, int Edges, int Severity, int Importance) Score(TopologySpec spec) =>
        (spec.Count,
         spec.EdgeCount,
         spec.Nodes.Sum(n => SeverityRank(n.Intrinsic)),
         spec.Nodes.Sum(n => n.Edges.Sum(e => ImportanceRank(e.Importance))));

    private static int Compare(
        (int Nodes, int Edges, int Severity, int Importance) a,
        (int Nodes, int Edges, int Severity, int Importance) b)
    {
        if (a.Nodes != b.Nodes) return a.Nodes.CompareTo(b.Nodes);
        if (a.Edges != b.Edges) return a.Edges.CompareTo(b.Edges);
        if (a.Severity != b.Severity) return a.Severity.CompareTo(b.Severity);
        return a.Importance.CompareTo(b.Importance);
    }

    private static int SeverityRank(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 0,
        HealthStatus.Unknown => 1,
        HealthStatus.Degraded => 2,
        HealthStatus.Unhealthy => 3,
        _ => 4,
    };

    // Optional first (an inert edge is the simplest thing an edge can be), then
    // Required (plain pass-through, the easiest rule to reason about by hand).
    private static int ImportanceRank(Importance importance) => importance switch
    {
        Importance.Optional => 0,
        Importance.Required => 1,
        Importance.Important => 2,
        Importance.Advisory => 3,
        Importance.Resilient => 4,
        _ => 5,
    };
}
