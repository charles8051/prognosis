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
        // Compare every snapshot — root and nodes — on the report-equality key
        // (Name, Status, Reason) explicitly (ADR-012 §1/§3). Deliberately NOT the
        // record `==`, which would drag Tags into the key; Tags are node identity,
        // carry no health signal, and are excluded from report-change detection.
        if (!SnapshotKeyEquals(x.Root, y.Root))
            return false;
        if (x.Nodes.Count != y.Nodes.Count)
            return false;

        var lookup = new Dictionary<string, HealthSnapshot>(x.Nodes.Count, StringComparer.Ordinal);
        foreach (var svc in x.Nodes)
            lookup[svc.Name] = svc;

        foreach (var svc in y.Nodes)
        {
            if (!lookup.TryGetValue(svc.Name, out var other) || !SnapshotKeyEquals(other, svc))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Equality on the report-equality key (Name, Status, Reason); Tags excluded.
    /// </summary>
    private static bool SnapshotKeyEquals(HealthSnapshot a, HealthSnapshot b)
        => string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && a.Status == b.Status
            && string.Equals(a.Reason, b.Reason, StringComparison.Ordinal);

    /// <summary>Hash of a snapshot on the same (Name, Status, Reason) key.</summary>
    private static int SnapshotKeyHash(HealthSnapshot s)
    {
        unchecked
        {
            var h = s.Name.GetHashCode() * 397 ^ s.Status.GetHashCode();
            return h * 31 + (s.Reason?.GetHashCode() ?? 0);
        }
    }

    public int GetHashCode(HealthReport obj)
    {
        unchecked
        {
            var hash = 17;
            // Hash the SAME fields Equals compares — (Name, Status, Reason) per
            // snapshot (ADR-012 §2) — retiring the old Name+Status-only node hash and
            // the record hash on the root. Those were valid (a strict coarsening of
            // the equality key) but misled every reader who inferred from the hash
            // that Reason does not participate in equality; it does. Tags are
            // excluded, matching Equals. This changes hash values, not the
            // equivalence relation.
            hash = hash * 31 + SnapshotKeyHash(obj.Root);
            hash = hash * 31 + obj.Nodes.Count;

            // XOR is commutative — order-independent.
            var nodeHash = 0;
            foreach (var svc in obj.Nodes)
                nodeHash ^= SnapshotKeyHash(svc);
            hash = hash * 31 + nodeHash;
            return hash;
        }
    }
}
