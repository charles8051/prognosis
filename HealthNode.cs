namespace Prognosis;

/// <summary>
/// Base class for all nodes in the health graph. Use the static factory methods
/// <see cref="CreateDelegate(string, Func{HealthEvaluation})"/> (wraps a
/// health-check delegate) and <see cref="CreateComposite"/> (aggregates
/// dependencies with no backing service of its own) to create nodes.
/// <para>
/// Consumers who own a service class should implement <see cref="IHealthAware"/>
/// and expose a <see cref="HealthNode"/> property — typically via
/// <see cref="CreateDelegate(string, Func{HealthEvaluation})"/> when the
/// service has its own intrinsic check, or <see cref="CreateComposite"/>
/// when health is derived entirely from sub-dependencies. There is no need
/// to subclass <see cref="HealthNode"/> directly.
/// </para>
/// </summary>
public abstract class HealthNode
{
    [ThreadStatic]
    private static HashSet<HealthNode>? s_propagating;

    [ThreadStatic]
    private static HashSet<HealthNode>? s_evaluating;

    private readonly Func<HealthEvaluation> _intrinsicCheck;
    private readonly object _dependencyWriteLock = new();
    private readonly object _parentWriteLock = new();
    private volatile IReadOnlyList<HealthNode> _parents = Array.Empty<HealthNode>();
    private volatile IReadOnlyList<HealthDependency> _dependencies = Array.Empty<HealthDependency>();
    internal volatile HealthEvaluation? _cachedEvaluation;

    private readonly object _graphSetLock = new();
    private HashSet<HealthGraph>? _attachedGraphs;

    /// <summary>
    /// Strategy for propagating health changes after topology mutations
    /// (<see cref="DependsOn"/> / <see cref="RemoveDependency"/>).
    /// Defaults to a direct <see cref="BubbleChange"/> call. When one or
    /// more <see cref="HealthGraph"/> instances are attached, the strategy
    /// is rebuilt to call each graph's serialized propagation, so multiple
    /// graphs sharing the same node each receive their own propagation wave.
    /// </summary>
    private volatile Action<HealthNode> _bubbleStrategy = static node => node.BubbleChange();

    internal void AttachGraph(HealthGraph graph)
    {
        lock (_graphSetLock)
        {
            _attachedGraphs ??= new(ReferenceEqualityComparer.Instance);
            _attachedGraphs.Add(graph);
            RebuildBubbleStrategy();
        }
    }

    internal void DetachGraph(HealthGraph graph)
    {
        lock (_graphSetLock)
        {
            _attachedGraphs?.Remove(graph);
            RebuildBubbleStrategy();
        }
    }

    private void RebuildBubbleStrategy()
    {
        if (_attachedGraphs is null || _attachedGraphs.Count == 0)
        {
            _bubbleStrategy = static node => node.BubbleChange();
            return;
        }

        var graphs = new HealthGraph[_attachedGraphs.Count];
        _attachedGraphs.CopyTo(graphs);

        if (graphs.Length == 1)
        {
            var single = graphs[0];
            _bubbleStrategy = single.SerializedBubble;
        }
        else
        {
            _bubbleStrategy = node =>
            {
                foreach (var g in graphs)
                    g.SerializedBubble(node);
            };
        }
    }

    /// <summary>
    /// Invokes the bubble strategy, dispatching to all attached graphs.
    /// Used by <see cref="HealthGraph.Refresh(HealthNode)"/> so that a
    /// refresh on any one graph propagates to every graph sharing this node.
    /// </summary>
    internal void InvokeBubbleStrategy() => _bubbleStrategy(this);

    /// <param name="intrinsicCheck">
    /// A callback that returns the owning service's intrinsic health
    /// (e.g., whether a connection is alive). Called on every <see cref="Evaluate"/>.
    /// </param>
    private protected HealthNode(Func<HealthEvaluation> intrinsicCheck)
    {
        _intrinsicCheck = intrinsicCheck;
    }

    /// <summary>Display name for this node in the health graph.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Creates a node backed by a health-check delegate. The delegate is
    /// called on every <see cref="Evaluate"/> to obtain the service's
    /// intrinsic health.
    /// </summary>
    /// <param name="name">Display name for the service.</param>
    /// <param name="healthCheck">
    /// A delegate that returns the service's intrinsic health evaluation.
    /// </param>
    public static HealthNode CreateDelegate(string name, Func<HealthEvaluation> healthCheck)
        => new DelegateHealthNode(name, healthCheck);

    /// <summary>
    /// Creates a node backed by a health-check delegate whose intrinsic
    /// status is always <see cref="HealthStatus.Healthy"/>.
    /// </summary>
    /// <param name="name">Display name for the service.</param>
    public static HealthNode CreateDelegate(string name)
        => new DelegateHealthNode(name);

    /// <summary>
    /// Creates an aggregation-only node with no underlying service of its own.
    /// Health is derived entirely from its dependencies.
    /// </summary>
    /// <param name="name">Display name for this composite in the health graph.</param>
    public static HealthNode CreateComposite(string name)
        => new CompositeHealthNode(name);

