using Prognosis.Diagnostics;
using Xunit.Abstractions;

namespace Prognosis.Tests.Fuzzing;

/// <summary>
/// Property-based tests over generated graph topologies (see <see cref="Fuzz"/> for the
/// driver and <see cref="TopologyGenerator"/> for the shape zoo).
/// <para>
/// The example-based suites elsewhere in this project pin the shapes we <em>thought</em>
/// to write down. These state the laws the fold must obey and let a generator look for a
/// shape that breaks them — deep chains, K<sub>n</sub> tournaments, stacked diamonds
/// whose unrolled tree is exponential, self-loops, figure-eights, and hairballs where
/// everything depends on everything.
/// </para>
/// <para>
/// <b>Two regimes.</b> Properties about the <em>live engine</em> (totality, topology
/// agreement, monotonicity, inert <see cref="Importance.Optional"/> edges) run over every
/// shape including cyclic ones. Properties about the <em>diagnostic re-fold</em>
/// (ADR-007) run over acyclic graphs, which is the domain the ADR claims; the exact
/// agreement properties additionally use <see cref="IntrinsicMode.LeavesOnly"/>, the
/// regime in which snapshot-only intrinsic reconstruction is provably exact (see the
/// "masked probe" limitation documented on <see cref="HealthGraphAnalysis"/>).
/// </para>
/// <para>
/// Each property body is a plain static method so <see cref="Corpus"/> can replay a
/// pinned counterexample through the identical assertions.
/// </para>
/// </summary>
public class TopologyFuzzTests
{
    private readonly ITestOutputHelper _output;

    public TopologyFuzzTests(ITestOutputHelper output) => _output = output;

    private static readonly HealthStatus[] AllStatuses = Enum.GetValues<HealthStatus>();

    // Materializing a graph per counterfactual is the point — these properties compare
    // the analysis against the *live* engine, not against a second model of it — but it
    // makes the per-node loops quadratic. Cap the nodes probed per case; the case count
    // and the shrinker cover far more ground than exhausting one large graph would.
    private const int ProbeLimit = 6;

    // ── The live engine, over every shape including cyclic ───────────────────────

    /// <summary>
    /// Nothing in the public surface throws, hangs, or overflows the stack on any
    /// topology — including self-loops, overlapping cycles, and a diamond ladder whose
    /// unrolled tree is exponential in the node count.
    /// </summary>
    [Fact]
    public void EveryPublicQuery_IsTotal_OnEveryTopology() =>
        Fuzz.Check("total-on-every-topology", CheckTotality, output: _output);

    private static void CheckTotality(TopologySpec spec)
    {
        using var live = spec.Materialize();

        var report = live.Graph.RefreshAll();
        var topology = live.Graph.GetTopology();
        var tree = live.Graph.CreateTreeSnapshot();

        live.Graph.DetectCycles();
        HealthGraphAnalysis.FindOrphans(report, topology);
        HealthGraphAnalysis.BuildTreeSnapshot(report, topology);
        HealthGraphAnalysis.WhatIf(tree, new Dictionary<string, HealthStatus>());
        HealthGraphAnalysis.Contributors(tree);
        HealthGraphAnalysis.MinimalHealingSet(tree, HealthStatus.Healthy);

        Assert.Equal(TopologySpec.NameOf(TopologySpec.Root), report.Root.Name);
    }

    /// <summary>The graph's node set is exactly the spec's reachable set — no more, no less.</summary>
    [Fact]
    public void Report_CoversExactlyTheReachableSet() =>
        Fuzz.Check("report-covers-reachable-set", CheckReachableSet, output: _output);

    private static void CheckReachableSet(TopologySpec spec)
    {
        using var live = spec.Materialize();
        var report = live.Graph.GetReport();
        var topology = live.Graph.GetTopology();

        var expected = spec.Reachable().Select(TopologySpec.NameOf).OrderBy(n => n).ToList();

        Assert.Equal(expected, report.Nodes.Select(n => n.Name).OrderBy(n => n));
        Assert.Equal(expected, topology.Edges.Keys.OrderBy(n => n));
        Assert.Empty(HealthGraphAnalysis.FindOrphans(report, topology));
    }

    /// <summary>
    /// The topology reproduces each node's edges in declaration order — ADR-009 makes
    /// edge order contract, because tree reconstruction walks it pre-order and order
    /// decides which occurrence of a shared node carries the expanded subtree.
    /// </summary>
    [Fact]
    public void Topology_PreservesEdgeOrderAndImportance() =>
        Fuzz.Check("topology-preserves-edge-order", CheckTopologyEdges, output: _output);

