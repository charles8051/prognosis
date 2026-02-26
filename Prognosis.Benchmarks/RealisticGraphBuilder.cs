using Prognosis;

namespace Prognosis.Benchmarks;

/// <summary>
/// Builds a realistic health graph that models a layered microservice
/// platform with the following topology:
///
///   Layer 0 — Infrastructure (databases, caches, message brokers, blob stores)
///   Layer 1 — Core services (auth, catalog, inventory, payment, etc.)
///   Layer 2 — Domain services (order processing, search, notifications, etc.)
///   Layer 3 — Gateway / BFF composites
///   Layer 4 — Platform root
///
/// Each layer depends on 2-4 nodes from the layer below, mixing Required,
/// Important, and Optional importance levels. Infrastructure nodes use
/// HealthAdapter with synthetic health delegates; higher layers use
/// HealthGroup composites. Some infrastructure nodes have sub-checks
/// (connection, latency, pool) to model fine-grained health like
/// DatabaseService in the examples.
///
/// The resulting graph has a realistic fan-out and depth, with shared
/// dependencies (e.g. multiple services depend on the same database),
/// exactly like a real production system.
/// </summary>
internal static class RealisticGraphBuilder
{
    /// <summary>
    /// Builds a graph with approximately <paramref name="targetNodeCount"/> nodes.
    /// Returns the platform root node from which all others are reachable.
    /// </summary>
    public static HealthNode Build(int targetNodeCount)
    {
        var nodes = new List<HealthNode>();
        var rng = new Random(42); // deterministic for reproducibility

        // ── Layer 0: Infrastructure ──────────────────────────────────
        // Each infra service gets 3 sub-checks (connection, latency, pool)
        // plus a HealthGroup parent = 4 nodes per infra service.
        var infraNames = GenerateNames("Infra", targetNodeCount / 20);
        var infraNodes = new List<HealthNode>();
        foreach (var name in infraNames)
        {
            var conn = new HealthAdapter($"{name}.Connection", RandomCheck(rng));
            var latency = new HealthAdapter($"{name}.Latency", RandomCheck(rng));
            var pool = new HealthAdapter($"{name}.Pool", RandomCheck(rng));
            var group = new HealthGroup(name)
                .DependsOn(conn, Importance.Required)
                .DependsOn(latency, Importance.Important)
                .DependsOn(pool, Importance.Required);

            nodes.Add(conn);
            nodes.Add(latency);
            nodes.Add(pool);
            nodes.Add(group);
            infraNodes.Add(group);
        }

        // ── Layer 1: Core services ──────────────────────────────────
        var coreCount = targetNodeCount / 8;
        var coreNodes = BuildLayer(nodes, "Core", coreCount, infraNodes, rng);

        // ── Layer 2: Domain services ────────────────────────────────
        var domainCount = targetNodeCount / 6;
        var allLowerNodes = new List<HealthNode>(infraNodes);
        allLowerNodes.AddRange(coreNodes);
        var domainNodes = BuildLayer(nodes, "Domain", domainCount, allLowerNodes, rng);

        // ── Layer 3: Gateway / BFF composites ───────────────────────
        var gatewayCount = Math.Max(4, targetNodeCount / 30);
        var gatewayPool = new List<HealthNode>(coreNodes);
        gatewayPool.AddRange(domainNodes);
        var gatewayNodes = BuildLayer(nodes, "Gateway", gatewayCount, gatewayPool, rng);

        // ── Fill remaining budget with mid-tier services ────────────
        var remaining = targetNodeCount - nodes.Count - 1; // -1 for the root
        if (remaining > 0)
        {
            var fillPool = new List<HealthNode>(infraNodes);
            fillPool.AddRange(coreNodes);
            fillPool.AddRange(domainNodes);
            var fillNodes = BuildLayer(nodes, "Service", remaining, fillPool, rng);
            gatewayPool.AddRange(fillNodes);
        }

        // ── Layer 4: Platform root ──────────────────────────────────
        var root = new HealthGroup("Platform");
        foreach (var gw in gatewayNodes)
        {
            root.DependsOn(gw, Importance.Required);
        }
        // Also pull in a few domain services directly.
        for (var i = 0; i < Math.Min(3, domainNodes.Count); i++)
        {
            root.DependsOn(domainNodes[i], Importance.Important);
        }

        nodes.Add(root);
        return root;
    }

    private static List<HealthNode> BuildLayer(
        List<HealthNode> allNodes,
        string prefix,
        int count,
        List<HealthNode> dependencyPool,
        Random rng)
    {
        var layerNodes = new List<HealthNode>();
        var importances = new[] { Importance.Required, Importance.Important, Importance.Optional };

        for (var i = 0; i < count; i++)
        {
            var name = $"{prefix}.{i:D3}";
            var depCount = rng.Next(2, Math.Min(5, dependencyPool.Count + 1));
            var node = new HealthAdapter(name, RandomCheck(rng));

            // Pick distinct random dependencies from the pool.
            var picked = new HashSet<int>();
            for (var d = 0; d < depCount && picked.Count < dependencyPool.Count; d++)
            {
                int idx;
                do { idx = rng.Next(dependencyPool.Count); }
                while (!picked.Add(idx));

                var importance = importances[rng.Next(importances.Length)];
                node.DependsOn(dependencyPool[idx], importance);
            }

            allNodes.Add(node);
            layerNodes.Add(node);
        }

        return layerNodes;
    }

    private static Func<HealthEvaluation> RandomCheck(Random rng)
    {
        // ~90% healthy, ~5% degraded, ~5% unhealthy — realistic steady state.
        var roll = rng.Next(100);
        if (roll < 90)
            return () => HealthStatus.Healthy;
        if (roll < 95)
            return () => new HealthEvaluation(HealthStatus.Degraded, "High latency");

        return () => new HealthEvaluation(HealthStatus.Unhealthy, "Connection refused");
    }

    private static string[] GenerateNames(string prefix, int count)
    {
        count = Math.Max(count, 4); // at least 4 infra services
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = $"{prefix}.{i:D3}";
        return names;
    }
}
