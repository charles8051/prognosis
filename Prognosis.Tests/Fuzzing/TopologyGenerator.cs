namespace Prognosis.Tests.Fuzzing;

/// <summary>
/// Where non-healthy intrinsic statuses are allowed to sit.
/// </summary>
public enum IntrinsicMode
{
    /// <summary>
    /// Any node may report any status from its own probe — including a composite that
    /// fails intrinsically while its dependencies are all fine.
    /// </summary>
    Anywhere,

    /// <summary>
    /// Only leaves report non-healthy; every composite is intrinsically healthy and
    /// therefore purely a function of its dependencies.
    /// <para>
    /// This is the regime in which the diagnostic re-fold's reconstructed intrinsic
    /// (<c>HealthGraphAnalysis.FoldModel.IntrinsicOf</c>) provably equals the real one,
    /// so counterfactual queries can be checked for <em>exact</em> agreement with the
    /// live engine rather than a one-sided bound. See ADR-007.
    /// </para>
    /// </summary>
    LeavesOnly,
}

/// <summary>
/// The topology zoo. Every generator here builds a spec whose node 0 is the root and
/// whose every node is reachable from it; edge multiplicity is deduped (the graph
/// rejects a duplicate edge), self-loops and cycles are deliberate in the shapes that
/// name them.
/// <para>
/// Shapes are chosen round-robin by case index rather than at random, so a run of
/// <c>N</c> cases covers every shape evenly and a failing case index always maps back
/// to the same shape and the same seeded <see cref="Random"/>.
/// </para>
/// </summary>
public static class TopologyGenerator
{
    /// <summary>Shapes that are acyclic by construction.</summary>
    public static readonly IReadOnlyList<string> AcyclicShapes = new[]
    {
        "chain",
        "star",
        "caterpillar",
        "binary-tree",
        "layered-dag",
        "random-dag",
        "transitive-tournament",
        "bipartite",
        "shared-leaf-fan-in",
        "diamond-ladder",
    };

    /// <summary>Shapes that plant at least one cycle (a self-loop counts).</summary>
    public static readonly IReadOnlyList<string> CyclicShapes = new[]
    {
        "self-loop",
        "simple-cycle",
        "figure-eight",
        "cycle-with-chords",
        "hairball",
        "dag-with-back-edges",
    };

    /// <summary>Every shape, acyclic first.</summary>
    public static readonly IReadOnlyList<string> AllShapes =
        AcyclicShapes.Concat(CyclicShapes).ToList();

    public static TopologySpec Generate(string shape, Random rng, IntrinsicMode mode)
    {
        var builder = shape switch
        {
            "chain" => Chain(rng),
            "star" => Star(rng),
            "caterpillar" => Caterpillar(rng),
            "binary-tree" => BinaryTree(rng),
            "layered-dag" => LayeredDag(rng),
            "random-dag" => RandomDag(rng),
            "transitive-tournament" => TransitiveTournament(rng),
            "bipartite" => Bipartite(rng),
            "shared-leaf-fan-in" => SharedLeafFanIn(rng),
            "diamond-ladder" => DiamondLadder(rng),
            "self-loop" => SelfLoop(rng),
            "simple-cycle" => SimpleCycle(rng),
            "figure-eight" => FigureEight(rng),
            "cycle-with-chords" => CycleWithChords(rng),
            "hairball" => Hairball(rng),
            "dag-with-back-edges" => DagWithBackEdges(rng),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown shape."),
        };

        return builder.Build(shape, rng, mode);
    }

    // ── Acyclic shapes ──────────────────────────────────────────────────────────

    /// <summary>A single deep spine — the recursion-depth case.</summary>
    private static Builder Chain(Random rng)
    {
        var b = new Builder(rng);
        var n = rng.Next(3, 26);
        b.AddNodes(n);
        for (var i = 0; i < n - 1; i++)
            b.Edge(i, i + 1);
        return b;
    }

    /// <summary>One root, many leaves — the widest possible single fold.</summary>
    private static Builder Star(Random rng)
    {
        var b = new Builder(rng);
        var n = rng.Next(3, 16);
        b.AddNodes(n);
        for (var i = 1; i < n; i++)
            b.Edge(0, i);
        return b;
    }

    /// <summary>A spine with pendant leaves hanging off each vertebra.</summary>
    private static Builder Caterpillar(Random rng)
    {
        var b = new Builder(rng);
        var spine = rng.Next(3, 9);
        b.AddNodes(spine);
        for (var i = 0; i < spine - 1; i++)
            b.Edge(i, i + 1);
        for (var i = 0; i < spine; i++)
        {
            for (var k = rng.Next(0, 3); k > 0; k--)
                b.Edge(i, b.AddNode());
        }
        return b;
    }

