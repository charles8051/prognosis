namespace Prognosis;

/// <summary>
/// A point-in-time capture of a single service's evaluated health.
/// </summary>
/// <param name="Name">Display name of the service.</param>
/// <param name="Status">The evaluated health status.</param>
/// <param name="Reason">Optional human-readable explanation for a non-healthy status.</param>
/// <param name="Tags">
/// Arbitrary string metadata copied from <see cref="HealthNode.Tags"/> at
/// report-build time. <see langword="null"/> when the node has no tags.
/// </param>
public sealed record HealthSnapshot(
    string Name,
    HealthStatus Status,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Tags = null)
{
    public override string ToString() =>
        Reason is not null ? $"{Name}: {Status} — {Reason}" : $"{Name}: {Status}";
}
