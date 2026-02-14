using System.Text.Json.Serialization;

namespace Prognosis;

/// <summary>
/// Describes how important a dependency is to its parent service.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceImportance
{
    /// <summary>
    /// The parent cannot function without this dependency.
    /// Any non-healthy state propagates directly.
    /// </summary>
    Required,

    /// <summary>
    /// Degradation or failure is significant but not fatal.
    /// An unhealthy dependency causes the parent to be degraded.
    /// </summary>
    Important,

    /// <summary>
    /// The dependency is nice-to-have.
    /// Its failure has no effect on the parent's reported health.
    /// </summary>
    Optional,
}
