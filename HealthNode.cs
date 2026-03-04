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
    internal volatile HealthEvaluation _cachedEvaluation;
    private volatile bool _skipNextIntrinsicEvaluation;

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
        _cachedEvaluation = intrinsicCheck();
    }

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
            BubbleChange();
    }

    /// <summary>Display name for this node in the health graph.</summary>
    public string Name { get; }

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
        _cachedEvaluation = healthCheck();
        return this;
    }

    /// <summary>
    /// Overwrites this node's cached health evaluation and immediately
    /// propagates upward through all ancestors. The reported value acts
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
        _cachedEvaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        _skipNextIntrinsicEvaluation = true;
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
        if (_intrinsicCheck == healthCheck) return;
        _intrinsicCheck = healthCheck;
        Refresh();
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var eval = _cachedEvaluation;
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
        var eval = node._cachedEvaluation;

        if (!visited.Add(node))
        {
            // Already visited — return a leaf to break cycles / diamonds.
            return new HealthTreeSnapshot(
                node.Name, eval.Status, eval.Reason,
                Array.Empty<HealthTreeDependency>());
        }

        var children = node.Dependencies
            .Select(dep => new HealthTreeDependency(
                dep.Importance,
                BuildTreeSnapshot(dep.Node, visited)))
            .ToList();

        return new HealthTreeSnapshot(node.Name, eval.Status, eval.Reason, children);
    }

    /// <summary>
    /// Re-evaluates the current health and automatically bubbles upward
    /// through <see cref="Parents"/> so that the entire ancestor chain
    /// is re-evaluated.
    /// <para>
    /// Diamond graphs and cycles are handled correctly — each node
    /// is visited at most once per propagation wave.
    /// </para>
    /// </summary>
    internal void BubbleChange()
    {
        var isRoot = s_propagating is null;
        s_propagating ??= new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);

        try
        {
            if (!s_propagating.Add(this))
                return;

            NotifyChangedCore();

            foreach (var parent in _parents)
                parent.BubbleChange();
        }
        finally
        {
            if (isRoot)
                s_propagating = null;
        }
    }

    /// <summary>
    /// Re-evaluates the current health and updates <see cref="_cachedEvaluation"/>.
    /// Does <b>not</b> propagate to parents — used internally by
    /// <see cref="RefreshDescendants"/> and <see cref="HealthGraph.RefreshAll"/>
    /// which walk the graph themselves.
    /// </summary>
    internal void NotifyChangedCore()
    {
        var deps = _dependencies;

        HealthEvaluation intrinsic;
        if (_skipNextIntrinsicEvaluation)
        {
            _skipNextIntrinsicEvaluation = false;
            intrinsic = _cachedEvaluation;
        }
        else
        {
            intrinsic = _intrinsicCheck();
        }

        _cachedEvaluation = Aggregate(intrinsic, deps);
    }

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
    /// Always reads dependency health from <see cref="_cachedEvaluation"/>.
    /// </summary>
    internal static HealthEvaluation Aggregate(
        HealthEvaluation intrinsic,
        IReadOnlyList<HealthDependency> dependencies)
    {
        var depCount = dependencies.Count;
        var evals = new (HealthDependency dep, HealthEvaluation eval)[depCount];
        var hasHealthyResilient = false;

        for (var i = 0; i < depCount; i++)
        {
            var dep = dependencies[i];
            var eval = dep.Node._cachedEvaluation;
            evals[i] = (dep, eval);

            if (dep.Importance == Importance.Resilient && eval.Status == HealthStatus.Healthy)
                hasHealthyResilient = true;
        }

        // Second pass: compute effective status.
        var effective = intrinsic.Status;
        string? reason = intrinsic.Reason;

        for (var i = 0; i < depCount; i++)
        {
            var (dep, depEval) = evals[i];

            var contribution = dep.Importance switch
            {
                Importance.Required => depEval.Status,

                Importance.Important => depEval.Status switch
                {
                    HealthStatus.Unhealthy => HealthStatus.Degraded,
                    _ => depEval.Status,
                },

                Importance.Optional => HealthStatus.Healthy,

                Importance.Resilient when depEval.Status == HealthStatus.Unhealthy && hasHealthyResilient
                    => HealthStatus.Degraded,
                Importance.Resilient
                    => depEval.Status,

                _ => HealthStatus.Healthy,
            };

            if (contribution.IsWorseThan(effective))
            {
                effective = contribution;
                reason = depEval.Reason is not null
                    ? $"{dep.Node.Name}: {depEval.Reason}"
                    : $"{dep.Node.Name} is {depEval.Status}";
            }
        }

        return new HealthEvaluation(effective, reason);
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
    internal void RefreshDescendants()
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        NotifyDfs(this, visited);
    }

    internal static void NotifyDfs(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
        {
            NotifyDfs(dep.Node, visited);
        }

        node.NotifyChangedCore();
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
