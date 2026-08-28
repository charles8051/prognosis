using System.Text;

namespace Prognosis.Tests.Fuzzing;

/// <summary>
/// A directed edge in a <see cref="TopologySpec"/>: the index of the dependency
/// node plus the <see cref="Prognosis.Importance"/> the parent assigns it.
/// </summary>
public sealed record FuzzEdge(int Target, Importance Importance);

/// <summary>
/// A node in a <see cref="TopologySpec"/>: the status its probe reports
/// (its <em>intrinsic</em> health) plus its outgoing dependency edges.
/// </summary>
public sealed record FuzzNode(HealthStatus Intrinsic, IReadOnlyList<FuzzEdge> Edges);

/// <summary>
/// A pure, immutable, serializable description of a health graph — the value the
/// fuzzer generates, the shrinker minimizes, and a failing test prints.
/// <para>
/// Node <c>0</c> is always the root. Everything the properties need to know about a
/// graph (does it have a cycle, which nodes sit behind an
/// <see cref="Importance.Optional"/> edge, which are leaves) is answered here,
/// against the spec, so a property never asks the implementation under test to
/// describe itself.
/// </para>
/// <para>
/// The spec round-trips through a one-line <see cref="ToLiteral"/> encoding, so any
/// case the fuzzer finds can be pinned verbatim into the regression corpus — see
/// <c>TopologyFuzzTests.Corpus</c>.
/// </para>
/// </summary>
/// <param name="Shape">The generator that produced this spec, for failure output.</param>
/// <param name="Nodes">Nodes by index; index 0 is the root.</param>
public sealed record TopologySpec(string Shape, IReadOnlyList<FuzzNode> Nodes)
{
    public const int Root = 0;

    public int Count => Nodes.Count;

    public int EdgeCount => Nodes.Sum(n => n.Edges.Count);

    /// <summary>The graph node name for a spec index. Names must be unique per graph.</summary>
    public static string NameOf(int index) => $"n{index}";

    // ── Structural queries, answered against the spec ────────────────────────────

