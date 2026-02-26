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

    private volatile IReadOnlyList<HealthNode> _parents = Array.Empty<HealthNode>();
    private readonly object _parentWriteLock = new();

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
    public abstract IReadOnlyList<HealthDependency> Dependencies { get; }

    /// <summary>
    /// The effective health of this service, taking its own state and all
    /// dependency statuses (weighted by importance) into account.
    /// </summary>
    public abstract HealthEvaluation Evaluate();

    /// <summary>
    /// Emits the new <see cref="HealthStatus"/> each time the service's
    /// effective health changes.
    /// </summary>
    public abstract IObservable<HealthStatus> StatusChanged { get; }

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
    internal abstract void NotifyChangedCore();

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
        AddDependency(node, importance);
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
        if (RemoveDependencyCore(node))
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
            return true;
        }
        return false;
    }

    /// <summary>Subclass hook used by <see cref="DependsOn"/>.</summary>
    private protected abstract void AddDependency(HealthNode node, Importance importance);

    /// <summary>Subclass hook used by <see cref="RemoveDependency"/>.</summary>
    private protected abstract bool RemoveDependencyCore(HealthNode node);
}
