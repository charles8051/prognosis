using Prognosis;

namespace Prognosis.Tests;

/// <summary>
/// Covers the topology-as-first-class-artifact surface (ADR-009):
/// <see cref="HealthGraph.GetTopology"/>, structural <see cref="HealthGraph.TopologyChanged"/>
/// emission (edges and importance, not just node membership), the topology carried on
/// <see cref="TopologyChange"/>, and the topology-before-status emission ordering.
/// </summary>
public class HealthTopologyTests
{
    // ── GetTopology ──────────────────────────────────────────────────

    [Fact]
    public void GetTopology_ReturnsRootAndOrderedEdgesWithImportance()
    {
        var db = HealthNode.Create("Database");
        var cache = HealthNode.Create("Cache");
        var root = HealthNode.Create("Root")
            .DependsOn(db, Importance.Required)
            .DependsOn(cache, Importance.Important);
        var graph = HealthGraph.Create(root);

        var topology = graph.GetTopology();

        Assert.Equal("Root", topology.Root);
        Assert.Equal(3, topology.Edges.Count);

        var rootEdges = topology.Edges["Root"];
        Assert.Equal(2, rootEdges.Count);
        Assert.Equal(new HealthTopologyEdge("Database", Importance.Required), rootEdges[0]);
        Assert.Equal(new HealthTopologyEdge("Cache", Importance.Important), rootEdges[1]);

        Assert.Empty(topology.Edges["Database"]);
        Assert.Empty(topology.Edges["Cache"]);
    }

    [Fact]
    public void GetTopology_CoversEveryReportNode()
    {
        // Pins the BuildTreeSnapshot contract's premise: the report and the
        // topology describe the same node set.
        var leaf = HealthNode.Create("Leaf");
        var mid = HealthNode.Create("Mid").DependsOn(leaf, Importance.Important);
        var root = HealthNode.Create("Root").DependsOn(mid, Importance.Required);
        var graph = HealthGraph.Create(root);

        var reportNames = graph.GetReport().Nodes.Select(n => n.Name).ToHashSet();
        var topologyNames = graph.GetTopology().Edges.Keys.ToHashSet();

        Assert.Equal(reportNames, topologyNames);
    }

    [Fact]
    public void GetTopology_AgreesWithLastEmittedTopologyChange()
    {
        // The two publication paths (pull via GetTopology, push via the event)
        // must hand out the same instance — they are one artifact, not two.
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        root.DependsOn(HealthNode.Create("Child"), Importance.Required);

        Assert.Same(emitted[^1].Topology, graph.GetTopology());
    }

    [Fact]
    public void GetTopology_ReferenceStable_AcrossStatusOnlyWaves()
    {
        // A wave that changes no structure must not mint a new topology
        // instance — consumers may memoize on identity.
        var isDown = false;
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => isDown ? HealthEvaluation.Unhealthy("down") : HealthEvaluation.Healthy);
        var root = HealthNode.Create("Root").DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var before = graph.GetTopology();

        isDown = true;
        child.Refresh();

