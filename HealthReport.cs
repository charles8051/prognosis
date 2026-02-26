namespace Prognosis;

/// <summary>
/// A point-in-time evaluation of a node and its full dependency subgraph.
/// Can be created from any <see cref="HealthNode"/>; when created from the
/// root the report covers the entire service graph.
/// </summary>
public sealed record HealthReport(IReadOnlyList<HealthSnapshot> Nodes)
{
    /// <summary>
    /// Returns the worst <see cref="HealthStatus"/> across all nodes,
    /// or <see cref="HealthStatus.Healthy"/> if the graph is empty.
    /// </summary>
    public HealthStatus OverallStatus =>
        Nodes.Count > 0 ? Nodes.Max(s => s.Status) : HealthStatus.Healthy;

    /// <summary>
    /// Compares this report (the baseline) with a <paramref name="newer"/> report
    /// and returns a change record for every service whose <see cref="HealthStatus"/>
    /// differs, including services that appeared in or disappeared from the graph.
    /// </summary>
    public IReadOnlyList<StatusChange> DiffTo(HealthReport newer)
    {
        var previousByName = new Dictionary<string, HealthSnapshot>(Nodes.Count);
        foreach (var snapshot in Nodes)
        {
            previousByName[snapshot.Name] = snapshot;
        }

        var changes = new List<StatusChange>();

        foreach (var curr in newer.Nodes)
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
                changes.Add(new StatusChange(
                    curr.Name, HealthStatus.Unknown, curr.Status, curr.Reason));
            }
        }

        foreach (var removed in previousByName.Values)
        {
            changes.Add(new StatusChange(
                removed.Name, removed.Status, HealthStatus.Unknown, "Service removed from graph"));
        }

        return changes;
    }
}