    private static void CheckTopologyEdges(TopologySpec spec)
    {
        using var live = spec.Materialize();
        var topology = live.Graph.GetTopology();

        foreach (var index in spec.Reachable())
        {
            var expected = spec.Nodes[index].Edges
                .Select(e => new HealthTopologyEdge(TopologySpec.NameOf(e.Target), e.Importance))
                .ToList();

            Assert.Equal(expected, topology.Edges[TopologySpec.NameOf(index)]);
        }
    }

    /// <summary>
    /// <c>DetectCycles</c> finds a cycle exactly when one exists. The generator plants
    /// them; ground truth comes from the spec, not from the implementation.
    /// </summary>
    [Fact]
    public void DetectCycles_AgreesWithTheSpec() =>
        Fuzz.Check("detect-cycles-agrees", CheckDetectCycles, output: _output);

    private static void CheckDetectCycles(TopologySpec spec)
    {
        using var live = spec.Materialize();
        Assert.Equal(spec.HasCycle(), live.Graph.DetectCycles().Count > 0);
    }

    /// <summary>
    /// A settled graph stays settled: re-folding it changes nothing, so
    /// <c>StatusChanged</c> and Rx <c>DistinctUntilChanged</c> stay quiet between real
    /// transitions.
    /// <para>
    /// <b>This is the acceptance test for the bounded reason chain.</b> It used to be
    /// scoped to acyclic graphs, because on a cycle <c>HealthNode.Aggregate</c> spliced a
    /// back edge's cached reason into its own and so gained a full lap of the cycle on
    /// every wave:
    /// </para>
    /// <code>
    /// // TopologySpec.Parse("cycle=H&gt;1R;H&gt;0R,2R;U") — n0 ⇄ n1, and n1 also sees n2 (Unknown)
    /// pass 0: Unknown — "n1: n0: n1: n2: n2 intrinsic Unknown"
    /// pass 1: Unknown — "n1: n0: n1: n0: n1: n2: n2 intrinsic Unknown"
    /// pass 2: Unknown — "n1: n0: n1: n0: n1: n0: n1: n2: n2 intrinsic Unknown"
    /// </code>
    /// <para>
    /// The status was stable; only the string grew, without bound — and because
    /// <see cref="HealthReportComparer"/> carries <c>Reason</c> in its equality key
    /// (ADR-012 §1), every wave then looked like a change, so a cyclic graph emitted on
    /// every beat forever. <c>Aggregate</c> now cuts the chain at a back edge, bounding it
    /// by the walk depth. Running over <see cref="TopologyGenerator.AllShapes"/> —
    /// self-loops, figure-eights, hairballs — is what holds that claim up.
    /// </para>
    /// </summary>
    [Fact]
    public void RefreshAll_IsIdempotent() =>
        Fuzz.Check("refresh-all-idempotent", CheckIdempotence, output: _output);

    private static void CheckIdempotence(TopologySpec spec)
    {
        using var live = spec.Materialize();
        Settle(live, spec);

        var first = live.Graph.RefreshAll();
        var second = live.Graph.RefreshAll();
        var third = live.Graph.RefreshAll();

        Assert.True(HealthReportComparer.Instance.Equals(first, second));
        Assert.True(HealthReportComparer.Instance.Equals(second, third));
    }

    /// <summary>
    /// Runs the graph to its fixpoint before a stability assertion.
    /// <para>
    /// An acyclic graph is settled the moment it is built — one post-order wave carries
    /// every leaf's status all the way to the root — so this is a no-op there, and the
    /// properties keep the strong "stable immediately" claim.
    /// </para>
    /// <para>
    /// A cyclic graph is not. A wave reads each node's dependencies from cache, and across
    /// a back edge that cache is one wave old, so information crosses one back edge per
    /// wave and the fold needs several waves to converge. That is inherent to aggregating
    /// a cycle from cached values, not a defect — the defect this suite found was the fold
    /// never converging at all, because the reason chain grew forever. Settling first and
    /// then demanding stability distinguishes the two: no fixpoint exists to settle into
    /// if the chain is unbounded.
    /// </para>
    /// </summary>
    private static void Settle(MaterializedGraph live, TopologySpec spec)
    {
        if (!spec.HasCycle())
            return;

        // One wave per node is a generous bound on crossing every back edge; the zoo's
        // longest cycle is well under its node count.
        for (var wave = 0; wave < spec.Count + 3; wave++)
            live.Graph.RefreshAll();
    }

