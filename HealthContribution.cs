using System;

namespace Prognosis;

/// <summary>
/// The single source of truth for how a dependency's health contributes to its
/// parent, given the parent's <see cref="Importance"/> weighting on the edge.
/// <para>
/// Both the live rollup (<see cref="HealthNode.Aggregate"/>) and the pure
/// diagnostic re-fold in <c>Prognosis.Diagnostics</c> call this one helper, so
/// production aggregation and counterfactual analysis can never silently drift
/// apart. Per ADR-007 (docs/adr/007-counterfactual-contributor-analysis.md) that
/// shared mapping is a correctness requirement — a diagnostic fold that disagreed
/// with the real one would give confidently wrong answers.
/// </para>
/// <para>
/// The mapping also carries ADR-006's guarantee: because <see cref="Of"/> passes
/// <see cref="HealthStatus.Unknown"/> through unchanged for every gating
/// importance (and <see cref="HealthStatus.Unknown"/> ranks below
/// <see cref="HealthStatus.Degraded"/>/<see cref="HealthStatus.Unhealthy"/>), an
/// <see cref="HealthStatus.Unknown"/> child raises its parent at most to
/// <see cref="HealthStatus.Unknown"/> — never to a failing state.
/// </para>
/// </summary>
public static class HealthContribution
{
    /// <summary>
    /// Maps a single dependency's <paramref name="childStatus"/> to the status it
    /// contributes to its parent under the given <paramref name="importance"/>.
    /// </summary>
    /// <param name="importance">The parent's importance weighting on this edge.</param>
    /// <param name="childStatus">The dependency's evaluated status.</param>
    /// <param name="hasHealthyResilientSibling">
    /// Whether the parent has at least one <see cref="Importance.Resilient"/>
    /// dependency that is <see cref="HealthStatus.Healthy"/> (the resilience
    /// quorum). Only consulted for <see cref="Importance.Resilient"/> edges.
    /// </param>
    /// <returns>The status this dependency contributes to its parent.</returns>
    public static HealthStatus Of(
        Importance importance,
        HealthStatus childStatus,
        bool hasHealthyResilientSibling)
        => importance switch
        {
            Importance.Required => childStatus,

            Importance.Important => childStatus switch
            {
                HealthStatus.Unhealthy => HealthStatus.Degraded,
                _ => childStatus,
            },

            Importance.Optional => HealthStatus.Healthy,

            Importance.Resilient when childStatus == HealthStatus.Unhealthy && hasHealthyResilientSibling
                => HealthStatus.Degraded,
            Importance.Resilient
                => childStatus,

            // ADR-008: Important, but Unknown is absorbed rather than propagated.
            Importance.Advisory => childStatus switch
            {
                HealthStatus.Unknown => HealthStatus.Healthy,
                HealthStatus.Unhealthy => HealthStatus.Degraded,
                _ => childStatus,
            },

            // No silent default: an unmapped member must not contribute a guessed status (ADR-008).
            _ => throw new ArgumentOutOfRangeException(
                nameof(importance),
                importance,
                $"Unhandled {nameof(Importance)} value. A new member was added without updating "
                    + $"{nameof(HealthContribution)}.{nameof(Of)}; see ADR-008."),
        };
}
