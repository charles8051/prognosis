namespace Prognosis;

/// <summary>
/// Describes a single service's status transition between two consecutive
/// health evaluations.
/// </summary>
public sealed record ServiceStatusChange(
    string Name,
    HealthStatus Previous,
    HealthStatus Current,
    string? Reason);
