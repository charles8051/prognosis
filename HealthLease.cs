using System.Diagnostics;
using System.Globalization;

namespace Prognosis;

/// <summary>
/// Options controlling a leased-verdict node's two-stage TTL decay (ADR-010).
/// Validated by <see cref="HealthNode.Lease"/> — invalid combinations throw at
/// lease time rather than being silently clamped.
/// </summary>
/// <param name="Ttl">
/// How long an affirmed verdict stays authoritative. While the age since the
/// last <see cref="HealthLease.Affirm"/> is at or below this value, the affirmed
/// verdict is reported unchanged. Must be non-negative.
/// </param>
/// <param name="EscalateAfter">
/// The stage-one <see cref="HealthStatus.Unknown"/> window that follows
/// <paramref name="Ttl"/>. Once the age exceeds <c>Ttl + EscalateAfter</c> the
/// node decays to <paramref name="Escalated"/>. Defaults to <paramref name="Ttl"/>
/// (escalation at 2×<c>Ttl</c> total age). Must be non-negative, and
/// <c>Ttl + EscalateAfter</c> must not overflow <see cref="TimeSpan.MaxValue"/>.
/// <see cref="TimeSpan.Zero"/> is legal — a node that wants immediate gating on
/// expiry, collapsing the <see cref="HealthStatus.Unknown"/> stage away.
/// </param>
/// <param name="Escalated">
/// The evaluation the node decays to past the escalation deadline. Its
/// <see cref="HealthEvaluation.Status"/> must be in the closed set
/// <c>{ Degraded, Unhealthy }</c> — <em>whether</em> staleness eventually gates
/// is a library-level guarantee and is not configurable. Its reason is prefixed
/// with <see cref="HealthLease.StaleReasonPrefix"/> by the library. Defaults to
/// <see cref="HealthStatus.Degraded"/>.
/// </param>
/// <param name="Clock">
/// A monotonic timestamp source in <see cref="Stopwatch.GetTimestamp"/> units,
/// used to measure the age since the last affirmation. Defaults to
/// <see cref="Stopwatch.GetTimestamp"/> — monotonic, not wall-clock, so it is
/// immune to NTP steps (a concern on RTC-less embedded devices). <b>An injected clock MUST
/// be lock-free and side-effect-free:</b> it is invoked inside the propagation
/// wave while the node's propagation lock is held, so a clock that acquires any
/// lock creates a lock-ordering hazard. Purity is not validated at runtime.
/// </param>
public sealed record HealthLeaseOptions(
    TimeSpan Ttl,
    TimeSpan? EscalateAfter = null,
    HealthEvaluation? Escalated = null,
    Func<long>? Clock = null);

/// <summary>
/// The push surface for a leased-verdict node (ADR-010). A producer pushes
/// verdicts with <see cref="Affirm"/>; each push renews the lease. When the lease
/// expires without re-affirmation the node's evaluation decays in two stages —
/// <see cref="HealthStatus.Unknown"/> at <see cref="Ttl"/>, then a gating status
/// (default <see cref="HealthStatus.Degraded"/>) at <c>Ttl + EscalateAfter</c>.
/// <para>
/// Obtain a lease from <see cref="HealthNode.Lease"/>. The guard cannot be
/// forgotten: declaring the TTL is the same call that hands out the push surface.
/// </para>
/// </summary>
public sealed class HealthLease
{
    /// <summary>
    /// Stable machine-checkable marker carried by every evaluation synthesized by
    /// decay — both the stage-one <see cref="HealthStatus.Unknown"/> and the
    /// escalated verdict (whose reason the library prefixes). Consumers should
    /// compare against this constant rather than a folklore string.
    /// </summary>
    public const string StaleReasonPrefix = "lease-expired: ";

    /// <summary>
    /// Stable marker carried by the seeded never-affirmed evaluation. Distinct
    /// from <see cref="StaleReasonPrefix"/> because the states are operationally
    /// distinct — "this node has never heard from its producer" (normal during
    /// startup) versus "this node's producer went silent" (never normal).
    /// </summary>
    public const string PendingReasonPrefix = "lease-pending: ";

