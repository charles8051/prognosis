namespace Prognosis;

/// <summary>
/// Compares two <see cref="HealthReport"/> instances for equality based on
/// overall status and per-service snapshots. Used by the core
/// <see cref="HealthMonitor"/> and Rx operators like
/// <c>DistinctUntilChanged</c> to suppress duplicate emissions.
/// Explicitly does not consider the timestamp.
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
        if (x.OverallStatus != y.OverallStatus)
            return false;
        if (x.Services.Count != y.Services.Count)
            return false;

        for (var i = 0; i < x.Services.Count; i++)
        {
            if (x.Services[i] != y.Services[i])
                return false;
        }

        return true;
    }

    public int GetHashCode(HealthReport obj)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + obj.OverallStatus.GetHashCode();
            hash = hash * 31 + obj.Services.Count;
            foreach (var svc in obj.Services)
            {
                hash = hash * 31 + svc.Name.GetHashCode();
                hash = hash * 31 + svc.Status.GetHashCode();
            }
            return hash;
        }
    }
}
