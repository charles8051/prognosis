namespace Prognosis;

/// <summary>
/// The result of evaluating a service's health — pairs a <see cref="HealthStatus"/>
/// with an optional human-readable reason explaining why the service is in that state.
/// </summary>
/// <param name="Status">The evaluated health status.</param>
/// <param name="Reason">
/// An optional explanation (e.g. "Connection pool exhausted").
/// Typically <see langword="null"/> when <see cref="Status"/> is <see cref="HealthStatus.Healthy"/>.
/// </param>
public sealed record HealthEvaluation(HealthStatus Status, string? Reason = null)
{
    /// <summary>A healthy evaluation with no reason.</summary>
    public static HealthEvaluation Healthy { get; } = new(HealthStatus.Healthy);

    /// <summary>Creates an unhealthy evaluation with the given reason.</summary>
    public static HealthEvaluation Unhealthy(string reason) => new(HealthStatus.Unhealthy, reason);

    /// <summary>Creates a degraded evaluation with the given reason.</summary>
    public static HealthEvaluation Degraded(string reason) => new(HealthStatus.Degraded, reason);

    /// <summary>
    /// Allows returning a bare <see cref="HealthStatus"/> from any method that
    /// expects a <see cref="HealthEvaluation"/>, treating it as a reason-less result.
    /// </summary>
    public static implicit operator HealthEvaluation(HealthStatus status) => new(status);

    public override string ToString() =>
        Reason is not null ? $"{Status}: {Reason}" : Status.ToString();
}
