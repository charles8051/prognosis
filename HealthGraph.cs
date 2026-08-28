using System.Diagnostics;

namespace Prognosis;

/// <summary>
/// A read-only view of a materialized health graph. Serves as the entry point
/// for report generation, monitoring, and Rx pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Build a graph manually with <see cref="Create"/> or let the DI builder in
/// <c>Prognosis.DependencyInjection</c> materialize one for you.
/// The node you pass in is the root — the full topology is discovered
/// by walking its dependency edges downward.
/// </para>
/// <code>
/// // Manual:
/// var graph = HealthGraph.Create(topLevelNode);
///
/// // DI:
/// var graph = serviceProvider.GetRequiredService&lt;HealthGraph&gt;();
/// </code>
/// </remarks>
public sealed class HealthGraph : IDisposable
{
    private readonly HealthNode _root;
    private readonly object _propagationLock = new();
    private readonly object _topologyLock = new();
    private readonly object _topologyObserverLock = new();
    private readonly List<IObserver<TopologyChange>> _topologyObservers = new();
    private readonly object _statusObserverLock = new();
    private readonly List<IObserver<HealthReport>> _statusObservers = new();
    private volatile NodeSnapshot _snapshot;
    private volatile HealthReport? _cachedReport;
    private volatile HealthTopology? _cachedTopology;
    private volatile bool _disposed;

    // The graph-owned monotonic clock (ADR-011 §5). Read ONCE at each wave entry and
    // threaded through the wave; ticks are converted to a TimeSpan since construction
    // at the graph boundary, in one place, via Stopwatch.Frequency.
    private readonly Func<long> _clock;
    private readonly long _constructedAtTimestamp;
    private long _lastElapsedTicks;   // monotonic-clamp guard for ElapsedNow (defence in depth)

    // The graph's minimum next-deadline (ADR-011 §6) and its change channel (§6a) — a
    // single min over BOTH policy pending-deadlines AND lease next-decay-instants
    // (ADR-010 OQ3 / ADR-011 OQ5), reconciled into wave time. The value is captured
    // inside the serialized wave; TemporalDeadlineChanged replays the current minimum
    // on subscribe and fires only when it moves. The observable defers its OnNext until
    // outside _propagationLock (the repo invariant).
    private readonly DeadlineObservable _temporalDeadline;

    // Set true once a wave source (a HealthMonitor) is attached to this graph. Used
    // only by the WarnIfTemporalWithoutWaveSource diagnostic; never on a hot path.
    private volatile bool _waveSourceAttached;

    // Graph-wide temporal policy defaults (ADR-011 §10), materialized into in-scope
    // nodes as they attach. Null when none were supplied — in which case this graph
    // makes no statement about any node's policies, including shared ones another
    // graph has already defaulted (§10e).
    private readonly TemporalDefaults? _defaults;

    // Per-node anchor of a lease's next-decay boundary into wave time (ADR-011 §5),
    // keyed by the lease's stable boundary tick, so the surfaced deadline is stable
    // between affirmations rather than jittering per wave. Touched ONLY inside the
    // serialized wave (under _propagationLock), so it needs no additional locking.
    private readonly Dictionary<HealthNode, (long BoundaryTimestamp, TimeSpan WaveDeadline)>
        _leaseDeadlineAnchors = new(ReferenceEqualityComparer.Instance);

    internal HealthGraph(HealthNode root)
        : this(root, clock: null, defaults: null) { }

    internal HealthGraph(HealthNode root, Func<long>? clock)
        : this(root, clock, defaults: null) { }

