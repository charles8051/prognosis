using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Prognosis.DependencyInjection;

/// <summary>
/// Fluent builder for configuring the Prognosis health graph within a
/// <see cref="IServiceCollection"/>. Obtained via
/// <see cref="ServiceCollectionExtensions.AddPrognosis"/>.
/// </summary>
public sealed class PrognosisBuilder
{
    internal IServiceCollection Services { get; }
    internal List<Assembly> ScanAssemblies { get; } = [];
    internal List<CompositeDefinition> Composites { get; } = [];
    internal List<DelegateDefinition> Delegates { get; } = [];

    internal PrognosisBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// Scans the given assemblies for all concrete <see cref="IHealthAware"/>
    /// implementations and registers them as singletons. Also reads
    /// <see cref="DependsOnAttribute"/> to auto-wire dependency edges.
    /// </summary>
    public PrognosisBuilder ScanForServices(params Assembly[] assemblies)
    {
        ScanAssemblies.AddRange(assemblies);
        return this;
    }

    /// <summary>
    /// Wraps a DI-registered service you don't own (or don't want to modify)
    /// with a health-check delegate. The service name defaults to
    /// <c>typeof(TService).Name</c>.
    /// </summary>
    /// <typeparam name="TService">
    /// The type of the service to resolve from DI and health-check.
    /// </typeparam>
    /// <param name="healthCheck">
    /// A function that inspects the resolved service and returns its health.
    /// </param>
    /// <param name="dependencies">
    /// Optional dependency configuration for this delegate wrapper.
    /// </param>
    public PrognosisBuilder AddDelegate<TService>(
        Func<TService, HealthEvaluation> healthCheck,
        Action<DependencyConfigurator>? dependencies = null)
        where TService : class
        => AddDelegate(typeof(TService).Name, healthCheck, dependencies);

    /// <summary>
    /// Wraps a DI-registered service you don't own (or don't want to modify)
    /// with a health-check delegate.
    /// </summary>
    /// <typeparam name="TService">
    /// The type of the service to resolve from DI and health-check.
    /// </typeparam>
    /// <param name="name">Display name for this service in the health graph.</param>
    /// <param name="healthCheck">
    /// A function that inspects the resolved service and returns its health.
    /// </param>
    /// <param name="dependencies">
    /// Optional dependency configuration for this delegate wrapper.
    /// </param>
    public PrognosisBuilder AddDelegate<TService>(
        string name,
        Func<TService, HealthEvaluation> healthCheck,
        Action<DependencyConfigurator>? dependencies = null)
        where TService : class
    {
        var configurator = new DependencyConfigurator();
        dependencies?.Invoke(configurator);
        Delegates.Add(new DelegateDefinition(
            name,
            typeof(TService),
            sp => healthCheck((TService)sp.GetRequiredService(typeof(TService))),
            configurator.Edges));
        return this;
    }

    /// <summary>
    /// Defines a pure composite aggregation node whose name is derived from
    /// <typeparamref name="TToken"/> (<c>typeof(TToken).Name</c>).
    /// The type is used only for naming — it is not resolved from DI.
    /// </summary>
    /// <typeparam name="TToken">
    /// A type whose name identifies this composite in the health graph.
    /// Typically a marker class, the service class it represents, or any
    /// meaningful type in the domain.
    /// </typeparam>
    public PrognosisBuilder AddComposite<TToken>(
        Action<DependencyConfigurator> configure)
        => AddComposite(typeof(TToken).Name, configure);

    /// <summary>
    /// Defines a pure composite aggregation node with no backing service.
    /// Its health is derived entirely from its dependencies.
    /// </summary>
    /// <param name="name">Display name for this composite in the health graph.</param>
    /// <param name="configure">
    /// A callback to declare dependencies via <see cref="DependencyConfigurator"/>.
    /// </param>
    public PrognosisBuilder AddComposite(
        string name,
        Action<DependencyConfigurator> configure)
    {
        var configurator = new DependencyConfigurator();
        configure(configurator);
        Composites.Add(new CompositeDefinition(name, configurator.Edges));
        return this;
    }

    /// <summary>
    /// Previously used to mark named services as roots. Roots are now
    /// discovered automatically as nodes that no other node depends on.
    /// This method is retained for source compatibility but has no effect.
    /// </summary>
    [Obsolete("Roots are now discovered automatically from the graph topology. This method has no effect.")]
    public PrognosisBuilder AddRoots(params string[] names)
    {
        return this;
    }
}

// ── Internal definition records ──────────────────────────────────────

internal sealed record EdgeDefinition(
    Type? ServiceType,
    string? ServiceName,
    Importance Importance);

internal sealed record CompositeDefinition(
    string Name,
    List<EdgeDefinition> Edges);

internal sealed record DelegateDefinition(
    string Name,
    Type ServiceType,
    Func<IServiceProvider, HealthEvaluation> HealthCheck,
    List<EdgeDefinition> Edges);
