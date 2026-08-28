namespace Prognosis;

/// <summary>
/// Where a node's temporal policy slot came from (ADR-011 §10b). Provenance is what
/// makes "explicit wins, regardless of attach order" decidable: a graph-wide default
/// fills only an <see cref="Unset"/> slot and never overwrites an <see cref="Explicit"/>
/// one, so a node configured at its own construction site keeps its tuning no matter
/// when a graph attaches it.
/// </summary>
public enum TemporalPolicyOrigin
{
    /// <summary>No policy in this slot.</summary>
    Unset = 0,

    /// <summary>Materialized from a <see cref="TemporalDefaults"/> bag at attach.</summary>
    GraphDefault = 1,

    /// <summary>Set by an explicit <c>WithDebounce</c>/<c>WithGrace</c> call on the node.</summary>
    Explicit = 2,
}

/// <summary>
/// A read-only view of one temporal policy slot (ADR-011 §10b): the value actually in
/// effect, where it came from, and the individual contributions it was resolved from.
/// <para>
/// The two source properties are the point of the type. Under graph-wide defaults a
/// node's behaviour is no longer explained by its own construction site, so
/// "why is this node damped" needs an answer that distinguishes "this node asked for it"
/// from "a graph that attached it asked for it" — and, when both are present, shows the
/// contribution that is currently losing rather than pretending it does not exist.
/// </para>
/// </summary>
/// <param name="Effective">
/// The value the chain applies: <see cref="Explicit"/> if set, else
/// <see cref="GraphDefault"/>, and <see langword="null"/> while the node is leased
/// (ADR-011 §7 — a lease means verdicts come from a producer, so no policy is in effect).
/// </param>
/// <param name="Origin">Which contribution <see cref="Effective"/> came from.</param>
/// <param name="Explicit">
/// What this node asked for by name via <c>WithDebounce</c>/<c>WithGrace</c>, or
/// <see langword="null"/>.
/// </param>
/// <param name="GraphDefault">
/// What an attaching graph's <see cref="TemporalDefaults"/> contributed, or
/// <see langword="null"/>. Retained even when an explicit value outranks it, and even
/// while leased.
/// </param>
public sealed record TemporalPolicyView<TOptions>(
    TOptions? Effective,
    TemporalPolicyOrigin Origin,
    TOptions? Explicit,
    TOptions? GraphDefault)
    where TOptions : class;

/// <summary>
/// Graph-wide temporal policy defaults (ADR-011 §10), supplied at
/// <see cref="HealthGraph.Create(HealthNode, TemporalDefaults)"/> and materialized into
/// each in-scope node as it attaches.
/// <para>
/// Per-node <see cref="HealthNode.WithDebounce"/>/<see cref="HealthNode.WithGrace"/> calls
/// always win (§10b), leased nodes are skipped rather than thrown at (§10c), and the
/// default scope is <b>leaves</b> — nodes with no dependencies at attach time — because
/// policies on composites remain an open question (§10d).
/// </para>
/// <para>
/// <b>Debounce is the safe blanket default; grace is the deliberate one.</b> Grace
/// suppresses to a non-gating <see cref="HealthStatus.Unknown"/> until
/// <see cref="HealthNode.MarkLive"/>, and liveness is a domain fact only each node's
/// owner can supply (§3) — so a blanket grace default leaves every in-scope node
/// non-gating for a full <see cref="GraceOptions.Deadline"/>. A graph carrying a grace
/// default MUST be driven by a wave source (§10f); see
/// <see cref="HealthGraph.WarnIfTemporalWithoutWaveSource"/>.
/// </para>
/// </summary>
/// <param name="Debounce">The debounce policy to materialize, or <see langword="null"/> for none.</param>
/// <param name="Grace">The grace policy to materialize, or <see langword="null"/> for none.</param>
/// <param name="AppliesTo">
/// Optional scope predicate. When <see langword="null"/>, the scope is leaves
/// (<c>Dependencies.Count == 0</c>). The predicate is invoked <b>once per node per
/// attach</b>, inside the attach critical section, and MUST be pure, non-blocking, and
/// free of any call back into <see cref="HealthGraph"/> or node mutation — reading
/// <see cref="HealthNode.Name"/>/<see cref="HealthNode.Tags"/>/<see cref="HealthNode.Dependencies"/>
/// is the intended use. This is the same unvalidated contract ADR-010 §2 accepts for the
/// injected clock; it is documented, not enforced. A throwing predicate fails the attach.
/// <para>
/// Widening this to select composites (<c>_ =&gt; true</c>) materializes policy slots on
/// nodes whose chain semantics ADR-011 OQ1 has not decided — that is opting into an open
/// question, not a supported configuration.
/// </para>
/// </param>
public sealed record TemporalDefaults(
    DebounceOptions? Debounce = null,
    GraceOptions? Grace = null,
    Func<HealthNode, bool>? AppliesTo = null)
{
    /// <summary>
    /// Whether this bag would materialize anything at all. A bag with no policies is
    /// inert regardless of its predicate.
    /// </summary>
    internal bool IsEmpty => Debounce is null && Grace is null;

    /// <summary>
    /// Whether <paramref name="node"/> is in scope: the predicate when supplied, else
    /// the default leaf scope (§10d).
    /// </summary>
    internal bool Selects(HealthNode node) =>
        AppliesTo is null ? node.Dependencies.Count == 0 : AppliesTo(node);

    internal void Validate()
    {
        if (Debounce is not null && Debounce.MinimumFaultDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(Debounce), Debounce.MinimumFaultDuration,
                "MinimumFaultDuration must be non-negative.");
        if (Grace is not null)
            GraceValidation.ValidateDeadline(Grace);
    }
}

