namespace Prognosis;

/// <summary>
/// A point-in-time capture of a single service's evaluated health.
/// </summary>
public sealed record HealthSnapshot(string Name, HealthStatus Status, int DependencyCount, string? Reason = null)
{
    public override string ToString()
    {
        var summary = DependencyCount > 0
            ? $"{Name}: {Status} ({DependencyCount} dependencies)"
            : $"{Name}: {Status}";

        return Reason is not null ? $"{summary} — {Reason}" : summary;
    }
}
