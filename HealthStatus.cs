using System.Text.Json.Serialization;

namespace ServiceHealthModel;

/// <summary>
/// Represents the health state of a service, ordered from worst to best
/// so that <c>Math.Max</c> / comparisons naturally pick the worst status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2,
}