    internal HealthGraph(HealthNode root, Func<long>? clock, TemporalDefaults? defaults)
    {
        _root = root;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _constructedAtTimestamp = _clock();
        defaults?.Validate();
        _defaults = defaults;

        var allNodes = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        Collect(root, allNodes);

        ValidateUniqueNames(allNodes);

        _snapshot = new NodeSnapshot(allNodes);

        // Materialize graph-wide defaults (ADR-011 §10a) BEFORE subscribing, before the
        // initial wave, and before the deadline seed below. Ordering rationale:
        //   - before the wave and the seed, so a defaulted node is policied ahead of its
        //     first evaluation and the seeded minimum accounts for its policies;
        //   - before the _bubbleStrategy subscriptions, so a conflict throw (§10c) or a
        //     throwing AppliesTo predicate (§10d) leaves NOTHING stranded. Subscribing
        //     first would leave shared nodes bubbling into a graph whose constructor
        //     threw, which no one can ever unsubscribe.
        // No lock is held or needed: the graph is not yet published. Materialization is
        // a lock-free CAS on node state (§10c), so a concurrent attach of a shared node
        // by another graph is still resolved deterministically.
        MaterializeDefaults(allNodes);

        foreach (var node in allNodes)
            node._bubbleStrategy += SerializedBubble;

        _temporalDeadline = new DeadlineObservable();

        // Initial wave at construction (now == 0 in the graph timebase): stamps every
        // node's first wave time and any grace deadline anchor.
        _root.RefreshDescendants(ElapsedNow());
        RebuildTopology();
        _temporalDeadline.Seed(ComputeMinDeadline(ElapsedNow()));

        TopologyChanged = new TopologyObservable(this);
        StatusChanged = new StatusObservable(this);
    }

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> rooted at the given node.
    /// The full topology is discovered by walking dependency edges downward,
    /// so all transitive dependencies are included automatically.
    /// </summary>
    public static HealthGraph Create(HealthNode root) => new HealthGraph(root);

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> rooted at the given node with an injected
    /// monotonic clock (ADR-011 §5) — a <see cref="Stopwatch.GetTimestamp"/>-unit
    /// timestamp source used to time temporal policies and lease decay. The clock
    /// MUST be lock-free and side-effect-free (it is read inside the propagation
    /// wave); purity is not validated at runtime. Defaults to
    /// <see cref="Stopwatch.GetTimestamp"/>.
    /// </summary>
    public static HealthGraph Create(HealthNode root, Func<long> clock) =>
        new HealthGraph(root, clock ?? throw new ArgumentNullException(nameof(clock)));

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> whose in-scope nodes receive
    /// <paramref name="defaults"/> as their temporal policies (ADR-011 §10) — one bag
    /// instead of N per-node <c>WithDebounce</c>/<c>WithGrace</c> registrations.
    /// <para>
    /// Defaults are materialized into each node as it attaches, here and on every later
    /// <see cref="HealthNode.DependsOn"/> that adds a node. Per-node calls always win
    /// (§10b), leased nodes are skipped (§10c), and the default scope is leaves —
    /// widen or narrow it with <see cref="TemporalDefaults.AppliesTo"/> (§10d).
    /// </para>
    /// <para>
    /// A grace default additionally requires a wave source: see
    /// <see cref="WarnIfTemporalWithoutWaveSource"/> and ADR-011 §10f.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A default's duration is negative.</exception>
    /// <exception cref="InvalidOperationException">
    /// A node already carries a conflicting graph-default policy installed by another
    /// graph (§10c).
    /// </exception>
    public static HealthGraph Create(HealthNode root, TemporalDefaults defaults) =>
        new HealthGraph(
            root, clock: null, defaults ?? throw new ArgumentNullException(nameof(defaults)));

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> with both an injected monotonic clock
    /// (see <see cref="Create(HealthNode, Func{long})"/>) and graph-wide temporal
    /// defaults (see <see cref="Create(HealthNode, TemporalDefaults)"/>).
    /// </summary>
    public static HealthGraph Create(
        HealthNode root, Func<long> clock, TemporalDefaults defaults) =>
        new HealthGraph(
            root,
            clock ?? throw new ArgumentNullException(nameof(clock)),
            defaults ?? throw new ArgumentNullException(nameof(defaults)));

    /// <summary>
    /// The graph-wide temporal defaults this graph materializes into in-scope nodes as
    /// they attach (ADR-011 §10), or <see langword="null"/> when none were supplied.
    /// <para>
    /// <see langword="null"/> here does <b>not</b> imply this graph's nodes carry no
    /// policies: a materialized default is node state and travels with the node, so a
    /// node shared with a defaulted graph is defaulted here too (§10e).
    /// </para>
    /// </summary>
    public TemporalDefaults? Defaults => _defaults;

    /// <summary>
    /// Applies <see cref="_defaults"/> to every in-scope node in <paramref name="nodes"/>
    /// (ADR-011 §10) as an <b>all-or-nothing</b> operation: either every selected node
    /// carries the contribution, or none does. A no-op when no defaults were supplied or
    /// the bag carries no policies; the scope predicate runs exactly once per node per
    /// attach.
    /// <para>
    /// Atomicity matters here in a way it did not before retained sources.
    /// A materialized default now <em>persists</em> — it survives the graph's disposal and
    /// is visible to later attachments' conflict checks — so a half-applied bag from a
    /// constructor that went on to throw would permanently mutate shared nodes with policy
    /// nobody successfully configured. Two phases:
    /// </para>
    /// <list type="number">
    /// <item><b>Select.</b> Every predicate runs first, writing nothing. A throwing
    /// <see cref="TemporalDefaults.AppliesTo"/> therefore fails before any node is
    /// touched, which is the majority of the failure surface.</item>
    /// <item><b>Apply, with rollback.</b> Conflicts (§10c) can only be detected against a
    /// node's current state, and under a concurrent attach by another graph that state can
    /// change between phases — so a conflict can still surface mid-apply. Every swap
    /// records the node's prior set, and a failure reverts them in reverse order before
    /// rethrowing.</item>
    /// </list>
    /// <para>
    /// The revert is <b>best-effort by construction</b>, and honestly so: if another graph
    /// has already overwritten our contribution we leave its value alone rather than
    /// clobbering it. Retention is what makes even best-effort possible — the prior value
    /// still exists to restore, which it did not under collapse-on-materialize.
    /// </para>
    /// </summary>
    private void MaterializeDefaults(IEnumerable<HealthNode> nodes)
    {
        var defaults = _defaults;
        if (defaults is null || defaults.IsEmpty)
            return;

        // Phase 1 — selection only. No writes, so a throwing predicate strands nothing.
        List<HealthNode>? selected = null;
        foreach (var node in nodes)
        {
            bool inScope;
            try
            {
                inScope = defaults.Selects(node);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The TemporalDefaults.AppliesTo predicate threw while scoping node "
                    + $"'{node.Name}'. The predicate must be pure and non-blocking "
                    + "(ADR-011 §10d); it runs inside the graph's attach critical section.",
                    ex);
            }

            if (inScope)
                (selected ??= new List<HealthNode>()).Add(node);
        }

        if (selected is null)
            return;

