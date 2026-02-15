namespace Prognosis;

/// <summary>
/// A virtual service whose health is derived entirely from its dependencies.
/// It has no underlying service of its own — it is a named aggregation point.
/// </summary>
public sealed class CompositeServiceHealth : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _tracker;

    public string Name { get; }

    public IReadOnlyList<ServiceDependency> Dependencies => _tracker.Dependencies;

    public IObservable<HealthStatus> StatusChanged => _tracker.StatusChanged;

    /// <param name="name">Display name for this composite in the health graph.</param>
    public CompositeServiceHealth(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A composite service must have a name.", nameof(name));

        Name = name;
        _tracker = new ServiceHealthTracker(() => HealthStatus.Healthy);
    }

    public CompositeServiceHealth(
        string name,
        IReadOnlyList<ServiceDependency> dependencies)
        : this(name)
    {
        foreach (var dep in dependencies)
        {
            _tracker.DependsOn(dep.Service, dep.Importance);
        }
    }

    /// <summary>Registers a dependency on another service.</summary>
    public CompositeServiceHealth DependsOn(IServiceHealth service, ServiceImportance importance)
    {
        _tracker.DependsOn(service, importance);
        return this;
    }

    public void NotifyChanged() => _tracker.NotifyChanged();

    public HealthEvaluation Evaluate() => _tracker.Evaluate();

    public override string ToString() => $"{Name}: {Evaluate()}";
}
