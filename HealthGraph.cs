namespace Prognosis;

/// <summary>
/// A read-only view of a materialized health graph. Serves as the entry point
/// for report generation, monitoring, and Rx pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Build a graph manually with <see cref="Create"/> or let the DI builder in
/// <c>Prognosis.DependencyInjection</c> materialize one for you.
/// The node you pass in is the root — the full topology is discovered
/// by walking its dependency edges downward.
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
    private readonly HealthNode _root;
    private readonly object _topologyLock = new();
    private volatile NodeSnapshot _snapshot;

    internal HealthGraph(HealthNode root)
    {
        _root = root;

        var allNodes = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        Collect(root, allNodes);

        _snapshot = new NodeSnapshot(allNodes);

        _root._topologyCallback = RefreshTopology;
    }

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> rooted at the given node.
    /// The full topology is discovered by walking dependency edges downward,
    /// so all transitive dependencies are included automatically.
    /// </summary>
    public static HealthGraph Create(HealthNode root) => new HealthGraph(root);

    /// <summary>
    /// The root node of the graph — the node passed to <see cref="Create"/>
    /// or provided by the DI builder.
    /// </summary>
    public HealthNode Root => _root;

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
        _snapshot.Index.TryGetValue(name, out node!);

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
    /// All nodes reachable from the root. Automatically kept in sync when
    /// dependencies are added or removed via <see cref="HealthNode.DependsOn"/>
    /// / <see cref="HealthNode.RemoveDependency"/>, because those operations
    /// trigger <see cref="HealthNode.BubbleChange"/> which refreshes the
    /// graph's internal collections.
    /// </summary>
    public IEnumerable<HealthNode> Nodes => _snapshot.Set;

    /// <summary>
    /// Evaluates the full graph and packages the result as a
    /// <see cref="HealthReport"/>.
    /// </summary>
    public HealthReport CreateReport()
    {
        var nodes = EvaluateAll();

        return new HealthReport(nodes);
    }

    /// <summary>
    /// Evaluates the full graph and returns a tree-shaped
    /// <see cref="HealthTreeSnapshot"/> whose nesting mirrors the dependency
    /// topology. Ideal for JSON serialization where hierarchy should be
    /// visible in the output structure.
    /// </summary>
    public HealthTreeSnapshot CreateTreeSnapshot() => _root.CreateTreeSnapshot();

    /// <summary>
    /// Walks the full dependency graph from the root and returns every
    /// node's evaluated status. Results are in depth-first post-order
    /// (leaves before their parents) and each node appears at most once.
    /// </summary>
    public IReadOnlyList<HealthSnapshot> EvaluateAll()
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var results = new List<HealthSnapshot>();
        HealthNode.WalkEvaluate(_root, visited, results);
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

        foreach (var node in _snapshot.Set)
        {
            DetectCyclesDfs(node, gray, black, path, cycles);
        }

        return cycles;
    }

    /// <summary>
    /// Walks the dependency graph depth-first from the root and calls
    /// <see cref="HealthNode.NotifyChangedCore"/> on every node encountered.
    /// Leaves are refreshed before their parents.
    /// </summary>
    public void RefreshAll() => _root.RefreshDescendants();

    private static void Collect(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
            Collect(dep.Node, visited);
    }

    private void RefreshTopology()
    {
        lock (_topologyLock)
        {
            var fresh = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
            Collect(_root, fresh);

            var current = _snapshot;
            if (fresh.Count == current.Set.Count && fresh.SetEquals(current.Set))
                return;

            _snapshot = new NodeSnapshot(fresh);
        }
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

    private sealed class NodeSnapshot
    {
        public readonly HashSet<HealthNode> Set;
        public readonly Dictionary<string, HealthNode> Index;

        public NodeSnapshot(HashSet<HealthNode> set)
        {
            Set = set;
            Index = new Dictionary<string, HealthNode>(set.Count, StringComparer.Ordinal);
            foreach (var node in set)
                Index[node.Name] = node;
        }
    }
}
