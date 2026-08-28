namespace Prognosis;

/// <summary>
/// Represents a single node in the health graph. Create instances via
/// <see cref="Create(string)"/> and optionally attach a health-check
/// delegate with <see cref="WithHealthProbe"/>. Wire dependencies with
/// <see cref="DependsOn"/>.
/// <para>
/// Service classes that own health state should expose a
/// <see cref="HealthNode"/> property — typically via
/// <see cref="Create(string)"/> with a <see cref="WithHealthProbe"/>
/// call when the service has its own intrinsic check, or plain
/// <see cref="Create(string)"/> when health is derived entirely from
/// sub-dependencies.
/// </para>
/// </summary>
public sealed class HealthNode
{
    [ThreadStatic]
    private static HashSet<HealthNode>? s_propagating;

    private volatile Func<HealthEvaluation> _intrinsicCheck;
    private readonly object _dependencyWriteLock = new();
    private readonly object _parentWriteLock = new();
    private volatile IReadOnlyList<HealthNode> _parents = Array.Empty<HealthNode>();
    private volatile IReadOnlyList<HealthDependency> _dependencies = Array.Empty<HealthDependency>();

    /// <summary>
    /// The single cached value (ADR-002), now an immutable record held behind one
    /// reference and swapped atomically via a CAS loop (ADR-011 §4) so no reader can
    /// ever pair a fresh evaluation with a stale history, and the real multi-writer
    /// paths (a node in two graphs, the no-graph <see cref="BubbleChange"/> fallback,
    /// <see cref="ReportStatus"/> outside any lock) are safe. It carries not only the
    /// <c>(Effective, History, Grace)</c> triple but also the two auxiliary
    /// evaluation-path fields that used to sit OUTSIDE the CAS — the wave-time
    /// baseline (§5) and the one-shot <see cref="ReportStatus"/> bypass (§4) — so all
    /// five swap as a unit and the multi-writer claim of §4 actually holds.
    /// Read via <see cref="Volatile.Read{T}(ref T)"/>; written via
    /// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>.
    /// </summary>
    private EvaluationState _state;

    private IReadOnlyDictionary<string, string> _tags = EmptyTags;

    // Temporal configuration (ADR-011 §1/§10c) as ONE immutable record swapped by CAS:
    // the two policy slots with their provenance, plus the latched leased bit. Empty
    // when unconfigured — an unconfigured node runs an empty chain (identity) and
    // behaves bit-for-bit as before. The leased bit lives inside the swap so a
    // concurrent Lease() and graph-default materialization cannot both win and leave
    // the node leased AND policied (§7). Read via Volatile.Read; written via
    // Interlocked.CompareExchange.
    private TemporalPolicySet _policies = TemporalPolicySet.Empty;

    // The most recently installed lease (ADR-010), kept so the graph can surface its
    // next-decay instant as part of the single next-deadline (ADR-011 §5 / OQ5). Null
    // until Lease() is called; a later probe/lease install detaches it, detected by
    // closure identity in NextLeaseDecay.
    private volatile HealthLease? _lease;


