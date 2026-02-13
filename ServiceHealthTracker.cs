namespace ServiceHealthModel;

/// <summary>
/// A composable helper that any existing service can embed to participate in the
/// health graph via delegation. The owning class implements <see cref="IServiceHealth"/>
/// and forwards <see cref="IServiceHealth.Dependencies"/> and <see cref="IServiceHealth.Evaluate"/>
/// to this tracker.
/// </summary>
/// <remarks>
/// Usage: embed a <see cref="ServiceHealthTracker"/> as a field, supply a
/// <c>Func&lt;HealthStatus&gt;</c> that returns the service's own intrinsic health,
/// then delegate the interface members.
/// </remarks>
public sealed class ServiceHealthTracker
{
    private readonly Func<HealthStatus> _intrinsicCheck;
    private readonly List<ServiceDependency> _dependencies = [];

    /// <param name="intrinsicCheck">
    /// A callback that returns the owning service's intrinsic health
    /// (e.g., whether a connection is alive). Called on every <see cref="Evaluate"/>.
    /// </param>
    public ServiceHealthTracker(Func<HealthStatus> intrinsicCheck)
    {
        _intrinsicCheck = intrinsicCheck;
    }

    /// <summary>Shortcut: always-healthy intrinsic status.</summary>
    public ServiceHealthTracker()
        : this(() => HealthStatus.Healthy) { }

    public IReadOnlyList<ServiceDependency> Dependencies => _dependencies;

    public ServiceHealthTracker DependsOn(IServiceHealth service, ServiceImportance importance)
    {
        _dependencies.Add(new ServiceDependency(service, importance));
        return this;
    }

    public HealthStatus Evaluate() =>
        HealthAggregator.Aggregate(_intrinsicCheck(), _dependencies);
}
