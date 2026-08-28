using System.Diagnostics;

namespace Prognosis;

/// <summary>
/// Options for the debounce policy (ADR-011 §1): an absence or failure must
/// persist for at least <see cref="MinimumFaultDuration"/> before it is allowed to
/// gate. A non-<see cref="HealthStatus.Healthy"/> run shorter than that holds the
/// node's prior effective status, damping a transient blip into no visible change.
/// </summary>
/// <param name="MinimumFaultDuration">
/// The minimum duration a non-<see cref="HealthStatus.Healthy"/> raw run must
/// persist before it gates. A run shorter than this is held. Must be non-negative.
/// </param>
/// <param name="HeldAs">
/// Optional. When set, the window reports this status (e.g.
/// <see cref="HealthStatus.Degraded"/>) instead of holding the prior effective
/// (last-known-good) status. When <see langword="null"/>, the prior effective
/// status is held.
/// </param>
public sealed record DebounceOptions(
    TimeSpan MinimumFaultDuration,
    HealthStatus? HeldAs = null);

/// <summary>
/// Options for the grace policy (ADR-011 §1/§3): a node may not gate before it has
/// ever been reported live (<see cref="HealthNode.MarkLive"/>), bounded by a
/// required <see cref="Deadline"/>. While never-live and before the deadline, the
/// raw verdict is suppressed to a non-gating <see cref="HealthStatus.Unknown"/>.
/// </summary>
/// <param name="Deadline">
/// The window past which a still-never-live node gates on its raw merits — the
/// mechanical resolution path that keeps the grace-emitted
/// <see cref="HealthStatus.Unknown"/> transient (ADR-008). Required, so a grace
/// whose <see cref="HealthStatus.Unknown"/> has no resolution is unrepresentable.
/// Must be non-negative.
/// </param>
public sealed record GraceOptions(TimeSpan Deadline);

/// <summary>
/// The immutable grace bookkeeping threaded through <see cref="GraceCore"/>: the
/// one-way first-live latch and the anchored deadline instant. A producer using
/// the exported <see cref="Prognosis.ApplyGrace"/> fold threads this itself; the
/// in-graph <c>WithGrace</c> policy and <see cref="GraceMachine"/> own it for you.
/// </summary>
/// <param name="HasEverBeenLive">The one-way latch: set once live, never cleared.</param>
/// <param name="DeadlineAt">
/// The absolute instant (in the caller's timebase) at which grace expires, anchored
/// on the first fold so the window does not slide. <see langword="null"/> before
/// the first fold.
/// </param>
public readonly record struct GraceState(bool HasEverBeenLive, TimeSpan? DeadlineAt);

/// <summary>
/// The result of one grace fold: the verdict to use (or <c>Affirm</c>) and the
/// grace state to thread into the next call.
/// </summary>
/// <param name="Effective">The grace-adjusted verdict.</param>
/// <param name="Next">The grace state to carry forward.</param>
public readonly record struct GraceResult(HealthEvaluation Effective, GraceState Next);

/// <summary>
/// THE ONE grace core (ADR-011 §9). Internal, pure, node-free. The <c>WithGrace</c>
/// policy (the in-graph caller) and the exported producer surfaces
/// (<see cref="Prognosis.ApplyGrace"/>, <see cref="GraceMachine"/>) all delegate to
/// this single function — there is no second grace implementation to drift.
/// </summary>
internal static class GraceCore
{
    /// <summary>The reason carried by a grace-suppressed verdict.</summary>
    internal const string GraceReason = "grace: awaiting first-live observation";

    /// <summary>
    /// Folds a raw verdict through grace. While the node has never been live and the
    /// deadline has not passed, the verdict is suppressed to a non-gating
    /// <see cref="HealthStatus.Unknown"/>. Once live (latched), or once the deadline
    /// passes, the raw verdict passes through unchanged.
    /// </summary>
    internal static GraceResult Apply(
        HealthEvaluation raw,
        bool isLiveNow,
        GraceState state,
        TimeSpan now,
        GraceOptions options)
    {
        var live = state.HasEverBeenLive || isLiveNow;
        // Anchor the deadline once, on the first fold, so the window is fixed rather
        // than sliding forward on every evaluation.
        var deadlineAt = state.DeadlineAt ?? TemporalMath.SafeAdd(now, options.Deadline);
        var next = new GraceState(live, deadlineAt);

        if (live || now >= deadlineAt)
            return new GraceResult(raw, next);

        return new GraceResult(HealthEvaluation.Unknown(GraceReason), next);
    }
}