    /// <summary>
    /// The reason chain is a bounded description of a path, not an accumulator: once the
    /// fold has settled, no node's reason moves again however many waves run. Stated
    /// separately from idempotence because it is the narrower, more direct claim —
    /// idempotence would also pass if reasons were dropped altogether; this would not.
    /// </summary>
    [Fact]
    public void ReasonChains_DoNotGrowAcrossWaves() =>
        Fuzz.Check("reason-chains-do-not-grow", CheckReasonChainsDoNotGrow, output: _output);

    private static void CheckReasonChainsDoNotGrow(TopologySpec spec)
    {
        using var live = spec.Materialize();
        Settle(live, spec);

        var baseline = live.Graph.RefreshAll().Nodes
            .ToDictionary(n => n.Name, n => n.Reason, StringComparer.Ordinal);

        // Growth was one lap of the cycle per wave, so ten post-settle waves is far more
        // than enough to expose it — a 2-cycle diverged by the second.
        for (var wave = 0; wave < 10; wave++)
        {
            foreach (var node in live.Graph.RefreshAll().Nodes)
            {
                Assert.Equal(baseline[node.Name], node.Reason);
            }
        }
    }

    /// <summary>
    /// The invariant behind the bounded chain, asserted directly rather than through its
    /// symptom: every node appears at most once as a <em>hop</em>, and the whole chain is
    /// bounded by the node count. That bound is what forbids growth — a chain that
    /// accumulates laps must exceed it — so this rejects unbounded nesting even in a graph
    /// that happened to look stable over the waves the other property samples.
    /// <para>
    /// Deliberately <em>not</em> asserted: that the full sequence of names is a simple
    /// path. The terminal segment may repeat a hop, because that is what closing a cycle
    /// looks like — on <c>A →Required C →Required B →Required A</c>, A reports
    /// <c>"C: B: A is Unhealthy"</c>. That terminal embeds a status rather than a nested
    /// chain, so it ends the chain and a lap can be entered at most once. Pinned as
    /// <c>found-cycle-terminal-repeats-hop</c> in <see cref="Corpus"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void ReasonChains_AreBoundedPaths() =>
        Fuzz.Check("reason-chains-are-bounded-paths", CheckReasonChainsAreBoundedPaths, output: _output);

    private static void CheckReasonChainsAreBoundedPaths(TopologySpec spec)
    {
        using var live = spec.Materialize();
        var names = spec.Reachable().Select(TopologySpec.NameOf).ToHashSet(StringComparer.Ordinal);

        foreach (var node in live.Graph.GetReport().Nodes)
        {
            if (node.Reason is null)
                continue;

            // "n1: n2: n2 intrinsic Degraded" — every segment but the last is a hop on the
            // path from this node to the culprit; the last is the culprit's own reason.
            // (Generated probes never put ": " in a reason, so this split is unambiguous
            // for these graphs.)
            var segments = node.Reason.Split(": ");
            var hops = segments.Take(segments.Length - 1).ToList();

            foreach (var hop in hops)
            {
                Assert.True(
                    names.Contains(hop),
                    $"'{node.Name}' reason hop '{hop}' is not a node in the graph: "
                        + $"\"{node.Reason}\"");
            }

            Assert.True(
                hops.Distinct(StringComparer.Ordinal).Count() == hops.Count,
                $"'{node.Name}' reason visits a hop twice, so it is accumulating: "
                    + $"\"{node.Reason}\"");

            // The hop check already implies this, but state the bound explicitly — it is
            // the property that forbids growth, and it survives a change to how hops are
            // rendered.
            Assert.True(
                segments.Length <= spec.Count + 1,
                $"'{node.Name}' reason has {segments.Length} segments in a {spec.Count}-node "
                    + $"graph, so it is not bounded by the topology: \"{node.Reason}\"");
        }
    }

    /// <summary>
    /// ADR-009's headline claim: for a quiescent graph the reactive path
    /// (<c>BuildTreeSnapshot(report, topology)</c>) reconstructs exactly what
    /// <c>CreateTreeSnapshot</c> produces — same flattening of diamonds and cycles, same
    /// statuses, same reasons, same edge order.
    /// </summary>
    [Fact]
    public void ProjectedTree_EqualsLiveTree_WhenQuiescent() =>
        Fuzz.Check("projected-tree-equals-live-tree", CheckProjectedTree, output: _output);

