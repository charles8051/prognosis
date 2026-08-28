using Prognosis;
using Prognosis.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// Covers the pure diagnostic query layer (ADR-007): <see cref="HealthGraphAnalysis.WhatIf"/>,
/// <see cref="HealthGraphAnalysis.Contributors"/>, and
/// <see cref="HealthGraphAnalysis.MinimalHealingSet"/> over a <see cref="HealthTreeSnapshot"/>.
/// </summary>
public class HealthGraphAnalysisTests
{
    private static HealthNode Unhealthy(string name) =>
        HealthNode.Create(name).WithHealthProbe(() => HealthEvaluation.Unhealthy($"{name} down"));

    private static HealthNode Status(string name, HealthStatus status) =>
        HealthNode.Create(name).WithHealthProbe(() => status);

    // ── The worked incident (ADR-007 §"The intuition is usually wrong") ──────
    //
    // Device (Required→Subsystem, Required→SecondarySubsystem)
    //   Subsystem: Required→Real.BackendApi (Unhealthy), Important→Real.Camera.Stream (Unhealthy)
    //   SecondarySubsystem: Important→Real.Camera.Stream (Unhealthy)   [camera is a shared diamond leaf]
    //
    // Root is Unhealthy. The API is load-bearing; the camera is not.

    private static HealthTreeSnapshot IncidentSnapshot()
    {
        var api = Unhealthy("Real.BackendApi");
        var camera = Unhealthy("Real.Camera.Stream");

        var deposit = HealthNode.Create("Subsystem")
            .DependsOn(api, Importance.Required)
            .DependsOn(camera, Importance.Important);

        var vending = HealthNode.Create("SecondarySubsystem")
            .DependsOn(camera, Importance.Important);

        var device = HealthNode.Create("Device")
            .DependsOn(deposit, Importance.Required)
            .DependsOn(vending, Importance.Required);

        return HealthGraph.Create(device).CreateTreeSnapshot();
    }

    [Fact]
    public void Incident_RootIsUnhealthy()
    {
        Assert.Equal(HealthStatus.Unhealthy, IncidentSnapshot().Status);
    }

    [Fact]
    public void WhatIf_HealingTheCamera_LeavesRootUnhealthy()
    {
        var tree = IncidentSnapshot();

        var result = HealthGraphAnalysis.WhatIf(
            tree,
            new Dictionary<string, HealthStatus>
            {
                ["Real.Camera.Stream"] = HealthStatus.Healthy,
            });

        // The camera was never why the root was Unhealthy — Important capped it at Degraded.
        Assert.Equal(HealthStatus.Unhealthy, result);
    }

    [Fact]
    public void WhatIf_HealingTheApi_BringsRootToDegradedNotHealthy()
    {
        var tree = IncidentSnapshot();

        var result = HealthGraphAnalysis.WhatIf(
            tree,
            new Dictionary<string, HealthStatus>
            {
                ["Real.BackendApi"] = HealthStatus.Healthy,
            });

        // The API is load-bearing for Unhealthy, but the camera still degrades the systems.
        Assert.Equal(HealthStatus.Degraded, result);
    }

    [Fact]
    public void WhatIf_HealingBoth_BringsRootToHealthy()
    {
        var tree = IncidentSnapshot();

        var result = HealthGraphAnalysis.WhatIf(
            tree,
            new Dictionary<string, HealthStatus>
            {
                ["Real.BackendApi"] = HealthStatus.Healthy,
                ["Real.Camera.Stream"] = HealthStatus.Healthy,
            });

        Assert.Equal(HealthStatus.Healthy, result);
    }

    [Fact]
    public void WhatIf_EmptyOverrides_ReproducesRecordedRoot()
    {
        var tree = IncidentSnapshot();

        var result = HealthGraphAnalysis.WhatIf(
            tree, new Dictionary<string, HealthStatus>());

        Assert.Equal(tree.Status, result);
    }

