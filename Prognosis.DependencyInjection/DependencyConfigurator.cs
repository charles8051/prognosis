namespace Prognosis.DependencyInjection;

/// <summary>
/// Fluent API for declaring dependency edges on composite and delegate
/// services within a <see cref="PrognosisBuilder"/>.
/// </summary>
public sealed class DependencyConfigurator
{
    internal List<EdgeDefinition> Edges { get; } = [];

    /// <summary>
    /// Declares a dependency on a DI-registered <see cref="IServiceHealth"/>
    /// implementation, referenced by its concrete type.
    /// </summary>
    public DependencyConfigurator DependsOn<TService>(ServiceImportance importance)
        where TService : IServiceHealth
    {
        Edges.Add(new EdgeDefinition(typeof(TService), null, importance));
        return this;
    }

    /// <summary>
    /// Declares a dependency on a named service in the health graph. Use this
    /// to reference composites, delegates, or any service by its
    /// <see cref="IServiceHealth.Name"/>.
    /// </summary>
    public DependencyConfigurator DependsOn(string serviceName, ServiceImportance importance)
    {
        Edges.Add(new EdgeDefinition(null, serviceName, importance));
        return this;
    }
}
