namespace Prognosis;

/// <summary>
/// A point-in-time capture of a single service's evaluated health.
/// </summary>
public sealed record HealthSnapshot(string Name, HealthStatus Status, string? Reason = null)
{
    public override string ToString() =>
        Reason is not null ? $"{Name}: {Status} — {Reason}" : $"{Name}: {Status}";
}
