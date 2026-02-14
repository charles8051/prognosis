namespace ServiceHealthModel;

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

    public CompositeServiceHealth(
        string name,
        IReadOnlyList<ServiceDependency> dependencies)
    {
        Name = name;
        _tracker = new ServiceHealthTracker();
        foreach (var dep in dependencies)
        {
            _tracker.DependsOn(dep.Service, dep.Importance);
        }
    }

    public void NotifyChanged() => _tracker.NotifyChanged();

    public HealthStatus Evaluate() => _tracker.Evaluate();

    public override string ToString() => $"{Name}: {Evaluate()}";
}
