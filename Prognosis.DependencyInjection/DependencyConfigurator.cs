namespace Prognosis.DependencyInjection;

/// <summary>
/// Fluent API for declaring dependency edges on composite and delegate
/// nodes within a <see cref="PrognosisBuilder"/>.
/// </summary>
public sealed class DependencyConfigurator
{
    internal List<EdgeDefinition> Edges { get; } = [];

    /// <summary>
    /// Declares a dependency on a node whose name matches
    /// <c>typeof(TService).Name</c>.
    /// </summary>
    public DependencyConfigurator DependsOn<TService>(Importance importance)
        where TService : class
    {
        Edges.Add(new EdgeDefinition(typeof(TService).Name, importance));
        return this;
    }

    /// <summary>
    /// Declares a dependency on a named node in the health graph.
    /// </summary>
    public DependencyConfigurator DependsOn(string serviceName, Importance importance)
    {
        Edges.Add(new EdgeDefinition(serviceName, importance));
        return this;
    }
}