    /// <summary>
    /// The immutable cached state (ADR-011 §4). <see cref="History"/> is the
    /// public observation record (<see cref="Observe"/>); <see cref="Grace"/> is
    /// internal grace bookkeeping persisted atomically alongside it so the grace
    /// deadline anchor is never torn from the history it belongs to.
    /// <para>
    /// <see cref="LastWaveTime"/> and <see cref="SkipNextIntrinsic"/> are folded in
    /// so they swap atomically with the triple rather than living in
    /// separate, non-atomic fields the §4 CAS did not actually cover:
    /// <list type="bullet">
    /// <item><see cref="LastWaveTime"/> — the last wave time this node was evaluated
    /// with (ADR-011 §5), the timebase for the no-graph <see cref="BubbleChange"/>
    /// fallback of an already-waved policied node. <see langword="null"/> until the
    /// first wave, when the chain is inert (identity). Folding it into the CAS makes a
    /// torn <c>Nullable&lt;TimeSpan&gt;</c> structurally impossible; on top of that it
    /// is advanced as <c>max(observed, now)</c>, so a non-<see langword="null"/> value
    /// never regresses even when two graphs wave the node with different <c>now</c>
    /// values and a smaller-<c>now</c> wave wins a later CAS.</item>
    /// <item><see cref="SkipNextIntrinsic"/> — the one-shot bypass armed by
    /// <see cref="ReportStatus"/> (§4). Read-and-cleared inside the CAS loop, so the
    /// interjection is consumed by exactly one wave (the CAS winner) rather than
    /// double-consumed or dropped by a non-atomic check-then-clear.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed record EvaluationState(
        HealthEvaluation Effective,
        NodeObservationHistory History,
        GraceState Grace,
        TimeSpan? LastWaveTime,
        bool SkipNextIntrinsic);

    private static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>();

    /// <summary>
    /// Multicast delegate for propagating health changes after topology
    /// mutations (<see cref="DependsOn"/> / <see cref="RemoveDependency"/>).
    /// <see langword="null"/> when no <see cref="HealthGraph"/> is attached,
    /// in which case callers fall back to a direct <see cref="BubbleChange"/>.
    /// Each attached graph adds its own callback via <c>+=</c>, so multiple
    /// graphs sharing a node all receive propagation notifications.
    /// </summary>
    internal volatile Action<HealthNode>? _bubbleStrategy;

    private HealthNode(string name, Func<HealthEvaluation> intrinsicCheck)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A node must have a name.", nameof(name));

        Name = name;
        _intrinsicCheck = intrinsicCheck;

        // Constructor seed (ADR-011 §4): the initial evaluation with an empty
        // history, WITHOUT running the chain — a build-time seed has no wave time to
        // fold with, and no observer can see it before publication. No wave time yet
        // (LastWaveTime null) and no armed interjection (SkipNextIntrinsic false).
        var initial = intrinsicCheck();
        _state = new EvaluationState(
            initial,
            NodeObservationHistory.Seed(initial.Status),
            default,
            LastWaveTime: null,
            SkipNextIntrinsic: false);
    }

    /// <summary>
    /// The node's current effective evaluation — the <see cref="EvaluationState.Effective"/>
    /// half of the cached pair. One volatile read. This is the value dependency
    /// aggregation and report building read.
    /// </summary>
    internal HealthEvaluation EffectiveEvaluation => Volatile.Read(ref _state).Effective;

    /// <summary>
    /// The node's stamped wave-time baseline (ADR-011 §5), read atomically off the
    /// cached state. Exposed for the concurrency test that asserts this baseline never
    /// regresses under concurrent multi-graph waves.
    /// </summary>
    internal TimeSpan? LastWaveTimeForTest => Volatile.Read(ref _state).LastWaveTime;

    /// <summary>
    /// The node's earliest pending policy deadline (ADR-011 §6), or
    /// <see langword="null"/> when nothing is pending. The graph exposes the minimum
    /// over its nodes as <see cref="HealthGraph.NextTemporalDeadline"/>.
    /// </summary>
    internal TimeSpan? PendingDeadline => Volatile.Read(ref _state).History.PendingDeadline;

    /// <summary>
    /// The raw CAS'd policy set (ADR-011 §10c), for tests that need to assert on the
    /// retained contributions independently of the public view's resolution rules.
    /// </summary>
    internal TemporalPolicySet PoliciesForTest => Volatile.Read(ref _policies);

    private HealthNode(string name)
        : this(name, () => HealthStatus.Healthy) { }

    /// <summary>
    /// Re-evaluates this node's health and propagates upward through all
    /// ancestors. If one or more <see cref="HealthGraph"/> instances are
    /// attached, propagation is serialized through each graph's lock and
    /// <see cref="HealthGraph.StatusChanged"/> is emitted when the report
    /// changes. If no graph is attached, falls back to a direct upward walk.
    /// <para>
    /// Call this from a node when the underlying state changes
    /// (e.g., a connection drops) to push the change immediately
    /// without waiting for the next poll tick.
    /// </para>
    /// </summary>
    public void Refresh()
    {
        var strategy = _bubbleStrategy;
        if (strategy is not null)
            strategy(this);
        else
            // No graph attached: no clock, so pass no wave time. A policied node uses
            // its last wave time if it has one, else the chain is inert (ADR-011 §5).
            BubbleChange(null);
    }

    /// <summary>Display name for this node in the health graph.</summary>
    public string Name { get; }

    /// <summary>
    /// Arbitrary string metadata associated with this node at construction
    /// time. Typical uses include environment, owner, region, and version
    /// labels. Tags are immutable after <see cref="WithTags"/> is called.
    /// Empty by default.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags => _tags;

    /// <summary>
    /// Creates a new health node whose intrinsic status is
    /// <see cref="HealthStatus.Healthy"/>. Attach a health-check delegate
    /// with <see cref="WithHealthProbe"/> and wire dependencies with
    /// <see cref="DependsOn"/>.
    /// </summary>
    /// <param name="name">Display name for the node in the health graph.</param>
    public static HealthNode Create(string name)
        => new HealthNode(name);

    /// <summary>
    /// Creates a new health node in leased-verdict mode and returns both the node
    /// used in the graph and the lease used by its producer to affirm fresh
    /// evaluations.
    /// </summary>
    /// <param name="name">Display name for the node in the health graph.</param>
    /// <param name="options">Validated lease options; see <see cref="HealthLeaseOptions"/>.</param>
    /// <returns>The leased node and its producer-facing lease.</returns>
    public static (HealthNode Node, HealthLease Lease) CreateLeased(
        string name,
        HealthLeaseOptions options)
    {
        var node = Create(name);
        return (node, node.Lease(options));
    }

    /// <summary>
    /// Attaches an intrinsic health-check delegate to this node and
    /// immediately re-evaluates. Returns <see langword="this"/> for
    /// fluent chaining.
    /// <para>
    /// The delegate is called on every <see cref="Refresh"/> to obtain
    /// the node's intrinsic health.
    /// </para>
    /// </summary>
    /// <param name="healthCheck">
    /// A delegate that returns the node's intrinsic health evaluation.
    /// </param>
    public HealthNode WithHealthProbe(Func<HealthEvaluation> healthCheck)
    {
        _intrinsicCheck = healthCheck ?? throw new ArgumentNullException(nameof(healthCheck));
        // Installing a probe detaches any lease; drop the reference too so the detached
        // lease can be collected (NextLeaseDecay already returns null via the
        // closure-identity check, but the field would otherwise pin it for the node's life).
        _lease = null;
        // Direct write, no wave (ADR-011 §4): CAS-swap only the effective half to the
        // new probe's immediate evaluation and leave the history untouched — the
        // history describes the node, not the probe. The chain is NOT applied, so the
        // pre-chain value is visible until the next wave, matching today's behaviour.
        SwapEffective(healthCheck());
        return this;
    }

    /// <summary>
    /// CAS-swaps only the <see cref="EvaluationState.Effective"/> half of the state,
    /// preserving history, grace, wave-time baseline, and any armed one-shot. The
    /// direct-write carve-out (§4) for <see cref="WithHealthProbe"/>. The loop handles
    /// a concurrent wave swapping the state underneath.
    /// </summary>
    private void SwapEffective(HealthEvaluation effective)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var next = observed with { Effective = effective };
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, next, observed), observed))
                return;
        }
    }

    /// <summary>
    /// Attaches arbitrary string metadata to this node. Returns
    /// <see langword="this"/> for fluent chaining.
    /// <para>
    /// Tags describe a node's identity (environment, owner, region, etc.)
    /// and are immutable after this call. They are included in every
    /// <see cref="HealthSnapshot"/> and <see cref="HealthTreeSnapshot"/>
    /// produced from this node.
    /// </para>
    /// </summary>
    /// <param name="tags">Key-value pairs to associate with the node.</param>
    public HealthNode WithTags(IReadOnlyDictionary<string, string> tags)
    {
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        return this;
    }

    /// <summary>
    /// Overwrites this node's cached health evaluation and immediately
    /// propagates upward through all ancestors.
    /// as the intrinsic evaluation until the next delegate-based refresh
    /// (poll tick or explicit <see cref="Refresh"/>) naturally replaces it.
    /// <para>
    /// Use this when an external observer detects a failure that belongs
    /// to this node rather than to itself — e.g., an API call that fails
    /// due to connectivity reports the failure on the shared Internet
    /// node so that all dependents are notified.
    /// </para>
    /// </summary>
    /// <param name="evaluation">The health evaluation to report.</param>
    public void ReportStatus(HealthEvaluation evaluation)
    {
        _ = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        // One-shot interjection (ADR-011 §4): write the pushed evaluation directly
        // into the effective half; the next wave's evaluation replaces it. The
        // interjection bypasses the policy chain and does not enter the history — it
        // is an override by design, so two rapid pushes coalescing undercount
        // transitions (a documented limitation, consistent with ADR-010).
        //
        // Push the value AND arm the one-shot bypass in a SINGLE CAS:
        // pre-fix these were two separate writes (SwapEffective, then a volatile flag
        // set), so a wave that raced in between could evaluate the probe against the
        // pushed value and overwrite the interjection before the bypass was even
        // armed. Folded together, no wave can observe an armed value without its
        // bypass, or a bypass without its value.
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var next = observed with { Effective = evaluation, SkipNextIntrinsic = true };
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, next, observed), observed))
                break;
        }
        Refresh();
    }

    /// <summary>
    /// Replaces the intrinsic health probe and immediately
    /// re-evaluates and propagates. The node's identity — name, edges,
    /// parents, and graph membership — is preserved.
    /// <para>
    /// Use this to swap between real and mock health probe implementations
    /// at runtime without rebuilding the graph topology.
    /// </para>
    /// </summary>
    /// <param name="healthCheck">
    /// The new delegate that returns this node's health evaluation.
    /// </param>
    public void ReplaceHealthProbe(Func<HealthEvaluation> healthCheck)
    {
        _ = healthCheck ?? throw new ArgumentNullException(nameof(healthCheck));
        if (_intrinsicCheck == healthCheck) return;
        _intrinsicCheck = healthCheck;
        _lease = null; // detach and release any prior lease (see WithHealthProbe)
        Refresh();
    }

    /// <summary>
    /// Switches this node into leased-verdict mode (ADR-010) and returns the
    /// <see cref="HealthLease"/> push surface. Instead of pulling health from a
    /// delegate on every wave, the node reports the most recently
    /// <see cref="HealthLease.Affirm"/>-ed verdict and decays in two stages when
    /// affirmations stop — <see cref="HealthStatus.Unknown"/> at
    /// <see cref="HealthLeaseOptions.Ttl"/>, then a gating status at
    /// <c>Ttl + EscalateAfter</c>.
    /// <para>
    /// Installs a library-owned closure into the same intrinsic-check slot
    /// <see cref="WithHealthProbe"/> / <see cref="ReplaceHealthProbe"/> fill —
    /// last-write-wins with those. Seeds the node to
    /// <see cref="HealthStatus.Unknown"/> (<see cref="HealthLease.PendingReasonPrefix"/>),
    /// starts the clock, and <see cref="Refresh"/>es before returning, so no report
    /// can still show the <see cref="Create"/> <see cref="HealthStatus.Healthy"/>
    /// default. A later <see cref="WithHealthProbe"/> / <see cref="ReplaceHealthProbe"/>
    /// / <see cref="Lease"/> call detaches this lease; a detached lease's
    /// <see cref="HealthLease.Affirm"/> throws.
    /// </para>
    /// <para>
    /// <b>Adoption requirement (ADR-010 §6):</b> a graph containing leased nodes MUST
    /// be driven by a wave source (poll loop, <see cref="HealthGraph.RefreshAll"/>,
    /// or any propagation) whose cadence is at least as fast as the tightest
    /// <see cref="HealthLeaseOptions.Ttl"/>. Decay is observed at evaluation time
    /// only — the library schedules nothing — so a leased node in a never-evaluated
    /// graph never visibly decays.
    /// </para>
    /// </summary>
    /// <param name="options">Validated lease options; see <see cref="HealthLeaseOptions"/>.</param>
    /// <returns>The lease push surface for this node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/>.Escalated.Status is not Degraded or Unhealthy.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Ttl or EscalateAfter is negative, or their sum overflows.
    /// </exception>
    public HealthLease Lease(HealthLeaseOptions options)
    {
        // Leases and policies are mutually exclusive per node (ADR-011 §7): a lease
        // guards a push-fed cache; a policy shapes a live edge-driven signal. A
        // producer that wants its affirmed stream damped damps before affirming
        // (ADR-011 §9, the exported grace fold).
        //
        // The exclusion is narrowed to EXPLICIT policies (§10c). A GraphDefault slot
        // does not block a lease — the lease clears it — because a default names
        // nobody while a lease is a per-node statement. Without that narrowing, any
        // node in a defaulted graph would be permanently un-leasable, silently
        // revoking ADR-010 §1's "callable at build time or at runtime".
        var lease = new HealthLease(this, options);

        while (true)
        {
            var observed = Volatile.Read(ref _policies);
            if (observed.HasExplicitPolicy)
                throw new InvalidOperationException(
                    $"'{Name}' has an explicit temporal policy (WithDebounce/WithGrace); leases and "
                    + "policies are mutually exclusive (ADR-011 §7). To damp a leased "
                    + "producer's stream, fold grace before Affirm (Grace.ApplyGrace / GraceMachine).");

            // Latch the leased bit in ONE swap, so a concurrent materialization cannot
            // interleave and leave the node leased AND policied. Any graph-default
            // contribution is RETAINED, not cleared: it is simply not in effect while
            // leased. The first implementation cleared it, which made acquiring a lease
            // silently mutate unrelated configuration — a wart §7 never asked for.
            var next = observed with { IsLeased = true };

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _policies, next, observed), observed))
                break;
        }

        _lease = lease;
        _intrinsicCheck = lease.Closure;

        // Refresh OUTSIDE any policy lock. Refresh reaches SerializedBubble ->
        // _propagationLock -> RefreshTopology -> _topologyLock; holding a node lock
        // across it establishes node-lock -> graph-lock, which deadlocks against
        // graph-side materialization taking a node lock under _topologyLock. There is
        // no node lock here at all, so the edge does not exist (ADR-011 §10c).
        Refresh();
        return lease;
    }

    /// <summary>
    /// Opts this node into the debounce policy (ADR-011 §1): an absence or failure
    /// must persist for at least <see cref="DebounceOptions.MinimumFaultDuration"/>
    /// before it gates. Returns <see langword="this"/> for fluent chaining. When both
    /// policies are configured the fixed chain is debounce-then-grace (§2).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="options"/>.MinimumFaultDuration is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">This node is leased (ADR-011 §7).</exception>
    public HealthNode WithDebounce(DebounceOptions options)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
        if (options.MinimumFaultDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MinimumFaultDuration,
                "MinimumFaultDuration must be non-negative.");

        SetPolicy(debounce: options, grace: null, TemporalPolicyOrigin.Explicit);
        return this;
    }

    /// <summary>
    /// Opts this node into the grace policy (ADR-011 §1/§3): no gating before the
    /// node has ever been reported live (<see cref="MarkLive"/>), bounded by the
    /// required <see cref="GraceOptions.Deadline"/>. Returns <see langword="this"/>
    /// for fluent chaining. When both policies are configured the fixed chain is
    /// debounce-then-grace (§2).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="options"/>.Deadline is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">This node is leased (ADR-011 §7).</exception>
    public HealthNode WithGrace(GraceOptions options)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
        GraceValidation.ValidateDeadline(options);

        SetPolicy(debounce: null, grace: options, TemporalPolicyOrigin.Explicit);
        return this;
    }

    /// <summary>
    /// Whether this node is in leased-verdict mode (ADR-010). Latches on the first
    /// <see cref="Lease"/> call and is never cleared, so lease/policy exclusion
    /// (ADR-011 §7) stays decidable even after a probe swap detaches the lease.
    /// </summary>
    public bool IsLeased => Volatile.Read(ref _policies).IsLeased;

    /// <summary>
    /// This node's debounce policy and where it came from (ADR-011 §10b). Read surface
    /// only — it answers "why is this node damped," which under graph-wide defaults is
    /// no longer answerable by reading the node's construction site.
    /// </summary>
    public TemporalPolicyView<DebounceOptions> DebouncePolicy
    {
        get
        {
            var p = Volatile.Read(ref _policies);
            return new TemporalPolicyView<DebounceOptions>(
                p.EffectiveDebounce, p.DebounceOrigin, p.ExplicitDebounce, p.DefaultDebounce);
        }
    }

    /// <summary>
    /// This node's grace policy, its provenance, and the contributions it resolved from
    /// (ADR-011 §10b). Read surface only.
    /// </summary>
    public TemporalPolicyView<GraceOptions> GracePolicy
    {
        get
        {
            var p = Volatile.Read(ref _policies);
            return new TemporalPolicyView<GraceOptions>(
                p.EffectiveGrace, p.GraceOrigin, p.ExplicitGrace, p.DefaultGrace);
        }
    }

    /// <summary>
    /// CAS-installs an explicit policy into one slot (ADR-011 §10b): the slot is set and
    /// latched <see cref="TemporalPolicyOrigin.Explicit"/>, overwriting a prior
    /// <see cref="TemporalPolicyOrigin.GraphDefault"/> — overriding a default is the
    /// point. Throws when the node is leased (§7, unamended for explicit calls).
    /// </summary>
    private void SetPolicy(
        DebounceOptions? debounce, GraceOptions? grace, TemporalPolicyOrigin origin)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _policies);
            if (observed.IsLeased)
                throw new InvalidOperationException(
                    $"'{Name}' is leased; leases and temporal policies are mutually "
                    + "exclusive (ADR-011 §7). To damp a leased producer's stream, fold "
                    + "grace before Affirm (Grace.ApplyGrace / GraceMachine).");

            // Writes only the explicit contribution. Any graph-default in the same slot
            // is retained, outranked rather than destroyed — so a future revocation of
            // the explicit value can fall back to it (OQ7).
            var next = debounce is not null
                ? observed with { ExplicitDebounce = debounce }
                : observed with { ExplicitGrace = grace };

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _policies, next, observed), observed))
                return;
        }
    }

    /// <summary>
    /// Materializes a graph's <see cref="TemporalDefaults"/> into this node at attach
    /// (ADR-011 §10c). Lock-free: reads the current set, computes the successor per the
    /// per-slot origin rules, and CAS-swaps, so a lost race re-decides against the
    /// winner's materialized values and the outcome collapses to the sequential case.
    /// <list type="bullet">
    /// <item><c>Unset</c> + an incoming default → fill, mark <c>GraphDefault</c>.</item>
    /// <item><c>GraphDefault</c> + an equal or absent incoming default → no-op.</item>
    /// <item><c>GraphDefault</c> + a <b>different</b> incoming default → throw.</item>
    /// <item><c>Explicit</c> → skip silently; explicit wins and is not a conflict (§10b).</item>
    /// <item>A leased node → skip both slots silently (§10c): a default names nobody,
    /// so it must not turn a legal lease into a startup crash.</item>
    /// </list>
    /// <para>
    /// The leased bit is read from the swapped set rather than from
    /// <see cref="_lease"/>, which is assigned outside the swap and is also cleared by a
    /// later probe install — reading it here would let a materialization race a
    /// concurrent <see cref="Lease"/> and leave the node leased <em>and</em> policied.
    /// </para>
    /// </summary>
    /// <param name="defaults">The attaching graph's defaults.</param>
    /// <exception cref="InvalidOperationException">
    /// A different <c>GraphDefault</c> already occupies a slot this bag would fill.
    /// </exception>
    internal TemporalPolicySet? MaterializeDefaults(TemporalDefaults defaults)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _policies);

            // A lease is the node's declaration that policies are not its story (§9's
            // producer-side fold is). Skip, do not throw.
            if (observed.IsLeased)
                return null;

            // Conflict detection compares the DEFAULT contributions, and only where no
            // explicit policy has settled the slot.
            //
            // An explicit policy is the node's own statement of what it wants, and it
            // outranks every default — so once it is present, two graphs disagreeing
            // about a default they cannot apply is not a conflict worth failing on. It
            // is also the most natural remedy for a contested shared node ("say what
            // this node should do and both graphs will respect it"), and it is the one
            // remedy that needs no restructuring: no rewiring, no second node, no graph
            // reconfiguration. An earlier revision threw here anyway, to keep the
            // retained layer unambiguous for a future revocation (OQ7) — that traded a
            // real, available escape hatch for a hypothetical one, and is reverted. If
            // revocation ever ships it can decide for itself what a revoked slot with
            // contested defaults does; falling back to unset is a fine answer.
            var debounce = observed.DefaultDebounce;
            if (defaults.Debounce is not null)
            {
                if (observed.ExplicitDebounce is not null)
                {
                    // Settled. Record the first contribution for diagnostics and never
                    // throw; these values are inert while the explicit one stands, so
                    // there is no right answer to pick between them and no need to.
                    debounce ??= defaults.Debounce;
                }
                else
                {
                    if (observed.DefaultDebounce is not null
                        && !Equals(observed.DefaultDebounce, defaults.Debounce))
                        throw new InvalidOperationException(ConflictMessage(
                            "debounce", observed.DefaultDebounce, defaults.Debounce));

                    debounce = defaults.Debounce;
                }
            }

            var grace = observed.DefaultGrace;
            if (defaults.Grace is not null)
            {
                if (observed.ExplicitGrace is not null)
                {
                    grace ??= defaults.Grace;
                }
                else
                {
                    if (observed.DefaultGrace is not null
                        && !Equals(observed.DefaultGrace, defaults.Grace))
                        throw new InvalidOperationException(ConflictMessage(
                            "grace", observed.DefaultGrace, defaults.Grace));

                    grace = defaults.Grace;
                }
            }

            // Only the default contributions are written; explicit ones are untouched.
            var next = observed with { DefaultDebounce = debounce, DefaultGrace = grace };
            if (next == observed)
                return null; // nothing to do — avoid a pointless swap

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _policies, next, observed), observed))
                return observed; // the prior set, so the caller can roll this back
        }
    }

    /// <summary>
    /// Best-effort undo of a <see cref="MaterializeDefaults"/> swap, used when a graph's
    /// attach fails partway and must not leave policy behind on shared nodes
    /// (ADR-011 §10c). Restores the default contributions from
    /// <paramref name="prior"/>.
    /// <para>
    /// Reverts only what this graph actually installed: if a slot no longer holds the
    /// value <paramref name="defaults"/> contributed, another writer has moved it since
    /// and its value is left alone. Explicit contributions and the leased bit are never
    /// touched — an explicit policy or a lease acquired during the failed attach is a
    /// deliberate act by someone else and outlives our rollback.
    /// </para>
    /// </summary>
    internal void RevertDefaults(TemporalPolicySet prior, TemporalDefaults defaults)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _policies);

            var debounce = observed.DefaultDebounce;
            if (defaults.Debounce is not null && Equals(debounce, defaults.Debounce))
                debounce = prior.DefaultDebounce;

            var grace = observed.DefaultGrace;
            if (defaults.Grace is not null && Equals(grace, defaults.Grace))
                grace = prior.DefaultGrace;

            var next = observed with { DefaultDebounce = debounce, DefaultGrace = grace };
            if (next == observed)
                return;

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _policies, next, observed), observed))
                return;
        }
    }

    private string ConflictMessage(string slot, object? existing, object? incoming) =>
        $"'{Name}' already carries a graph-default {slot} policy ({existing}) that "
        + $"conflicts with the {slot} default of a graph now attaching it ({incoming}). "
        + "A node shared across graphs whose temporal defaults differ for the slots it is "
        + "in scope for is a wiring error (ADR-011 §10c) — the alternatives are silent and "
        + $"order-dependent. Simplest fix: state the policy on the node itself "
        + $"(With{(slot == "debounce" ? "Debounce" : "Grace")}), which outranks every graph "
        + "default and settles the disagreement without rewiring. Otherwise: give the graphs "
        + "matching defaults, scope one of them with TemporalDefaults.AppliesTo, or use two nodes.";

    /// <summary>
    /// Reports that the node's underlying subsystem has been observed live (ADR-011
    /// §3) — the device tracker's <c>Active</c> edge, the session's <c>Open</c>,
    /// whatever the domain means by it. One-way, idempotent, callable from any
    /// thread. Advances the grace latch so grace stops suppressing on the next wave;
    /// leaves <see cref="EvaluationState.Effective"/> untouched and schedules nothing
    /// (the consumer's existing <see cref="Refresh"/> wiring carries the
    /// re-evaluation). Has no effect on a node without a grace policy.
    /// </summary>
    public void MarkLive()
    {
        // Participate in the same CAS loop over the pair (ADR-011 §3): swap a history
        // whose HasEverBeenLive is set, leaving Effective untouched, so MarkLive
        // composes with a concurrent wave rather than racing it.
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (observed.History.HasEverBeenLive && observed.Grace.HasEverBeenLive)
                return; // already latched — idempotent
            var next = observed with
            {
                History = observed.History with { HasEverBeenLive = true },
                Grace = observed.Grace with { HasEverBeenLive = true },
            };
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, next, observed), observed))
                return;
        }
    }

    /// <summary>
    /// The node's current cached pair (ADR-011 §8): the effective evaluation and its
    /// raw observation history, read as one volatile read so the two are never torn.
    /// Project flap over the history with <see cref="FlapWindow.Count"/>.
    /// </summary>
    public (HealthEvaluation Effective, NodeObservationHistory History) Observe()
    {
        var s = Volatile.Read(ref _state);
        return (s.Effective, s.History);
    }

    /// <summary>
    /// Builds this node's sparse <see cref="TemporalState"/> for a report snapshot
    /// (ADR-013), or <see langword="null"/> when the node carries no lease, no policy,
    /// and has not flapped within <see cref="TemporalState.FlapWindowDuration"/>. Read
    /// entirely off the one cached pair plus the node's policy/lease configuration — it
    /// touches neither the chain nor the choke point.
    /// </summary>
    /// <param name="now">The capture instant, in the graph's monotonic timebase.</param>
    internal TemporalState? BuildTemporalState(TimeSpan now)
    {
        var s = Volatile.Read(ref _state);
        var eff = s.Effective;
        var history = s.History;
        var policies = Volatile.Read(ref _policies);
        var leased = policies.IsLeased;
        var hasGrace = policies.EffectiveGrace is not null;
        var hasDebounce = policies.EffectiveDebounce is not null;

        var flapCount = FlapWindow.Count(history, now, TemporalState.FlapWindowDuration);

        // Sparse: no lease, no policy, no recent flap => no temporal state at all.
        if (!leased && !hasGrace && !hasDebounce && flapCount == 0)
            return null;

        // Lease staleness, keyed solely on the effective verdict's stable public
        // StaleReasonPrefix marker (ADR-010) — no lease internals crossed. Only the
        // library's own decay synthesizes that prefix, so any leased verdict WITHOUT it
        // (an affirmed verdict, or the PendingReasonPrefix seed) is Fresh by the `else`.
        StalenessMarker? staleness = null;
        int? ttlBand = null;
        if (leased)
        {
            var reason = eff.Reason;
            if (reason is not null
                && reason.StartsWith(HealthLease.StaleReasonPrefix, StringComparison.Ordinal))
            {
                if (eff.Status == HealthStatus.Unknown)
                {
                    staleness = StalenessMarker.Expired;   // stage-one Unknown decay
                    ttlBand = ParseTtlBand(reason);        // >= 1
                }
                else
                {
                    staleness = StalenessMarker.Escalated; // past the escalation deadline
                    ttlBand = null;                        // banding applies to the Expired stage only
                }
            }
            else
            {
                // Affirmed-within-Ttl, or seeded-pending (never affirmed) — both are
                // operationally not-yet-stale (documented lossy: consult Reason to split).
                staleness = StalenessMarker.Fresh;
                ttlBand = 0;
            }
        }

        // Policy phase, derived from the effective verdict + latch (grace suppresses to
        // exactly Unknown(GraceReason)). Grace and debounce are disjoint by construction
        // (§2/§3), so at most one is active on a node at a time.
        var inGrace = hasGrace
            && !history.HasEverBeenLive
            && eff.Status == HealthStatus.Unknown
            && string.Equals(eff.Reason, GraceCore.GraceReason, StringComparison.Ordinal);
        // A debounce hold is signalled by the chain installing a PendingDeadline on a
        // non-Healthy raw run (DebounceCore returns a window-end deadline exactly while
        // holding, and null otherwise). Deriving from the deadline is authoritative,
        // unlike the earlier `eff.Status != history.LastRaw` heuristic, which read false
        // for a genuine hold whenever the held value coincided with the raw fault status
        // (HeldAs equal to the raw status, or a prior effective already equal to it).
        // With !inGrace the pending deadline can only be the debounce window (grace and
        // debounce are disjoint), so no false positive.
        var inHold = hasDebounce
            && !inGrace
            && history.LastRaw != HealthStatus.Healthy
            && history.PendingDeadline is not null;

        TimeSpan? pendingRelative = history.PendingDeadline is TimeSpan deadline
            ? (deadline > now ? deadline - now : TimeSpan.Zero)
            : null;

        return new TemporalState(staleness, ttlBand, flapCount, inHold, inGrace, pendingRelative);
    }

    /// <summary>
    /// Extracts the integer <c>Ttl</c>-band from a stage-one stale reason
    /// (<c>"...no affirmation for over {N} ttl (ttl ...)"</c>, ADR-010 §Decay). Falls
    /// back to <c>1</c> — the minimum for the <see cref="StalenessMarker.Expired"/>
    /// stage — if the (library-owned, test-pinned) format ever drifts.
    /// </summary>
    private static int ParseTtlBand(string reason)
    {
        const string marker = "over ";
        var i = reason.IndexOf(marker, StringComparison.Ordinal);
        if (i >= 0)
        {
            i += marker.Length;
            var j = i;
            while (j < reason.Length && reason[j] >= '0' && reason[j] <= '9')
                j++;
            if (j > i && int.TryParse(reason.Substring(i, j - i), out var band) && band >= 1)
                return band;
        }
        return 1;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="closure"/> is still the
    /// node's installed intrinsic check — i.e. the owning lease has not been
    /// detached by a later probe or lease installation. Reads the volatile slot.
    /// </summary>
    internal bool OwnsLeaseClosure(Func<HealthEvaluation> closure)
        => ReferenceEquals(_intrinsicCheck, closure);

    /// <summary>
    /// This node's next lease-decay boundary (ADR-010 §3), or <see langword="null"/>
    /// when the node is not leased, its lease has been detached by a later probe/lease
    /// install, or the lease is fully escalated (no further decay). The graph reconciles
    /// the boundary's duration into wave time and folds it into
    /// <see cref="HealthGraph.NextTemporalDeadline"/> alongside policy deadlines
    /// (ADR-011 §5 / OQ5, ADR-010 OQ3).
    /// </summary>
    internal HealthLease.LeaseDecay? NextLeaseDecay()
    {
        var lease = _lease;
        if (lease is null)
            return null;
        // A later WithHealthProbe/ReplaceHealthProbe/Lease install detaches this lease;
        // once detached it contributes no deadline (slot-identity check, same test
        // Affirm uses).
        if (!ReferenceEquals(_intrinsicCheck, lease.Closure))
            return null;
        return lease.NextDecay();
    }

    /// <summary>
    /// Whether this node carries any temporal behaviour — a lease (ADR-010) or a
    /// debounce/grace policy (ADR-011 §1). The graph aggregates this to decide whether
    /// it needs a wave source at all (the "perfectly correct and never once evaluated"
    /// trap).
    /// </summary>
    internal bool IsTemporal
    {
        get
        {
            var p = Volatile.Read(ref _policies);
            return p.IsLeased || p.HasPolicy;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var eval = EffectiveEvaluation;
        return $"{Name}: {eval}";
    }

    /// <summary>
    /// The nodes that list this node as a dependency. Updated automatically
    /// when edges are added or removed via <see cref="DependsOn"/> /
    /// <see cref="RemoveDependency"/>. Thread-safe (copy-on-write).
    /// </summary>
    public IReadOnlyList<HealthNode> Parents => _parents;

    /// <summary>
    /// Returns <see langword="true"/> when at least one other node in the
    /// graph lists this node as a dependency.
    /// </summary>
    public bool HasParents => _parents.Count > 0;

    /// <summary>
    /// Zero or more nodes this node depends on, each tagged with an
    /// importance level.
    /// </summary>
    public IReadOnlyList<HealthDependency> Dependencies => _dependencies;

    internal static HealthTreeSnapshot BuildTreeSnapshot(
        HealthNode node, HashSet<HealthNode> visited)
    {
        var eval = node.EffectiveEvaluation;
        var tags = node._tags.Count > 0 ? node._tags : null;

        if (!visited.Add(node))
        {
            // Already visited — return a leaf to break cycles / diamonds.
            return new HealthTreeSnapshot(
                node.Name, eval.Status, eval.Reason,
                Array.Empty<HealthTreeDependency>(),
                tags);
        }

        var children = node.Dependencies
            .Select(dep => new HealthTreeDependency(
                dep.Importance,
                BuildTreeSnapshot(dep.Node, visited)))
            .ToList();

        return new HealthTreeSnapshot(node.Name, eval.Status, eval.Reason, children, tags);
    }

    /// <summary>
    /// Re-evaluates the current health and automatically bubbles upward
    /// through <see cref="Parents"/> so that the entire ancestor chain
    /// is re-evaluated.
    /// <para>
    /// Diamond graphs and cycles are handled correctly — each node
    /// is visited at most once per propagation wave. A two-phase
    /// approach ensures that in diamond topologies every node reads
    /// up-to-date cached evaluations from its dependencies: phase 1
    /// collects all reachable ancestors, phase 2 evaluates them in
    /// dependency order (children before parents).
    /// </para>
    /// </summary>
    internal void BubbleChange(TimeSpan? now)
    {
        var isRoot = s_propagating is null;
        s_propagating ??= new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);

        try
        {
            if (!s_propagating.Add(this))
                return;

            foreach (var parent in _parents)
                parent.BubbleChange(now);
        }
        finally
        {
            if (isRoot)
            {
                var scope = s_propagating;
                s_propagating = null;

                var evaluated = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
                var onStack = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
                foreach (var node in scope)
                    EvalInDependencyOrder(node, scope, evaluated, now, onStack);
            }
        }
    }

    /// <summary>
    /// Recursively evaluates <paramref name="node"/> after first evaluating
    /// any of its dependencies that are also in <paramref name="scope"/>.
    /// This ensures that in diamond topologies every node reads up-to-date
    /// cached evaluations from its dependencies.
    /// </summary>
    private static void EvalInDependencyOrder(
        HealthNode node,
        HashSet<HealthNode> scope,
        HashSet<HealthNode> evaluated,
        TimeSpan? now,
        HashSet<HealthNode> onStack)
    {
        if (!evaluated.Add(node))
            return;
        if (!scope.Contains(node))
            return;

        // Gray only for in-scope nodes, which is exactly right: a node outside the scope
        // is not re-evaluated on this wave, so its cached reason is a settled value from a
        // completed wave and stays safe to nest. (A directed cycle is always wholly in
        // scope or wholly out — every member is a transitive parent of every other, and
        // BubbleChange collects parents transitively — so a cycle is never half-gray.)
        onStack.Add(node);

        foreach (var dep in node._dependencies)
            EvalInDependencyOrder(dep.Node, scope, evaluated, now, onStack);

        // Evaluated while still gray, so a self-loop counts as its own back edge.
        node.NotifyChangedCore(now, onStack);
        onStack.Remove(node);
    }

    /// <summary>
    /// Re-evaluates the current health and updates the cached <c>(Effective, History)</c> pair.
    /// Does <b>not</b> propagate to parents — used internally by
    /// <see cref="RefreshDescendants"/> and <see cref="HealthGraph.RefreshAll"/>
    /// which walk the graph themselves.
    /// </summary>
    /// <param name="now">The wave time, or <see langword="null"/> outside a wave.</param>
    /// <param name="onStack">
    /// The walker's gray set, passed straight through to <see cref="Aggregate"/> so a back
    /// edge's reason chain is cut rather than spliced. Both callers are wave walkers that
    /// maintain it; there is no wave-less path into this method.
    /// </param>
    internal void NotifyChangedCore(TimeSpan? now, HashSet<HealthNode> onStack)
    {
        // The choke point (ADR-011 §4). Computes the raw (post-Aggregate) evaluation,
        // records a raw transition if the raw status changed, applies the fixed
        // debounce->grace chain, and swaps the whole EvaluationState as a single
        // atomic CAS — so the multi-writer paths (a node in two graphs, the no-graph
        // fallback, ReportStatus) cannot tear it. The wave-time baseline (§5) and the
        // one-shot bypass (§4) now ride inside that same swap rather
        // than in separate non-atomic fields, so the §4 multi-writer claim actually
        // holds: firstWave, chainNow, and the bypass are all derived from `observed`
        // INSIDE the loop and persisted in `next`, and a CAS retry re-derives them.

        var deps = _dependencies;
        var policies = Volatile.Read(ref _policies);
        var debounce = policies.EffectiveDebounce;
        var grace = policies.EffectiveGrace;
        var hasPolicy = debounce is not null || grace is not null;

        // Invoke the intrinsic probe at most once across the whole CAS loop, and only
        // if a normal (non-bypass) evaluation actually needs it — lazily, because a
        // retry can flip from the bypass path to the normal path when the CAS winner
        // consumes the one-shot underneath us. The bypass path reads the already-pushed
        // effective value from `observed` instead of calling the probe.
        HealthEvaluation? intrinsicNormal = null;
        var probeInvoked = false;

        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var priorEffective = observed.Effective;

            // The timebase for the chain (§5): the wave's `now`, else this node's last
            // wave time (a never-refreshed graph holds its answer), else null — a
            // never-waved node has no timebase, so its chain is inert (identity).
            // firstWave is a property of the OBSERVED baseline, so a CAS retry that
            // now sees LastWaveTime established treats itself as a later wave — the
            // first evaluation with a timebase establishes the baseline rather than
            // recording a (spurious cold-start) transition off the constructor seed.
            var firstWave = observed.LastWaveTime is null;
            var chainNow = now ?? observed.LastWaveTime;
            // Advance the baseline MONOTONICALLY: max(observed, now). A plain `now`
            // overwrite would regress the stored baseline when two graphs wave one node
            // with different `now` values and the smaller-`now` wave wins a later CAS
            // (a real change on its retry). Taking the max keeps LastWaveTime
            // non-regressing under every interleaving, so the no-graph fallback (§5)
            // never reads a baseline earlier than the latest wave that touched the node.
            var nextLastWave = now is TimeSpan waveNow
                ? (observed.LastWaveTime is TimeSpan existing && existing > waveNow
                    ? existing
                    : waveNow)
                : observed.LastWaveTime;

            // The one-shot ReportStatus interjection bypasses the chain and the history
            // (§4). Read from `observed` and cleared in `next`, so exactly one wave (the
            // CAS winner) consumes it — a losing writer re-reads with the flag already
            // cleared and takes the normal path.
            var bypass = observed.SkipNextIntrinsic;

            HealthEvaluation intrinsic;
            if (bypass)
            {
                intrinsic = priorEffective;
            }
            else
            {
                if (!probeInvoked)
                {
                    intrinsicNormal = _intrinsicCheck();
                    probeInvoked = true;
                }
                intrinsic = intrinsicNormal!;
            }

            var raw = Aggregate(intrinsic, deps, onStack);

            EvaluationState next;
            if (bypass)
            {
                // One-shot interjection: effective is the pushed (aggregated) value;
                // the chain and the history are bypassed entirely. Clear the one-shot
                // and advance the wave-time baseline, both inside the swap.
                next = observed with
                {
                    Effective = raw,
                    LastWaveTime = nextLastWave,
                    SkipNextIntrinsic = false,
                };
            }
            else
            {
                var history = observed.History;

                // Maintain the raw observation trail (for flap), only with a timebase.
                if (chainNow is TimeSpan tnow)
                {
                    if (firstWave)
                    {
                        // First evaluation with a timebase establishes the baseline and
                        // stamps the run start — a cold start is not a transition.
                        if (history.LastRaw != raw.Status
                            || history.CurrentRunStartedAt != tnow)
                        {
                            history = history with
                            {
                                LastRaw = raw.Status,
                                CurrentRunStartedAt = tnow,
                            };
                        }
                    }
                    else if (history.LastRaw != raw.Status)
                    {
                        history = history.RecordTransition(raw.Status, tnow);
                    }
                }

                if (hasPolicy && chainNow is TimeSpan t)
                {
                    // Configured policied node with a timebase: run the fixed chain over
                    // the just-updated history.
                    var result = TemporalChain.Apply(
                        raw, priorEffective, history, observed.Grace, t, debounce, grace);
                    // Only re-allocate the history when the pending deadline actually
                    // moved, so a node steadily held/in-grace keeps a stable history
                    // reference and the no-swap fast path below can fire.
                    if (history.PendingDeadline != result.PendingDeadline)
                        history = history with { PendingDeadline = result.PendingDeadline };
                    next = new EvaluationState(
                        result.Effective, history, result.Grace, nextLastWave, SkipNextIntrinsic: false);
                }
                else
                {
                    // Unconfigured (empty chain == identity), or configured but with no
                    // timebase yet (inert until the first wave). Effective is the raw
                    // value; clear any stale pending deadline.
                    if (history.PendingDeadline is not null)
                        history = history with { PendingDeadline = null };
                    next = observed with
                    {
                        Effective = raw,
                        History = history,
                        LastWaveTime = nextLastWave,
                        SkipNextIntrinsic = false,
                    };
                }
            }

            // Skip the swap when nothing meaningful changed — keeps the steady-state
            // allocation profile (and reference stability) at today's level for
            // non-users. LastWaveTime advancing alone does NOT justify a swap (it is
            // only read by the no-graph fallback, and it never regresses), EXCEPT for
            // the very first wave that establishes it from null — that must persist so
            // firstWave flips and a later real transition is recorded rather than being
            // re-absorbed as a fresh baseline.
            if (Unchanged(observed, next))
                return;

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, next, observed), observed))
                return;
        }
    }

    private static bool Unchanged(EvaluationState a, EvaluationState b) =>
        ReferenceEquals(a, b)
        || (a.Effective.Equals(b.Effective)
            && ReferenceEquals(a.History, b.History)
            && a.Grace.Equals(b.Grace)
            && a.SkipNextIntrinsic == b.SkipNextIntrinsic
            // LastWaveTime is excluded from the meaningful-change test (steady-state
            // advance is a no-op swap we skip), but a null -> non-null establishment on
            // the first wave IS meaningful and must swap.
            && (a.LastWaveTime is not null || b.LastWaveTime is null));

    /// <summary>
    /// Registers a dependency on another node. Thread-safe and may be
    /// called at any time, including after the graph has been created. The
    /// new edge is visible to the next <see cref="Refresh"/> call.
    /// Immediately triggers propagation so the new dependency's current
    /// health is reflected in all ancestors without waiting for the next
    /// poll cycle.
    /// </summary>
    public HealthNode DependsOn(HealthNode node, Importance importance)
    {
        lock (_dependencyWriteLock)
        {
            if (_dependencies.Any(d => ReferenceEquals(d.Node, node)))
                throw new InvalidOperationException(
                    $"'{Name}' already depends on '{node.Name}'.");

            var updated = new List<HealthDependency>(_dependencies)
            {
                new(node, importance)
            };
            _dependencies = updated;
        }
        AddParentBackReference(node);
        Refresh();
        return this;
    }

    /// <summary>
    /// Removes the first dependency that references <paramref name="node"/>.
    /// Returns <see langword="true"/> if a dependency was removed; otherwise
    /// <see langword="false"/>. Immediately calls <see cref="BubbleChange"/>
    /// so the removal is reflected in all ancestors without waiting for the
    /// next poll cycle. Orphaned subgraphs naturally stop appearing in
    /// reports generated from the roots.
    /// </summary>
    public bool RemoveDependency(HealthNode node)
    {
        lock (_dependencyWriteLock)
        {
            var depToRemove = _dependencies.FirstOrDefault(d => ReferenceEquals(d.Node, node));
            if (depToRemove is null)
                return false;

            var updated = _dependencies.Where(d => !ReferenceEquals(d, depToRemove)).ToList();
            _dependencies = updated;
        }

        RemoveParentBackReference(node);
        Refresh();
        return true;
    }

    /// <summary>
    /// Updates the importance level of an existing dependency.
    /// Returns <see langword="true"/> if the dependency was found and updated;
    /// otherwise <see langword="false"/>. Immediately triggers propagation
    /// so the new importance is reflected in all ancestors without waiting
    /// for the next poll cycle.
    /// </summary>
    /// <param name="node">The dependency node whose importance should be updated.</param>
    /// <param name="newImportance">The new importance level.</param>
    public bool UpdateDependencyImportance(HealthNode node, Importance newImportance)
    {
        lock (_dependencyWriteLock)
        {
            var depToUpdate = _dependencies.FirstOrDefault(d => ReferenceEquals(d.Node, node));
            if (depToUpdate is null)
                return false;

            var updated = _dependencies
                .Select(d => ReferenceEquals(d.Node, node)
                    ? new HealthDependency(node, newImportance)
                    : d)
                .ToList();
            _dependencies = updated;
        }

        Refresh();
        return true;
    }

    /// <summary>
    /// Atomically replaces all dependency edges on this node with a new set.
    /// Old edges are removed and their parent back-references cleaned up;
    /// new edges are added and their parent back-references established.
    /// A single <see cref="Refresh"/> propagation fires at the end.
    /// <para>
    /// Use this to switch between dependency profiles at runtime — for
    /// example, swapping from a real implementation's dependencies to a
    /// mock's dependencies — without rebuilding the graph.
    /// </para>
    /// </summary>
    /// <param name="newDependencies">
    /// The complete set of dependencies that should replace the current
    /// edges. Pass no arguments to remove all dependencies. Duplicate
    /// nodes are not allowed.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="newDependencies"/> contains duplicate nodes.
    /// </exception>
    public void ReplaceDependencies(
        params (HealthNode Node, Importance Importance)[] newDependencies)
    {
        // Validate no duplicates in the incoming set.
        var nodeSet = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        foreach (var (node, _) in newDependencies)
        {
            if (!nodeSet.Add(node))
                throw new ArgumentException(
                    $"Duplicate dependency on '{node.Name}'.",
                    nameof(newDependencies));
        }

        IReadOnlyList<HealthDependency> oldDeps;

        lock (_dependencyWriteLock)
        {
            oldDeps = _dependencies;

            var updated = newDependencies
                .Select(t => new HealthDependency(t.Node, t.Importance))
                .ToList();
            _dependencies = updated;
        }

        var newNodes = new HashSet<HealthNode>(
            newDependencies.Select(t => t.Node),
            ReferenceEqualityComparer.Instance);
        var oldNodes = new HashSet<HealthNode>(
            oldDeps.Select(d => d.Node),
            ReferenceEqualityComparer.Instance);

        // Remove parent back-references for edges that were dropped.
        foreach (var oldNode in oldNodes.Where(n => !newNodes.Contains(n)))
        {
            RemoveParentBackReference(oldNode);
        }

        // Add parent back-references for edges that are new.
        foreach (var newNode in newNodes.Where(n => !oldNodes.Contains(n)))
        {
            AddParentBackReference(newNode);
        }

        Refresh();
    }

    /// <summary>
    /// Computes the worst-case health across the intrinsic evaluation and every
    /// dependency, with the propagation rules driven by <see cref="Importance"/>.
    /// Always reads dependency health from each dependency's effective evaluation.
    /// </summary>
    /// <param name="intrinsic">This node's own probe result.</param>
    /// <param name="dependencies">The edges to fold over, in declaration order.</param>
    /// <param name="onStack">
    /// The nodes currently being evaluated on this wave's DFS stack — the gray set of a
    /// standard cycle-detecting walk. A dependency in this set is reached by a
    /// <em>back edge</em>: its cached evaluation is from an earlier wave and, on a cycle,
    /// transitively contains this node's own previous reason.
    /// <para>
    /// Splicing a back edge's reason in makes the chain unbounded: on a cycle it gains a
    /// full lap per wave, which both retains an ever-growing string and — because
    /// <see cref="HealthReportComparer"/> carries <c>Reason</c> in its equality key
    /// (ADR-012 §1) — makes every wave look like a change, so a cyclic graph emits on
    /// every beat forever. A back edge is therefore cut here and reported flat, exactly as
    /// <see cref="BuildTreeSnapshot"/> cuts a revisited node to a childless stub and
    /// <c>HealthGraph.DetectCyclesDfs</c> stops at a gray node. ADR-012's amendment of
    /// 2026-08-22 makes this normative: a reason produced by composition is bound by its
    /// §5 content rule exactly as one produced by a probe.
    /// </para>
    /// <para>
    /// <b>What the cut does and does not guarantee.</b> Each node appears at most once as
    /// a <em>hop</em> (a <c>"name: "</c> prefix), and the cut form embeds a status rather
    /// than a nested chain, so it always terminates the chain. Length is therefore bounded
    /// by the walk depth and a lap can be entered at most once — which is what makes the
    /// chain stable across waves.
    /// </para>
    /// <para>
    /// The chain is <em>not</em> a simple path in the full sequence of names: the terminal
    /// may name a node that is already a hop, because that is exactly what closing a cycle
    /// looks like. On <c>A →Required C →Required B →Required A</c>, A reports
    /// <c>"C: B: A is Unhealthy"</c> — read as "A is unhealthy because C, because B,
    /// because it comes back around to A". That is the honest report for a cyclic
    /// dependency, and suppressing it would lose the only signal that the cycle is what
    /// closed the loop.
    /// </para>
    /// <para>
    /// This is a no-op on an acyclic graph: both wave walkers evaluate a node's
    /// dependencies before the node itself, so a dependency can only be on the stack if an
    /// edge runs backwards. Nothing about DAG output changes.
    /// </para>
    /// </param>
    internal static HealthEvaluation Aggregate(
        HealthEvaluation intrinsic,
        IReadOnlyList<HealthDependency> dependencies,
        HashSet<HealthNode> onStack)
    {
        var depCount = dependencies.Count;
        var evals = new (HealthDependency dep, HealthEvaluation eval)[depCount];
        var hasHealthyResilient = false;

        for (var i = 0; i < depCount; i++)
        {
            var dep = dependencies[i];
            var eval = dep.Node.EffectiveEvaluation;
            evals[i] = (dep, eval);

            if (dep.Importance == Importance.Resilient && eval.Status == HealthStatus.Healthy)
                hasHealthyResilient = true;
        }

        // Second pass: compute effective status.
        var effective = intrinsic.Status;
        string? reason = intrinsic.Reason;

        // Whether `reason` currently comes from a back edge we had to cut. Such a reason
        // explains nothing beyond "a node in my cycle is also bad", so it yields to an
        // equally-bad dependency that can explain itself. Never true on an acyclic graph,
        // where there are no back edges — so neither branch below can change DAG output.
        var reasonIsCut = false;

        for (var i = 0; i < depCount; i++)
        {
            var (dep, depEval) = evals[i];

            // Single source of truth for the per-importance mapping, shared with
            // the pure diagnostic re-fold in Prognosis.Diagnostics (ADR-007).
            var contribution = HealthContribution.Of(
                dep.Importance, depEval.Status, hasHealthyResilient);

            // Nest the dependency's own chain, except across a back edge — see the
            // `onStack` parameter docs. The cut form is the one already used for a
            // dependency carrying no reason of its own, so no new shape of string is
            // introduced.
            var cut = onStack.Contains(dep.Node);

            if (contribution.IsWorseThan(effective))
            {
                effective = contribution;
                reason = Describe(dep, depEval, cut);
                reasonIsCut = cut;
            }
            else if (reasonIsCut && !cut && contribution == effective)
            {
                // Equally bad, but this one survives to a real cause. Without this, a
                // cycle peer scanned first keeps the reason and the actual culprit —
                // typically a node outside the cycle — never gets named at all.
                reason = Describe(dep, depEval, cut: false);
                reasonIsCut = false;
            }
        }

        return new HealthEvaluation(effective, reason);

        static string Describe(HealthDependency dep, HealthEvaluation eval, bool cut) =>
            eval.Reason is not null && !cut
                ? $"{dep.Node.Name}: {eval.Reason}"
                : $"{dep.Node.Name} is {eval.Status}";
    }

    /// <summary>
    /// Re-evaluates the intrinsic health of every node in this node's
    /// dependency subtree (depth-first, leaves before parents).
    /// <para>
    /// Use this for poll-based scenarios where the underlying service state
    /// may have changed without an explicit <see cref="BubbleChange"/> call.
    /// Unlike <see cref="BubbleChange"/>, which propagates <em>upward</em>
    /// from a single change, this method walks <em>downward</em> through all
    /// dependencies to refresh the entire subtree.
    /// </para>
    /// </summary>
    internal void RefreshDescendants(TimeSpan? now)
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var onStack = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        NotifyDfs(this, visited, now, onStack);
    }

    /// <param name="visited">
    /// Nodes already reached on this walk — the black set. Entered on the way down, so a
    /// diamond's second occurrence is skipped rather than re-walked.
    /// </param>
    /// <param name="onStack">
    /// Nodes on the current DFS path — the gray set, entered on the way down and left on
    /// the way back up. Handed to <see cref="Aggregate"/> so a back edge's reason chain is
    /// cut rather than spliced; see that method's parameter docs.
    /// </param>
    internal static void NotifyDfs(
        HealthNode node, HashSet<HealthNode> visited, TimeSpan? now, HashSet<HealthNode> onStack)
    {
        if (!visited.Add(node))
            return;

        onStack.Add(node);

        foreach (var dep in node.Dependencies)
        {
            NotifyDfs(dep.Node, visited, now, onStack);
        }

        // Evaluated while still gray, so a self-loop counts as its own back edge.
        node.NotifyChangedCore(now, onStack);
        onStack.Remove(node);
    }

    private void RemoveParentBackReference(HealthNode child)
    {
        lock (child._parentWriteLock)
        {
            var parentToRemove = child._parents.FirstOrDefault(p => ReferenceEquals(p, this));
            if (parentToRemove is not null)
            {
                var updated = child._parents.Where(p => !ReferenceEquals(p, parentToRemove)).ToList();
                child._parents = updated;
            }
        }
    }

    private void AddParentBackReference(HealthNode child)
    {
        lock (child._parentWriteLock)
        {
            var updated = new List<HealthNode>(child._parents) { this };
            child._parents = updated;
        }
    }
}
