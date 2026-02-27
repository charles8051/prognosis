namespace Prognosis;

/// <summary>
/// Describes a structural change to the health graph — which nodes became
/// reachable and which were removed since the previous topology snapshot.
/// Emitted by <see cref="HealthGraph.TopologyChanged"/>.
/// </summary>
public sealed class TopologyChange
{
    /// <summary>Nodes that became reachable from the root.</summary>
    public IReadOnlyList<HealthNode> Added { get; }

    /// <summary>Nodes that are no longer reachable from the root.</summary>
    public IReadOnlyList<HealthNode> Removed { get; }

    internal TopologyChange(
        IReadOnlyList<HealthNode> added,
        IReadOnlyList<HealthNode> removed)
    {
        Added = added;
        Removed = removed;
    }
}
