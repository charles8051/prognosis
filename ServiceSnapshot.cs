namespace Prognosis;

/// <summary>
/// A point-in-time capture of a single service's evaluated health.
/// </summary>
public sealed record ServiceSnapshot(string Name, HealthStatus Status, int DependencyCount)
{
    public override string ToString() =>
        DependencyCount > 0
            ? $"{Name}: {Status} ({DependencyCount} dependencies)"
            : $"{Name}: {Status}";
}
