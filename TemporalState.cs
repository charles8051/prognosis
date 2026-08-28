namespace Prognosis;

/// <summary>
/// The lease-staleness marker carried on <see cref="TemporalState.Staleness"/>,
/// mirroring ADR-010's decay taxonomy. <see langword="null"/> on a non-leased node.
/// </summary>
public enum StalenessMarker
{
    /// <summary>Affirmed and within <c>Ttl</c>, or seeded-pending (never affirmed) — operationally not-yet-stale.</summary>
    Fresh,

    /// <summary>Stage-one decay: <see cref="HealthStatus.Unknown"/> past <c>Ttl</c> (ADR-010 <c>StaleReasonPrefix</c>).</summary>
    Expired,

    /// <summary>Past the escalation deadline: a gating status (ADR-010 <c>StaleReasonPrefix</c>).</summary>
    Escalated,
}

/// <summary>
/// A sparse, structured snapshot of a node's temporal substrate state (ADR-013):
/// lease staleness, windowed flap, policy phase, and the pending policy deadline —
/// the continuously-varying data ADR-012 §5 forbids in <see cref="HealthEvaluation.Reason"/>.
/// <para>
/// Carried on <see cref="HealthSnapshot.Temporal"/> for point-in-time readers (a
/// heartbeat, a <c>.clef</c> frame). It is <b>excluded from report-change detection</b>
/// exactly like <see cref="HealthSnapshot.Tags"/> (ADR-012 §3 as amended by ADR-013):
/// a live count or age in the equality key would reintroduce the per-wave churn
/// ADR-012 fought. It is <see langword="null"/> when a node carries no lease, no
/// policy, and has not flapped within the window — a quiescent graph pays no
/// populated <c>Temporal</c>.
/// </para>
/// </summary>
/// <param name="Staleness">
/// The lease-staleness marker, or <see langword="null"/> when the node is not leased.
/// </param>
/// <param name="TtlBand">
/// The <c>Ttl</c>-band the lease age has reached (ADR-010 / ADR-012 §5): <c>0</c> when
/// fresh, <c>&gt;= 1</c> in the stage-one <see cref="StalenessMarker.Expired"/> stage,
/// and <see langword="null"/> both when the node is not leased and past escalation
/// (banding applies to the stage-one <see cref="HealthStatus.Unknown"/> window only).
/// </param>
/// <param name="FlapCount">
/// Raw status transitions recorded within <see cref="FlapWindowDuration"/> (ADR-011
/// §8). Always <c>&gt;= 0</c>.
/// </param>
/// <param name="InDebounceHold">
/// <see langword="true"/> while a debounce policy is currently holding a raw fault
/// below its threshold (ADR-011 §1).
/// </param>
/// <param name="InGraceWindow">
/// <see langword="true"/> while a grace policy is currently suppressing a
/// not-yet-live verdict (ADR-011 §3).
/// </param>
/// <param name="PendingDeadline">
/// Time from this capture until the node's next policy deadline (ADR-011 §6), or
/// <see langword="null"/> when nothing is pending. Stored relative to the capture
/// instant so a reader needs no graph epoch; clamped to zero if the deadline has
/// just passed. The graph-level minimum is <see cref="HealthGraph.NextTemporalDeadline"/>.
/// </param>
public sealed record TemporalState(
    StalenessMarker? Staleness = null,
    int? TtlBand = null,
    int FlapCount = 0,
    bool InDebounceHold = false,
    bool InGraceWindow = false,
    TimeSpan? PendingDeadline = null)
{
    /// <summary>
    /// The library-fixed window over which <see cref="FlapCount"/> counts raw
    /// transitions (ADR-013). Fixed rather than configurable for the same reason the
    /// <see cref="NodeObservationHistory.TransitionBound"/> is fixed: deterministic,
    /// implementation-independent reads. A local reader that needs a different window
    /// can project <see cref="FlapWindow.Count"/> over <see cref="HealthNode.Observe"/>.
    /// </summary>
    public static readonly TimeSpan FlapWindowDuration = TimeSpan.FromMinutes(5);
}
