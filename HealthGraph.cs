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
    private readonly object _propagationLock = new();
    private readonly object _topologyLock = new();
    private readonly object _topologyObserverLock = new();
    private readonly List<IObserver<TopologyChange>> _topologyObservers = new();
    private readonly object _statusObserverLock = new();
    private readonly List<IObserver<HealthReport>> _statusObservers = new();
    private volatile NodeSnapshot _snapshot;
    private volatile HealthReport? _cachedReport;

    internal HealthGraph(HealthNode root)
    {
        _root = root;

        var allNodes = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        Collect(root, allNodes);

        _snapshot = new NodeSnapshot(allNodes);

        foreach (var node in allNodes)
            node._bubbleStrategy = SerializedBubble;

        TopologyChanged = new TopologyObservable(this);
        StatusChanged = new StatusObservable(this);
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
    /// Emits a <see cref="TopologyChange"/> each time nodes are added to or
    /// removed from the graph (via <see cref="HealthNode.DependsOn"/> or
    /// <see cref="HealthNode.RemoveDependency"/>). Does not fire when only
    /// health statuses change.
    /// </summary>
    public IObservable<TopologyChange> TopologyChanged { get; }

    /// <summary>
    /// Emits a <see cref="HealthReport"/> each time the graph's effective
    /// health state changes. Emissions are driven by
    /// <see cref="Refresh(HealthNode)"/>, <see cref="HealthNode.DependsOn"/>,
    /// <see cref="HealthNode.RemoveDependency"/>, and <see cref="RefreshAll"/>.
    /// Only fires when the report actually differs from the previous one.
    /// </summary>
    public IObservable<HealthReport> StatusChanged { get; }

    /// <summary>
    /// Re-evaluates <paramref name="node"/> and propagates upward through
    /// its ancestors, rebuilds the cached report, and emits
    /// <see cref="StatusChanged"/> if the overall state changed.
    /// </summary>
    public void Refresh(HealthNode node)
    {
        SerializedBubble(node);
    }

    /// <summary>
    /// Re-evaluates the node with the given <paramref name="name"/> and
    /// propagates upward through its ancestors, rebuilds the cached report,
    /// and emits <see cref="StatusChanged"/> if the overall state changed.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No node with the given name exists in the graph.
    /// </exception>
    public void Refresh(string name) => Refresh(this[name]);

    /// <summary>
    /// Evaluates a single node's effective health (intrinsic check plus
    /// its dependency subtree).
    /// </summary>
    /// <param name="name">The <see cref="HealthNode.Name"/> of the node to evaluate.</param>
    /// <exception cref="KeyNotFoundException">
    /// No node with the given name exists in the graph.
    /// </exception>
    public HealthEvaluation Evaluate(string name) => this[name].Evaluate();

    /// <summary>
    /// Evaluates a single node's effective health (intrinsic check plus
    /// its dependency subtree).
    /// </summary>
    public HealthEvaluation Evaluate(HealthNode node) => node.Evaluate();

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
    public IEnumerable<HealthNode> Nodes => _snapshot.Nodes;

    /// <summary>
    /// Returns the cached <see cref="HealthReport"/> that reflects the
    /// latest state after the most recent propagation or refresh. If no
    /// propagation has occurred yet, builds the report on first access.
    /// </summary>
    public HealthReport CreateReport() =>
        _cachedReport ?? RebuildReport();

    /// <summary>
    /// Evaluates the full graph and returns a tree-shaped
    /// <see cref="HealthTreeSnapshot"/> whose nesting mirrors the dependency
    /// topology. Ideal for JSON serialization where hierarchy should be
    /// visible in the output structure.
    /// </summary>
    public HealthTreeSnapshot CreateTreeSnapshot()
    {
        var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        return HealthNode.BuildTreeSnapshot(_root, visited);
    }

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

        foreach (var node in _snapshot.Nodes)
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
    public void RefreshAll()
    {
        HealthReport? reportToEmit = null;

        lock (_propagationLock)
        {
            _root.RefreshDescendants();

            var previous = _cachedReport;
            var report = RebuildReport();

            if (previous is null
                || !HealthReportComparer.Instance.Equals(previous, report))
            {
                reportToEmit = report;
            }
        }

        if (reportToEmit is not null)
            EmitStatusChanged(reportToEmit);
    }

    private static void Collect(HealthNode node, HashSet<HealthNode> visited)
    {
        if (!visited.Add(node))
            return;

        foreach (var dep in node.Dependencies)
            Collect(dep.Node, visited);
    }

    private HealthReport RebuildReport()
    {
        var nodes = _snapshot.Nodes;
        var results = new List<HealthSnapshot>(nodes.Length);
        foreach (var node in nodes)
        {
            var eval = node._cachedEvaluation ?? node.Evaluate();
            results.Add(new HealthSnapshot(node.Name, eval.Status, eval.Reason));
        }
        var report = new HealthReport(results);
        _cachedReport = report;
        return report;
    }

    private void SerializedBubble(HealthNode origin)
    {
        HealthReport? reportToEmit = null;

        lock (_propagationLock)
        {
            origin.BubbleChange();
            RefreshTopology();

            var previous = _cachedReport;
            var report = RebuildReport();

            if (previous is null
                || !HealthReportComparer.Instance.Equals(previous, report))
            {
                reportToEmit = report;
            }
        }

        if (reportToEmit is not null)
            EmitStatusChanged(reportToEmit);
    }

    private void RefreshTopology()
    {
        TopologyChange? change = null;

        lock (_topologyLock)
        {
            var fresh = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
            Collect(_root, fresh);

            var current = _snapshot;
            if (fresh.Count == current.Set.Count && fresh.SetEquals(current.Set))
                return;

            var added = new List<HealthNode>();
            var removed = new List<HealthNode>();

            foreach (var node in fresh)
            {
                if (!current.Set.Contains(node))
                    added.Add(node);
            }

            foreach (var node in current.Set)
            {
                if (!fresh.Contains(node))
                    removed.Add(node);
            }

            foreach (var node in added)
                node._bubbleStrategy = SerializedBubble;

            foreach (var node in removed)
                node._bubbleStrategy = static n => n.BubbleChange();

            _snapshot = new NodeSnapshot(fresh);
            change = new TopologyChange(added, removed);
        }

        NotifyTopologyObservers(change);
    }

    private void NotifyTopologyObservers(TopologyChange change)
    {
        List<IObserver<TopologyChange>>? snapshot;
        lock (_topologyObserverLock)
        {
            if (_topologyObservers.Count == 0)
                return;
            snapshot = new List<IObserver<TopologyChange>>(_topologyObservers);
        }

        foreach (var observer in snapshot)
        {
            observer.OnNext(change);
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

    private sealed class TopologyObservable(HealthGraph graph) : IObservable<TopologyChange>
    {
        public IDisposable Subscribe(IObserver<TopologyChange> observer)
        {
            lock (graph._topologyObserverLock)
            {
                graph._topologyObservers.Add(observer);
            }
            return new Unsubscriber(graph, observer);
        }
    }

    private sealed class Unsubscriber(HealthGraph graph, IObserver<TopologyChange> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (graph._topologyObserverLock)
            {
                graph._topologyObservers.Remove(observer);
            }
        }
    }

    private void EmitStatusChanged(HealthReport report)
    {
        List<IObserver<HealthReport>>? snapshot;
        lock (_statusObserverLock)
        {
            if (_statusObservers.Count == 0)
                return;
            snapshot = new List<IObserver<HealthReport>>(_statusObservers);
        }

        foreach (var observer in snapshot)
        {
            observer.OnNext(report);
        }
    }

    private sealed class StatusObservable(HealthGraph graph) : IObservable<HealthReport>
    {
        public IDisposable Subscribe(IObserver<HealthReport> observer)
        {
            lock (graph._statusObserverLock)
            {
                graph._statusObservers.Add(observer);
            }
            return new StatusUnsubscriber(graph, observer);
        }
    }

    private sealed class StatusUnsubscriber(HealthGraph graph, IObserver<HealthReport> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (graph._statusObserverLock)
            {
                graph._statusObservers.Remove(observer);
            }
        }
    }

    private sealed class NodeSnapshot
    {
        public readonly HashSet<HealthNode> Set;
        public readonly Dictionary<string, HealthNode> Index;
        public readonly HealthNode[] Nodes;

        public NodeSnapshot(HashSet<HealthNode> set)
        {
            Set = set;
            Nodes = new HealthNode[set.Count];
            set.CopyTo(Nodes);
            Index = new Dictionary<string, HealthNode>(set.Count, StringComparer.Ordinal);
            foreach (var node in set)
                Index[node.Name] = node;
        }
    }
}