    /// <summary>Indices reachable from the root by following every edge.</summary>
    public IReadOnlySet<int> Reachable()
    {
        var seen = new HashSet<int> { Root };
        var stack = new Stack<int>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            foreach (var edge in Nodes[stack.Pop()].Edges)
            {
                if (seen.Add(edge.Target))
                    stack.Push(edge.Target);
            }
        }
        return seen;
    }

    /// <summary>
    /// Indices reachable from the root <em>without</em> traversing an
    /// <see cref="Importance.Optional"/> edge. A reachable node outside this set can
    /// only be seen through an ignored edge, so nothing it does may reach the root.
    /// </summary>
    public IReadOnlySet<int> ReachableWithoutOptional()
    {
        var seen = new HashSet<int> { Root };
        var stack = new Stack<int>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            foreach (var edge in Nodes[stack.Pop()].Edges)
            {
                if (edge.Importance == Importance.Optional)
                    continue;
                if (seen.Add(edge.Target))
                    stack.Push(edge.Target);
            }
        }
        return seen;
    }

    /// <summary>
    /// Whether every composite is intrinsically healthy — the
    /// <see cref="IntrinsicMode.LeavesOnly"/> regime, in which snapshot-only intrinsic
    /// reconstruction is exact and the diagnostic layer can be held to exact agreement
    /// with the live engine.
    /// </summary>
    public bool HasLeafFailuresOnly() =>
        Reachable().All(i =>
            Nodes[i].Edges.Count == 0 || Nodes[i].Intrinsic == HealthStatus.Healthy);

    /// <summary>Reachable indices with no outgoing edges.</summary>
    public IReadOnlyList<int> Leaves() =>
        Reachable().Where(i => Nodes[i].Edges.Count == 0).OrderBy(i => i).ToList();

    /// <summary>
    /// Whether the reachable subgraph contains a directed cycle (a self-loop counts).
    /// Ground truth for the <c>DetectCycles</c> property — computed here rather than
    /// trusted from the shape label, because a random generator can produce an acyclic
    /// instance of a nominally cyclic shape and vice versa.
    /// </summary>
    public bool HasCycle()
    {
        var gray = new HashSet<int>();
        var black = new HashSet<int>();

        foreach (var start in Reachable().OrderBy(i => i))
        {
            if (Visit(start))
                return true;
        }
        return false;

        bool Visit(int node)
        {
            if (black.Contains(node))
                return false;
            if (!gray.Add(node))
                return true;

            foreach (var edge in Nodes[node].Edges)
            {
                if (Visit(edge.Target))
                    return true;
            }

            gray.Remove(node);
            black.Add(node);
            return false;
        }
    }

    // ── Edits (used by the shrinker; every one re-normalizes) ────────────────────

    /// <summary>
    /// Drops unreachable nodes and reindexes what remains, preserving order. Every
    /// edit funnels through this, so a spec is always exactly its reachable set —
    /// which is also what <see cref="HealthGraph.Create"/> materializes.
    /// </summary>
    public TopologySpec Normalize()
    {
        var reachable = Reachable();
        if (reachable.Count == Count)
            return this;

        var kept = reachable.OrderBy(i => i).ToList();
        var remap = new Dictionary<int, int>(kept.Count);
        for (var i = 0; i < kept.Count; i++)
            remap[kept[i]] = i;

        var nodes = kept
            .Select(old => new FuzzNode(
                Nodes[old].Intrinsic,
                Nodes[old].Edges
                    .Where(e => remap.ContainsKey(e.Target))
                    .Select(e => new FuzzEdge(remap[e.Target], e.Importance))
                    .ToList()))
            .ToList();

        return this with { Nodes = nodes };
    }

    public TopologySpec WithIntrinsic(int index, HealthStatus status) =>
        this with
        {
            Nodes = Nodes
                .Select((n, i) => i == index ? n with { Intrinsic = status } : n)
                .ToList(),
        };

    public TopologySpec WithImportance(int node, int edgeIndex, Importance importance) =>
        this with
        {
            Nodes = Nodes
                .Select((n, i) => i != node
                    ? n
                    : n with
                    {
                        Edges = n.Edges
                            .Select((e, j) => j == edgeIndex ? e with { Importance = importance } : e)
                            .ToList(),
                    })
                .ToList(),
        };

    public TopologySpec WithoutEdge(int node, int edgeIndex) =>
        (this with
        {
            Nodes = Nodes
                .Select((n, i) => i != node
                    ? n
                    : n with { Edges = n.Edges.Where((_, j) => j != edgeIndex).ToList() })
                .ToList(),
        }).Normalize();

    /// <summary>
    /// Removes a node and every edge pointing at it. The root cannot be removed;
    /// removing a cut vertex orphans its subtree, which <see cref="Normalize"/> then
    /// prunes — that is the shrinker's biggest single win.
    /// </summary>
    public TopologySpec WithoutNode(int index)
    {
        if (index == Root)
            return this;

        var nodes = Nodes
            .Where((_, i) => i != index)
            .Select(n => new FuzzNode(
                n.Intrinsic,
                n.Edges
                    .Where(e => e.Target != index)
                    .Select(e => new FuzzEdge(e.Target > index ? e.Target - 1 : e.Target, e.Importance))
                    .ToList()))
            .ToList();

        return (this with { Nodes = nodes }).Normalize();
    }

    // ── Materialization ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the live graph this spec describes. Every node gets a constant probe
    /// returning its intrinsic status, so the graph is quiescent the moment it is
    /// built and every property observes a settled fold.
    /// </summary>
    public MaterializedGraph Materialize()
    {
        var nodes = new HealthNode[Count];
        for (var i = 0; i < Count; i++)
        {
            var status = Nodes[i].Intrinsic;
            var name = NameOf(i);
            nodes[i] = HealthNode.Create(name)
                .WithHealthProbe(() => status == HealthStatus.Healthy
                    ? HealthEvaluation.Healthy
                    : new HealthEvaluation(status, $"{name} intrinsic {status}"));
        }

        for (var i = 0; i < Count; i++)
        {
            foreach (var edge in Nodes[i].Edges)
                nodes[i].DependsOn(nodes[edge.Target], edge.Importance);
        }

        return new MaterializedGraph(HealthGraph.Create(nodes[Root]), nodes);
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    /// <summary>A Mermaid diagram of the spec, printed on failure so the shape is legible.</summary>
    public string ToMermaid()
    {
        var sb = new StringBuilder();
        sb.AppendLine("graph TD");
        foreach (var i in Reachable().OrderBy(i => i))
        {
            sb.AppendLine($"    {NameOf(i)}[\"{NameOf(i)} ({Nodes[i].Intrinsic})\"]");
            foreach (var edge in Nodes[i].Edges)
                sb.AppendLine($"    {NameOf(i)} -->|{edge.Importance}| {NameOf(edge.Target)}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// A one-line encoding: <c>shape=H&gt;1R,2O;X;D&gt;0S</c> — nodes separated by
    /// <c>;</c>, each an intrinsic-status letter optionally followed by <c>&gt;</c>
    /// and comma-separated <c>target</c>+importance-letter edges. Round-trips through
    /// <see cref="Parse"/>, so a shrunk counterexample pastes straight into the
    /// regression corpus.
    /// </summary>
    public string ToLiteral()
    {
        var sb = new StringBuilder(Shape).Append('=');
        for (var i = 0; i < Count; i++)
        {
            if (i > 0)
                sb.Append(';');
            sb.Append(StatusChar(Nodes[i].Intrinsic));
            if (Nodes[i].Edges.Count == 0)
                continue;
            sb.Append('>');
            for (var j = 0; j < Nodes[i].Edges.Count; j++)
            {
                if (j > 0)
                    sb.Append(',');
                sb.Append(Nodes[i].Edges[j].Target)
                  .Append(ImportanceChar(Nodes[i].Edges[j].Importance));
            }
        }
        return sb.ToString();
    }

    /// <summary>Inverse of <see cref="ToLiteral"/>.</summary>
    public static TopologySpec Parse(string literal)
    {
        var split = literal.IndexOf('=');
        var shape = split < 0 ? "pinned" : literal[..split];
        var body = split < 0 ? literal : literal[(split + 1)..];

        var nodes = new List<FuzzNode>();
        foreach (var part in body.Split(';'))
        {
            var arrow = part.IndexOf('>');
            var statusPart = arrow < 0 ? part : part[..arrow];
            var edges = new List<FuzzEdge>();

            if (arrow >= 0)
            {
                foreach (var edgeText in part[(arrow + 1)..].Split(','))
                {
                    var target = int.Parse(edgeText[..^1]);
                    edges.Add(new FuzzEdge(target, ImportanceOf(edgeText[^1])));
                }
            }

            nodes.Add(new FuzzNode(StatusOf(statusPart.Trim()[0]), edges));
        }

        return new TopologySpec(shape, nodes);
    }

    private static char StatusChar(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 'H',
        HealthStatus.Unknown => 'U',
        HealthStatus.Degraded => 'D',
        HealthStatus.Unhealthy => 'X',
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static HealthStatus StatusOf(char c) => c switch
    {
        'H' => HealthStatus.Healthy,
        'U' => HealthStatus.Unknown,
        'D' => HealthStatus.Degraded,
        'X' => HealthStatus.Unhealthy,
        _ => throw new FormatException($"Unknown status letter '{c}'."),
    };

    private static char ImportanceChar(Importance importance) => importance switch
    {
        Importance.Required => 'R',
        Importance.Important => 'I',
        Importance.Optional => 'O',
        Importance.Resilient => 'S',
        Importance.Advisory => 'A',
        _ => throw new ArgumentOutOfRangeException(nameof(importance), importance, null),
    };

    private static Importance ImportanceOf(char c) => c switch
    {
        'R' => Importance.Required,
        'I' => Importance.Important,
        'O' => Importance.Optional,
        'S' => Importance.Resilient,
        'A' => Importance.Advisory,
        _ => throw new FormatException($"Unknown importance letter '{c}'."),
    };
}

/// <summary>A live graph built from a <see cref="TopologySpec"/>, plus its nodes by index.</summary>
public sealed class MaterializedGraph : IDisposable
{
    public MaterializedGraph(HealthGraph graph, IReadOnlyList<HealthNode> nodes)
    {
        Graph = graph;
        Nodes = nodes;
    }

    public HealthGraph Graph { get; }

    public IReadOnlyList<HealthNode> Nodes { get; }

    public HealthStatus RootStatus => Graph.GetReport().Root.Status;

    public void Dispose() => Graph.Dispose();
}
