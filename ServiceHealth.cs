namespace Prognosis;

/// <summary>
/// Base class for all nodes in the health graph. Concrete implementations are
/// <see cref="DelegatingServiceHealth"/> (wraps a health-check delegate) and
/// <see cref="CompositeServiceHealth"/> (aggregates dependencies with no
/// backing service of its own).
/// <para>
/// Consumers who own a service class should implement <see cref="IServiceHealth"/>
/// and expose a <see cref="ServiceHealth"/> property — typically a
/// <see cref="DelegatingServiceHealth"/> when the service has its own intrinsic
/// check, or a <see cref="CompositeServiceHealth"/> when health is derived
/// entirely from sub-dependencies. There is no need to subclass
/// <see cref="ServiceHealth"/> directly.
/// </para>
/// </summary>
public abstract class ServiceHealth
{
    /// <summary>Display name for this node in the health graph.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Zero or more services this service depends on, each tagged with an
    /// importance level.
    /// </summary>
    public abstract IReadOnlyList<ServiceDependency> Dependencies { get; }

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
    /// Re-evaluates the current health and pushes a notification through
    /// <see cref="StatusChanged"/> if the status has changed.
    /// Call this when the service knows something has changed, or let a
    /// <see cref="HealthMonitor"/> call it on a polling interval.
    /// </summary>
    public abstract void NotifyChanged();

    /// <summary>
    /// Registers a dependency on another service. Must be called before the
    /// first <see cref="Evaluate"/> — the dependency list is frozen once
    /// evaluation begins.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if called after <see cref="Evaluate"/> has been invoked.
    /// </exception>
    public ServiceHealth DependsOn(ServiceHealth service, ServiceImportance importance)
    {
        AddDependency(service, importance);
        return this;
    }

    /// <summary>Subclass hook used by <see cref="DependsOn"/>.</summary>
    private protected abstract void AddDependency(ServiceHealth service, ServiceImportance importance);
}