        // Phase 2 — apply, remembering enough to undo.
        List<(HealthNode Node, TemporalPolicySet Prior)>? applied = null;
        for (var i = 0; i < selected.Count; i++)
        {
            try
            {
                var prior = selected[i].MaterializeDefaults(defaults);
                if (prior is not null)
                    (applied ??= new List<(HealthNode, TemporalPolicySet)>()).Add((selected[i], prior));
            }
            catch
            {
                if (applied is not null)
                {
                    for (var j = applied.Count - 1; j >= 0; j--)
                        applied[j].Node.RevertDefaults(applied[j].Prior, defaults);
                }
                throw;
            }
        }
    }

    /// <summary>
    /// The wave time since graph construction, in the graph's monotonic timebase.
    /// The single place ticks are converted to a <see cref="TimeSpan"/> (ADR-011 §5).
    /// <para>
    /// The injected clock's monotonicity is a documented, unvalidated contract. As
    /// defence in depth this method <b>clamps wave time forward</b> — it never returns
    /// less than a prior call — so a mis-injected clock that steps backwards (or
    /// stalls at a constant) cannot make wave time regress and silently pin the
    /// deadline pump on a stale minimum. A backwards step is absorbed (wave time
    /// holds); it does not throw, matching ADR-010's clamp-not-throw treatment of a
    /// non-monotonic step.
    /// </para>
    /// </summary>
    private TimeSpan ElapsedNow()
    {
        var raw = _clock() - _constructedAtTimestamp;
        if (raw < 0)
            raw = 0;

        // Monotonic clamp (Interlocked max): wave time never regresses across calls,
        // even under a contract-violating non-monotonic injected clock.
        long prev;
        long next;
        do
        {
            prev = Interlocked.Read(ref _lastElapsedTicks);
            next = raw > prev ? raw : prev;
            if (next == prev)
                break;
        }
        while (Interlocked.CompareExchange(ref _lastElapsedTicks, next, prev) != prev);

        return TimeSpan.FromSeconds(next / (double)Stopwatch.Frequency);
    }

    /// <summary>
    /// The root node of the graph — the node passed to <see cref="Create"/>
    /// or provided by the DI builder.
    /// </summary>
    public HealthNode Root => _root;

    /// <summary>
    /// Emits a <see cref="TopologyChange"/> on any structural change to the
    /// graph — nodes added or removed, edges added or removed, or an edge's
    /// <see cref="Importance"/> updated (ADR-009). Does not fire when only
    /// health statuses change. Within a propagation wave, this emission is
    /// observed <em>before</em> the wave's <see cref="StatusChanged"/>, so a
    /// consumer that replaces its held <see cref="HealthTopology"/> here always
    /// holds the structure that describes the next report.
    /// <para>
    /// The ordering guarantee is per-wave only: emissions from <em>concurrent</em>
    /// propagation waves may interleave, so an older wave's pair can be observed
    /// after a newer wave's — the same semantics <see cref="StatusChanged"/> has
    /// always had. The state self-corrects on the next wave; propagation is
    /// single-threaded in the overwhelming case (ADR-001).
    /// </para>
    /// </summary>
    public IObservable<TopologyChange> TopologyChanged { get; }

    /// <summary>
    /// Emits a <see cref="HealthReport"/> each time the graph's effective
    /// health state changes. Emissions are driven by
    /// <see cref="HealthNode.Refresh"/>, <see cref="HealthNode.DependsOn"/>,
    /// <see cref="HealthNode.RemoveDependency"/>, and <see cref="RefreshAll"/>.
    /// Only fires when the report actually differs from the previous one.
    /// </summary>
    public IObservable<HealthReport> StatusChanged { get; }

    /// <summary>
    /// Looks up any node in the graph by its <see cref="HealthNode.Name"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No service with the given name exists in the graph.
    /// </exception>
    public HealthNode this[string name]
    {
        get
        {
            if (TryGetNode(name, out var node))
                return node;

            throw new KeyNotFoundException(
                $"No service named '{name}' exists in the graph.");
        }
    }

    /// <summary>
    /// Attempts to look up a node by name, returning <see langword="false"/>
    /// if no node with the given name exists.
    /// </summary>
    public bool TryGetNode(string name, out HealthNode node) =>
        _snapshot.Index.TryGetValue(name, out node!);

    /// <summary>
    /// Looks up a node whose <see cref="HealthNode.Name"/> matches
    /// <c>typeof(T).Name</c>. This is a convenience for the common convention
    /// where node names are derived from their concrete types.
    /// </summary>
    /// <typeparam name="T">
    /// The type whose <see cref="System.Type.Name"/> is used as the lookup key.
    /// </typeparam>
    public bool TryGetNode<T>(out HealthNode node) where T : class =>
        TryGetNode(typeof(T).Name, out node);

    /// <summary>
    /// All nodes reachable from the root. Automatically kept in sync when
    /// dependencies are added or removed via <see cref="HealthNode.DependsOn"/>
    /// / <see cref="HealthNode.RemoveDependency"/>, because those operations
    /// trigger <see cref="HealthNode.BubbleChange"/> which refreshes the
    /// graph's internal collections.
    /// </summary>
    public IEnumerable<HealthNode> Nodes => _snapshot.Nodes;

    /// <summary>
    /// Returns the cached <see cref="HealthReport"/> that reflects the
    /// latest state after the most recent propagation or refresh. If no
    /// propagation has occurred yet, builds the report on first access.
    /// </summary>
    public HealthReport GetReport() =>
        _cachedReport ?? RebuildReport();

    /// <summary>
    /// Returns the cached <see cref="HealthTopology"/> — the graph's structure
    /// (root name and per-node dependency edges with <see cref="Importance"/>),
    /// rebuilt atomically inside each propagation wave (ADR-009). Reactive
    /// consumers should prefer <see cref="TopologyChange.Topology"/>, which
    /// delivers the same value push-style on every structural change.
    /// </summary>
    public HealthTopology GetTopology() =>
        _cachedTopology ?? RebuildTopology();

    /// <summary>
    /// Builds a tree-shaped <see cref="HealthTreeSnapshot"/> whose nesting
    /// mirrors the dependency topology. Ideal for JSON serialization where
    /// hierarchy should be visible in the output structure.
    /// <para>
    /// This does <b>not</b> evaluate anything: it re-reads the same per-node
    /// cached evaluations the cached <see cref="HealthReport"/> is built from,
    /// without synchronizing against in-flight propagation — so a tree captured
    /// while a wave is propagating can mix pre- and post-wave statuses. Suitable
    /// for quiescent or single-threaded use. Reactive consumers should instead
    /// derive the tree from the atomically-built report:
    /// <c>HealthGraphAnalysis.BuildTreeSnapshot(report, topology)</c> with the topology
    /// from <see cref="TopologyChange.Topology"/> or <see cref="GetTopology"/>
    /// (ADR-009).
    /// </para>
    /// </summary>
    public HealthTreeSnapshot CreateTreeSnapshot()
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        return HealthNode.BuildTreeSnapshot(_root, visited);
    }

    /// <summary>
    /// Performs a DFS from all discovered nodes and returns every cycle found
    /// as an ordered list of node names (e.g. ["A", "B", "A"]).
    /// Returns an empty list when the graph is acyclic.
    /// </summary>
    /// <remarks>
    /// Walks from all nodes — not just roots — because when every node in a
    /// cycle has a parent, none of them appear as roots.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<string>> DetectCycles()
    {
        var gray = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var black = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var path = new List<HealthNode>();
        var cycles = new List<IReadOnlyList<string>>();

        foreach (var node in _snapshot.Nodes)
        {
            DetectCyclesDfs(node, gray, black, path, cycles);
        }

        return cycles;
    }

    /// <summary>
    /// Walks the dependency graph depth-first from the root and calls
    /// <see cref="HealthNode.NotifyChangedCore"/> on every node encountered.
    /// Leaves are refreshed before their parents. Returns the resulting
    /// <see cref="HealthReport"/> and emits <see cref="StatusChanged"/> if
    /// the overall state changed.
    /// </summary>
    public HealthReport RefreshAll()
    {
        HealthReport? reportToEmit = null;
        HealthReport report;
        DeadlineCapture? deadlineToEmit;

        lock (_propagationLock)
        {
            var now = ElapsedNow();
            _root.RefreshDescendants(now);

            var previous = _cachedReport;
            report = RebuildReport();

            if (previous is null
                || !HealthReportComparer.Instance.Equals(previous, report))
            {
                reportToEmit = report;
            }

            deadlineToEmit = CaptureDeadlineChange(now);
        }

        if (reportToEmit is not null)
            EmitStatusChanged(reportToEmit);

        // ADR-011 §6a: the deadline notification fires AFTER _propagationLock is
        // released — it is an observer notification, not a health emission.
        if (deadlineToEmit is DeadlineCapture capture)
            _temporalDeadline.Emit(capture.Value);

        return report;
    }

    /// <summary>
    /// Detaches this graph from all tracked nodes by removing its
    /// <see cref="HealthNode._bubbleStrategy"/> callback, and completes all
    /// <see cref="StatusChanged"/> and <see cref="TopologyChanged"/> observers.
    /// After disposal, the graph still holds its last snapshot and can be
    /// queried, but it will no longer receive propagation notifications.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_topologyLock)
        {
            foreach (var node in _snapshot.Nodes)
                node._bubbleStrategy -= SerializedBubble;
        }

        List<IObserver<TopologyChange>> topoSnapshot;
        lock (_topologyObserverLock)
        {
            topoSnapshot = new List<IObserver<TopologyChange>>(_topologyObservers);
            _topologyObservers.Clear();
        }
        foreach (var observer in topoSnapshot)
            observer.OnCompleted();

        List<IObserver<HealthReport>> statusSnapshot;
        lock (_statusObserverLock)
        {
            statusSnapshot = new List<IObserver<HealthReport>>(_statusObservers);
            _statusObservers.Clear();
        }
        foreach (var observer in statusSnapshot)
            observer.OnCompleted();

        _temporalDeadline.Complete();
    }

    private static void ValidateUniqueNames(HashSet<HealthNode> nodes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!seen.Add(node.Name))
                throw new ArgumentException(
                    $"Duplicate node name '{node.Name}'. Each node in the graph must have a unique name.");
        }
    }

    private static void Collect(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
            Collect(dep.Node, visited);
    }

    private HealthReport RebuildReport()
    {
        // The capture instant for TemporalState (ADR-013): the current wave time. When
        // RebuildReport runs inside a wave this is the same monotonic timebase the
        // histories were stamped in; when GetReport() rebuilds outside a wave it is a
        // fresh read of the same clock. Temporal is sparse and excluded from report
        // equality (ADR-012 §3 as amended), so its varying values never churn the
        // change-detection stream.
        var now = ElapsedNow();
        var nodes = _snapshot.Nodes;
        var results = new List<HealthSnapshot>(nodes.Length);
        foreach (var node in nodes)
        {
            var eval = node.EffectiveEvaluation;
            var tags = node.Tags.Count > 0 ? node.Tags : null;
            results.Add(new HealthSnapshot(
                node.Name, eval.Status, eval.Reason, tags, node.BuildTemporalState(now)));
        }
        var rootEval = _root.EffectiveEvaluation;
        var rootTags = _root.Tags.Count > 0 ? _root.Tags : null;
        var rootSnapshot = new HealthSnapshot(
            _root.Name, rootEval.Status, rootEval.Reason, rootTags, _root.BuildTemporalState(now));
        var report = new HealthReport(rootSnapshot, results);
        _cachedReport = report;
        return report;
    }

    private void SerializedBubble(HealthNode origin)
    {
        TopologyChange? topologyToEmit;
        HealthReport? reportToEmit = null;
        DeadlineCapture? deadlineToEmit;

        lock (_propagationLock)
        {
            // ADR-011 §5: read the clock ONCE at wave entry and thread the single
            // `now` through every policy evaluation in the wave.
            var now = ElapsedNow();
            origin.BubbleChange(now);
            topologyToEmit = RefreshTopology();

            var previous = _cachedReport;
            var report = RebuildReport();

            if (previous is null
                || !HealthReportComparer.Instance.Equals(previous, report))
            {
                reportToEmit = report;
            }

            deadlineToEmit = CaptureDeadlineChange(now);
        }

        // ADR-009: within a wave, the topology change is observed before the
        // status change, and neither emission runs under the propagation lock.
        if (topologyToEmit is not null)
            NotifyTopologyObservers(topologyToEmit);

        if (reportToEmit is not null)
            EmitStatusChanged(reportToEmit);

        // ADR-011 §6a: fired outside _propagationLock, only when the minimum pending
        // deadline moved. NOT a health emission and NOT carried in the report.
        if (deadlineToEmit is DeadlineCapture capture)
            _temporalDeadline.Emit(capture.Value);
    }

    /// <summary>
    /// The earliest instant at which some node's temporal answer could change with no
    /// new observation — the single minimum over BOTH the graph's per-node policy
    /// pending-deadlines (ADR-011 §6) AND its leased nodes' next-decay instants
    /// (ADR-010 §3), reconciled into the graph's wave timebase (ADR-011 §5), or
    /// <see langword="null"/> when nothing is pending. A deadline-driven consumer pump
    /// (or the built-in <see cref="HealthMonitor"/>) sleeps until this instant, waves
    /// the graph, and re-reads. This unified min is the resolution of ADR-010 OQ3 /
    /// ADR-011 OQ5: one deadline the monitor mins over, not a policy deadline and a
    /// separate lease deadline.
    /// <para>
    /// <b>Widening (8.0.0-beta):</b> this field previously surfaced policy
    /// deadlines only; it now also folds in lease next-decay instants. The change is
    /// additive — the min can only move <em>earlier</em> for a leased graph, never later —
    /// and lands inside the 8.0 prerelease line before stable, so no compat shim is owed
    /// (a policy-only consumer of a lease-free graph sees identical values).
    /// </para>
    /// </summary>
    public TimeSpan? NextTemporalDeadline => _temporalDeadline.Current;

    /// <summary>
    /// Whether any node in this graph currently carries temporal behaviour — a lease
    /// (ADR-010) or a debounce/grace policy (ADR-011). Computed live over the current
    /// node set, not frozen at construction, because <see cref="HealthNode.Lease"/> /
    /// <c>WithDebounce</c> / <c>WithGrace</c> may be called at runtime (ADR-010 §1: a
    /// lease is installable "at build time or at runtime"). A graph for which this is
    /// <see langword="true"/> needs a wave source to make its temporal answers visible;
    /// see <see cref="WarnIfTemporalWithoutWaveSource"/>.
    /// </summary>
    public bool HasTemporalNodes
    {
        get
        {
            foreach (var node in _snapshot.Nodes)
                if (node.IsTemporal)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// The current wave time since construction, in the graph's monotonic timebase
    /// (ADR-011 §5). The <see cref="HealthMonitor"/> reads this to convert an absolute
    /// wave-time <see cref="NextTemporalDeadline"/> into a sleep duration. Advances the
    /// forward-clamp like a wave entry does, so it never returns less than a prior read.
    /// </summary>
    internal TimeSpan CurrentWaveTime => ElapsedNow();

    /// <summary>
    /// Marks that a wave source (a <see cref="HealthMonitor"/>) has been attached to
    /// this graph, so <see cref="WarnIfTemporalWithoutWaveSource"/> stays silent. Called
    /// by the monitor's constructor — declaring a monitor for this graph is the signal,
    /// independent of hosted-service start ordering.
    /// </summary>
    internal void AttachWaveSource() => _waveSourceAttached = true;

    /// <summary>
    /// A point-in-time diagnostic check: invokes <paramref name="warn"/> with one
    /// diagnostic message when, <em>at the moment of the call</em>, this graph contains
    /// temporal nodes (<see cref="HasTemporalNodes"/>) but no wave source has been
    /// attached — the "perfectly correct and never once evaluated" trap:
    /// a lease never decays and a debounce hold never gates in a graph nothing ever
    /// waves. A no-op otherwise. It is idempotent, not one-shot: the caller decides when
    /// to run it (typically once after wiring), and each call re-evaluates the condition.
    /// This is the library's diagnostic hook; it takes an <see cref="Action{String}"/>
    /// rather than a logging dependency, so the core adds none. The blessed fix is
    /// <c>graph.RunMonitor()</c> (or DI <c>UseMonitor</c>), which attaches a wave source
    /// and silences this.
    /// </summary>
    /// <param name="warn">The sink to receive the diagnostic message, if any.</param>
    public void WarnIfTemporalWithoutWaveSource(Action<string> warn)
    {
        _ = warn ?? throw new ArgumentNullException(nameof(warn));
        if (HasTemporalNodes && !_waveSourceAttached)
            warn(
                $"Health graph rooted at '{_root.Name}' contains temporal nodes (leases "
                + "and/or debounce/grace policies) but no wave source is attached, so their "
                + "time-based answers will never be evaluated: a lease will not decay and a "
                + "debounce hold will not gate. Drive the graph with "
                + "graph.RunMonitor() (or the DI UseMonitor), or wave it yourself on a cadence "
                + "at least as fast as the tightest Ttl / policy window.");
    }

    /// <summary>
    /// Fires when <see cref="NextTemporalDeadline"/> moves (ADR-011 §6a) — the signal
    /// a consumer's pump (or the <see cref="HealthMonitor"/>) re-arms on. Distinct from
    /// <see cref="StatusChanged"/> because a debounce <em>hold</em> installs a deadline
    /// WITHOUT changing the effective evaluation, so the report compares equal and
    /// <see cref="StatusChanged"/> stays silent; likewise a fresh <see cref="HealthLease.Affirm"/>
    /// pushes the lease's next-decay instant later without changing the current verdict.
    /// Replays the current minimum on subscribe (a late subscriber is not left blind to
    /// a deadline already pending), fires only on a change of the minimum <em>value</em>,
    /// and a wave over unchanged nodes stays silent. Emitted outside
    /// <see cref="_propagationLock"/>; never carried in the report, so it never enters
    /// the ADR-012 report-equality key.
    /// </summary>
    public IObservable<TimeSpan?> TemporalDeadlineChanged => _temporalDeadline;

    /// <summary>
    /// Recomputes the graph's minimum pending deadline and, if it changed value since
    /// the last capture, advances the stored minimum (under <see cref="_propagationLock"/>,
    /// per §6a) and returns the value to emit outside the lock. Returns
    /// <see langword="null"/> when the minimum is unchanged.
    /// </summary>
    private DeadlineCapture? CaptureDeadlineChange(TimeSpan now)
    {
        var min = ComputeMinDeadline(now);
        return _temporalDeadline.TryAdvance(min) ? new DeadlineCapture(min) : null;
    }

    /// <summary>
    /// The single minimum next-deadline (ADR-010 OQ3 / ADR-011 OQ5) over BOTH policy
    /// pending-deadlines AND leased nodes' next-decay boundaries, in wave time.
    /// <paramref name="now"/> is the wave's canonical wave time (ADR-011 §5).
    /// </summary>
    private TimeSpan? ComputeMinDeadline(TimeSpan now)
    {
        TimeSpan? min = null;
        foreach (var node in _snapshot.Nodes)
        {
            // Policy pending deadline (ADR-011 §6) — already in the wave TimeSpan
            // timebase.
            var d = node.PendingDeadline;
            if (d is TimeSpan v && (min is null || v < min.Value))
                min = v;

            // Lease next-decay boundary (ADR-010 §3) reconciled into wave time as
            // `now + TimeUntil` — a DURATION within the lease clock, so the clock's epoch
            // cancels (lease and graph need only share a rate, not an epoch). The result
            // is ANCHORED once per boundary (keyed by the stable BoundaryTimestamp) and
            // reused until the boundary changes (an affirm or a stage crossing), so the
            // surfaced deadline is a fixed wave instant rather than a per-wave `now + …`
            // that would jitter and busy-spin the monitor at the boundary.
            var decay = node.NextLeaseDecay();
            if (decay is HealthLease.LeaseDecay info)
            {
                if (!_leaseDeadlineAnchors.TryGetValue(node, out var anchor)
                    || anchor.BoundaryTimestamp != info.BoundaryTimestamp)
                {
                    anchor = (info.BoundaryTimestamp, TemporalMath.SafeAdd(now, info.TimeUntil));
                    _leaseDeadlineAnchors[node] = anchor;
                }
                if (min is null || anchor.WaveDeadline < min.Value)
                    min = anchor.WaveDeadline;
            }
            else
            {
                // Detached or fully escalated: drop this node's anchor if present
                // (Dictionary.Remove is O(1) and a no-op on a missing key).
                _leaseDeadlineAnchors.Remove(node);
            }
        }
        return min;
    }

    /// <summary>
    /// Reconciles the tracked node set and cached <see cref="HealthTopology"/>
    /// with the graph's current edges. Returns the <see cref="TopologyChange"/>
    /// to emit when the structure changed — edges and importance compared, not
    /// just node membership (ADR-009) — or <see langword="null"/> when it did
    /// not. The caller emits outside the propagation lock.
    /// </summary>
    private TopologyChange? RefreshTopology()
    {
        lock (_topologyLock)
        {
            var fresh = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
            Collect(_root, fresh);

            var current = _snapshot;
            var added = new List<HealthNode>();
            var removed = new List<HealthNode>();

            if (!(fresh.Count == current.Set.Count && fresh.SetEquals(current.Set)))
            {
                foreach (var node in fresh)
                {
                    if (!current.Set.Contains(node))
                        added.Add(node);
                }

                foreach (var node in current.Set)
                {
                    if (!fresh.Contains(node))
                        removed.Add(node);
                }

                // Materialize graph-wide defaults into the newly attached nodes BEFORE
                // subscribing them or swapping the snapshot (ADR-011 §10a). Ordering
                // rationale, mirroring the constructor: a conflict (§10c) or a throwing
                // predicate (§10d) must not leave a node subscribed to, or listed in,
                // a graph that rejected it — the materialization itself is all-or-nothing
                // and reverts its own writes.
                //
                // Note this still runs AFTER this wave's BubbleChange, not before it: a
                // node added by DependsOn was not in the propagation scope that wave
                // walked, so it was not evaluated by it, and its first evaluation is the
                // next wave — by which time it is policied.
                //
                // What this ordering does NOT undo is the dependency edge itself:
                // DependsOn committed that before the wave, and rolling it back would
                // mean the library silently discarding the caller's explicit topology
                // change. A conflicting late attach therefore surfaces as an exception
                // out of DependsOn with the edge in place and the node unsubscribed.
                MaterializeDefaults(added);

                foreach (var node in added)
                    node._bubbleStrategy += SerializedBubble;

                foreach (var node in removed)
                {
                    node._bubbleStrategy -= SerializedBubble;
                    _leaseDeadlineAnchors.Remove(node); // drop any lease-deadline anchor
                }

                _snapshot = new NodeSnapshot(fresh);
            }

            var previous = _cachedTopology;
            var topology = RebuildTopology();

            if (previous is not null
                && HealthTopologyComparer.Instance.Equals(previous, topology))
            {
                // Structurally unchanged — keep the previous instance so
                // GetTopology() stays reference-stable across no-change waves
                // (consumers may memoize on identity).
                _cachedTopology = previous;
                return null;
            }

            return new TopologyChange(added, removed, topology);
        }
    }

    private HealthTopology RebuildTopology()
    {
        var nodes = _snapshot.Nodes;
        var edges = new Dictionary<string, IReadOnlyList<HealthTopologyEdge>>(
            nodes.Length, StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var deps = node.Dependencies;
            if (deps.Count == 0)
            {
                edges[node.Name] = Array.Empty<HealthTopologyEdge>();
                continue;
            }

            var list = new HealthTopologyEdge[deps.Count];
            for (var i = 0; i < deps.Count; i++)
                list[i] = new HealthTopologyEdge(deps[i].Node.Name, deps[i].Importance);
            edges[node.Name] = list;
        }

        var topology = new HealthTopology(_root.Name, edges);
        _cachedTopology = topology;
        return topology;
    }

    private void NotifyTopologyObservers(TopologyChange change)
    {
        List<IObserver<TopologyChange>>? snapshot;
        lock (_topologyObserverLock)
        {
            if (_topologyObservers.Count == 0)
                return;
            snapshot = new List<IObserver<TopologyChange>>(_topologyObservers);
        }

        foreach (var observer in snapshot)
        {
            observer.OnNext(change);
        }
    }

    private static void DetectCyclesDfs(
        HealthNode node,
        HashSet<HealthNode> gray,
        HashSet<HealthNode> black,
        List<HealthNode> path,
        List<IReadOnlyList<string>> cycles)
    {
        if (black.Contains(node))
            return;

        if (!gray.Add(node))
        {
            var cycleStart = path.IndexOf(node);
            var cycle = new List<string>(path.Count - cycleStart + 1);
            for (var i = cycleStart; i < path.Count; i++)
            {
                cycle.Add(path[i].Name);
            }
            cycle.Add(node.Name);
            cycles.Add(cycle);
            return;
        }

        path.Add(node);

        foreach (var dep in node.Dependencies)
        {
            DetectCyclesDfs(dep.Node, gray, black, path, cycles);
        }

        path.RemoveAt(path.Count - 1);
        gray.Remove(node);
        black.Add(node);
    }

    private sealed class TopologyObservable(HealthGraph graph) : IObservable<TopologyChange>
    {
        public IDisposable Subscribe(IObserver<TopologyChange> observer)
        {
            lock (graph._topologyObserverLock)
            {
                graph._topologyObservers.Add(observer);
            }
            return new Unsubscriber(graph, observer);
        }
    }

    private sealed class Unsubscriber(HealthGraph graph, IObserver<TopologyChange> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (graph._topologyObserverLock)
            {
                graph._topologyObservers.Remove(observer);
            }
        }
    }

    private void EmitStatusChanged(HealthReport report)
    {
        List<IObserver<HealthReport>>? snapshot;
        lock (_statusObserverLock)
        {
            if (_statusObservers.Count == 0)
                return;
            snapshot = new List<IObserver<HealthReport>>(_statusObservers);
        }

        foreach (var observer in snapshot)
        {
            observer.OnNext(report);
        }
    }

    private sealed class StatusObservable(HealthGraph graph) : IObservable<HealthReport>
    {
        public IDisposable Subscribe(IObserver<HealthReport> observer)
        {
            lock (graph._statusObserverLock)
            {
                graph._statusObservers.Add(observer);
            }
            return new StatusUnsubscriber(graph, observer);
        }
    }

    private sealed class StatusUnsubscriber(HealthGraph graph, IObserver<HealthReport> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (graph._statusObserverLock)
            {
                graph._statusObservers.Remove(observer);
            }
        }
    }

    private readonly record struct DeadlineCapture(TimeSpan? Value);

    /// <summary>
    /// The <see cref="TemporalDeadlineChanged"/> channel (ADR-011 §6a): a shared,
    /// replay-latest observable of the graph's minimum pending deadline. The current
    /// minimum is advanced under the graph's propagation lock (<see cref="TryAdvance"/>)
    /// and emitted outside it (<see cref="Emit"/>). Subscribing replays the current
    /// minimum immediately so a late subscriber is not blind to a pending deadline.
    /// </summary>
    private sealed class DeadlineObservable : IObservable<TimeSpan?>
    {
        private readonly object _gate = new();
        // Each observer is wrapped in a per-subscriber serializer (SerializedObserver) so
        // terminal/replay delivery is ordered and OnNext can never follow OnCompleted
        // (prognosis: replay racing Complete), even though notifications fire outside
        // the shared _gate.
        private readonly List<SerializedObserver> _subscriptions = new();
        private TimeSpan? _current;
        private bool _hasValue;
        private bool _completed;   // sticky: set once on Complete, never cleared

        private static readonly IDisposable Noop = new NoopDisposable();

        public TimeSpan? Current
        {
            get { lock (_gate) return _current; }
        }

        /// <summary>Sets the initial minimum at construction, before any subscriber exists.</summary>
        public void Seed(TimeSpan? value)
        {
            lock (_gate)
            {
                _current = value;
                _hasValue = true;
            }
        }

        /// <summary>
        /// Compares <paramref name="value"/> to the stored minimum by value; if it
        /// changed, stores it and returns <see langword="true"/>. Called under the
        /// graph's propagation lock so the compare-and-advance is serialized per wave.
        /// </summary>
        public bool TryAdvance(TimeSpan? value)
        {
            lock (_gate)
            {
                if (_completed)
                    return false;
                if (_hasValue && Nullable.Equals(_current, value))
                    return false;
                _current = value;
                _hasValue = true;
                return true;
            }
        }

        public void Emit(TimeSpan? value)
        {
            SerializedObserver[] snapshot;
            lock (_gate)
            {
                if (_completed || _subscriptions.Count == 0)
                    return;
                snapshot = _subscriptions.ToArray();
            }
            foreach (var sub in snapshot)
                sub.OnNext(value);
        }

        public IDisposable Subscribe(IObserver<TimeSpan?> observer)
        {
            if (observer is null)
                throw new ArgumentNullException(nameof(observer));

            SerializedObserver? subscription = null;
            bool replay;
            TimeSpan? current;
            lock (_gate)
            {
                // Complete is sticky: a subscriber arriving after Complete is not added
                // (so it can never be stranded without an OnCompleted) — it is completed
                // immediately below, upholding the Rx grammar for late subscribers.
                if (_completed)
                {
                    replay = false;
                    current = default;
                }
                else
                {
                    subscription = new SerializedObserver(observer);
                    _subscriptions.Add(subscription);
                    replay = _hasValue;
                    current = _current;
                }
            }

            if (subscription is null)
            {
                observer.OnCompleted();
                return Noop;
            }

            // Replay-latest on subscribe (§6a): deliver the current minimum immediately,
            // THROUGH the per-subscriber serializer. A Complete that raced in between
            // delivers OnCompleted on the same serializer; its lock orders the two and
            // its done-flag drops this OnNext if completion won, so OnNext can never land
            // after OnCompleted (the earlier lock-free re-check could not guarantee that —
            // Complete could interpose between the check and the OnNext call).
            if (replay)
                subscription.OnNext(current);

            return new Unsub(this, subscription);
        }

        public void Complete()
        {
            SerializedObserver[] snapshot;
            lock (_gate)
            {
                if (_completed)
                    return;   // idempotent
                _completed = true;
                snapshot = _subscriptions.ToArray();
                _subscriptions.Clear();
            }
            foreach (var sub in snapshot)
                sub.OnCompleted();
        }

        /// <summary>
        /// Serializes one observer's notifications and enforces the Rx terminal grammar:
        /// once <see cref="OnCompleted"/> has run, further <see cref="OnNext"/> calls are
        /// dropped. This is what makes a replay racing <see cref="Complete"/> safe — both
        /// go through this per-subscriber lock, so they are ordered and a post-completion
        /// OnNext is silently discarded rather than delivered out of grammar.
        /// </summary>
        private sealed class SerializedObserver(IObserver<TimeSpan?> observer)
        {
            private readonly object _lock = new();
            private bool _done;

            public void OnNext(TimeSpan? value)
            {
                lock (_lock)
                {
                    if (_done)
                        return;
                    observer.OnNext(value);
                }
            }

            public void OnCompleted()
            {
                lock (_lock)
                {
                    if (_done)
                        return;
                    _done = true;
                    observer.OnCompleted();
                }
            }
        }

        private sealed class Unsub(DeadlineObservable owner, SerializedObserver subscription) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._gate)
                    owner._subscriptions.Remove(subscription);
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class NodeSnapshot
    {
        public readonly HashSet<HealthNode> Set;
        public readonly Dictionary<string, HealthNode> Index;
        public readonly HealthNode[] Nodes;

        public NodeSnapshot(HashSet<HealthNode> set)
        {
            Set = set;
            Nodes = new HealthNode[set.Count];
            set.CopyTo(Nodes);
            Index = new Dictionary<string, HealthNode>(set.Count, StringComparer.Ordinal);
            foreach (var node in set)
                Index[node.Name] = node;
        }
    }
}
