namespace Prognosis.Reactive;

/// <summary>
/// Compares two <see cref="HealthReport"/> instances for equality based on
/// overall status and per-service snapshots. Used by Rx operators like
/// <c>DistinctUntilChanged</c> to suppress duplicate emissions.
/// </summary>
internal sealed class HealthReportComparer : IEqualityComparer<HealthReport>
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

    public int GetHashCode(HealthReport obj) =>
        obj.OverallStatus.GetHashCode();
}
