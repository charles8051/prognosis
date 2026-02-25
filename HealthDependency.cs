namespace Prognosis;

/// <summary>
/// A weighted edge from a parent service to one of its dependencies.
/// Read-only — edges are created exclusively through
/// <see cref="HealthNode.DependsOn"/>.
/// </summary>
public sealed class HealthDependency
{
    internal HealthDependency(HealthNode service, Importance importance)
    {
        Node = service;
        Importance = importance;
    }

    /// <summary>The dependency target.</summary>
    public HealthNode Node { get; }

    /// <summary>How failures in this dependency propagate to the parent.</summary>
    public Importance Importance { get; }
}
