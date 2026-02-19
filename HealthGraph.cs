namespace Prognosis;

/// <summary>
/// A read-only view of a materialized health graph. Serves as the entry point
/// for report generation, monitoring, and Rx pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Build a graph manually with <see cref="Create"/> or let the DI builder in
/// <c>Prognosis.DependencyInjection</c> materialize one for you.
/// Roots are computed dynamically — a root is any node that is not a
/// dependency of another node. When edges are added or removed at runtime
/// the set of roots updates automatically.
/// </para>
/// <code>
/// // Manual:
/// var graph = HealthGraph.Create(topLevelNode);
///
/// // DI:
/// var graph = serviceProvider.GetRequiredService&lt;HealthGraph&gt;();
/// </code>
/// </remarks>
public sealed class HealthGraph
{
    private readonly Dictionary<string, HealthNode> _nodes;

    internal HealthGraph(Dictionary<string, HealthNode> nodes)
    {
        _nodes = nodes;
    }

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> by walking the dependency graph
    /// from the given entry-point nodes and indexing every reachable node by
    /// <see cref="HealthNode.Name"/>. Roots are discovered automatically as
    /// nodes that no other node depends on.
    /// </summary>
    public static HealthGraph Create(params HealthNode[] nodes)
    {
        var nodesByName = new Dictionary<string, HealthNode>();
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);

        foreach (var node in nodes)
            Walk(node, visited, nodesByName);

        return new HealthGraph(nodesByName);
    }

    /// <summary>
    /// The current root nodes — nodes that are not a dependency of any other
    /// node. Computed dynamically so that runtime edge changes (via
    /// <see cref="HealthNode.DependsOn"/> / <see cref="HealthNode.RemoveDependency"/>)
    /// are reflected automatically.
    /// </summary>
    public HealthNode[] Roots
    {
        get
        {
            var roots = new List<HealthNode>();
            foreach (var node in _nodes.Values)
            {
                if (!node.HasParents)
                    roots.Add(node);
            }

            return roots.ToArray();
        }
    }

    /// <summary>
    /// Looks up any node in the graph by its <see cref="HealthNode.Name"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No service with the given name exists in the graph.
    /// </exception>
    public HealthNode this[string name] => _nodes[name];

    /// <summary>
    /// Attempts to look up a node by name, returning <see langword="false"/>
    /// if no service with the given name exists.
    /// </summary>
    public bool TryGetService(string name, out HealthNode node) =>
        _nodes.TryGetValue(name, out node!);

    /// <summary>
    /// Looks up a service whose <see cref="HealthNode.Name"/> matches
    /// <c>typeof(T).Name</c>. This is a convenience for the common convention
    /// where service names are derived from their concrete types.
    /// </summary>
    /// <typeparam name="T">
    /// The type whose <see cref="System.Type.Name"/> is used as the lookup key.
    /// </typeparam>
    public bool TryGetService<T>(out HealthNode node) where T : class, IHealthAware =>
        _nodes.TryGetValue(typeof(T).Name, out node!);

    /// <summary>
    /// All named services in the graph (leaves, composites, and delegates).
    /// </summary>
    public IEnumerable<HealthNode> Services => _nodes.Values;

    /// <summary>Convenience: creates a point-in-time report from all roots.</summary>
    public HealthReport CreateReport() => HealthAggregator.CreateReport(Roots);

    private static void Walk(
        HealthNode node,
        HashSet<HealthNode> visited,
        Dictionary<string, HealthNode> nodesByName)
    {
        if (!visited.Add(node))
            return;

        nodesByName[node.Name] = node;

        foreach (var dep in node.Dependencies)
            Walk(dep.Service, visited, nodesByName);
    }
}
