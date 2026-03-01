namespace Prognosis;

/// <summary>
/// Compares two <see cref="HealthReport"/> instances for equality based on
/// per-node snapshots, matched by name. Used by the core
/// <see cref="HealthMonitor"/> and Rx operators like
/// <c>DistinctUntilChanged</c> to suppress duplicate emissions.
/// Order-independent.
/// </summary>
public sealed class HealthReportComparer : IEqualityComparer<HealthReport>
{
    public static readonly HealthReportComparer Instance = new();

    public bool Equals(HealthReport? x, HealthReport? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        if (x.Root != y.Root)
            return false;
        if (x.Nodes.Count != y.Nodes.Count)
            return false;

        var lookup = new Dictionary<string, HealthSnapshot>(x.Nodes.Count, StringComparer.Ordinal);
        foreach (var svc in x.Nodes)
            lookup[svc.Name] = svc;

        foreach (var svc in y.Nodes)
        {
            if (!lookup.TryGetValue(svc.Name, out var other) || other != svc)
                return false;
        }

        return true;
    }

    public int GetHashCode(HealthReport obj)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + obj.Root.GetHashCode();
            hash = hash * 31 + obj.Nodes.Count;

            // XOR is commutative — order-independent.
            var nodeHash = 0;
            foreach (var svc in obj.Nodes)
            {
                nodeHash ^= svc.Name.GetHashCode() * 397 ^ svc.Status.GetHashCode();
            }
            hash = hash * 31 + nodeHash;
            return hash;
        }
    }
}