    private readonly HealthNode _node;
    private readonly Func<long> _clock;
    private readonly HealthEvaluation _escalated;
    private readonly Func<HealthEvaluation> _closure;
    private volatile State _state;

    /// <summary>
    /// The immutable (verdict, affirmed-at) pair. Swapped as a single volatile
    /// reference on each <see cref="Affirm"/> — the library's copy-on-write
    /// convention; readers never lock.
    /// </summary>
    private sealed record State(HealthEvaluation Verdict, long AffirmedAtTimestamp);

    internal HealthLease(HealthNode node, HealthLeaseOptions options)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        if (options.Ttl < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Ttl, "Ttl must be non-negative.");

        var escalateAfter = options.EscalateAfter ?? options.Ttl;
        if (escalateAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options), escalateAfter, "EscalateAfter must be non-negative.");

        // Guard the Decay ttl + escalateAfter comparison against overflow: an
        // overflowed (negative) sum would silently make stage one unreachable.
        if (escalateAfter > TimeSpan.MaxValue - options.Ttl)
            throw new ArgumentOutOfRangeException(
                nameof(options), escalateAfter,
                "Ttl + EscalateAfter must not overflow TimeSpan.MaxValue.");

        var rawEscalated = options.Escalated
            ?? HealthEvaluation.Degraded("escalated after ttl+escalateAfter with no affirmation");
        if (rawEscalated.Status != HealthStatus.Degraded
            && rawEscalated.Status != HealthStatus.Unhealthy)
        {
            throw new ArgumentException(
                "Escalated.Status must be Degraded or Unhealthy; "
                + $"whether staleness gates is not configurable (was {rawEscalated.Status}).",
                nameof(options));
        }

        Ttl = options.Ttl;
        EscalateAfter = escalateAfter;
        _escalated = PrefixEscalated(rawEscalated);
        _clock = options.Clock ?? Stopwatch.GetTimestamp;

        // Seed: Unknown(pending), clock started now. The seed reaches the node's
        // cache synchronously via the Refresh() HealthNode.Lease performs after
        // installing the closure.
        _state = new State(
            HealthEvaluation.Unknown(PendingReasonPrefix + "awaiting first affirmation"),
            _clock());

        // The impure shell installed into the node's intrinsic-check slot: reads
        // the current lease state and folds it through the pure Decay core at
        // evaluation time. No timers, no threads.
        _closure = () =>
        {
            var s = _state;                                    // volatile read, immutable pair
            var age = ElapsedSince(s.AffirmedAtTimestamp, _clock());
            return Decay(s.Verdict, age, Ttl, EscalateAfter, _escalated);
        };
    }

    /// <summary>How long an affirmed verdict stays authoritative.</summary>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// The stage-one <see cref="HealthStatus.Unknown"/> window after
    /// <see cref="Ttl"/>, before escalation.
    /// </summary>
    public TimeSpan EscalateAfter { get; }

    /// <summary>
    /// Stores the verdict, renews the lease, and propagates via
    /// <see cref="HealthNode.Refresh"/>.
    /// </summary>
    /// <param name="evaluation">The freshly sampled verdict to push.</param>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// This lease has been detached — a later <see cref="HealthNode.WithHealthProbe"/>,
    /// <see cref="HealthNode.ReplaceHealthProbe"/>, or <see cref="HealthNode.Lease"/>
    /// call reverted the node away from this lease. Throws rather than silently
    /// no-op'ing, which would recreate the silent-failure class inside the guard.
    /// </exception>
    public void Affirm(HealthEvaluation evaluation)
    {
        _ = evaluation ?? throw new ArgumentNullException(nameof(evaluation));

        if (!_node.OwnsLeaseClosure(_closure))
            throw new InvalidOperationException(
                "This lease has been detached from its node by a later probe or lease "
                + "installation; Affirm is no longer valid.");

        _state = new State(evaluation, _clock());
        _node.Refresh();
    }

    /// <summary>
    /// The library-owned closure installed into the node's intrinsic-check slot.
    /// Held as a stable reference so detachment can be detected by slot identity.
    /// </summary>
    internal Func<HealthEvaluation> Closure => _closure;

    /// <summary>
    /// One lease decay boundary: a stable identity for the current
    /// (affirmation, stage) — <see cref="BoundaryTimestamp"/>, the lease-clock tick of
    /// the next decay — plus <see cref="TimeUntil"/>, how long from now until it. The
    /// graph anchors the boundary into wave time <em>once</em> (keyed on
    /// <see cref="BoundaryTimestamp"/>) and reuses it until it changes, so the surfaced
    /// deadline is stable between affirmations rather than jittering per wave.
    /// </summary>
    internal readonly record struct LeaseDecay(long BoundaryTimestamp, TimeSpan TimeUntil);

    /// <summary>
    /// The next decay boundary for this lease (ADR-010 §3), or <see langword="null"/>
    /// once fully escalated (the verdict is then stable, so there is no further
    /// deadline). The boundary is <c>AffirmedAtTimestamp + Ttl</c> (→ stage-one
    /// <see cref="HealthStatus.Unknown"/>) while affirmed, then
    /// <c>+ Ttl + EscalateAfter</c> (→ escalation) while expired.
    /// <para>
    /// <see cref="LeaseDecay.TimeUntil"/> is a <b>duration</b> (boundary minus the lease
    /// clock's current reading, clamped non-negative), so the graph reconciles it into
    /// wave time as <c>waveNow + TimeUntil</c> (ADR-011 §5 / OQ5, ADR-010 OQ3) —
    /// a difference within ONE clock, which cancels the clock's epoch. The lease and the
    /// graph therefore need only share a monotonic <em>rate</em> (any
    /// <see cref="Stopwatch"/>-derived source, the default for both, or the same injected
    /// clock in tests), never a shared epoch: a lease on a different-epoch clock computes
    /// a correct duration rather than a nonsensical absolute instant. Reads the lease
    /// clock once.
    /// </para>
    /// </summary>
    internal LeaseDecay? NextDecay()
    {
        var s = _state;                               // volatile read, immutable pair
        var now = _clock();
        var expireAt = SaturatingAdd(s.AffirmedAtTimestamp, ToTicks(Ttl));
        var escalateAt = SaturatingAdd(expireAt, ToTicks(EscalateAfter));

        // The stage test MUST match Decay, which keeps the LOWER stage while `age <=`
        // each boundary (`now <= expireAt` is still authoritative; `now <= escalateAt` is
        // still Unknown). The verdict changes at the FIRST tick PAST the boundary, so the
        // deadline is that first-instant-of-the-next-stage (boundary + 1 tick).
        //
        // The earlier strict `<` misaligned with Decay's `<=`: at an exact boundary it
        // reported the stage as already advanced, so a wave landing exactly on `expireAt`
        // skipped straight to the escalation deadline (never scheduling the Unknown
        // stage), and a wave on exactly `escalateAt` returned no deadline at all while the
        // verdict was still Unknown (so it could sit Unknown forever, escalating only on
        // unrelated activity). Using `boundary + 1` also keeps the surfaced duration
        // strictly positive at the boundary, so the monitor never re-arms to "now".
        long boundary;
        if (now <= expireAt)
            boundary = SaturatingAdd(expireAt, 1);     // authoritative — next change is expiry to Unknown
        else if (now <= escalateAt)
            boundary = SaturatingAdd(escalateAt, 1);   // Unknown — next change is escalation
        else
            return null;              // fully escalated — verdict is stable, no next deadline

        var deltaTicks = boundary - now;
        if (deltaTicks < 0)
            deltaTicks = 0;
        return new LeaseDecay(boundary, TimeSpan.FromSeconds(deltaTicks / (double)Stopwatch.Frequency));
    }

    /// <summary>
    /// Converts a <see cref="TimeSpan"/> to <see cref="Stopwatch"/> ticks, saturating
    /// at <see cref="long.MaxValue"/> rather than overflowing for a very large window.
    /// </summary>
    private static long ToTicks(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return 0;
        var ticks = span.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    /// <summary>Adds two tick counts, saturating at <see cref="long.MaxValue"/>.</summary>
    private static long SaturatingAdd(long a, long b)
    {
        if (b > 0 && a > long.MaxValue - b)
            return long.MaxValue;
        return a + b;
    }

    /// <summary>
    /// The pure two-stage decay decision — a total function of ages, with no clock
    /// read and no node state, so it is table-testable without a graph.
    /// </summary>
    /// <param name="lastAffirmed">The most recently affirmed verdict (or the seed).</param>
    /// <param name="age">Now minus the last affirmation, both from the injected clock.</param>
    /// <param name="ttl">The authoritative window.</param>
    /// <param name="escalateAfter">The Unknown window following <paramref name="ttl"/>.</param>
    /// <param name="escalated">The gating verdict past the escalation deadline.</param>
    internal static HealthEvaluation Decay(
        HealthEvaluation lastAffirmed,
        TimeSpan age,
        TimeSpan ttl,
        TimeSpan escalateAfter,
        HealthEvaluation escalated)
    {
        if (age <= ttl)
            return lastAffirmed;

        if (age <= ttl + escalateAfter)
        {
            // ADR-012 §5: an emitted Reason must be stable between meaningful
            // changes, never a per-wave telemetry channel. Band `age` to whole
            // multiples of `ttl`, so the string changes only when `age` crosses a
            // whole multiple of `ttl` — not on every evaluation. (The entry into
            // this stage, at `age` first exceeding `ttl`, is itself a status
            // transition Healthy/last-good -> Unknown, which legitimately emits;
            // thereafter the reason is stable within each ttl-wide band.) The
            // earlier `(int)age.TotalSeconds` differed on essentially every wave and
            // defeated HealthReportComparer suppression: a single
            // expired lease made every report unequal to its predecessor forever,
            // firing StatusChanged each wave while SelectHealthChanges (DiffTo,
            // Status-only) stayed silent. `age.Ticks` and `ttl.Ticks` are both long,
            // so the quotient is long without a cast; the guard avoids div-by-zero
            // for a degenerate zero ttl.
            var ttlBands = ttl.Ticks > 0 ? age.Ticks / ttl.Ticks : 1L;
            // Format the (constant) ttl without narrowing: `(int)ttl.TotalSeconds`
            // would overflow for TTLs past ~68 years and truncate a sub-second ttl
            // to a misleading "0s". "0.###" keeps whole seconds whole ("90s", "0s")
            // while surfacing sub-second granularity ("0.5s"), and the double never
            // narrows. Invariant culture so the decimal separator is stable. The ttl
            // text is band-stable regardless (ttl does not change), so this does not
            // reintroduce churn.
            var ttlSeconds = ttl.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            return HealthEvaluation.Unknown(
                $"{StaleReasonPrefix}no affirmation for over {ttlBands} ttl "
                + $"(ttl {ttlSeconds}s)");
        }

        return escalated;
    }

    /// <summary>
    /// Prefixes a (default or consumer-supplied) escalated evaluation's reason with
    /// <see cref="StaleReasonPrefix"/> so every decay-synthesized verdict is
    /// machine-distinguishable, without double-prefixing.
    /// </summary>
    private static HealthEvaluation PrefixEscalated(HealthEvaluation escalated)
    {
        var reason = escalated.Reason ?? "escalated after lease expiry";
        if (!reason.StartsWith(StaleReasonPrefix, StringComparison.Ordinal))
            reason = StaleReasonPrefix + reason;
        // `with` rather than a fresh constructor so any future HealthEvaluation
        // field beyond (Status, Reason) is preserved rather than silently dropped.
        return escalated with { Reason = reason };
    }

    /// <summary>
    /// Elapsed time between two monotonic <see cref="Stopwatch"/>-tick timestamps.
    /// A non-monotonic step backwards is clamped to zero rather than producing a
    /// negative age.
    /// </summary>
    private static TimeSpan ElapsedSince(long start, long now)
    {
        var ticks = now - start;
        if (ticks <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }
}