        Assert.Same(before, graph.GetTopology());
    }

    [Fact]
    public void Topology_JsonRoundTrip_PreservesStructureAndShipsImportanceAsString()
    {
        // Shipping the topology north is ADR-009's fleet-scale motivation and
        // the moment Importance becomes a wire type (ADR-008) — pin the shape.
        var db = HealthNode.Create("Database");
        var cache = HealthNode.Create("Cache");
        var root = HealthNode.Create("Root")
            .DependsOn(db, Importance.Important)
            .DependsOn(cache, Importance.Advisory);
        var topology = HealthGraph.Create(root).GetTopology();

        var json = System.Text.Json.JsonSerializer.Serialize(topology);
        var restored = System.Text.Json.JsonSerializer.Deserialize<HealthTopology>(json);

        Assert.NotNull(restored);
        Assert.True(HealthTopologyComparer.Instance.Equals(topology, restored));
        Assert.Contains("\"Important\"", json);
        Assert.Contains("\"Advisory\"", json);
    }

    [Fact]
    public void GetTopology_ReflectsImportanceUpdate()
    {
        var child = HealthNode.Create("Child");
        var root = HealthNode.Create("Root").DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        root.UpdateDependencyImportance(child, Importance.Optional);

        var edge = Assert.Single(graph.GetTopology().Edges["Root"]);
        Assert.Equal(Importance.Optional, edge.Importance);
    }

    // ── TopologyChanged: structural completeness (ADR-009 §3) ────────

    [Fact]
    public void TopologyChanged_UpdateDependencyImportance_EmitsWithEmptyAddedRemoved()
    {
        var child = HealthNode.Create("Child");
        var root = HealthNode.Create("Root").DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        root.UpdateDependencyImportance(child, Importance.Advisory);

        var change = Assert.Single(emitted);
        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.Equal(
            new HealthTopologyEdge("Child", Importance.Advisory),
            Assert.Single(change.Topology.Edges["Root"]));
    }

    [Fact]
    public void TopologyChanged_DiamondEdgeRemoval_NodeStillReachable_Emits()
    {
        var shared = HealthNode.Create("Shared");
        var a = HealthNode.Create("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(shared, Importance.Required);
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        // Shared stays reachable via A — the node set is unchanged, only an edge went.
        b.RemoveDependency(shared);

        var change = Assert.Single(emitted);
        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.Empty(change.Topology.Edges["B"]);
        Assert.Single(change.Topology.Edges["A"]);
    }

    [Fact]
    public void TopologyChanged_ReplaceDependencies_SameNodeSet_Emits()
    {
        var x = HealthNode.Create("X");
        var y = HealthNode.Create("Y");
        var root = HealthNode.Create("Root")
            .DependsOn(x, Importance.Required)
            .DependsOn(y, Importance.Optional);
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        // Same reachable set, importance swapped between the edges.
        root.ReplaceDependencies((x, Importance.Optional), (y, Importance.Required));

        var change = Assert.Single(emitted);
        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.Equal(
            new[]
            {
                new HealthTopologyEdge("X", Importance.Optional),
                new HealthTopologyEdge("Y", Importance.Required),
            },
            change.Topology.Edges["Root"]);
    }

    [Fact]
    public void TopologyChanged_StatusOnlyChange_DoesNotEmit()
    {
        var isDown = false;
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => isDown ? HealthEvaluation.Unhealthy("down") : HealthEvaluation.Healthy);
        var root = HealthNode.Create("Root").DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        isDown = true;
        child.Refresh();

        Assert.Empty(emitted);
    }

    [Fact]
    public void TopologyChanged_NodeAddition_CarriesPostChangeTopology()
    {
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        var emitted = new List<TopologyChange>();
        graph.TopologyChanged.Subscribe(new TestObserver<TopologyChange>(emitted.Add));

        var child = HealthNode.Create("Child");
        root.DependsOn(child, Importance.Important);

        var change = Assert.Single(emitted);
        Assert.Same(child, Assert.Single(change.Added));
        Assert.Equal(
            new HealthTopologyEdge("Child", Importance.Important),
            Assert.Single(change.Topology.Edges["Root"]));
        Assert.Empty(change.Topology.Edges["Child"]);
    }

    // ── Emission ordering (ADR-009 §4) ───────────────────────────────

    [Fact]
    public void TopologyChanged_IsObservedBeforeStatusChanged_WithinOneWave()
    {
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);

        var sequence = new List<string>();
        graph.TopologyChanged.Subscribe(
            new TestObserver<TopologyChange>(_ => sequence.Add("topology")));
        graph.StatusChanged.Subscribe(
            new TestObserver<HealthReport>(_ => sequence.Add("status")));

        // One wave that changes both structure and the report.
        var child = HealthNode.Create("Child").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        root.DependsOn(child, Importance.Required);

        Assert.Contains("topology", sequence);
        Assert.Contains("status", sequence);
        Assert.True(
            sequence.IndexOf("topology") < sequence.IndexOf("status"),
            $"expected topology before status, got [{string.Join(", ", sequence)}]");
    }

    // ── HealthTopologyComparer ───────────────────────────────────────

    [Fact]
    public void Comparer_EqualTopologies_AreEqual()
    {
        var a = MakeTopology(("Root", new[] { ("Child", Importance.Required) }), ("Child", Array.Empty<(string, Importance)>()));
        var b = MakeTopology(("Root", new[] { ("Child", Importance.Required) }), ("Child", Array.Empty<(string, Importance)>()));

        Assert.True(HealthTopologyComparer.Instance.Equals(a, b));
        Assert.Equal(
            HealthTopologyComparer.Instance.GetHashCode(a),
            HealthTopologyComparer.Instance.GetHashCode(b));
    }

    [Fact]
    public void Comparer_ImportanceDiffers_NotEqual()
    {
        var a = MakeTopology(("Root", new[] { ("Child", Importance.Required) }), ("Child", Array.Empty<(string, Importance)>()));
        var b = MakeTopology(("Root", new[] { ("Child", Importance.Optional) }), ("Child", Array.Empty<(string, Importance)>()));

        Assert.False(HealthTopologyComparer.Instance.Equals(a, b));
    }

    [Fact]
    public void Comparer_EdgeOrderDiffers_NotEqual()
    {
        // Edge order is part of the topology contract (it drives which diamond
        // occurrence carries the expanded subtree), so it participates in equality.
        var a = MakeTopology(
            ("Root", new[] { ("X", Importance.Required), ("Y", Importance.Required) }),
            ("X", Array.Empty<(string, Importance)>()),
            ("Y", Array.Empty<(string, Importance)>()));
        var b = MakeTopology(
            ("Root", new[] { ("Y", Importance.Required), ("X", Importance.Required) }),
            ("X", Array.Empty<(string, Importance)>()),
            ("Y", Array.Empty<(string, Importance)>()));

        Assert.False(HealthTopologyComparer.Instance.Equals(a, b));
    }

    private static HealthTopology MakeTopology(
        params (string Name, (string Name, Importance Importance)[] Edges)[] nodes)
    {
        var edges = new Dictionary<string, IReadOnlyList<HealthTopologyEdge>>(StringComparer.Ordinal);
        foreach (var (name, nodeEdges) in nodes)
        {
            edges[name] = nodeEdges
                .Select(e => new HealthTopologyEdge(e.Name, e.Importance))
                .ToList();
        }
        return new HealthTopology(nodes[0].Name, edges);
    }
}

file class TestObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