/// <summary>
/// The pure debounce fold (ADR-011 §1/§2). Node-free and table-testable.
/// </summary>
internal static class DebounceCore
{
    /// <summary>
    /// Holds a sub-threshold non-<see cref="HealthStatus.Healthy"/> run at the prior
    /// effective status (or <see cref="DebounceOptions.HeldAs"/>), returning the
    /// pending window-end deadline while holding. A healthy raw, or a fault that has
    /// persisted at least <see cref="DebounceOptions.MinimumFaultDuration"/>, passes
    /// through with no deadline.
    /// </summary>
    /// <param name="raw">The raw (post-<c>Aggregate</c>) verdict.</param>
    /// <param name="runStartedAt">When the current raw-status run began.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="options">The debounce options.</param>
    /// <param name="priorEffective">The node's prior effective verdict (held when no <see cref="DebounceOptions.HeldAs"/>).</param>
    internal static (HealthEvaluation Effective, TimeSpan? Deadline) Apply(
        HealthEvaluation raw,
        TimeSpan runStartedAt,
        TimeSpan now,
        DebounceOptions options,
        HealthEvaluation priorEffective)
    {
        if (raw.Status == HealthStatus.Healthy)
            return (raw, null);

        var runDuration = now - runStartedAt;
        if (runDuration < options.MinimumFaultDuration)
        {
            var held = options.HeldAs is HealthStatus h
                ? new HealthEvaluation(h, raw.Reason)
                : priorEffective;
            return (held, TemporalMath.SafeAdd(runStartedAt, options.MinimumFaultDuration));
        }

        return (raw, null);
    }
}

/// <summary>
/// The fixed policy chain (ADR-011 §2): <c>raw → debounce → grace</c>. Library-
/// internal and not configurable — running debounce first lets the grace latch
/// advance on the same observations (the field-proven composition ADR-011 §2
/// records).
/// </summary>
internal static class TemporalChain
{
    internal readonly record struct Result(
        HealthEvaluation Effective,
        GraceState Grace,
        TimeSpan? PendingDeadline,
        bool InDebounceHold,
        bool InGraceWindow);

    internal static Result Apply(
        HealthEvaluation raw,
        HealthEvaluation priorEffective,
        NodeObservationHistory history,
        GraceState grace,
        TimeSpan now,
        DebounceOptions? debounce,
        GraceOptions? graceOptions)
    {
        var effective = raw;
        TimeSpan? debounceDeadline = null;
        TimeSpan? graceDeadline = null;
        var inHold = false;
        var inGrace = false;
        var nextGrace = grace;

        if (debounce is not null)
        {
            var (damped, deadline) = DebounceCore.Apply(
                raw, history.CurrentRunStartedAt, now, debounce, priorEffective);
            effective = damped;
            debounceDeadline = deadline;
            inHold = deadline is not null;
        }

        if (graceOptions is not null)
        {
            var res = GraceCore.Apply(effective, isLiveNow: false, grace, now, graceOptions);
            effective = res.Effective;
            nextGrace = res.Next;
            if (!nextGrace.HasEverBeenLive
                && nextGrace.DeadlineAt is TimeSpan gd
                && now < gd)
            {
                graceDeadline = gd;
                inGrace = true;
            }
        }

        return new Result(
            effective,
            nextGrace,
            TemporalMath.MinDeadline(debounceDeadline, graceDeadline),
            inHold,
            inGrace);
    }
}

internal static class TemporalMath
{
    /// <summary>Adds two <see cref="TimeSpan"/>s, saturating at <see cref="TimeSpan.MaxValue"/> rather than overflowing.</summary>
    internal static TimeSpan SafeAdd(TimeSpan a, TimeSpan b)
    {
        if (b > TimeSpan.Zero && a > TimeSpan.MaxValue - b)
            return TimeSpan.MaxValue;
        return a + b;
    }

    /// <summary>The minimum of two nullable deadlines, treating <see langword="null"/> as "none".</summary>
    internal static TimeSpan? MinDeadline(TimeSpan? a, TimeSpan? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value <= b.Value ? a : b;
    }
}

/// <summary>
/// The library's monotonic clock conversion for node-free callers: a
/// <see cref="Stopwatch.GetTimestamp"/> reading expressed as a
/// <see cref="TimeSpan"/> in a process-stable, monotonic timebase (ADR-010 §2 /
/// ADR-011 §5). Used as the default <c>now</c> for the exported grace surfaces, so
/// the sanctioned clock is the easy path.
/// </summary>
internal static class MonotonicClock
{
    internal static TimeSpan Now =>
        TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
}
