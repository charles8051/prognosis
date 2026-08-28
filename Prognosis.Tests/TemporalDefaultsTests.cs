using System.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// ADR-011 §10 — graph-wide temporal defaults. Falsification discipline as in
/// <see cref="TemporalPolicyTests"/>: every rule §10 states is pinned by a test that
/// a mutation of the production logic turns red, and the concurrency claims are
/// exercised against real multi-writer paths rather than asserted.
/// </summary>
public class TemporalDefaultsTests
{
    private static readonly DebounceOptions Debounce =
        new(TimeSpan.FromSeconds(10));
    private static readonly DebounceOptions OtherDebounce =
        new(TimeSpan.FromSeconds(30));
    private static readonly GraceOptions Grace =
        new(TimeSpan.FromSeconds(100));

    private static HealthNode Leaf(string name) =>
        HealthNode.Create(name).WithHealthProbe(() => HealthEvaluation.Healthy);

    // ───────────────────────── §10a — materialized at attach ─────────────────────────

    [Fact]
    public void Defaults_MaterializeIntoLeaves_AtConstruction()
    {
        var leaf = Leaf("leaf");
        var root = HealthNode.Create("root").DependsOn(leaf, Importance.Required);

        using var graph = HealthGraph.Create(root, new TemporalDefaults(Debounce));

        Assert.Equal(Debounce, leaf.DebouncePolicy.Effective);
        Assert.Equal(TemporalPolicyOrigin.GraphDefault, leaf.DebouncePolicy.Origin);
    }

    [Fact]
    public void Defaults_MaterializeIntoNodesAddedLater_ViaDependsOn()
    {
        // The defect a one-shot `foreach (graph.Nodes)` loop cannot fix: a node that
        // appears after Create. Mutation check — dropping the MaterializeDefaults call
        // in RefreshTopology leaves this Unset.
        var root = HealthNode.Create("root");
        using var graph = HealthGraph.Create(root, new TemporalDefaults(Debounce));

        var late = Leaf("late");
        root.DependsOn(late, Importance.Required);

        Assert.Equal(Debounce, late.DebouncePolicy.Effective);
        Assert.Equal(TemporalPolicyOrigin.GraphDefault, late.DebouncePolicy.Origin);
    }

    [Fact]
    public void Defaults_ArePoliciedBeforeTheFirstWave_SoTheSeededDeadlineSeesThem()
    {
        // §10a's defect (4): the constructor's initial wave and _temporalDeadline.Seed
        // must run against the materialized policies. A node already failing at
        // construction enters a debounce hold on that first wave, which installs a
        // pending deadline — and the seeded minimum can only see that deadline if the
        // policy was materialized BEFORE the seed. Mutation check: moving
        // MaterializeDefaults after _temporalDeadline.Seed leaves this null.
        //
        // Note the node's effective status here is Unhealthy, not held-Healthy: the §4
        // constructor seed captures the probe's value pre-chain, so the debounce holds
        // a prior effective that is already the failure. That is ADR-011 §4 behaviour,
        // unrelated to §10 — the deadline is what this test pins.
        var clock = new FakeClock();
        var leaf = HealthNode.Create("leaf")
            .WithHealthProbe(() => HealthEvaluation.Unhealthy("down"));
        var root = HealthNode.Create("root").DependsOn(leaf, Importance.Required);

        using var graph = HealthGraph.Create(root, clock.Read, new TemporalDefaults(Debounce));

        Assert.NotNull(graph.NextTemporalDeadline);
        Assert.Equal(Debounce.MinimumFaultDuration, graph.NextTemporalDeadline);
    }

    [Fact]
    public void NoDefaults_LeavesEveryNodeUnconfigured_ZeroCostToNonUsers()
    {
        var leaf = Leaf("leaf");
        using var graph = HealthGraph.Create(HealthNode.Create("root").DependsOn(leaf, Importance.Required));

        Assert.Equal(TemporalPolicyOrigin.Unset, leaf.DebouncePolicy.Origin);
        Assert.Equal(TemporalPolicyOrigin.Unset, leaf.GracePolicy.Origin);
        Assert.False(graph.HasTemporalNodes);
        Assert.Null(graph.Defaults);
    }

    // ───────────────────────── §10b — explicit wins, either order ─────────────────────────

