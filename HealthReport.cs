namespace Prognosis;

/// <summary>
/// A serialization-friendly, point-in-time health report for the entire
/// service graph. Intended to be the top-level payload for HTTP responses.
/// </summary>
public sealed record HealthReport(
    DateTimeOffset Timestamp,
    HealthStatus OverallStatus,
    IReadOnlyList<HealthSnapshot> Services);
