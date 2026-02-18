namespace Prognosis.DependencyInjection;

/// <summary>
/// Non-generic base for <see cref="DependsOnAttribute{TDependency}"/>.
/// Used for reflection access during assembly scanning.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public abstract class DependsOnAttribute(Type dependencyType, Importance importance) : Attribute
{
    /// <summary>The concrete <see cref="IHealthAware"/> type this service depends on.</summary>
    public Type DependencyType { get; } = dependencyType;

    /// <summary>How important this dependency is to the declaring service.</summary>
    public Importance Importance { get; } = importance;
}

/// <summary>
/// Declares that the annotated <see cref="IHealthAware"/> implementation depends
/// on another service in the health graph. Read during assembly scanning by
/// <see cref="PrognosisBuilder.ScanForServices"/>.
/// </summary>
/// <typeparam name="TDependency">
/// The concrete <see cref="IHealthAware"/> type this service depends on.
/// </typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DependsOnAttribute<TDependency>(
    Importance importance = Importance.Required)
    : DependsOnAttribute(typeof(TDependency), importance)
    where TDependency : class, IHealthAware;