    private static void CheckProjectedTree(TopologySpec spec)
    {
        using var live = spec.Materialize();

        var direct = live.Graph.CreateTreeSnapshot();
        var projected = HealthGraphAnalysis.BuildTreeSnapshot(
            live.Graph.GetReport(), live.Graph.GetTopology());

        AssertSameTree(direct, projected);
    }

    /// <summary>
    /// The unrolled tree is finite and non-repeating: a name is expanded at most once and
    /// every later occurrence is a childless stub carrying the same status. Without this,
    /// the diamond ladder (linear in nodes, exponential in paths) would not terminate.
    /// </summary>
    [Fact]
    public void TreeSnapshot_ExpandsEachNameOnce_AndAgreesWithItself() =>
        Fuzz.Check("tree-expands-each-name-once", CheckTreeFlattening, output: _output);

    private static void CheckTreeFlattening(TopologySpec spec)
    {
        using var live = spec.Materialize();

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var statuses = new Dictionary<string, HealthStatus>(StringComparer.Ordinal);
        Walk(live.Graph.CreateTreeSnapshot());

        void Walk(HealthTreeSnapshot node)
        {
            // Every occurrence of a name carries the same status: one node, one health,
            // however many paths reach it.
            if (statuses.TryGetValue(node.Name, out var seen))
                Assert.Equal(seen, node.Status);
            else
                statuses[node.Name] = node.Status;

            if (node.Dependencies.Count > 0)
            {
                Assert.True(
                    expanded.Add(node.Name),
                    $"'{node.Name}' was expanded more than once — the tree is not flattened.");
            }

            foreach (var dep in node.Dependencies)
                Walk(dep.Node);
        }
    }

    // ── Algebra of the fold ─────────────────────────────────────────────────────

    /// <summary>
    /// Worsening any single node never improves the root, and improving it never worsens
    /// the root. The whole diagnostic layer rests on this —
    /// <see cref="HealthGraphAnalysis.MinimalHealingSet"/> says so outright ("the fold is
    /// monotone, so the problem is well-posed").
    /// </summary>
    [Fact]
    public void Fold_IsMonotoneInEveryNode() =>
        Fuzz.Check("fold-is-monotone", CheckMonotonicity, output: _output);

    private static void CheckMonotonicity(TopologySpec spec)
    {
        foreach (var index in spec.Reachable().OrderBy(i => i).Take(ProbeLimit))
        {
            foreach (var lower in AllStatuses)
            {
                foreach (var higher in AllStatuses)
                {
                    if (!higher.IsWorseThan(lower))
                        continue;

                    var better = LiveRoot(spec.WithIntrinsic(index, lower));
                    var worse = LiveRoot(spec.WithIntrinsic(index, higher));

                    Assert.False(
                        better.IsWorseThan(worse),
                        $"Worsening '{TopologySpec.NameOf(index)}' from {lower} to {higher} "
                            + $"improved the root from {better} to {worse}.");
                }
            }
        }
    }

    /// <summary>
    /// ADR-006: an <see cref="HealthStatus.Unknown"/> child is strictly non-gating. From
    /// an all-healthy graph, making any one node <see cref="HealthStatus.Unknown"/> can
    /// raise the root to <see cref="HealthStatus.Unknown"/> at worst — never to a failing
    /// state. This is what lets "we cannot tell yet" be safe during startup.
    /// </summary>
    [Fact]
    public void Unknown_NeverGatesTheRootIntoAFailingState() =>
        Fuzz.Check("unknown-never-gates", CheckUnknownIsNonGating, output: _output);

    private static void CheckUnknownIsNonGating(TopologySpec spec)
    {
        var healthy = AllHealthy(spec);
        Assert.Equal(HealthStatus.Healthy, LiveRoot(healthy));

        foreach (var index in healthy.Reachable().OrderBy(i => i).Take(ProbeLimit))
        {
            var root = LiveRoot(healthy.WithIntrinsic(index, HealthStatus.Unknown));

            Assert.True(
                root is HealthStatus.Healthy or HealthStatus.Unknown,
                $"'{TopologySpec.NameOf(index)}' being Unknown drove the root to {root}.");
        }
    }

