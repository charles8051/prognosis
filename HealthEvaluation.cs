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
    /// <summary>
    /// Allows returning a bare <see cref="HealthStatus"/> from any method that
    /// expects a <see cref="HealthEvaluation"/>, treating it as a reason-less result.
    /// </summary>
    public static implicit operator HealthEvaluation(HealthStatus status) => new(status);

    public override string ToString() =>
        Reason is not null ? $"{Status}: {Reason}" : Status.ToString();
}
