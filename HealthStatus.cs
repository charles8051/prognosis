using System.Text.Json.Serialization;

namespace Prognosis;

/// <summary>
/// Represents the health state of a service, ordered from worst to best
/// so that <c>Math.Max</c> / comparisons naturally pick the worst status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus
{
    Healthy = 0,

    /// <summary>
    /// Not-yet-determined startup state (not yet probed). Ranks below
    /// <see cref="Degraded"/> / <see cref="Unhealthy"/>, so per ADR-006
    /// (docs/adr/006-unknown-non-gating-rollup.md) it is strictly non-gating
    /// in the rollup: an <c>Unknown</c> dependency raises its parent at most
    /// to <c>Unknown</c>, never to <see cref="Degraded"/> or <see cref="Unhealthy"/>,
    /// regardless of <see cref="Importance"/>.
    /// </summary>
    Unknown = 1,

    Degraded = 2,
    Unhealthy = 3,
}
