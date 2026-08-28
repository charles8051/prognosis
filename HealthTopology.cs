namespace Prognosis;

/// <summary>
/// A directed edge in a <see cref="HealthTopology"/> — the name of the
/// dependency node plus the <see cref="Importance"/> the parent assigns to it.
/// </summary>
/// <param name="Name">The dependency node's <see cref="HealthNode.Name"/>.</param>
/// <param name="Importance">How failures in this dependency propagate to the parent.</param>
public sealed record HealthTopologyEdge(string Name, Importance Importance);

/// <summary>
/// A point-in-time capture of the graph's <em>structure</em> only — the root
/// name and, per node, its ordered dependency edges with their
/// <see cref="Importance"/> weights. Carries no statuses: statuses arrive
/// per beat via <see cref="HealthReport"/>, and the two recombine into a
/// <see cref="HealthTreeSnapshot"/> through
/// <c>HealthGraphAnalysis.BuildTreeSnapshot(report, topology)</c> (ADR-009).
/// <para>
/// Obtain via <see cref="HealthGraph.GetTopology"/> or from
/// <see cref="TopologyChange.Topology"/>. Structure changes only on topology
/// mutation, so consumers hold one instance and replace it on
/// <see cref="HealthGraph.TopologyChanged"/> — never per beat.
/// </para>
/// </summary>
/// <param name="Root">The root node's name.</param>
/// <param name="Edges">
/// Every node reachable from the root, keyed by name, mapped to its direct
/// dependency edges. Edge order matches <see cref="HealthNode.Dependencies"/>
/// order — this is contract, not incident: tree reconstruction walks edges
/// pre-order, so order determines which occurrence of a diamond/cycle node
/// carries the expanded subtree. Leaf nodes map to an empty list.
/// </param>
public sealed record HealthTopology(
    string Root,
    IReadOnlyDictionary<string, IReadOnlyList<HealthTopologyEdge>> Edges);

/// <summary>
/// Compares two <see cref="HealthTopology"/> instances structurally: same root,
/// same node set, and per node the same dependency edges in the same order with
/// the same <see cref="Importance"/>. Record equality alone compares the
/// <c>Edges</c> dictionary by reference, so <see cref="HealthGraph"/> uses this
/// comparer to decide whether a propagation wave structurally changed the graph
/// (ADR-009). Order-independent across nodes, order-sensitive within a node's
/// edge list (edge order is part of the topology contract).
/// </summary>
public sealed class HealthTopologyComparer : IEqualityComparer<HealthTopology>
{
    public static readonly HealthTopologyComparer Instance = new();

    public bool Equals(HealthTopology? x, HealthTopology? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        if (!string.Equals(x.Root, y.Root, StringComparison.Ordinal))
            return false;
        if (x.Edges.Count != y.Edges.Count)
            return false;

        foreach (var kvp in x.Edges)
        {
            if (!y.Edges.TryGetValue(kvp.Key, out var otherEdges))
                return false;

            var edges = kvp.Value;
            if (edges.Count != otherEdges.Count)
                return false;

            for (var i = 0; i < edges.Count; i++)
            {
                if (edges[i] != otherEdges[i])
                    return false;
            }
        }

        return true;
    }

    public int GetHashCode(HealthTopology obj)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(obj.Root);
            hash = hash * 31 + obj.Edges.Count;

            // XOR is commutative — order-independent across nodes.
            var nodeHash = 0;
            foreach (var kvp in obj.Edges)
            {
                var perNode = StringComparer.Ordinal.GetHashCode(kvp.Key);
                foreach (var edge in kvp.Value)
                    perNode = perNode * 397 ^ edge.GetHashCode();
                nodeHash ^= perNode;
            }
            hash = hash * 31 + nodeHash;
            return hash;
        }
    }
}
