namespace Prognosis.DependencyInjection;

/// <summary>
/// Non-generic base for <see cref="DependsOnAttribute{TDependency}"/>.
/// Used for reflection access during assembly scanning.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public abstract class DependsOnAttribute(Type dependencyType, ServiceImportance importance) : Attribute
{
    /// <summary>The concrete <see cref="IServiceHealth"/> type this service depends on.</summary>
    public Type DependencyType { get; } = dependencyType;

    /// <summary>How important this dependency is to the declaring service.</summary>
    public ServiceImportance Importance { get; } = importance;
}

/// <summary>
/// Declares that the annotated <see cref="IServiceHealth"/> implementation depends
/// on another service in the health graph. Read during assembly scanning by
/// <see cref="PrognosisBuilder.ScanForServices"/>.
/// </summary>
/// <typeparam name="TDependency">
/// The concrete <see cref="IServiceHealth"/> type this service depends on.
/// </typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DependsOnAttribute<TDependency>(
    ServiceImportance importance = ServiceImportance.Required)
    : DependsOnAttribute(typeof(TDependency), importance)
    where TDependency : IServiceHealth;
