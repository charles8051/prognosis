using Prognosis;
using Prognosis.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// Covers <see cref="HealthGraphAnalysis.BuildTreeSnapshot"/> and
/// <see cref="HealthGraphAnalysis.FindOrphans"/> (ADR-009): the round-trip
/// guarantee (<c>BuildTreeSnapshot(GetReport(), GetTopology())</c> structurally equals
/// <c>CreateTreeSnapshot()</c> on a quiescent graph — cyclic, diamond, and
/// tagged), and the totality contract in both mismatch directions.
/// </summary>
public class BuildTreeSnapshotTests
{
    // ── Round-trip fidelity (ADR-009 §6) ─────────────────────────────

    [Fact]
    public void BuildTreeSnapshot_RoundTrip_ChainWithMixedStatuses()
    {
        var leaf = HealthNode.Create("Leaf").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("disk full"));
        var mid = HealthNode.Create("Mid").DependsOn(leaf, Importance.Important);
        var root = HealthNode.Create("Root").DependsOn(mid, Importance.Required);
        var graph = HealthGraph.Create(root);

        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(graph.GetReport(), graph.GetTopology());

        AssertTreeEqual(graph.CreateTreeSnapshot(), enriched);
    }

    [Fact]
    public void BuildTreeSnapshot_RoundTrip_Diamond_SecondOccurrenceIsLeaf()
    {
        var shared = HealthNode.Create("Shared").WithHealthProbe(
            () => HealthEvaluation.Degraded("slow"));
        var a = HealthNode.Create("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(shared, Importance.Resilient);
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Important);
        var graph = HealthGraph.Create(root);

        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(graph.GetReport(), graph.GetTopology());

        AssertTreeEqual(graph.CreateTreeSnapshot(), enriched);

        // The flattening rule itself: first occurrence expanded, second a stub leaf.
        var sharedUnderA = enriched.Dependencies[0].Node.Dependencies[0].Node;
        var sharedUnderB = enriched.Dependencies[1].Node.Dependencies[0].Node;
        Assert.Equal("Shared", sharedUnderA.Name);
        Assert.Equal("Shared", sharedUnderB.Name);
        Assert.Empty(sharedUnderB.Dependencies);
    }

    [Fact]
    public void BuildTreeSnapshot_RoundTrip_Cycle()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");
        a.DependsOn(b, Importance.Required);
        b.DependsOn(a, Importance.Required);
        var root = HealthNode.Create("Root").DependsOn(a, Importance.Required);
        var graph = HealthGraph.Create(root);

        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(graph.GetReport(), graph.GetTopology());

        AssertTreeEqual(graph.CreateTreeSnapshot(), enriched);
    }

    [Fact]
    public void BuildTreeSnapshot_RoundTrip_TaggedNodes()
    {
        var dep = HealthNode.Create("Dep")
            .WithTags(new Dictionary<string, string> { ["region"] = "us-east-1" });
        var root = HealthNode.Create("Root")
            .WithTags(new Dictionary<string, string> { ["owner"] = "platform-team" })
            .DependsOn(dep, Importance.Required);
        var graph = HealthGraph.Create(root);

        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(graph.GetReport(), graph.GetTopology());

        AssertTreeEqual(graph.CreateTreeSnapshot(), enriched);
        Assert.Equal("us-east-1", enriched.Dependencies[0].Node.Tags!["region"]);
    }

    [Fact]
    public void BuildTreeSnapshot_ComposesWithContributors_SameAnswerAsDirectSnapshot()
    {
        // The reactive pipeline shape from the topology-projection gap: report → BuildTreeSnapshot → Contributors.
        var api = HealthNode.Create("Api").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("api down"));
        var camera = HealthNode.Create("Camera").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("camera down"));
        var root = HealthNode.Create("Root")
            .DependsOn(api, Importance.Required)
            .DependsOn(camera, Importance.Important);
        var graph = HealthGraph.Create(root);

        var fromProjection = HealthGraphAnalysis.Contributors(
            HealthGraphAnalysis.BuildTreeSnapshot(graph.GetReport(), graph.GetTopology()));
        var fromSnapshot = HealthGraphAnalysis.Contributors(graph.CreateTreeSnapshot());

        Assert.Equal(fromSnapshot, fromProjection);
        Assert.Equal("Api", Assert.Single(fromProjection).Name);
    }

    // ── Totality: topology name absent from report (ADR-009 §5) ──────

    [Fact]
    public void BuildTreeSnapshot_TopologyNodeMissingFromReport_SynthesizesUnknownWithReason()
    {
        var child = HealthNode.Create("Child");
        var root = HealthNode.Create("Root").DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);
        var staleTopology = graph.GetTopology();

        // The node is removed after the topology was captured.
        root.RemoveDependency(child);
        var report = graph.GetReport();

        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(report, staleTopology);

        var childTree = Assert.Single(enriched.Dependencies).Node;
        Assert.Equal("Child", childTree.Name);
        Assert.Equal(HealthStatus.Unknown, childTree.Status);
        Assert.Contains("not in the report", childTree.Reason);
        // ADR-006: the synthetic Unknown never gates — the root keeps its
        // reported status; the stale slot cannot invent a culprit.
        Assert.Equal(report.Root.Status, enriched.Status);
    }

    [Fact]
    public void BuildTreeSnapshot_SyntheticUnknown_DoesNotBecomeAContributor()
    {
        var missing = HealthNode.Create("Missing");
        var failing = HealthNode.Create("Failing").WithHealthProbe(
            () => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.Create("Root")
            .DependsOn(missing, Importance.Required)
            .DependsOn(failing, Importance.Required);
        var graph = HealthGraph.Create(root);
        var staleTopology = graph.GetTopology();

        root.RemoveDependency(missing);
        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(graph.GetReport(), staleTopology);

        var contributors = HealthGraphAnalysis.Contributors(enriched);
        Assert.DoesNotContain(contributors, c => c.Name == "Missing");
        Assert.Contains(contributors, c => c.Name == "Failing");
    }

    [Fact]
    public void BuildTreeSnapshot_RootMissingFromReport_SynthesizesUnknownRoot()
    {
        // The fully-stale extreme: a report from a disjoint graph — even the
        // topology's root is absent. BuildTreeSnapshot stays total.
        var graphA = HealthGraph.Create(HealthNode.Create("A"));
        var graphB = HealthGraph.Create(HealthNode.Create("B"));

        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(graphB.GetReport(), graphA.GetTopology());

        Assert.Equal("A", enriched.Name);
        Assert.Equal(HealthStatus.Unknown, enriched.Status);
        Assert.Contains("not in the report", enriched.Reason);
        Assert.Empty(enriched.Dependencies);
    }

    // ── Totality: report name absent from topology (ADR-009 §5) ──────

    [Fact]
    public void BuildTreeSnapshot_ReportNodeMissingFromTopology_IsOutsideProjection()
    {
        var root = HealthNode.Create("Root");
        var graph = HealthGraph.Create(root);
        var staleTopology = graph.GetTopology();

        // The node is added after the topology was captured.
        root.DependsOn(
            HealthNode.Create("NewNode").WithHealthProbe(
                () => HealthEvaluation.Unhealthy("down")),
            Importance.Required);
        var report = graph.GetReport();

        var enriched = HealthGraphAnalysis.BuildTreeSnapshot(report, staleTopology);

        Assert.Empty(enriched.Dependencies);

        var orphan = Assert.Single(HealthGraphAnalysis.FindOrphans(report, staleTopology));
        Assert.Equal("NewNode", orphan.Name);
        Assert.Equal(HealthStatus.Unhealthy, orphan.Status);
    }

    [Fact]
    public void FindOrphans_AlignedReportAndTopology_ReturnsEmpty()
    {
        var child = HealthNode.Create("Child");
        var root = HealthNode.Create("Root").DependsOn(child, Importance.Required);
        var graph = HealthGraph.Create(root);

        Assert.Empty(HealthGraphAnalysis.FindOrphans(graph.GetReport(), graph.GetTopology()));
    }

    // ── Structural tree comparison ───────────────────────────────────
    //
    // HealthTreeSnapshot is a record with collection members, so its generated
    // Equals compares Dependencies/Tags by reference (ADR-009 §6) — compare
    // structurally instead.

    private static void AssertTreeEqual(HealthTreeSnapshot expected, HealthTreeSnapshot actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Reason, actual.Reason);

        if (expected.Tags is null)
        {
            Assert.Null(actual.Tags);
        }
        else
        {
            Assert.NotNull(actual.Tags);
            Assert.Equal(
                expected.Tags.OrderBy(t => t.Key, StringComparer.Ordinal),
                actual.Tags!.OrderBy(t => t.Key, StringComparer.Ordinal));
        }

        Assert.Equal(expected.Dependencies.Count, actual.Dependencies.Count);
        for (var i = 0; i < expected.Dependencies.Count; i++)
        {
            Assert.Equal(expected.Dependencies[i].Importance, actual.Dependencies[i].Importance);
            AssertTreeEqual(expected.Dependencies[i].Node, actual.Dependencies[i].Node);
        }
    }
}
