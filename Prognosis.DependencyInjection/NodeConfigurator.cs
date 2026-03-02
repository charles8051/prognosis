using Microsoft.Extensions.DependencyInjection;

namespace Prognosis.DependencyInjection;

/// <summary>
/// Fluent API for configuring a health node within a
/// <see cref="PrognosisBuilder"/>. Returned by
/// <see cref="PrognosisBuilder.AddNode(string)"/>.
/// <para>
/// Mirrors the core <see cref="HealthNode"/> fluent pattern:
/// <c>builder.AddNode("name").WithHealthProbe&lt;T&gt;(...).DependsOn(...)</c>.
/// </para>
/// </summary>
public sealed class NodeConfigurator
{
    internal string Name { get; }
    internal Type? ServiceType { get; private set; }
    internal Func<IServiceProvider, HealthEvaluation>? HealthCheck { get; private set; }
    internal List<EdgeDefinition> Edges { get; } = [];

    internal NodeConfigurator(string name) => Name = name;

    /// <summary>
    /// Attaches a health-check delegate that wraps a DI-registered service.
    /// The service is resolved from DI at graph materialization time.
    /// Returns <see langword="this"/> for fluent chaining.
    /// </summary>
    /// <typeparam name="TService">
    /// The type of the service to resolve from DI and health-check.
    /// </typeparam>
    /// <param name="healthCheck">
    /// A function that inspects the resolved service and returns its health.
    /// </param>
    public NodeConfigurator WithHealthProbe<TService>(
        Func<TService, HealthEvaluation> healthCheck)
        where TService : class
    {
        ServiceType = typeof(TService);
        HealthCheck = sp => healthCheck(
            (TService)sp.GetRequiredService(typeof(TService)));
        return this;
    }

    /// <summary>
    /// Declares a dependency on a named node in the health graph.
    /// Returns <see langword="this"/> for fluent chaining.
    /// </summary>
    public NodeConfigurator DependsOn(string serviceName, Importance importance)
    {
        Edges.Add(new EdgeDefinition(serviceName, importance));
        return this;
    }

    /// <summary>
    /// Declares a dependency on a node whose name matches
    /// <c>typeof(TService).Name</c>.
    /// Returns <see langword="this"/> for fluent chaining.
    /// </summary>
    public NodeConfigurator DependsOn<TService>(Importance importance)
        where TService : class
        => DependsOn(typeof(TService).Name, importance);
}
