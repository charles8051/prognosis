namespace Prognosis;

/// <summary>
/// Represents a single node in the health graph. Create instances via the
/// static factory methods <see cref="CreateDelegate(string, Func{HealthEvaluation})"/>
/// (wraps a health-check delegate) and <see cref="CreateComposite"/>
/// (aggregates dependencies with no backing service of its own).
/// <para>
/// Consumers who own a service class should implement <see cref="IHealthAware"/>
/// and expose a <see cref="HealthNode"/> property — typically via
/// <see cref="CreateDelegate(string, Func{HealthEvaluation})"/> when the
/// service has its own intrinsic check, or <see cref="CreateComposite"/>
/// when health is derived entirely from sub-dependencies.
/// </para>
/// </summary>
public sealed class HealthNode
{
    [ThreadStatic]
    private static HashSet<HealthNode>? s_propagating;

    private readonly Func<HealthEvaluation> _intrinsicCheck;
    private readonly object _dependencyWriteLock = new();
    private readonly object _parentWriteLock = new();
    private volatile IReadOnlyList<HealthNode> _parents = Array.Empty<HealthNode>();
    private volatile IReadOnlyList<HealthDependency> _dependencies = Array.Empty<HealthDependency>();
    internal volatile HealthEvaluation _cachedEvaluation;

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
            throw new ArgumentException("A service must have a name.", nameof(name));

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
    /// Call this from an <see cref="IHealthAware"/> service when the
    /// underlying state changes (e.g., a connection drops) to push the
    /// change immediately without waiting for the next poll tick.
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
    /// Creates a node backed by a health-check delegate. The delegate is
    /// called on every <see cref="Refresh"/> to obtain the service's
    /// intrinsic health.
    /// </summary>
    /// <param name="name">Display name for the service.</param>
    /// <param name="healthCheck">
    /// A delegate that returns the service's intrinsic health evaluation.
    /// </param>
    public static HealthNode CreateDelegate(string name, Func<HealthEvaluation> healthCheck)
        => new HealthNode(name, healthCheck);

    /// <summary>
    /// Creates a node backed by a health-check delegate whose intrinsic
    /// status is always <see cref="HealthStatus.Healthy"/>.
    /// </summary>
    /// <param name="name">Display name for the service.</param>
    public static HealthNode CreateDelegate(string name)
        => new HealthNode(name);

    /// <summary>
    /// Creates an aggregation-only node with no underlying service of its own.
    /// Health is derived entirely from its dependencies.
    /// </summary>
    /// <param name="name">Display name for this composite in the health graph.</param>
    public static HealthNode CreateComposite(string name)
        => new HealthNode(name);

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
    /// Zero or more services this service depends on, each tagged with an
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
        var eval = Aggregate(_intrinsicCheck(), deps);
        _cachedEvaluation = eval;
    }

    /// <summary>
    /// Registers a dependency on another service. Thread-safe and may be
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
            var current = _dependencies;
            for (var i = 0; i < current.Count; i++)
            {
                if (ReferenceEquals(current[i].Node, node))
                    throw new InvalidOperationException(
                        $"'{Name}' already depends on '{node.Name}'.");
            }

            var updated = new List<HealthDependency>(current)
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
        }

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
        Refresh();
        return true;
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
}