    /// <summary>
    /// A node reachable only through an <see cref="Importance.Optional"/> edge cannot
    /// affect the root, whatever it does. "If Reviews go down? Nothing happens."
    /// </summary>
    [Fact]
    public void OptionalOnlySubgraphs_AreInert()
    {
        var probed = 0;

        Fuzz.Check("optional-subgraphs-are-inert", spec =>
        {
            var optionalOnly = spec.Reachable().Except(spec.ReachableWithoutOptional());
            var baseline = LiveRoot(spec);

            foreach (var index in optionalOnly.OrderBy(i => i).Take(ProbeLimit))
            {
                probed++;
                foreach (var status in AllStatuses)
                    Assert.Equal(baseline, LiveRoot(spec.WithIntrinsic(index, status)));
            }
        }, output: _output);

        Assert.True(probed > 0, "No optional-only node was generated — the property is vacuous.");
    }

    /// <summary>
    /// ADR-008: <see cref="Importance.Advisory"/> is <see cref="Importance.Important"/>
    /// with <see cref="HealthStatus.Unknown"/> absorbed, so swapping every Advisory edge
    /// for Important can only make the root the same or worse — and makes it exactly the
    /// same when no node is Unknown.
    /// </summary>
    [Fact]
    public void Advisory_IsNeverStricterThanImportant() =>
        Fuzz.Check("advisory-is-never-stricter", CheckAdvisoryVsImportant, output: _output);

    private static void CheckAdvisoryVsImportant(TopologySpec spec)
    {
        var advisory = LiveRoot(spec);
        var important = LiveRoot(Remap(spec, Importance.Advisory, Importance.Important));

        Assert.False(
            advisory.IsWorseThan(important),
            $"Advisory edges produced {advisory} where Important produced {important}.");

        if (spec.Reachable().All(i => spec.Nodes[i].Intrinsic != HealthStatus.Unknown))
            Assert.Equal(important, advisory);
    }

    /// <summary>
    /// The importance levels form an ordered lattice at every child status:
    /// Optional ≤ Advisory ≤ Important ≤ Required, and Resilient never exceeds Required.
    /// Exhaustive rather than generated — the space is 5 × 4 × 2 — and it is the algebra
    /// every generated property leans on, so a new <see cref="Importance"/> member breaks
    /// this first and loudest.
    /// </summary>
    [Fact]
    public void ImportanceLevels_AreTotallyOrdered()
    {
        foreach (var child in AllStatuses)
        {
            foreach (var quorum in new[] { false, true })
            {
                var optional = HealthContribution.Of(Importance.Optional, child, quorum);
                var advisory = HealthContribution.Of(Importance.Advisory, child, quorum);
                var important = HealthContribution.Of(Importance.Important, child, quorum);
                var required = HealthContribution.Of(Importance.Required, child, quorum);
                var resilient = HealthContribution.Of(Importance.Resilient, child, quorum);

                Assert.Equal(HealthStatus.Healthy, optional);
                Assert.False(advisory.IsWorseThan(important), $"Advisory > Important at {child}.");
                Assert.False(important.IsWorseThan(required), $"Important > Required at {child}.");
                Assert.False(resilient.IsWorseThan(required), $"Resilient > Required at {child}.");

                // No importance can invent a failure the child does not have.
                Assert.False(
                    required.IsWorseThan(child),
                    $"Required manufactured {required} from a {child} child.");
            }
        }
    }

    // ── The diagnostic re-fold must not drift from production (ADR-007) ──────────

    /// <summary>
    /// Re-folding a snapshot with no hypotheticals reproduces the live root exactly. If
    /// this drifts, every counterfactual answer built on it is confidently wrong.
    /// </summary>
    [Fact]
    public void WhatIf_WithNoOverrides_ReproducesTheLiveRoot() =>
        Fuzz.Check(
            "whatif-reproduces-live-root",
            CheckWhatIfReproducesRoot,
            shapes: TopologyGenerator.AcyclicShapes,
            precondition: spec => !spec.HasCycle(),
            output: _output);

    private static void CheckWhatIfReproducesRoot(TopologySpec spec)
    {
        using var live = spec.Materialize();

        Assert.Equal(
            live.RootStatus,
            HealthGraphAnalysis.WhatIf(
                live.Graph.CreateTreeSnapshot(), new Dictionary<string, HealthStatus>()));
    }