/// <summary>
/// The node's temporal configuration as ONE immutable record swapped by
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> (ADR-011 §10c).
/// <para>
/// It is a single CAS'd record rather than separate fields for the same reason §4's
/// <c>EvaluationState</c> is: the writers genuinely race. Graph-wide
/// defaults materialize from an attaching graph while another graph may be attaching the
/// same shared node, and <c>_topologyLock</c> is <b>per graph</b> — two graphs serialize
/// against nothing. A check-then-act would let both fill an <see cref="TemporalPolicyOrigin.Unset"/>
/// slot and produce no conflict exception in exactly the disagreeing case the rule exists
/// to catch.
/// </para>
/// <para>
/// <b>Why this is a CAS and not a lock.</b> The obvious repair — materialize under the
/// node's policy lock — is a deadlock. <see cref="HealthNode.Lease"/> historically called
/// <c>Refresh()</c> inside that lock, and <c>Refresh</c> reaches
/// <c>SerializedBubble → _propagationLock → RefreshTopology → _topologyLock</c>; taking
/// the policy lock under <c>_topologyLock</c> is the exact inversion. The lock-free swap
/// has no lock-order edge at all.
/// </para>
/// <para>
/// <see cref="IsLeased"/> rides inside the same record deliberately: if leasing used a
/// lock while defaults used a CAS the two would not serialize, and a concurrent
/// <c>Lease()</c> and materialization could both succeed, leaving a node that is leased
/// <em>and</em> policied — the state §7 claims to make structurally impossible.
/// </para>
/// <para>
/// <b>Sources are retained, not collapsed.</b> Each policy slot stores the explicit and
/// graph-default contributions <em>separately</em> and derives the effective value as
/// <c>Explicit ?? GraphDefault</c>. Materializing the winner and discarding the loser —
/// the first implementation — is what forced three warts: <c>Lease()</c> had to
/// <em>clear</em> a default because there was nowhere for it to live, a default could
/// never be revoked because there was no prior value to fall back to, and a detached
/// graph's contribution was indistinguishable from the node's own. Retaining both costs
/// one reference per slot and makes precedence a <em>read</em> rather than a destructive
/// write. The chain still reads exactly one resolved value, so the §4 constraint that
/// forced eager resolution (one node, one <c>EvaluationState</c>, one <c>GraceState</c>
/// latch, however many graphs) is untouched.
/// </para>
/// </summary>
internal sealed record TemporalPolicySet(
    DebounceOptions? ExplicitDebounce,
    DebounceOptions? DefaultDebounce,
    GraceOptions? ExplicitGrace,
    GraceOptions? DefaultGrace,
    bool IsLeased)
{
    internal static readonly TemporalPolicySet Empty = new(null, null, null, null, false);

    /// <summary>
    /// The debounce policy actually applied by the chain: explicit beats graph-default,
    /// and a lease makes both inert. Leases are <b>not</b> a precedence tier (that would
    /// silently downgrade §7's designed error into an override) — the exclusion is
    /// enforced at the write side, and this null is only the read-side consequence of a
    /// node whose verdicts now come from a producer.
    /// </summary>
    internal DebounceOptions? EffectiveDebounce =>
        IsLeased ? null : ExplicitDebounce ?? DefaultDebounce;

    /// <inheritdoc cref="EffectiveDebounce"/>
    internal GraceOptions? EffectiveGrace =>
        IsLeased ? null : ExplicitGrace ?? DefaultGrace;

    internal bool HasPolicy => EffectiveDebounce is not null || EffectiveGrace is not null;

    /// <summary>
    /// Whether the node carries a policy it asked for <em>by name</em> — the narrowed
    /// condition on which <see cref="HealthNode.Lease"/> throws (§10c). A graph-default
    /// contribution does not block a lease: a default names nobody, so the lease simply
    /// takes precedence and the default stays on record, inert.
    /// </summary>
    internal bool HasExplicitPolicy =>
        ExplicitDebounce is not null || ExplicitGrace is not null;

    internal TemporalPolicyOrigin DebounceOrigin => Origin(ExplicitDebounce, DefaultDebounce);

    internal TemporalPolicyOrigin GraceOrigin => Origin(ExplicitGrace, DefaultGrace);

    /// <summary>
    /// The provenance of the <em>effective</em> value. A leased node reports
    /// <see cref="TemporalPolicyOrigin.Unset"/> because nothing is in effect; the
    /// retained contributions remain visible on the view's source properties.
    /// </summary>
    private TemporalPolicyOrigin Origin(object? explicitValue, object? defaultValue)
    {
        if (IsLeased) return TemporalPolicyOrigin.Unset;
        if (explicitValue is not null) return TemporalPolicyOrigin.Explicit;
        if (defaultValue is not null) return TemporalPolicyOrigin.GraphDefault;
        return TemporalPolicyOrigin.Unset;
    }
}
