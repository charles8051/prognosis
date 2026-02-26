namespace Prognosis;

/// <summary>
/// Base class for all nodes in the health graph. Concrete implementations are
/// <see cref="HealthCheck"/> (wraps a health-check delegate) and
/// <see cref="HealthGroup"/> (aggregates dependencies with no
/// backing service of its own).
/// <para>
/// Consumers who own a service class should implement <see cref="IHealthAware"/>
/// and expose a <see cref="HealthNode"/> property — typically a
/// <see cref="HealthCheck"/> when the service has its own intrinsic
/// check, or a <see cref="HealthGroup"/> when health is derived
/// entirely from sub-dependencies. There is no need to subclass
/// <see cref="HealthNode"/> directly.
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
    private readonly object _observerLock = new();
    private readonly List<IObserver<HealthStatus>> _observers = new();
    private volatile IReadOnlyList<HealthNode> _parents = Array.Empty<HealthNode>();
    private volatile IReadOnlyList<HealthDependency> _dependencies = Array.Empty<HealthDependency>();
    private HealthStatus? _lastEmitted;

    /// <param name="intrinsicCheck">
    /// A callback that returns the owning service's intrinsic health
    /// (e.g., whether a connection is alive). Called on every <see cref="Evaluate"/>.
    /// </param>
    private protected HealthNode(Func<HealthEvaluation> intrinsicCheck)
    {
        _intrinsicCheck = intrinsicCheck;
        StatusChanged = new StatusObservable(this);
    }

    /// <summary>Display name for this node in the health graph.</summary>
    public abstract string Name { get; }

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
    public HealthEvaluation Evaluate()
    {
        s_evaluating ??= new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);

        if (!s_evaluating.Add(this))
            return new HealthEvaluation(HealthStatus.Unhealthy, "Circular dependency detected");

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

    /// <summary>
    /// Emits the new <see cref="HealthStatus"/> each time the service's
    /// effective health changes.
    /// </summary>
    public IObservable<HealthStatus> StatusChanged { get; }

    /// <summary>
    /// Re-evaluates the current health, pushes a notification through
    /// <see cref="StatusChanged"/> if the status has changed, and
    /// automatically propagates upward through <see cref="Parents"/>
    /// so that the entire ancestor chain is re-evaluated.
    /// <para>
    /// Diamond graphs and cycles are handled correctly — each node
    /// is visited at most once per propagation wave.
    /// </para>
    /// </summary>
    public void NotifyChanged()
    {
        var isRoot = s_propagating is null;
        s_propagating ??= new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);

        try
        {
            if (!s_propagating.Add(this))
                return;

            NotifyChangedCore();

            foreach (var parent in _parents)
                parent.NotifyChanged();
        }
        finally
        {
            if (isRoot)
                s_propagating = null;
        }
    }

    /// <summary>
    /// Re-evaluates the current health and pushes a notification through
    /// <see cref="StatusChanged"/> if the status has changed.
    /// Does <b>not</b> propagate to parents — used internally by
    /// <see cref="HealthGraph.NotifyAll"/> which walks the full graph itself.
    /// </summary>
    internal void NotifyChangedCore()
    {
        var current = Evaluate().Status;

        List<IObserver<HealthStatus>>? snapshot = null;
        lock (_observerLock)
        {
            if (current == _lastEmitted)
                return;
            _lastEmitted = current;
            if (_observers.Count > 0)
                snapshot = new List<IObserver<HealthStatus>>(_observers);
        }

        if (snapshot is not null)
        {
            foreach (var observer in snapshot)
            {
                observer.OnNext(current);
            }
        }
    }

    /// <summary>
    /// Registers a dependency on another service. Thread-safe and may be
    /// called at any time, including after evaluation has started. The new
    /// edge is visible to the next <see cref="Evaluate"/> call.
    /// Immediately calls <see cref="NotifyChanged"/> so the new dependency's
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
        NotifyChanged();
        return this;
    }

    /// <summary>
    /// Removes the first dependency that references <paramref name="node"/>.
    /// Returns <see langword="true"/> if a dependency was removed; otherwise
    /// <see langword="false"/>. Immediately calls <see cref="NotifyChanged"/>
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
            NotifyChanged();
        }
        return removed;
    }

    /// <summary>
    /// Computes the worst-case health across the intrinsic evaluation and every
    /// dependency, with the propagation rules driven by <see cref="Importance"/>.
    /// </summary>
    internal static HealthEvaluation Aggregate(
        HealthEvaluation intrinsic,
        IReadOnlyList<HealthDependency> dependencies)
    {
        // First pass: evaluate all dependencies and check whether any
        // Resilient sibling is healthy (needed for the Resilient rule).
        var depCount = dependencies.Count;
        var evals = new (HealthDependency dep, HealthEvaluation eval)[depCount];
        var hasHealthyResilient = false;

        for (var i = 0; i < depCount; i++)
        {
            var dep = dependencies[i];
            var eval = dep.Node.Evaluate();
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

            if (contribution > effective)
            {
                effective = contribution;
                reason = depEval.Reason is not null
                    ? $"{dep.Node.Name}: {depEval.Reason}"
                    : $"{dep.Node.Name} is {depEval.Status}";
            }
        }

        return new HealthEvaluation(effective, reason);
    }

    private void AddObserver(IObserver<HealthStatus> observer)
    {
        lock (_observerLock)
        {
            _observers.Add(observer);
        }
    }

    private void RemoveObserver(IObserver<HealthStatus> observer)
    {
        lock (_observerLock)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class StatusObservable(HealthNode node) : IObservable<HealthStatus>
    {
        public IDisposable Subscribe(IObserver<HealthStatus> observer)
        {
            node.AddObserver(observer);
            return new Unsubscriber(node, observer);
        }
    }

    private sealed class Unsubscriber(HealthNode node, IObserver<HealthStatus> observer) : IDisposable
    {
        public void Dispose() => node.RemoveObserver(observer);
    }
}
