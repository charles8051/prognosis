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
    private readonly HealthNode[] _seeds;

    internal HealthGraph(params HealthNode[] seeds)
    {
        _seeds = seeds;
    }

    /// <summary>
    /// Creates a <see cref="HealthGraph"/> anchored at the given seed nodes.
    /// The full topology — including roots, services, and lookup results — is
    /// discovered dynamically by walking parent and dependency edges, so
    /// runtime changes to the graph are always reflected.
    /// </summary>
    public static HealthGraph Create(params HealthNode[] nodes) => new HealthGraph(nodes);

    /// <summary>
    /// The current root nodes — nodes that are not a dependency of any other
    /// node. Discovered dynamically by exploring the full reachable graph
    /// from the seeds, so runtime edge changes (via
    /// <see cref="HealthNode.DependsOn"/> / <see cref="HealthNode.RemoveDependency"/>)
    /// are always reflected.
    /// </summary>
    public HealthNode[] Roots
    {
        get
        {
            var all = DiscoverAll();
            var roots = new List<HealthNode>();
            foreach (var node in all)
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
    public bool TryGetNode(string name, out HealthNode node)
    {
        foreach (var n in DiscoverAll())
        {
            if (n.Name == name)
            {
                node = n;
                return true;
            }
        }

        node = null!;
        return false;
    }

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
    /// All nodes reachable from the seed nodes (leaves, composites, and
    /// delegates). Discovered dynamically from the current graph topology.
    /// </summary>
    public IEnumerable<HealthNode> Nodes => DiscoverAll();

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
        var roots = Roots;
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        var results = new List<HealthSnapshot>();

        foreach (var root in roots)
        {
            WalkEvaluate(root, visited, results);
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

        foreach (var node in Nodes)
        {
            DetectCyclesDfs(node, gray, black, path, cycles);
        }

        return cycles;
    }

    /// <summary>
    /// Walks the dependency graph depth-first from all roots and calls
    /// <see cref="HealthNode.NotifyChanged"/> on every node encountered.
    /// Leaves are notified before their parents.
    /// </summary>
    public void NotifyAll()
    {
        var roots = Roots;
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
        {
            NotifyDfs(root, visited);
        }
    }

    /// <summary>
    /// Walks parent and dependency edges from every seed to discover all
    /// reachable nodes in the graph. Bi-directional traversal ensures that
    /// nodes added above or below the original seeds at runtime are found.
    /// </summary>
    private HashSet<HealthNode> DiscoverAll()
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        foreach (var seed in _seeds)
            Explore(seed, visited);

        return visited;
    }

    private static void Explore(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        foreach (var parent in node.Parents)
            Explore(parent, visited);

        foreach (var dep in node.Dependencies)
            Explore(dep.Node, visited);
    }

    private static void WalkEvaluate(
        HealthNode node,
        HashSet<HealthNode> visited,
        List<HealthSnapshot> results)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
        {
            WalkEvaluate(dep.Node, visited, results);
        }

        var eval = node.Evaluate();
        results.Add(new HealthSnapshot(node.Name, eval.Status, node.Dependencies.Count, eval.Reason));
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

    private static void NotifyDfs(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
        {
            NotifyDfs(dep.Node, visited);
        }

        node.NotifyChangedCore();
    }
}
