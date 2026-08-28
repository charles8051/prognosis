using System.Text.Json.Serialization;

namespace Prognosis;

/// <summary>
/// Describes how important a dependency is to its parent service.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Importance
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

    /// <summary>
    /// The dependency is required but has resilient peers.
    /// If at least one sibling <see cref="Resilient"/> dependency is healthy,
    /// an unhealthy status is capped at <c>Degraded</c> rather than propagating directly.
    /// If all <see cref="Resilient"/> siblings are unhealthy, the parent becomes unhealthy.
    /// </summary>
    Resilient,

    /// <summary>
    /// The dependency is observed, but the parent does not depend on that observation being
    /// available. Like <see cref="Important"/> — unhealthy degrades the parent — except an
    /// <see cref="HealthStatus.Unknown"/> child is absorbed rather than propagated.
    /// Rationale and trade-offs: ADR-008 (docs/adr/008-unknown-must-be-transient.md).
    /// <para>
    /// <b>When this differs from <see cref="Important"/>.</b> Only while the child is
    /// <see cref="HealthStatus.Unknown"/> — and ADR-008 §1 forbids a node resting there, so
    /// for a probe that honours the contract the two levels are indistinguishable. Reach for
    /// <see cref="Advisory"/> on an edge to a <em>leased</em> observational node, whose
    /// stage-one decay (ADR-010, between <c>Ttl</c> and <c>Ttl + EscalateAfter</c>) and
    /// never-affirmed seed are expected, bounded <see cref="HealthStatus.Unknown"/> windows
    /// rather than startup artefacts. See ADR-008's 2026-08-13 amendment.
    /// </para>
    /// </summary>
    Advisory,
}
