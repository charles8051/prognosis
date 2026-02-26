namespace Prognosis;

/// <summary>
/// A read-only view of a materialized health graph. Serves as the entry point
/// for report generation, monitoring, and Rx pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Build a graph manually with <see cref="Create"/> or let the DI builder in
/// <c>Prognosis.DependencyInjection</c> materialize one for you.
/// The nodes you pass in are the roots — the full topology is discovered
/// by walking their dependency edges downward.
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
    private readonly HealthNode[] _roots;
    private readonly HashSet<HealthNode> _allNodes;
    private readonly Dictionary<string, HealthNode> _nodesByName;

    internal HealthGraph(params HealthNode[] roots)
    {
        _roots = roots;

        _allNodes = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
            Collect(root, _allNodes);

        _nodesByName = new Dictionary<string, HealthNode>(_allNodes.Count, StringComparer.Ordinal);
        foreach (var node in _allNodes)
            _nodesByName[node.Name] = node;
    }

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> rooted at the given nodes.
    /// The full topology is discovered by walking dependency edges downward,
    /// so all transitive dependencies are included automatically.
    /// </summary>
    public static HealthGraph Create(params HealthNode[] roots) => new HealthGraph(roots);

    /// <summary>
    /// The root nodes of the graph — the nodes passed to <see cref="Create"/>
    /// or provided by the DI builder.
    /// </summary>
    public IReadOnlyList<HealthNode> Roots => _roots;

    /// <summary>
    /// Looks up any node in the graph by its <see cref="HealthNode.Name"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No service with the given name exists in the graph.
    /// </exception>
    public HealthNode this[string name]
    {
        get
        {
            if (TryGetNode(name, out var node))
                return node;

            throw new KeyNotFoundException(
                $"No service named '{name}' exists in the graph.");
        }
    }

    /// <summary>
    /// Attempts to look up a node by name, returning <see langword="false"/>
    /// if no node with the given name exists.
    /// </summary>
    public bool TryGetNode(string name, out HealthNode node) =>
        _nodesByName.TryGetValue(name, out node!);

    /// <summary>
    /// Looks up a node whose <see cref="HealthNode.Name"/> matches
    /// <c>typeof(T).Name</c>. This is a convenience for the common convention
    /// where node names are derived from their concrete types.
    /// </summary>
    /// <typeparam name="T">
    /// The type whose <see cref="System.Type.Name"/> is used as the lookup key.
    /// </typeparam>
    public bool TryGetNode<T>(out HealthNode node) where T : class, IHealthAware =>
        TryGetNode(typeof(T).Name, out node);

    /// <summary>
    /// All nodes reachable from the roots, discovered at construction time
    /// by walking dependency edges downward.
    /// </summary>
    public IEnumerable<HealthNode> Nodes => _allNodes;

    /// <summary>
    /// Evaluates the full graph and packages the result as a serialization-ready
    /// <see cref="HealthReport"/> with a timestamp and overall status.
    /// </summary>
    public HealthReport CreateReport()
    {
        var services = EvaluateAll();
        var overall = services.Count > 0
            ? services.Max(s => s.Status)
            : HealthStatus.Healthy;

        return new HealthReport(DateTimeOffset.UtcNow, overall, services);
    }

    /// <summary>
    /// Walks the full dependency graph from all roots and returns every
    /// node's evaluated status. Results are in depth-first post-order
    /// (leaves before their parents) and each node appears at most once.
    /// </summary>
    public IReadOnlyList<HealthSnapshot> EvaluateAll()
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var results = new List<HealthSnapshot>();

        foreach (var root in _roots)
        {
            HealthNode.WalkEvaluate(root, visited, results);
        }

        return results;
    }

    /// <summary>
    /// Performs a DFS from all discovered nodes and returns every cycle found
    /// as an ordered list of node names (e.g. ["A", "B", "A"]).
    /// Returns an empty list when the graph is acyclic.
    /// </summary>
    /// <remarks>
    /// Walks from all nodes — not just roots — because when every node in a
    /// cycle has a parent, none of them appear as roots.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<string>> DetectCycles()
    {
        var gray = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var black = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var path = new List<HealthNode>();
        var cycles = new List<IReadOnlyList<string>>();

        foreach (var node in _allNodes)
        {
            DetectCyclesDfs(node, gray, black, path, cycles);
        }

        return cycles;
    }

    /// <summary>
    /// Walks the dependency graph depth-first from all roots and calls
    /// <see cref="HealthNode.NotifyChangedCore"/> on every node encountered.
    /// Leaves are notified before their parents.
    /// </summary>
    public void NotifyAll()
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        foreach (var root in _roots)
        {
            HealthNode.NotifyDfs(root, visited);
        }
    }

    private static void Collect(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
            Collect(dep.Node, visited);
    }

    private static void DetectCyclesDfs(
        HealthNode node,
        HashSet<HealthNode> gray,
        HashSet<HealthNode> black,
        List<HealthNode> path,
        List<IReadOnlyList<string>> cycles)
    {
        if (black.Contains(node))
            return;

        if (!gray.Add(node))
        {
            var cycleStart = path.IndexOf(node);
            var cycle = new List<string>(path.Count - cycleStart + 1);
            for (var i = cycleStart; i < path.Count; i++)
            {
                cycle.Add(path[i].Name);
            }
            cycle.Add(node.Name);
            cycles.Add(cycle);
            return;
        }

        path.Add(node);

        foreach (var dep in node.Dependencies)
        {
            DetectCyclesDfs(dep.Node, gray, black, path, cycles);
        }

        path.RemoveAt(path.Count - 1);
        gray.Remove(node);
        black.Add(node);
    }
}