    /// <summary>
    /// The strong no-drift property: for a graph whose composites are intrinsically
    /// healthy — the ADR-004 model, and the regime where snapshot-only intrinsic
    /// reconstruction is exact — a counterfactual on a leaf predicts the live engine's
    /// answer <em>exactly</em>, for every leaf and every status.
    /// </summary>
    [Fact]
    public void WhatIf_PredictsTheLiveEngine_UnderLeafCounterfactuals() =>
        Fuzz.Check(
            "whatif-predicts-live-engine",
            CheckWhatIfPredictsEngine,
            shapes: TopologyGenerator.AcyclicShapes,
            mode: IntrinsicMode.LeavesOnly,
            precondition: spec => !spec.HasCycle() && spec.HasLeafFailuresOnly(),
            output: _output);

    private static void CheckWhatIfPredictsEngine(TopologySpec spec)
    {
        using var live = spec.Materialize();
        var tree = live.Graph.CreateTreeSnapshot();

        foreach (var leaf in spec.Leaves().Take(ProbeLimit))
        {
            foreach (var status in AllStatuses)
            {
                var predicted = HealthGraphAnalysis.WhatIf(
                    tree,
                    new Dictionary<string, HealthStatus> { [TopologySpec.NameOf(leaf)] = status });

                Assert.Equal(LiveRoot(spec.WithIntrinsic(leaf, status)), predicted);
            }
        }
    }

    /// <summary>
    /// Outside that regime — composites failing intrinsically, so a probe failure can be
    /// masked by an equal-or-worse child — the analysis is allowed to be optimistic
    /// (repairing the children looks like it heals the node) but never pessimistic. It may
    /// under-report the root; it must never over-report it, which would send an operator
    /// chasing a failure the engine would not produce.
    /// </summary>
    [Fact]
    public void WhatIf_NeverOverstatesTheRoot_UnderMaskedProbes() =>
        Fuzz.Check(
            "whatif-never-overstates",
            CheckWhatIfNeverOverstates,
            shapes: TopologyGenerator.AcyclicShapes,
            precondition: spec => !spec.HasCycle(),
            output: _output);

    private static void CheckWhatIfNeverOverstates(TopologySpec spec)
    {
        using var live = spec.Materialize();
        var tree = live.Graph.CreateTreeSnapshot();

        foreach (var index in spec.Reachable().OrderBy(i => i).Take(ProbeLimit))
        {
            foreach (var status in AllStatuses)
            {
                var predicted = HealthGraphAnalysis.WhatIf(
                    tree,
                    new Dictionary<string, HealthStatus> { [TopologySpec.NameOf(index)] = status });

                var actual = LiveRoot(spec.WithIntrinsic(index, status));

                Assert.False(
                    predicted.IsWorseThan(actual),
                    $"WhatIf predicted {predicted} where the engine produces {actual}.");
            }
        }
    }

    /// <summary>
    /// A healing set actually heals (soundness) and carries nothing it does not need
    /// (irredundancy) — verified against the live engine by repairing the named nodes'
    /// probes and re-materializing, not by re-running the fold that proposed them.
    /// </summary>
    [Fact]
    public void MinimalHealingSet_IsSoundAndMinimal() =>
        Fuzz.Check(
            "healing-set-sound-and-minimal",
            CheckHealingSet,
            shapes: TopologyGenerator.AcyclicShapes,
            mode: IntrinsicMode.LeavesOnly,
            precondition: spec => !spec.HasCycle() && spec.HasLeafFailuresOnly(),
            output: _output);

    private static void CheckHealingSet(TopologySpec spec)
    {
        using var live = spec.Materialize();
        var tree = live.Graph.CreateTreeSnapshot();

        foreach (var target in new[] { HealthStatus.Healthy, HealthStatus.Degraded })
        {
            var steps = HealthGraphAnalysis.MinimalHealingSet(tree, target);
            if (steps.Count == 0)
                continue;

            var indices = steps.Select(s => IndexOf(s.Name)).ToList();

            var healed = LiveRoot(HealAll(spec, indices));
            Assert.False(
                healed.IsWorseThan(target),
                $"Healing [{string.Join(", ", steps.Select(s => s.Name))}] left the root at "
                    + $"{healed}, short of {target}.");

            foreach (var omitted in indices)
            {
                var partial = LiveRoot(HealAll(spec, indices.Where(i => i != omitted)));

                Assert.True(
                    partial.IsWorseThan(target),
                    $"'{TopologySpec.NameOf(omitted)}' is not needed to reach {target} — "
                        + "the healing set is not minimal.");
            }
        }
    }

