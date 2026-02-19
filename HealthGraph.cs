namespace Prognosis;

/// <summary>
/// A read-only view of a materialized health graph. Serves as the entry point
/// for report generation, monitoring, and Rx pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Build a graph manually with <see cref="Create"/> or let the DI builder in
/// <c>Prognosis.DependencyInjection</c> materialize one for you:
/// </para>
/// <code>
/// // Manual:
/// var graph = HealthGraph.Create(rootNode);
///
/// // DI:
/// var graph = serviceProvider.GetRequiredService&lt;HealthGraph&gt;();
/// </code>
/// </remarks>
public sealed class HealthGraph
{
    private readonly Dictionary<string, HealthNode> _services;

    internal HealthGraph(HealthNode[] roots, Dictionary<string, HealthNode> services)
    {
        Roots = roots;
        _services = services;
    }

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> by walking the dependency graph
    /// from the given roots and indexing every reachable node by
    /// <see cref="HealthNode.Name"/>.
    /// </summary>
    public static HealthGraph Create(params HealthNode[] roots)
    {
        var services = new Dictionary<string, HealthNode>();
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);

        foreach (var root in roots)
            Walk(root, visited, services);

        return new HealthGraph(roots, services);
    }

    /// <summary>
    /// The top-level graph entry points. Pass directly to
    /// <see cref="HealthAggregator.CreateReport"/>,
    /// <see cref="HealthAggregator.EvaluateAll"/>, or the Rx
    /// <c>PollHealthReport</c> / <c>ObserveHealthReport</c> extensions.
    /// </summary>
    public HealthNode[] Roots { get; }

    /// <summary>
    /// Looks up any node in the graph by its <see cref="HealthNode.Name"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No service with the given name exists in the graph.
    /// </exception>
    public HealthNode this[string name] => _services[name];

    /// <summary>
    /// Attempts to look up a node by name, returning <see langword="false"/>
    /// if no service with the given name exists.
    /// </summary>
    public bool TryGetService(string name, out HealthNode service) =>
        _services.TryGetValue(name, out service!);

    /// <summary>
    /// Looks up a service whose <see cref="HealthNode.Name"/> matches
    /// <c>typeof(T).Name</c>. This is a convenience for the common convention
    /// where service names are derived from their concrete types.
    /// </summary>
    /// <typeparam name="T">
    /// The type whose <see cref="System.Type.Name"/> is used as the lookup key.
    /// </typeparam>
    public bool TryGetService<T>(out HealthNode service) where T : class, IHealthAware =>
        _services.TryGetValue(typeof(T).Name, out service!);

    /// <summary>
    /// All named services in the graph (leaves, composites, and delegates).
    /// </summary>
    public IEnumerable<HealthNode> Services => _services.Values;

    /// <summary>Convenience: creates a point-in-time report from all roots.</summary>
    public HealthReport CreateReport() => HealthAggregator.CreateReport(Roots);

    private static void Walk(
        HealthNode node,
        HashSet<HealthNode> visited,
        Dictionary<string, HealthNode> services)
    {
        if (!visited.Add(node))
            return;

        services[node.Name] = node;

        foreach (var dep in node.Dependencies)
            Walk(dep.Service, visited, services);
    }
}