    /// <summary>A heap-indexed binary tree plus a few forward cross edges (diamonds).</summary>
    private static Builder BinaryTree(Random rng)
    {
        var b = new Builder(rng);
        var depth = rng.Next(2, 5);
        var n = (1 << (depth + 1)) - 1;
        b.AddNodes(n);
        for (var i = 0; i < n; i++)
        {
            if (2 * i + 1 < n) b.Edge(i, 2 * i + 1);
            if (2 * i + 2 < n) b.Edge(i, 2 * i + 2);
        }

        // Forward-only, so the graph stays acyclic while gaining shared subtrees.
        for (var k = rng.Next(0, 4); k > 0; k--)
        {
            var from = rng.Next(0, n - 1);
            var to = rng.Next(from + 1, n);
            b.Edge(from, to);
        }
        return b;
    }

    /// <summary>Layers wired downward, with occasional skip edges over a layer.</summary>
    private static Builder LayeredDag(Random rng)
    {
        var b = new Builder(rng);
        var layerCount = rng.Next(2, 6);
        var layers = new List<List<int>> { new() { b.AddNode() } };

        for (var l = 1; l < layerCount; l++)
        {
            var width = rng.Next(1, 5);
            var layer = new List<int>();
            for (var i = 0; i < width; i++)
                layer.Add(b.AddNode());
            layers.Add(layer);

            // Every node in this layer gets at least one parent above it...
            foreach (var node in layer)
                b.Edge(layers[l - 1][rng.Next(layers[l - 1].Count)], node);

            // ...and every parent gets at least one child, so no layer dead-ends early.
            foreach (var parent in layers[l - 1])
                b.Edge(parent, layer[rng.Next(layer.Count)]);

            if (l >= 2 && rng.NextDouble() < 0.4)
                b.Edge(layers[l - 2][rng.Next(layers[l - 2].Count)], layer[rng.Next(layer.Count)]);
        }
        return b;
    }

    /// <summary>Uniformly random edges, always low index to high index.</summary>
    private static Builder RandomDag(Random rng)
    {
        var b = new Builder(rng);
        var n = rng.Next(4, 15);
        b.AddNodes(n);
        for (var j = 1; j < n; j++)
        {
            b.Edge(rng.Next(0, j), j);
            for (var i = 0; i < j; i++)
            {
                if (rng.NextDouble() < 0.2)
                    b.Edge(i, j);
            }
        }
        return b;
    }