    [Fact]
    public void ExplicitPolicySetBeforeAttach_IsNotOverwrittenByADefault()
    {
        var leaf = Leaf("leaf").WithDebounce(OtherDebounce);
        using var graph = HealthGraph.Create(
            HealthNode.Create("root").DependsOn(leaf, Importance.Required), new TemporalDefaults(Debounce));

        Assert.Equal(OtherDebounce, leaf.DebouncePolicy.Effective);
        Assert.Equal(TemporalPolicyOrigin.Explicit, leaf.DebouncePolicy.Origin);
    }

    [Fact]
    public void ExplicitPolicySetAfterAttach_OverwritesTheMaterializedDefault()
    {
        // The reverse order, which a consumer-side loop cannot get right for
        // late-added nodes: overriding a default at runtime is the point.
        var leaf = Leaf("leaf");
        using var graph = HealthGraph.Create(
            HealthNode.Create("root").DependsOn(leaf, Importance.Required), new TemporalDefaults(Debounce));

        leaf.WithDebounce(OtherDebounce);

        Assert.Equal(OtherDebounce, leaf.DebouncePolicy.Effective);
        Assert.Equal(TemporalPolicyOrigin.Explicit, leaf.DebouncePolicy.Origin);
    }

    [Fact]
    public void AnExplicitSlot_DoesNotMakeAMatchingDefaultAConflict()
    {
        // An explicit value outranks the default without turning a second graph's
        // MATCHING default into a conflict. Named precisely since retained sources: the
        // default is recorded rather than "skipped", and two graphs contributing
        // *different* defaults DO now conflict even under an explicit value — see
        // DefaultsConflict_EvenWhenAnExplicitValueOutranksThem.
        var shared = Leaf("shared").WithDebounce(OtherDebounce);
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));
        using var b = HealthGraph.Create(
            HealthNode.Create("b").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));

        Assert.Equal(OtherDebounce, shared.DebouncePolicy.Effective);
    }

    // ───────────────────────── §10c — conflicts, slots, leases ─────────────────────────

    [Fact]
    public void TwoGraphsWithDifferentDefaults_OverASharedNode_Throw()
    {
        var shared = Leaf("shared");
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            HealthGraph.Create(
                HealthNode.Create("b").DependsOn(shared, Importance.Required), new TemporalDefaults(OtherDebounce)));

        Assert.Contains("shared", ex.Message);
        Assert.Contains("§10c", ex.Message);
    }

    [Fact]
    public void TwoGraphsWithIdenticalDefaults_OverASharedNode_AreIdempotent()
    {
        var shared = Leaf("shared");
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));
        using var b = HealthGraph.Create(
            HealthNode.Create("b").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));

        Assert.Equal(Debounce, shared.DebouncePolicy.Effective);
    }

    [Fact]
    public void CompatibilityIsComparedPerSlot_NotByWholeRecordEquality()
    {
        // {Debounce: X} then {Debounce: X, Grace: Y}: unequal TemporalDefaults records,
        // but no slot conflicts — the second must fill the still-Unset grace slot.
        // Mutation check: comparing the bags by record equality turns this red.
        var shared = Leaf("shared");
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));
        using var b = HealthGraph.Create(
            HealthNode.Create("b").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce, Grace));

        Assert.Equal(Debounce, shared.DebouncePolicy.Effective);
        Assert.Equal(Grace, shared.GracePolicy.Effective);
        Assert.Equal(TemporalPolicyOrigin.GraphDefault, shared.GracePolicy.Origin);
    }

    [Fact]
    public void ANodeOutOfScope_CannotConflict_EvenWithDifferingDefaults()
    {
        var shared = Leaf("shared");
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));

        // B's differing default excludes the shared node, so there is nothing to conflict over.
        using var b = HealthGraph.Create(
            HealthNode.Create("b").DependsOn(shared, Importance.Required),
            new TemporalDefaults(OtherDebounce, AppliesTo: n => n.Name == "nothing"));

        Assert.Equal(Debounce, shared.DebouncePolicy.Effective);
    }

    [Fact]
    public void ALeasedNode_IsSkippedSilently_NotThrownAt()
    {
        // Defect (2): a blanket default must not turn one legal Lease() into a
        // startup crash. Mutation check — routing the default through WithDebounce
        // instead of MaterializeDefaults makes this throw.
        var leased = HealthNode.Create("leased");
        leased.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30)));

        using var graph = HealthGraph.Create(
            HealthNode.Create("root").DependsOn(leased, Importance.Required).DependsOn(Leaf("plain"), Importance.Required),
            new TemporalDefaults(Debounce));

        Assert.Equal(TemporalPolicyOrigin.Unset, leased.DebouncePolicy.Origin);
        Assert.True(leased.IsLeased);
    }

    [Fact]
    public void LeaseAfterADefaultWasMaterialized_Succeeds_AndTheDefaultBecomesInertNotDestroyed()
    {
        // The reciprocal §10 owes ADR-010 §1 ("callable at build time or at runtime").
        // Without the Explicit narrowing this throws and the node is permanently
        // un-leasable — the startup crash relocated to the other ordering.
        //
        // Retained sources: the lease does not CLEAR the default, it outranks it. The
        // first implementation cleared it, which made acquiring a lease silently mutate
        // unrelated configuration. Nothing in effect, everything still on record.
        var node = Leaf("node");
        using var graph = HealthGraph.Create(
            HealthNode.Create("root").DependsOn(node, Importance.Required), new TemporalDefaults(Debounce));
        Assert.Equal(TemporalPolicyOrigin.GraphDefault, node.DebouncePolicy.Origin);

        var lease = node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30)));

        Assert.NotNull(lease);
        Assert.True(node.IsLeased);
        Assert.Null(node.DebouncePolicy.Effective);                     // nothing applies
        Assert.Equal(TemporalPolicyOrigin.Unset, node.DebouncePolicy.Origin);
        Assert.Equal(Debounce, node.DebouncePolicy.GraphDefault);       // but still on record
    }

    [Fact]
    public void AnExplicitPolicyOutranksAGraphDefault_WithoutDestroyingIt_InEitherOrder()
    {
        // The core of retained sources: both contributions stay visible and the losing
        // one is not pretended out of existence.
        //
        // BOTH orders are asserted deliberately. An earlier version of this test only
        // set the explicit value first, which cannot detect a SetPolicy that clears the
        // default slot — at that point the slot is empty anyway. Only the default-then-
        // explicit order exercises the destructive write. (Mutation check: adding
        // `DefaultDebounce = null` to SetPolicy goes red here, and did not on the
        // one-order version.)
        var explicitFirst = Leaf("explicit-first").WithDebounce(OtherDebounce);
        using var g1 = HealthGraph.Create(
            HealthNode.Create("r1").DependsOn(explicitFirst, Importance.Required),
            new TemporalDefaults(Debounce));

        var defaultFirst = Leaf("default-first");
        using var g2 = HealthGraph.Create(
            HealthNode.Create("r2").DependsOn(defaultFirst, Importance.Required),
            new TemporalDefaults(Debounce));
        defaultFirst.WithDebounce(OtherDebounce);

        foreach (var view in new[] { explicitFirst.DebouncePolicy, defaultFirst.DebouncePolicy })
        {
            Assert.Equal(OtherDebounce, view.Effective);
            Assert.Equal(TemporalPolicyOrigin.Explicit, view.Origin);
            Assert.Equal(OtherDebounce, view.Explicit);
            Assert.Equal(Debounce, view.GraphDefault);
        }
    }

    [Fact]
    public void AnExplicitPolicy_SettlesADefaultDisagreement()
    {
        // The node's own statement outranks every default, so once it is present two
        // graphs disagreeing about a default they cannot apply is not worth failing on.
        // This is the remedy that needs no restructuring — no rewiring, no second node,
        // no graph reconfiguration — and an earlier revision threw here anyway to keep
        // the retained layer unambiguous for a revocation feature that does not exist.
        // Mutation check: restoring that throw turns this red.
        var own = new DebounceOptions(TimeSpan.FromSeconds(5));
        var shared = Leaf("shared").WithDebounce(own);

        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));
        using var b = HealthGraph.Create(
            HealthNode.Create("b").DependsOn(shared, Importance.Required), new TemporalDefaults(OtherDebounce));

        Assert.Equal(own, shared.DebouncePolicy.Effective);
        Assert.Equal(TemporalPolicyOrigin.Explicit, shared.DebouncePolicy.Origin);

        // The retained default is the first contribution, recorded for diagnostics only.
        // These values are inert while the explicit one stands, so there is no right
        // answer to pick between them — and picking is not required.
        Assert.Equal(Debounce, shared.DebouncePolicy.GraphDefault);
    }

    [Fact]
    public void ADefaultDisagreement_StillThrows_WhenNoExplicitPolicySettlesIt()
    {
        // The narrowing above must not swallow the case it was built for: with no
        // explicit policy, differing defaults are still a loud wiring error.
        var shared = Leaf("shared");
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            HealthGraph.Create(
                HealthNode.Create("b").DependsOn(shared, Importance.Required),
                new TemporalDefaults(OtherDebounce)));

        // And the message leads with the remedy that needs no restructuring.
        Assert.Contains("WithDebounce", ex.Message);
    }

    [Fact]
    public void SettlingAContestedNodeExplicitly_UnpoisonsAFailedLateAttach()
    {
        // The payoff, and the reason this beats merely suppressing the repeated error:
        // a late attach rejected for a default disagreement can now be COMPLETED, not
        // just silenced. Stating the node's policy settles the argument, and the next
        // reconcile attaches it successfully.
        var conflicted = Leaf("conflicted");
        using var first = HealthGraph.Create(
            HealthNode.Create("first").DependsOn(conflicted, Importance.Required),
            new TemporalDefaults(Debounce));

        var root = HealthNode.Create("second");
        using var second = HealthGraph.Create(root, new TemporalDefaults(OtherDebounce));

        Assert.Throws<InvalidOperationException>(
            () => root.DependsOn(conflicted, Importance.Required));
        Assert.Throws<InvalidOperationException>(() => root.Refresh());   // poisoned

        // One call on the node, no rewiring.
        var settled = new DebounceOptions(TimeSpan.FromSeconds(7));
        conflicted.WithDebounce(settled);

        root.Refresh();                                                    // heals

        Assert.Contains(second.Nodes, n => ReferenceEquals(n, conflicted));
        Assert.Equal(settled, conflicted.DebouncePolicy.Effective);
    }

    [Fact]
    public void LeaseOnAnExplicitlyPoliciedNode_StillThrows_Section7Unamended()
    {
        var node = Leaf("node").WithDebounce(Debounce);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30))));

        Assert.Contains("§7", ex.Message);
    }

    [Fact]
    public void ExplicitPolicyOnALeasedNode_StillThrows_Section7Unamended()
    {
        var node = HealthNode.Create("node");
        node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30)));

        Assert.Throws<InvalidOperationException>(() => node.WithDebounce(Debounce));
        Assert.Throws<InvalidOperationException>(() => node.WithGrace(Grace));
    }

    // ─────────────────── §10c — attach atomicity ───────────────────

    [Fact]
    public void AFailedConstruction_LeavesNoDefaultsBehindOnEarlierNodes()
    {
        // Retention raises the stakes on partial application: a materialized default now
        // OUTLIVES the graph, so a constructor that writes to some nodes and then throws
        // would permanently policy shared nodes with a bag nobody successfully applied.
        // Mutation check — dropping the rollback in MaterializeDefaults leaves `clean`
        // carrying a GraphDefault here.
        var conflicted = Leaf("conflicted");
        using var first = HealthGraph.Create(
            HealthNode.Create("first").DependsOn(conflicted, Importance.Required),
            new TemporalDefaults(Debounce));

        // A second graph over several leaves, one of which now conflicts. Whichever
        // order the node set is walked in, either `clean` is written before the throw
        // (and must be reverted) or it is never reached.
        var clean = Leaf("clean");
        var alsoClean = Leaf("also-clean");
        var root = HealthNode.Create("second")
            .DependsOn(clean, Importance.Required)
            .DependsOn(conflicted, Importance.Required)
            .DependsOn(alsoClean, Importance.Required);

        Assert.Throws<InvalidOperationException>(() =>
            HealthGraph.Create(root, new TemporalDefaults(OtherDebounce)));

        Assert.Equal(TemporalPolicyOrigin.Unset, clean.DebouncePolicy.Origin);
        Assert.Null(clean.DebouncePolicy.GraphDefault);
        Assert.Equal(TemporalPolicyOrigin.Unset, alsoClean.DebouncePolicy.Origin);
        Assert.Null(alsoClean.DebouncePolicy.GraphDefault);

        // The pre-existing contribution from the graph that DID succeed is untouched.
        Assert.Equal(Debounce, conflicted.DebouncePolicy.GraphDefault);
    }

    [Fact]
    public void AThrowingPredicate_WritesNothingAtAll()
    {
        // Selection runs as its own phase precisely so the most common failure — a
        // predicate that throws partway through the node set — cannot leave writes
        // behind. Mutation check: folding selection back into the apply loop lets a node
        // selected before the throw keep its default.
        var a = Leaf("aaa");
        var b = Leaf("bbb");
        var root = HealthNode.Create("root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);

        Assert.Throws<InvalidOperationException>(() =>
            HealthGraph.Create(root, new TemporalDefaults(
                Debounce,
                AppliesTo: n => n.Name == "bbb"
                    ? throw new NotSupportedException("boom")
                    : n.Dependencies.Count == 0)));

        Assert.Equal(TemporalPolicyOrigin.Unset, a.DebouncePolicy.Origin);
        Assert.Equal(TemporalPolicyOrigin.Unset, b.DebouncePolicy.Origin);
    }

    [Fact]
    public void AFailedLateAttach_WritesNoDefaults_ButPoisonsTheGraphUntilTheWiringIsFixed()
    {
        // The late-add mirror of the constructor case. Materialization now precedes
        // subscription and snapshot swap, so a rejected node is not left subscribed —
        // but be honest about what this test can and cannot show: it CANNOT distinguish
        // the two orderings, because the rejected node's dependency edge means a refresh
        // reaches the graph through its PARENT's subscription regardless of whether the
        // node itself is subscribed. The ordering change is hygiene, not something a
        // behavioural test here pins.
        //
        // What is pinned: no partial policy write, and the honest consequence that the
        // graph is left unusable.
        var conflicted = Leaf("conflicted");
        using var first = HealthGraph.Create(
            HealthNode.Create("first").DependsOn(conflicted, Importance.Required),
            new TemporalDefaults(Debounce));

        var root = HealthNode.Create("second");
        using var second = HealthGraph.Create(root, new TemporalDefaults(OtherDebounce));

        Assert.Throws<InvalidOperationException>(
            () => root.DependsOn(conflicted, Importance.Required));

        // Still only the first graph's contribution, and the rejected node is not in the
        // second graph's node set.
        Assert.Equal(Debounce, conflicted.DebouncePolicy.GraphDefault);
        Assert.DoesNotContain(second.Nodes, n => ReferenceEquals(n, conflicted));

        // Known limitation, asserted rather than hidden — but note the remedy is now a
        // single call, not a restructuring: settling the node's policy explicitly makes
        // the next reconcile succeed (see
        // SettlingAContestedNodeExplicitly_UnpoisonsAFailedLateAttach). The dependency
        // EDGE survives:
        // DependsOn committed it before the wave, and the library does not silently
        // discard a caller's topology change. Every subsequent wave therefore re-attempts
        // the conflicting attach and throws — including a refresh of the rejected node
        // itself, which reaches the graph through its parent's subscription. So a
        // conflicting late attach leaves a graph you cannot use until the wiring is
        // fixed. Failing loudly on a hard wiring error is defensible; failing loudly
        // forever is a sharp edge, and it is recorded here rather than discovered.
        // The edge is asymmetric, which is worth pinning precisely rather than describing
        // loosely. RefreshAll walks the existing snapshot and does NOT reconcile
        // topology, so it is unaffected...
        second.RefreshAll();

        // ...and refreshing the rejected node is also fine, because the ordering fix
        // means it was never subscribed to the graph that refused it — so its refresh
        // drives only the graph that accepted it.
        conflicted.Refresh();

        // ...but any propagation that DOES reconcile topology re-attempts the attach and
        // throws. Refreshing the second graph's own root is the reachable case: the root
        // is subscribed, so its wave rebuilds topology, rediscovers the edge DependsOn
        // committed, and hits the same conflict. That is the poisoning — the graph is
        // unusable on its normal propagation path until the wiring is fixed.
        Assert.Throws<InvalidOperationException>(() => root.Refresh());
    }

    // ───────────────────────── §10d — scope ─────────────────────────

    [Fact]
    public void DefaultScopeIsLeaves_CompositesAreNotPolicied()
    {
        // OQ1 must not be resolved by accident: a composite gets nothing by default.
        var leaf = Leaf("leaf");
        var mid = HealthNode.Create("mid").DependsOn(leaf, Importance.Required);
        var root = HealthNode.Create("root").DependsOn(mid, Importance.Required);

        using var graph = HealthGraph.Create(root, new TemporalDefaults(Debounce));

        Assert.Equal(TemporalPolicyOrigin.GraphDefault, leaf.DebouncePolicy.Origin);
        Assert.Equal(TemporalPolicyOrigin.Unset, mid.DebouncePolicy.Origin);
        Assert.Equal(TemporalPolicyOrigin.Unset, root.DebouncePolicy.Origin);
    }

    [Fact]
    public void AppliesTo_NarrowsTheScope_ByTag()
    {
        var device = HealthNode.Create("device")
            .WithTags(new Dictionary<string, string> { ["kind"] = "device" })
            .WithHealthProbe(() => HealthEvaluation.Healthy);
        var other = Leaf("other");

        using var graph = HealthGraph.Create(
            HealthNode.Create("root").DependsOn(device, Importance.Required).DependsOn(other, Importance.Required),
            new TemporalDefaults(Debounce, AppliesTo: n => n.Tags.ContainsKey("kind")));

        Assert.Equal(TemporalPolicyOrigin.GraphDefault, device.DebouncePolicy.Origin);
        Assert.Equal(TemporalPolicyOrigin.Unset, other.DebouncePolicy.Origin);
    }

    [Fact]
    public void AppliesTo_CanWidenToComposites()
    {
        var leaf = Leaf("leaf");
        var root = HealthNode.Create("root").DependsOn(leaf, Importance.Required);

        using var graph = HealthGraph.Create(
            root, new TemporalDefaults(Debounce, AppliesTo: _ => true));

        Assert.Equal(TemporalPolicyOrigin.GraphDefault, root.DebouncePolicy.Origin);
    }

    [Fact]
    public void AThrowingPredicate_FailsTheAttach_AndNamesTheNode()
    {
        var leaf = Leaf("leaf");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            HealthGraph.Create(
                HealthNode.Create("root").DependsOn(leaf, Importance.Required),
                new TemporalDefaults(Debounce, AppliesTo: _ => throw new NotSupportedException("boom"))));

        // The predicate is asked about every attaching node (that is how scope is
        // decided), so which node it throws on is iteration order — the contract is
        // that the attach fails, names the node it was scoping, and preserves the cause.
        Assert.Contains("AppliesTo predicate threw while scoping node", ex.Message);
        Assert.Contains("§10d", ex.Message);
        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    [Fact]
    public void ThePredicateIsInvokedOncePerNodePerAttach_NotPerWave()
    {
        // A non-deterministic predicate must not be able to make policy flicker.
        var calls = 0;
        var leaf = Leaf("leaf");
        var root = HealthNode.Create("root").DependsOn(leaf, Importance.Required);

        using var graph = HealthGraph.Create(
            root,
            new TemporalDefaults(Debounce, AppliesTo: _ => { Interlocked.Increment(ref calls); return true; }));

        var afterAttach = Volatile.Read(ref calls);
        graph.RefreshAll();
        graph.RefreshAll();

        Assert.Equal(afterAttach, Volatile.Read(ref calls));
    }

    // ───────────────────────── §10e — defaults are node state ─────────────────────────

    [Fact]
    public void AnUndefaultedGraph_DoesNotClearADefaultAnotherGraphInstalled()
    {
        var shared = Leaf("shared");
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));

        using var b = HealthGraph.Create(HealthNode.Create("b").DependsOn(shared, Importance.Required));

        Assert.Equal(Debounce, shared.DebouncePolicy.Effective);
        Assert.Null(b.Defaults);
        Assert.True(b.HasTemporalNodes); // "no defaults" != "no policies"
    }

    [Fact]
    public void TheUndefaultedCase_IsOrderIndependent()
    {
        // B-then-A reaches the same end state as A-then-B, since an undefaulted graph
        // neither fills nor clears. This is the claim §10e scopes; it deliberately
        // does NOT generalize to differing defaults (which throw — see above).
        var shared = Leaf("shared");
        using var b = HealthGraph.Create(HealthNode.Create("b").DependsOn(shared, Importance.Required));
        using var a = HealthGraph.Create(
            HealthNode.Create("a").DependsOn(shared, Importance.Required), new TemporalDefaults(Debounce));

        Assert.Equal(Debounce, shared.DebouncePolicy.Effective);
    }

    [Fact]
    public void ADefaultSurvivesDetach_AndStillConflictsAfterItsInstallerIsGone()
    {
        var shared = Leaf("shared");
        var rootA = HealthNode.Create("a").DependsOn(shared, Importance.Required);
        var a = HealthGraph.Create(rootA, new TemporalDefaults(Debounce));
        a.Dispose();

        Assert.Equal(Debounce, shared.DebouncePolicy.Effective); // survives disposal

        Assert.Throws<InvalidOperationException>(() =>
            HealthGraph.Create(
                HealthNode.Create("b").DependsOn(shared, Importance.Required), new TemporalDefaults(OtherDebounce)));
    }

    // ───────────────────────── §10f / diagnostics ─────────────────────────

    [Fact]
    public void ADefaultedGraph_WithoutAWaveSource_IsDiagnosed()
    {
        using var graph = HealthGraph.Create(
            HealthNode.Create("root").DependsOn(Leaf("leaf"), Importance.Required), new TemporalDefaults(Grace: Grace));

        string? warning = null;
        graph.WarnIfTemporalWithoutWaveSource(w => warning = w);

        Assert.NotNull(warning);
        Assert.True(graph.HasTemporalNodes);
    }

    [Fact]
    public void AGraceDefault_SuppressesUntilMarkLive_AndResolvesAtTheDeadline()
    {
        // The §10f safety property: resolution needs no owner cooperation. Mutation
        // check — a grace fold that keeps suppressing past the deadline turns this red.
        var clock = new FakeClock();
        var leaf = HealthNode.Create("leaf")
            .WithHealthProbe(() => HealthEvaluation.Unhealthy("never live"));
        var root = HealthNode.Create("root").DependsOn(leaf, Importance.Required);

        using var graph = HealthGraph.Create(root, clock.Read, new TemporalDefaults(Grace: Grace));

        clock.AdvanceSeconds(1);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unknown, leaf.Observe().Effective.Status);   // suppressed

        clock.AdvanceSeconds(Grace.Deadline.TotalSeconds + 1);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, leaf.Observe().Effective.Status); // gates on raw merits
    }

    [Fact]
    public void ADebounceDefault_DampsATransientBlip_AcrossTheWholeGraph()
    {
        // The end-to-end reason §10 exists: one bag, every leaf damped.
        var clock = new FakeClock();
        var up = true;
        var a = HealthNode.Create("a").WithHealthProbe(
            () => up ? HealthEvaluation.Healthy : HealthEvaluation.Unhealthy("blip"));
        var b = HealthNode.Create("b").WithHealthProbe(
            () => up ? HealthEvaluation.Healthy : HealthEvaluation.Unhealthy("blip"));
        var root = HealthNode.Create("root").DependsOn(a, Importance.Required).DependsOn(b, Importance.Required);

        using var graph = HealthGraph.Create(root, clock.Read, new TemporalDefaults(Debounce));

        up = false;
        clock.AdvanceSeconds(1);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);   // both held

        clock.AdvanceSeconds(Debounce.MinimumFaultDuration.TotalSeconds + 1);
        graph.RefreshAll();
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status); // both gate
    }

    // ───────────────────────── validation ─────────────────────────

    [Fact]
    public void NegativeDefaultDurations_AreRejectedAtCreate()
    {
        var root = HealthNode.Create("root");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HealthGraph.Create(root, new TemporalDefaults(new DebounceOptions(TimeSpan.FromSeconds(-1)))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HealthGraph.Create(root, new TemporalDefaults(Grace: new GraceOptions(TimeSpan.FromSeconds(-1)))));
    }

    [Fact]
    public void NullDefaults_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HealthGraph.Create(HealthNode.Create("root"), (TemporalDefaults)null!));
    }

    // ───────────────────────── §10c — concurrency ─────────────────────────

    [Fact]
    public void SimultaneousAttachOfASharedNode_ByGraphsWithDifferentDefaults_ThrowsExactlyOnce()
    {
        // The race the per-graph _topologyLock does not serialize: two graphs attaching
        // one node take different locks. A check-then-act lets both observe an Unset
        // slot and both materialize — a coin-flip winner and NO exception in exactly
        // the disagreeing case §10c exists to catch.
        //
        // The interleave is forced rather than raced: AppliesTo runs immediately before
        // node.MaterializeDefaults, so a two-party barrier there releases both threads
        // into the CAS at the same instant. (A sleep-free timing loop was tried first
        // and was flaky in both directions — a test that only sometimes exercises the
        // window is not evidence about the window.) Using the predicate as a barrier
        // deliberately violates its purity contract; that is legitimate here precisely
        // because the contract is what the production path relies on and the test does
        // not.
        //
        // Mutation check: replacing the CAS loop in MaterializeDefaults with a plain
        // read-then-write makes this go red (0 exceptions).
        var shared = Leaf("shared");
        var rootA = HealthNode.Create("a").DependsOn(shared, Importance.Required);
        var rootB = HealthNode.Create("b").DependsOn(shared, Importance.Required);

        using var barrier = new Barrier(2);
        bool Gate(HealthNode n)
        {
            if (ReferenceEquals(n, shared))
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            return n.Dependencies.Count == 0;
        }

        var exceptions = 0;
        Exception? unexpected = null;
        HealthGraph? ga = null, gb = null;

        void Attach(HealthNode root, DebounceOptions options, ref HealthGraph? sink)
        {
            try { sink = HealthGraph.Create(root, new TemporalDefaults(options, AppliesTo: Gate)); }
            catch (InvalidOperationException) { Interlocked.Increment(ref exceptions); }
            // Never let an unexpected exception escape a raw Thread: that tears down the
            // test host and silently drops the rest of the run.
            catch (Exception ex) { Interlocked.CompareExchange(ref unexpected, ex, null); }
        }

        var t1 = new Thread(() => Attach(rootA, Debounce, ref ga));
        var t2 = new Thread(() => Attach(rootB, OtherDebounce, ref gb));
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();

        ga?.Dispose();
        gb?.Dispose();

        Assert.Null(Volatile.Read(ref unexpected));

        // Exactly one graph wins; which one is unspecified and does not matter. What is
        // guaranteed is that the disagreement is never silent.
        Assert.Equal(1, Volatile.Read(ref exceptions));
        Assert.Equal(TemporalPolicyOrigin.GraphDefault, shared.DebouncePolicy.Origin);
    }

    [Fact]
    public void TheLeasedBitIsReadFromTheSwappedSet_NotFromTheLeaseField()
    {
        // §7's exclusion made structural (§10c). The deterministic falsification of
        // "the leased bit rides inside the CAS'd record": WithHealthProbe detaches a
        // lease by nulling _lease, but IsLeased stays latched in the set. An
        // implementation that consulted _lease instead of the set would materialize a
        // default into a leased node here — this test goes red for that mutation,
        // where the concurrency stress below does not (its race window is too narrow
        // to hit reliably, which is why both exist).
        var node = HealthNode.Create("node");
        node.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30)));
        node.WithHealthProbe(() => HealthEvaluation.Healthy);   // detaches the lease

        using var graph = HealthGraph.Create(
            HealthNode.Create("root").DependsOn(node, Importance.Required),
            new TemporalDefaults(Debounce));

        Assert.True(node.IsLeased);
        Assert.Equal(TemporalPolicyOrigin.Unset, node.DebouncePolicy.Origin);
    }

    [Fact]
    public void ConcurrentLeaseAndDefaultMaterialization_NeverLeavesTheNodeLeasedAndPolicied()
    {
        // A stress check on the same invariant. Honest limitation: unlike the test
        // above and the attach-conflict test below, this one does NOT reliably go red
        // for a leased-bit-outside-the-swap mutation — the window between the CAS and
        // the _lease assignment is too narrow to hit. It guards against gross
        // regressions, not subtle ones.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var shared = Leaf("shared");
            var root = HealthNode.Create("root").DependsOn(shared, Importance.Required);
            var start = new ManualResetEventSlim();
            Exception? unexpected = null;

            var t1 = new Thread(() =>
            {
                start.Wait();
                try { shared.Lease(new HealthLeaseOptions(TimeSpan.FromSeconds(30))); }
                catch (InvalidOperationException) { /* lost the race — legal */ }
                catch (Exception ex) { Interlocked.CompareExchange(ref unexpected, ex, null); }
            });
            HealthGraph? graph = null;
            var t2 = new Thread(() =>
            {
                start.Wait();
                try { graph = HealthGraph.Create(root, new TemporalDefaults(Debounce)); }
                catch (InvalidOperationException) { /* legal */ }
                catch (Exception ex) { Interlocked.CompareExchange(ref unexpected, ex, null); }
            });

            t1.Start();
            t2.Start();
            start.Set();
            t1.Join();
            t2.Join();
            graph?.Dispose();

            Assert.Null(Volatile.Read(ref unexpected));

            var leased = shared.IsLeased;
            var policied = shared.DebouncePolicy.Effective is not null;
            Assert.False(leased && policied,
                "a node was left both leased and policied — ADR-011 §7 is not structural");
        }
    }

    private sealed class FakeClock
    {
        private long _ticks;
        public long Read() => Volatile.Read(ref _ticks);
        public void AdvanceSeconds(double seconds)
            => Interlocked.Add(ref _ticks, (long)(seconds * Stopwatch.Frequency));
    }
}