    /// <summary>
    /// Contributors always name a real, load-bearing frontier, so repeatedly repairing
    /// what they name drives the root to <see cref="HealthStatus.Healthy"/> — in at most
    /// one round per node, since every round retires at least one non-healthy node and the
    /// fold never un-heals anything.
    /// <para>
    /// Note what this deliberately does <em>not</em> claim: that one round improves the
    /// <em>root</em>. It need not. Under an <see cref="Importance.Important"/> or
    /// <see cref="Importance.Advisory"/> cap, several nodes can independently hold the
    /// root at the capped status while only the arg-worst one is named, so a round can
    /// improve the subtree and leave the root exactly where it was — the generator finds
    /// <c>n0 →Required n1 →Advisory n2 →Required {n3 Degraded, n4 Unhealthy}</c>
    /// immediately. Callers wanting the complete repair in one answer want
    /// <see cref="HealthGraphAnalysis.MinimalHealingSet"/>, which is specified to return
    /// it; <c>Contributors</c> is the determining frontier, and it iterates.
    /// </para>
    /// </summary>
    [Fact]
    public void Contributors_NameALoadBearingFrontier() =>
        Fuzz.Check(
            "contributors-name-a-load-bearing-frontier",
            CheckContributorFrontier,
            shapes: TopologyGenerator.AcyclicShapes,
            mode: IntrinsicMode.LeavesOnly,
            precondition: spec => !spec.HasCycle() && spec.HasLeafFailuresOnly(),
            output: _output);

    private static void CheckContributorFrontier(TopologySpec spec)
    {
        var current = spec;
        var reachable = spec.Reachable();

        for (var round = 0; round <= spec.Count; round++)
        {
            using var live = current.Materialize();
            var contributors = HealthGraphAnalysis.Contributors(live.Graph.CreateTreeSnapshot());

            if (live.RootStatus == HealthStatus.Healthy)
            {
                Assert.Empty(contributors);
                return;
            }

            Assert.NotEmpty(contributors);

            foreach (var contributor in contributors)
            {
                var index = IndexOf(contributor.Name);
                Assert.Contains(index, reachable);

                // A contributor that is already healthy would name a repair with nothing
                // to repair, and the loop below would never terminate.
                Assert.NotEqual(HealthStatus.Healthy, contributor.Status);
                Assert.NotEqual(HealthStatus.Healthy, current.Nodes[index].Intrinsic);
            }

            current = HealAll(current, contributors.Select(c => IndexOf(c.Name)));
        }

        Assert.Fail(
            $"Repairing the contributor frontier {spec.Count + 1} times never reached a healthy "
                + "root — the frontier is not making progress.");
    }

    // ── Regression corpus ───────────────────────────────────────────────────────

