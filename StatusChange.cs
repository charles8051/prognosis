namespace Prognosis;

/// <summary>
/// Describes a single service's status transition between two consecutive
/// health evaluations.
/// </summary>
public sealed record StatusChange(
    string Name,
    HealthStatus Previous,
    HealthStatus Current,
    string? Reason);
