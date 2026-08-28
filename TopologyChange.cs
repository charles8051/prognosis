namespace Prognosis;

/// <summary>
/// Describes a structural change to the health graph, emitted by
/// <see cref="HealthGraph.TopologyChanged"/>. Fires on <em>any</em> structural
/// change — nodes added or removed, edges added or removed, or an edge's
/// <see cref="Importance"/> updated (ADR-009). For edge-only changes (e.g.
/// <see cref="HealthNode.UpdateDependencyImportance"/>, or removing one edge
/// of a diamond whose node stays reachable) <see cref="Added"/> and
/// <see cref="Removed"/> are both empty; <see cref="Topology"/> always carries
/// the post-change structure.
/// </summary>
public sealed class TopologyChange
{
    /// <summary>Nodes that became reachable from the root. May be empty.</summary>
    public IReadOnlyList<HealthNode> Added { get; }

    /// <summary>Nodes that are no longer reachable from the root. May be empty.</summary>
    public IReadOnlyList<HealthNode> Removed { get; }

    /// <summary>
    /// The graph's structure after this change. Hold this (replacing it on each
    /// <see cref="HealthGraph.TopologyChanged"/> emission) and recombine with
    /// per-beat <see cref="HealthReport"/>s via
    /// <c>HealthGraphAnalysis.BuildTreeSnapshot(report, topology)</c> — no out-of-band
    /// capture needed (ADR-009).
    /// </summary>
    public HealthTopology Topology { get; }

    internal TopologyChange(
        IReadOnlyList<HealthNode> added,
        IReadOnlyList<HealthNode> removed,
        HealthTopology topology)
    {
        Added = added;
        Removed = removed;
        Topology = topology;
    }
}