    /// <inheritdoc/>
    public override string ToString() => $"{Name}: {Evaluate()}";

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
    /// Zero or more services this service depends on, each tagged with an
    /// importance level.
    /// </summary>
    public IReadOnlyList<HealthDependency> Dependencies => _dependencies;

    /// <summary>
    /// The effective health of this service, taking its own state and all
    /// dependency statuses (weighted by importance) into account.
    /// </summary>
    internal HealthEvaluation Evaluate()
    {
        s_evaluating ??= new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);

        if (!s_evaluating.Add(this))
            return HealthEvaluation.Unhealthy("Circular dependency detected");

        try
        {
            // Capture the volatile snapshot once — iteration is safe because
            // writers always replace the entire list (copy-on-write).
            var deps = _dependencies;
            return Aggregate(_intrinsicCheck(), deps);
        }
        finally
        {
            s_evaluating.Remove(this);
        }
    }

    internal static HealthTreeSnapshot BuildTreeSnapshot(
        HealthNode node, HashSet<HealthNode> visited)
    {
        var eval = node.Evaluate();

        if (!visited.Add(node))
        {
            // Already visited — return a leaf to break cycles / diamonds.
            return new HealthTreeSnapshot(
                node.Name, eval.Status, eval.Reason,
                Array.Empty<HealthTreeDependency>());
        }

        var deps = node.Dependencies;
        var children = new List<HealthTreeDependency>(deps.Count);
        foreach (var dep in deps)
        {
            children.Add(new HealthTreeDependency(
                dep.Importance,
                BuildTreeSnapshot(dep.Node, visited)));
        }

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
        var eval = Aggregate(_intrinsicCheck(), deps, useCachedDependencies: true);
        _cachedEvaluation = eval;
    }

    /// <summary>
    /// Registers a dependency on another service. Thread-safe and may be
    /// called at any time, including after evaluation has started. The new
    /// edge is visible to the next <see cref="Evaluate"/> call.
    /// Immediately calls <see cref="BubbleChange"/> so the new dependency's
    /// current health is reflected in all ancestors without waiting for the
    /// next poll cycle.
    /// </summary>
    public HealthNode DependsOn(HealthNode node, Importance importance)
    {
        lock (_dependencyWriteLock)
        {
            var updated = new List<HealthDependency>(_dependencies)
            {
                new(node, importance)
            };
            _dependencies = updated;
        }
        lock (node._parentWriteLock)
        {
            var updated = new List<HealthNode>(node._parents) { this };
            node._parents = updated;
        }
        _bubbleStrategy(this);
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
        bool removed;
        lock (_dependencyWriteLock)
        {
            var current = _dependencies;
            var index = -1;
            for (var i = 0; i < current.Count; i++)
            {
                if (ReferenceEquals(current[i].Node, node))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return false;

            var updated = new List<HealthDependency>(current.Count - 1);
            for (var i = 0; i < current.Count; i++)
            {
                if (i != index)
                    updated.Add(current[i]);
            }
            _dependencies = updated;
            removed = true;
        }

        if (removed)
        {
            lock (node._parentWriteLock)
            {
                var current = node._parents;
                var index = -1;
                for (var i = 0; i < current.Count; i++)
                {
                    if (ReferenceEquals(current[i], this))
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0)
                {
                    var updated = new List<HealthNode>(current.Count - 1);
                    for (var i = 0; i < current.Count; i++)
                    {
                        if (i != index)
                            updated.Add(current[i]);
                    }
                    node._parents = updated;
                }
            }
            _bubbleStrategy(this);
        }
        return removed;
    }

    /// <summary>
    /// Computes the worst-case health across the intrinsic evaluation and every
    /// dependency, with the propagation rules driven by <see cref="Importance"/>.
    /// </summary>
    internal static HealthEvaluation Aggregate(
        HealthEvaluation intrinsic,
        IReadOnlyList<HealthDependency> dependencies,
        bool useCachedDependencies = false)
    {
        // First pass: evaluate all dependencies and check whether any
        // Resilient sibling is healthy (needed for the Resilient rule).
        var depCount = dependencies.Count;
        var evals = new (HealthDependency dep, HealthEvaluation eval)[depCount];
        var hasHealthyResilient = false;

        for (var i = 0; i < depCount; i++)
        {
            var dep = dependencies[i];
            var eval = useCachedDependencies
                ? (dep.Node._cachedEvaluation ?? dep.Node.Evaluate())
                : dep.Node.Evaluate();
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

    internal static void WalkEvaluate(
        HealthNode node,
        HashSet<HealthNode> visited,
        List<HealthSnapshot> results)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
        {
            WalkEvaluate(dep.Node, visited, results);
        }

        var eval = node._cachedEvaluation ?? node.Evaluate();
        results.Add(new HealthSnapshot(node.Name, eval.Status, eval.Reason));
    }

    /// <summary>
    /// Re-evaluates the intrinsic health of every node in this node's
    /// dependency subtree (depth-first, leaves before parents), firing
    /// <see cref="StatusChanged"/> on any node whose effective health changed.
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
}
