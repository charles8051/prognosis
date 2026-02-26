namespace Prognosis;

/// <summary>
/// A serialization-friendly, point-in-time health report for the entire
/// service graph. Intended to be the top-level payload for HTTP responses.
/// </summary>
public sealed record HealthReport(
    DateTimeOffset Timestamp,
    HealthStatus OverallStatus,
    IReadOnlyList<HealthSnapshot> Services)
{
    /// <summary>
    /// Compares this report (the baseline) with a <paramref name="newer"/> report
    /// and returns a change record for every service whose <see cref="HealthStatus"/>
    /// differs, including services that appeared in or disappeared from the graph.
    /// </summary>
    public IReadOnlyList<StatusChange> DiffTo(HealthReport newer)
    {
        var previousByName = new Dictionary<string, HealthSnapshot>(Services.Count);
        foreach (var snapshot in Services)
        {
            previousByName[snapshot.Name] = snapshot;
        }

        var changes = new List<StatusChange>();

        foreach (var curr in newer.Services)
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
