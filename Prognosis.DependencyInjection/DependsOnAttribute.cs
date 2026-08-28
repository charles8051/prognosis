namespace Prognosis.DependencyInjection;

/// <summary>
/// Declares that the annotated <see cref="HealthNode"/> property depends on
/// another node in the health graph, referenced by name. Applied to
/// <see cref="HealthNode"/> properties on service classes; read by the
/// <c>Prognosis.Generators</c> source generator to emit
/// <c>AddDiscoveredNodes()</c> wiring code.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class DependsOnAttribute(string dependencyName, Importance importance = Importance.Required) : Attribute
{
    /// <summary>The <see cref="HealthNode.Name"/> of the dependency node.</summary>
    public string DependencyName { get; } = dependencyName;

    /// <summary>How important this dependency is to the declaring node.</summary>
    public Importance Importance { get; } = importance;
}