    /// <summary>
    /// Topologies pinned by their <see cref="TopologySpec.ToLiteral"/> encoding, so they
    /// run on every build no matter what seed is in force. A failure message tells you
    /// exactly what to paste here.
    /// <para>
    /// The <c>found/</c> entries are counterexamples this suite produced against real
    /// defects; the <c>hand/</c> entries are pathologies written by hand.
    /// </para>
    /// </summary>
    public static TheoryData<string> Corpus() => new()
    {
        // Advisory was absent from the switch in FoldModel.Heal, so an Advisory edge was
        // silently healed like an Optional one: "fix n1" left the root Degraded.
        "found-advisory-unhealed=H>1R,2A;U;D",

        // Then the first fix over-healed: Advisory absorbs Unknown, so bringing the child
        // to Unknown is enough and repairing n2 as well was redundant.
        "found-advisory-overhealed=H>1A;H>2R,3R;U;D",

        // A resilient group can reach Degraded either by establishing the quorum (one deep
        // repair) or by fixing every sibling (several shallow ones). Only the first was
        // costed.
        "found-resilient-route=H>1R;H>2R;H>3S;H>4R,5R;X;U",

        // n4 is shared: repairing it for n1's Required path also satisfies n2's quorum for
        // free, which made Heal's separately-computed repair of n3 dead weight.
        "found-resilient-shared=H>1R,2R;H>4R;H>3S,4S;X;X",

        // An Advisory cap between root and frontier: healing the named contributor n4
        // leaves the root Degraded because n3 takes over. Pins the iteration semantics.
        "found-advisory-cap-frontier=H>1R;H>2A;H>3R,4R;D;X",

        // The reason chain gained a lap of the cycle per wave here: n0 ⇄ n1, with the bad
        // status arriving from n2 outside the cycle.
        "found-cycle-reason-lap=H>1R;H>0R,2R;U",

        // From PR review: A →R C, B, D; C →R B; B →R A; D unhealthy. B's back edge to A is
        // cut, but C is black by the time A aggregates it, so A splices "B: A is Unhealthy"
        // and A's own chain names A. Bounded and stable — the cut terminal carries a status,
        // not a nested chain, so a lap is entered at most once — but it is why this suite
        // claims bounded hops rather than a simple path.
        "found-cycle-terminal-repeats-hop=H>1R,2R,3R;H>2R;H>0R;X",

        // A self-loop plus a long cycle closed through Optional edges. Converges, but not
        // on the first wave — it takes several for the status to cross the back edges.
        // Exercises the settling path in Settle().
        "found-cycle-late-convergence=H>4R;H>2O;H>2R,0R;U>1O;H>3R",

        // A root that depends on itself, alongside a real dependency. The self-loop is its
        // own back edge, which is the degenerate case of the same cut.
        "hand-self-loop-root=H>0R,1R;X",

        // K4: every edge a DAG can have, one of each importance.
        "hand-k4=H>1R,2I,3S;H>2S,3O;H>3A;X",

        // Two rungs of a diamond ladder — linear in nodes, exponential in tree paths.
        "hand-diamond-ladder=H>1R,2R;H>3I,4S;H>3S,4R;U;X",
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_TopologiesStillHold(string literal)
    {
        var spec = TopologySpec.Parse(literal);

        // Applicable to every topology.
        CheckTotality(spec);
        CheckReachableSet(spec);
        CheckTopologyEdges(spec);
        CheckDetectCycles(spec);
        CheckProjectedTree(spec);
        CheckTreeFlattening(spec);
        CheckMonotonicity(spec);
        CheckUnknownIsNonGating(spec);
        CheckAdvisoryVsImportant(spec);
        CheckIdempotence(spec);
        CheckReasonChainsDoNotGrow(spec);
        CheckReasonChainsAreBoundedPaths(spec);

        if (spec.HasCycle())
            return;

        CheckWhatIfReproducesRoot(spec);
        CheckWhatIfNeverOverstates(spec);

        if (!spec.HasLeafFailuresOnly())
            return;

        CheckWhatIfPredictsEngine(spec);
        CheckHealingSet(spec);
        CheckContributorFrontier(spec);
    }

    [Fact]
    public void Literal_RoundTrips() =>
        Fuzz.Check("literal-round-trips", spec =>
        {
            var reparsed = TopologySpec.Parse(spec.ToLiteral());

            Assert.Equal(spec.Count, reparsed.Count);
            Assert.Equal(spec.ToLiteral(), reparsed.ToLiteral());
            Assert.Equal(LiveRoot(spec), LiveRoot(reparsed));
        }, output: _output);

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static HealthStatus LiveRoot(TopologySpec spec)
    {
        using var live = spec.Materialize();
        return live.RootStatus;
    }

    private static int IndexOf(string name) => int.Parse(name.AsSpan(1));

    private static TopologySpec AllHealthy(TopologySpec spec) =>
        spec with
        {
            Nodes = spec.Nodes.Select(n => n with { Intrinsic = HealthStatus.Healthy }).ToList(),
        };

    private static TopologySpec HealAll(TopologySpec spec, IEnumerable<int> indices) =>
        indices.Aggregate(spec, (current, i) => current.WithIntrinsic(i, HealthStatus.Healthy));

    private static TopologySpec Remap(TopologySpec spec, Importance from, Importance to) =>
        spec with
        {
            Nodes = spec.Nodes
                .Select(n => n with
                {
                    Edges = n.Edges
                        .Select(e => e.Importance == from ? e with { Importance = to } : e)
                        .ToList(),
                })
                .ToList(),
        };

    private static void AssertSameTree(HealthTreeSnapshot expected, HealthTreeSnapshot actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.Dependencies.Count, actual.Dependencies.Count);

        for (var i = 0; i < expected.Dependencies.Count; i++)
        {
            Assert.Equal(expected.Dependencies[i].Importance, actual.Dependencies[i].Importance);
            AssertSameTree(expected.Dependencies[i].Node, actual.Dependencies[i].Node);
        }
    }
}