    /// <summary>Every edge that can exist without a cycle does — quadratic edge count.</summary>
    private static Builder TransitiveTournament(Random rng)
    {
        var b = new Builder(rng);
        var n = rng.Next(3, 9);
        b.AddNodes(n);
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
                b.Edge(i, j);
        }
        return b;
    }

    /// <summary>Root over a full bipartite middle-to-leaf mesh.</summary>
    private static Builder Bipartite(Random rng)
    {
        var b = new Builder(rng);
        b.AddNode();
        var mids = Enumerable.Range(0, rng.Next(1, 5)).Select(_ => b.AddNode()).ToList();
        var leaves = Enumerable.Range(0, rng.Next(1, 5)).Select(_ => b.AddNode()).ToList();
        foreach (var mid in mids)
        {
            b.Edge(0, mid);
            foreach (var leaf in leaves)
                b.Edge(mid, leaf);
        }
        return b;
    }

    /// <summary>"Everything depends on the one database" — maximal fan-in on a single leaf.</summary>
    private static Builder SharedLeafFanIn(Random rng)
    {
        var b = new Builder(rng);
        b.AddNode();
        var mids = Enumerable.Range(0, rng.Next(2, 7)).Select(_ => b.AddNode()).ToList();
        var leaf = b.AddNode();
        foreach (var mid in mids)
        {
            b.Edge(0, mid);
            b.Edge(mid, leaf);
        }
        if (rng.NextDouble() < 0.3)
            b.Edge(0, leaf);
        return b;
    }

    /// <summary>
    /// Stacked diamonds: each rung's two nodes both depend on both nodes of the next
    /// rung. Node count is linear, but the unrolled <em>tree</em> is exponential, so
    /// this is the shape that catches a snapshot builder without cycle/diamond
    /// flattening.
    /// </summary>
    private static Builder DiamondLadder(Random rng)
    {
        var b = new Builder(rng);
        b.AddNode();
        var rungs = rng.Next(2, 6);
        var previous = new List<int> { 0 };
        for (var r = 0; r < rungs; r++)
        {
            var rung = new List<int> { b.AddNode(), b.AddNode() };
            foreach (var parent in previous)
            {
                foreach (var child in rung)
                    b.Edge(parent, child);
            }
            previous = rung;
        }
        return b;
    }

    // ── Cyclic shapes ───────────────────────────────────────────────────────────

    /// <summary>A DAG with one node depending on itself.</summary>
    private static Builder SelfLoop(Random rng)
    {
        var b = RandomDag(rng);
        var victim = rng.Next(0, b.Count);
        b.Edge(victim, victim);
        return b;
    }

    /// <summary>A chain whose tail closes back onto an earlier link.</summary>
    private static Builder SimpleCycle(Random rng)
    {
        var b = new Builder(rng);
        var n = rng.Next(3, 9);
        b.AddNodes(n);
        for (var i = 0; i < n - 1; i++)
            b.Edge(i, i + 1);
        b.Edge(n - 1, rng.Next(0, n - 1));
        return b;
    }

    /// <summary>Two cycles sharing the root — a graph with no single "bottom".</summary>
    private static Builder FigureEight(Random rng)
    {
        var b = new Builder(rng);
        b.AddNode();
        for (var loop = 0; loop < 2; loop++)
        {
            var length = rng.Next(2, 5);
            var previous = 0;
            for (var i = 0; i < length; i++)
            {
                var next = b.AddNode();
                b.Edge(previous, next);
                previous = next;
            }
            b.Edge(previous, 0);
        }
        return b;
    }

    /// <summary>A cycle with extra chords, so several cycles overlap.</summary>
    private static Builder CycleWithChords(Random rng)
    {
        var b = new Builder(rng);
        var n = rng.Next(4, 9);
        b.AddNodes(n);
        for (var i = 0; i < n; i++)
            b.Edge(i, (i + 1) % n);
        for (var k = rng.Next(1, 4); k > 0; k--)
            b.Edge(rng.Next(0, n), rng.Next(0, n));
        return b;
    }

    /// <summary>Edges in every direction, then patched until everything is reachable.</summary>
    private static Builder Hairball(Random rng)
    {
        var b = new Builder(rng);
        var n = rng.Next(4, 13);
        b.AddNodes(n);
        for (var k = rng.Next(n, 2 * n); k > 0; k--)
            b.Edge(rng.Next(0, n), rng.Next(0, n));
        b.ConnectUnreachable();
        return b;
    }

    /// <summary>A well-formed DAG with a couple of back edges bolted on.</summary>
    private static Builder DagWithBackEdges(Random rng)
    {
        var b = RandomDag(rng);
        for (var k = rng.Next(1, 3); k > 0; k--)
        {
            var from = rng.Next(1, b.Count);
            b.Edge(from, rng.Next(0, from));
        }
        return b;
    }

    // ── Builder ─────────────────────────────────────────────────────────────────

    private sealed class Builder
    {
        private readonly Random _rng;
        private readonly List<List<FuzzEdge>> _edges = new();
        private readonly List<HashSet<int>> _targets = new();

        public Builder(Random rng)
        {
            _rng = rng;
            AddNode();
        }

        public int Count => _edges.Count;

        public int AddNode()
        {
            _edges.Add(new List<FuzzEdge>());
            _targets.Add(new HashSet<int>());
            return _edges.Count - 1;
        }

        public void AddNodes(int total)
        {
            while (Count < total)
                AddNode();
        }

        /// <summary>Adds an edge unless it duplicates one — the graph rejects duplicates.</summary>
        public void Edge(int from, int to)
        {
            if (!_targets[from].Add(to))
                return;
            _edges[from].Add(new FuzzEdge(to, Importance.Required));
        }

        /// <summary>Wires any unreachable node to a reachable one, until all are reachable.</summary>
        public void ConnectUnreachable()
        {
            while (true)
            {
                var reachable = new HashSet<int> { 0 };
                var stack = new Stack<int>();
                stack.Push(0);
                while (stack.Count > 0)
                {
                    foreach (var edge in _edges[stack.Pop()])
                    {
                        if (reachable.Add(edge.Target))
                            stack.Push(edge.Target);
                    }
                }

                var orphan = Enumerable.Range(0, Count).FirstOrDefault(i => !reachable.Contains(i), -1);
                if (orphan < 0)
                    return;

                var parents = reachable.ToList();
                Edge(parents[_rng.Next(parents.Count)], orphan);
            }
        }

        /// <summary>
        /// Freezes the shape into a spec, assigning importances and intrinsic statuses.
        /// <para>
        /// Importance is uniform over all five levels, except that a node has a 1-in-5
        /// chance of having <em>all</em> its edges made <see cref="Importance.Resilient"/>
        /// — a real quorum. Uniformly random importance almost never produces two
        /// resilient siblings, which is exactly the case the quorum rule is about.
        /// </para>
        /// </summary>
        public TopologySpec Build(string shape, Random rng, IntrinsicMode mode)
        {
            var importances = Enum.GetValues<Importance>();
            var nodes = new List<FuzzNode>(Count);

            for (var i = 0; i < Count; i++)
            {
                var quorum = _edges[i].Count > 1 && rng.NextDouble() < 0.2;
                var edges = _edges[i]
                    .Select(e => e with
                    {
                        Importance = quorum
                            ? Importance.Resilient
                            : importances[rng.Next(importances.Length)],
                    })
                    .ToList();

                var isLeaf = edges.Count == 0;
                var intrinsic = mode == IntrinsicMode.LeavesOnly && !isLeaf
                    ? HealthStatus.Healthy
                    : RandomStatus(rng);

                nodes.Add(new FuzzNode(intrinsic, edges));
            }

            return new TopologySpec(shape, nodes).Normalize();
        }

        private static HealthStatus RandomStatus(Random rng)
        {
            var roll = rng.NextDouble();
            return roll switch
            {
                < 0.45 => HealthStatus.Healthy,
                < 0.70 => HealthStatus.Unhealthy,
                < 0.90 => HealthStatus.Degraded,
                _ => HealthStatus.Unknown,
            };
        }
    }
}
