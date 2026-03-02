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
    internal List<ServiceNodeDefinition> ServiceNodes { get; } = [];
    internal List<NodeConfigurator> NodeDefinitions { get; } = [];
    internal List<RootDefinition> Roots { get; } = [];

    internal PrognosisBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// Designates the node whose <see cref="HealthNode.Name"/> matches
    /// <c>typeof(T).Name</c> as a root of the health graph and registers
    /// a <see cref="HealthGraph{TRoot}"/> so the graph can be resolved
    /// from DI without keyed services.
    /// <para>
    /// When only one root is declared, the graph is also registered as a
    /// plain <see cref="HealthGraph"/> singleton.  When multiple roots are
    /// declared, each is registered as a keyed <see cref="HealthGraph"/>
    /// (keyed by the root name) and optionally as
    /// <see cref="HealthGraph{TRoot}"/>.
    /// </para>
    /// </summary>
    /// <typeparam name="T">
    /// A marker type whose <see cref="System.Type.Name"/> identifies the
    /// root node. Typically a service class registered via
    /// <see cref="AddServiceNode{TService}"/> or a node defined via
    /// <see cref="AddNode(string)"/>.
    /// </typeparam>
    public PrognosisBuilder MarkAsRoot<T>() where T : class
    {
        var name = typeof(T).Name;
        Roots.Add(new RootDefinition(
            name,
            static (services, graphFactory) =>
                services.AddSingleton(sp => new HealthGraph<T>(graphFactory(sp)))));
        return this;
    }

    /// <summary>
    /// Designates the node with the given name as a root of the health graph.
    /// <para>
    /// When only one root is declared, the graph is registered as a plain
    /// <see cref="HealthGraph"/> singleton.  When multiple roots are
    /// declared, each is registered as a keyed <see cref="HealthGraph"/>
    /// (keyed by the root name).  To resolve a specific graph without
    /// keyed services, use the generic <see cref="MarkAsRoot{T}"/>
    /// overload instead.
    /// </para>
    /// </summary>
    public PrognosisBuilder MarkAsRoot(string name)
    {
        Roots.Add(new RootDefinition(name, null));
        return this;
    }

    /// <summary>
    /// Registers a DI service that exposes one or more <see cref="HealthNode"/>
    /// properties. The service is registered as a singleton (if not already)
    /// and the selector extracts the <see cref="HealthNode"/> at graph
    /// materialization time.
    /// <para>
    /// Typically called by generated code from <c>AddDiscoveredNodes()</c>,
    /// but can also be called manually.
    /// </para>
    /// </summary>
    /// <typeparam name="TService">
    /// The concrete service type to resolve from DI.
    /// </typeparam>
    /// <param name="nodeSelector">
    /// A function that extracts the <see cref="HealthNode"/> from the resolved service.
    /// </param>
    /// <param name="dependencies">
    /// Optional dependency configuration for this node.
    /// </param>
    public PrognosisBuilder AddServiceNode<TService>(
        Func<TService, HealthNode> nodeSelector,
        Action<DependencyConfigurator>? dependencies = null)
        where TService : class
    {
        var configurator = new DependencyConfigurator();
        dependencies?.Invoke(configurator);
        ServiceNodes.Add(new ServiceNodeDefinition(
            typeof(TService),
            sp => nodeSelector((TService)sp.GetRequiredService(typeof(TService))),
            configurator.Edges));
        return this;
    }

    /// <summary>
    /// Defines a new node in the health graph. Optionally attach a
    /// health-check probe via <see cref="NodeConfigurator.WithHealthProbe{TService}"/>
    /// and declare dependencies via <see cref="NodeConfigurator.DependsOn(string, Importance)"/>.
    /// <para>
    /// Mirrors the core <see cref="HealthNode.Create(string)"/> fluent
    /// pattern:
    /// <c>builder.AddNode("name").WithHealthProbe&lt;T&gt;(...).DependsOn(...)</c>.
    /// </para>
    /// </summary>
    /// <param name="name">
    /// Display name for this node in the health graph. Used as the key for
    /// <see cref="HealthGraph.TryGetNode(string, out HealthNode)"/> and
    /// reported in <see cref="HealthSnapshot.Name"/>.
    /// </param>
    public NodeConfigurator AddNode(string name)
    {
        var configurator = new NodeConfigurator(name);
        NodeDefinitions.Add(configurator);
        return configurator;
    }
}

// ── Internal definition records ──────────────────────────────────────

internal sealed record EdgeDefinition(
    string? ServiceName,
    Importance Importance);

internal sealed record ServiceNodeDefinition(
    Type ServiceType,
    Func<IServiceProvider, HealthNode> NodeSelector,
    List<EdgeDefinition> Edges);

internal sealed record RootDefinition(
    string Name,
    Action<IServiceCollection, Func<IServiceProvider, HealthGraph>>? RegisterTyped);