    [Fact]
    public void Contributors_Incident_OnlyTheApiIsGating()
    {
        var tree = IncidentSnapshot();

        var contributors = HealthGraphAnalysis.Contributors(tree);

        var only = Assert.Single(contributors);
        Assert.Equal("Real.BackendApi", only.Name);
        Assert.Equal(HealthStatus.Unhealthy, only.Status);
        // The camera is Unhealthy but capped — it is NOT a contributor.
        Assert.DoesNotContain(contributors, c => c.Name == "Real.Camera.Stream");
    }

    [Fact]
    public void MinimalHealingSet_Incident_ToDegraded_IsJustTheApi()
    {
        var tree = IncidentSnapshot();

        var steps = HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Degraded);

        var only = Assert.Single(steps);
        Assert.Equal("Real.BackendApi", only.Name);
        Assert.Null(only.Quorum);
    }

    [Fact]
    public void MinimalHealingSet_Incident_ToHealthy_NeedsApiAndCamera()
    {
        var tree = IncidentSnapshot();

        var steps = HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Healthy);

        Assert.Equal(
            new[] { "Real.BackendApi", "Real.Camera.Stream" },
            steps.Select(s => s.Name).ToArray());
        Assert.All(steps, s => Assert.Null(s.Quorum));
    }

    // ── Multiple Required-unhealthy leaves ──────────────────────────────────────────

    [Fact]
    public void MultipleRequiredUnhealthy_AllAreContributorsAndAllMustHeal()
    {
        var a = Unhealthy("A");
        var b = Unhealthy("B");
        var c = Unhealthy("C");
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required)
            .DependsOn(c, Importance.Required);
        var tree = HealthGraph.Create(root).CreateTreeSnapshot();

        var contributors = HealthGraphAnalysis.Contributors(tree);
        Assert.Equal(new[] { "A", "B", "C" }, contributors.Select(x => x.Name).ToArray());

        // Root stays Unhealthy until ALL Required leaves are fixed.
        var steps = HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Degraded);
        Assert.Equal(new[] { "A", "B", "C" }, steps.Select(s => s.Name).ToArray());
        Assert.All(steps, s => Assert.Null(s.Quorum));
    }

    // ── Resilient quorum ────────────────────────────────────────────────────────────

    [Fact]
    public void ResilientQuorum_AllUnhealthy_HealingSetIsOneSiblingWithQuorumMark()
    {
        var r1 = Unhealthy("R1");
        var r2 = Unhealthy("R2");
        var r3 = Unhealthy("R3");
        var root = HealthNode.Create("Root")
            .DependsOn(r1, Importance.Resilient)
            .DependsOn(r2, Importance.Resilient)
            .DependsOn(r3, Importance.Resilient);
        var tree = HealthGraph.Create(root).CreateTreeSnapshot();

        // All resilient siblings unhealthy → no quorum → root Unhealthy.
        Assert.Equal(HealthStatus.Unhealthy, tree.Status);

        // Every sibling is on a determining path.
        var contributors = HealthGraphAnalysis.Contributors(tree);
        Assert.Equal(new[] { "R1", "R2", "R3" }, contributors.Select(x => x.Name).ToArray());

        // To reach Degraded, restore ONE sibling (any one) to Healthy for quorum.
        var toDegraded = HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Degraded);
        var one = Assert.Single(toDegraded);
        Assert.NotNull(one.Quorum);
        Assert.Equal("Root", one.Quorum!.Parent);
        Assert.Equal(1, one.Quorum.Required);
        Assert.Equal(new[] { "R1", "R2", "R3" }, one.Quorum.Candidates.OrderBy(x => x).ToArray());

        // Quorum cannot reach Healthy — every sibling must be healed, no choice remains.
        var toHealthy = HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Healthy);
        Assert.Equal(new[] { "R1", "R2", "R3" }, toHealthy.Select(s => s.Name).ToArray());
        Assert.All(toHealthy, s => Assert.Null(s.Quorum));
    }

    [Fact]
    public void ResilientQuorum_OneHealthySibling_RootDegraded_OnlyUnhealthyGates()
    {
        var healthy = HealthNode.Create("Primary");
        var down = Unhealthy("Replica");
        var root = HealthNode.Create("Root")
            .DependsOn(healthy, Importance.Resilient)
            .DependsOn(down, Importance.Resilient);
        var tree = HealthGraph.Create(root).CreateTreeSnapshot();

        // Healthy sibling provides quorum → the unhealthy one caps to Degraded.
        Assert.Equal(HealthStatus.Degraded, tree.Status);

        var contributors = HealthGraphAnalysis.Contributors(tree);
        var only = Assert.Single(contributors);
        Assert.Equal("Replica", only.Name);

        // Healing to Healthy just fixes the one unhealthy sibling — no quorum choice.
        var steps = HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Healthy);
        var one = Assert.Single(steps);
        Assert.Equal("Replica", one.Name);
        Assert.Null(one.Quorum);
    }

    // ── Unknown non-gating preserved under WhatIf (ADR-006) ─────────────────────────

    [Fact]
    public void WhatIf_UnknownChildStaysNonGating()
    {
        var failing = Unhealthy("Failing");
        var probing = Status("Probing", HealthStatus.Unknown);
        var root = HealthNode.Create("Root")
            .DependsOn(failing, Importance.Required)
            .DependsOn(probing, Importance.Required);
        var tree = HealthGraph.Create(root).CreateTreeSnapshot();

        Assert.Equal(HealthStatus.Unhealthy, tree.Status);

        // Force the real failure Healthy. The remaining Unknown child must raise the
        // root only to Unknown — never Degraded/Unhealthy (ADR-006, inside the re-fold).
        var result = HealthGraphAnalysis.WhatIf(
            tree,
            new Dictionary<string, HealthStatus>
            {
                ["Failing"] = HealthStatus.Healthy,
            });

        Assert.Equal(HealthStatus.Unknown, result);
    }

    // ── Capped-but-unhealthy leaf is excluded from contributors ─────────────────────

    [Fact]
    public void Contributors_ImportantLeafUnderUnhealthyRoot_IsExcluded()
    {
        var required = Unhealthy("Required");
        var important = Unhealthy("Important");
        var root = HealthNode.Create("Root")
            .DependsOn(required, Importance.Required)
            .DependsOn(important, Importance.Important);
        var tree = HealthGraph.Create(root).CreateTreeSnapshot();

        Assert.Equal(HealthStatus.Unhealthy, tree.Status);

        var contributors = HealthGraphAnalysis.Contributors(tree);
        var only = Assert.Single(contributors);
        Assert.Equal("Required", only.Name);
    }

    // ── Degrees of freedom: healthy root and no-op target ───────────────────────────

    [Fact]
    public void Contributors_HealthyRoot_IsEmpty()
    {
        var root = HealthNode.Create("Root")
            .DependsOn(HealthNode.Create("Leaf"), Importance.Required);
        var tree = HealthGraph.Create(root).CreateTreeSnapshot();

        Assert.Empty(HealthGraphAnalysis.Contributors(tree));
    }

    [Fact]
    public void MinimalHealingSet_RootAlreadyAtOrBetterThanTarget_IsEmpty()
    {
        var tree = IncidentSnapshot(); // Unhealthy

        // Root is already <= Unhealthy, so nothing is required to reach "Unhealthy or better".
        Assert.Empty(HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Unhealthy));
    }

    // ── Composite nodes carrying their own intrinsic probe (a legal, tested shape:
    //    see HealthAggregatorTests.Aggregate_IntrinsicWorseThanDeps_IntrinsicWins) ───

    private static HealthTreeSnapshot IntrinsicCompositeSnapshot()
    {
        // Mid's OWN probe is Unhealthy; its only dependency is Healthy.
        var leaf = HealthNode.Create("Leaf"); // healthy
        var mid = HealthNode.Create("Mid")
            .WithHealthProbe(() => HealthEvaluation.Unhealthy("mid probe down"))
            .DependsOn(leaf, Importance.Required);
        var root = HealthNode.Create("Root").DependsOn(mid, Importance.Required);
        return HealthGraph.Create(root).CreateTreeSnapshot();
    }

    [Fact]
    public void Contributors_CompositeGatingViaOwnProbe_ReportsTheComposite()
    {
        var tree = IntrinsicCompositeSnapshot();
        Assert.Equal(HealthStatus.Unhealthy, tree.Status);

        // The unmasked intrinsic is recovered — Mid gates by name, the healthy leaf does not.
        var only = Assert.Single(HealthGraphAnalysis.Contributors(tree));
        Assert.Equal("Mid", only.Name);
        Assert.Equal(HealthStatus.Unhealthy, only.Status);
    }

    [Fact]
    public void WhatIf_HealingBelowAnIntrinsicComposite_DoesNotHelp_ButHealingItDoes()
    {
        var tree = IntrinsicCompositeSnapshot();

        // The leaf is already Healthy and Mid's own probe is the cause: forcing anything
        // below Mid cannot lower Mid — the re-fold recovers Mid's intrinsic Unhealthy.
        var belowMid = HealthGraphAnalysis.WhatIf(
            tree, new Dictionary<string, HealthStatus> { ["Leaf"] = HealthStatus.Healthy });
        Assert.Equal(HealthStatus.Unhealthy, belowMid);

        // Forcing Mid itself (its probe repaired) heals the root.
        var repaired = HealthGraphAnalysis.WhatIf(
            tree, new Dictionary<string, HealthStatus> { ["Mid"] = HealthStatus.Healthy });
        Assert.Equal(HealthStatus.Healthy, repaired);
    }

    [Fact]
    public void MinimalHealingSet_CompositeGatingViaOwnProbe_RepairsTheCompositeItself()
    {
        var tree = IntrinsicCompositeSnapshot();

        var steps = HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Degraded);
        var only = Assert.Single(steps);
        Assert.Equal("Mid", only.Name); // the composite's own probe, not a leaf
        Assert.Null(only.Quorum);

        // Applying the healing set through the re-fold actually reaches the target.
        var after = HealthGraphAnalysis.WhatIf(
            tree, steps.ToDictionary(s => s.Name, _ => HealthStatus.Healthy));
        Assert.True(HealthStatus.Degraded == after || HealthStatus.Healthy == after);
    }

    [Fact]
    public void WhatIf_MaskedIntrinsic_IsAttributedToChildren_DocumentedLimitation()
    {
        // P has intrinsic Degraded AND a Degraded Required child. The snapshot records only
        // the effective (Degraded) status, so P's intrinsic is indistinguishable from Healthy
        // (ADR-002: a single, effective cache). The analysis attributes the Degraded to the
        // child — the one theoretical blind spot of snapshot-only analysis. Pinned here so the
        // behavior is explicit and cannot drift silently.
        var child = Status("Child", HealthStatus.Degraded);
        var p = HealthNode.Create("P")
            .WithHealthProbe(() => HealthEvaluation.Degraded("p intrinsic"))
            .DependsOn(child, Importance.Required);
        var root = HealthNode.Create("Root").DependsOn(p, Importance.Required);
        var tree = HealthGraph.Create(root).CreateTreeSnapshot();

        Assert.Equal(HealthStatus.Degraded, tree.Status);

        var result = HealthGraphAnalysis.WhatIf(
            tree, new Dictionary<string, HealthStatus> { ["Child"] = HealthStatus.Healthy });
        Assert.Equal(HealthStatus.Healthy, result);
    }

    [Fact]
    public void WhatIf_DoesNotMutateTree()
    {
        var tree = IncidentSnapshot();
        var before = tree.Status;

        HealthGraphAnalysis.WhatIf(
            tree,
            new Dictionary<string, HealthStatus> { ["Real.BackendApi"] = HealthStatus.Healthy });

        Assert.Equal(before, tree.Status);
        Assert.Equal(HealthStatus.Unhealthy, tree.Status);
    }
}
