namespace Prognosis.Diagnostics;

/// <summary>
/// A node that is currently gating the root of a <see cref="HealthTreeSnapshot"/>
/// at its effective status — one on a determining (arg-worst) path whose
/// importance-mapped contribution equals the root status. This is the structured,
/// multi-culprit replacement for the single worst-path reason carried by
/// <see cref="HealthReport"/>. See ADR-007.
/// <para>
/// Usually a graph leaf, but a composite that carries its own failing probe (a node
/// with both a health probe and dependencies) can gate the rollup through its own
/// intrinsic status — the gating unit is a node by name, not necessarily a leaf.
/// </para>
/// </summary>
/// <param name="Name">The gating node's name.</param>
/// <param name="Status">The node's own evaluated status in the snapshot.</param>
/// <param name="Reason">The node's reason string, if any.</param>
public sealed record Contributor(string Name, HealthStatus Status, string? Reason);

/// <summary>
/// A single repair in a <see cref="HealthGraphAnalysis.MinimalHealingSet"/> — a
/// node that must be restored to <see cref="HealthStatus.Healthy"/> (at its own
/// probe) to move the root toward the requested target. Usually a leaf, but a
/// composite whose own intrinsic status gates the rollup is repaired at the composite.
/// </summary>
/// <param name="Name">The node to repair.</param>
/// <param name="CurrentStatus">The node's current status in the snapshot.</param>
/// <param name="Quorum">
/// Non-<see langword="null"/> when this repair is one arbitrary choice within a
/// <see cref="Importance.Resilient"/> quorum — i.e. any one of several siblings
/// could have been repaired instead. See <see cref="QuorumChoice"/>.
/// </param>
public sealed record HealingStep(
    string Name,
    HealthStatus CurrentStatus,
    QuorumChoice? Quorum = null);

/// <summary>
/// Marks a <see cref="HealingStep"/> that exists only to satisfy a
/// <see cref="Importance.Resilient"/> quorum. Because a resilient group only
/// needs <see cref="Required"/> of its members healthy, the minimal healing set
/// is not unique: any one of <see cref="Candidates"/> could have been chosen.
/// <see cref="HealthGraphAnalysis"/> returns one such set and records the choice
/// here rather than pretending the answer is unique (ADR-007). Callers wanting
/// the full determining frontier use <see cref="HealthGraphAnalysis.Contributors"/>.
/// </summary>
/// <param name="Parent">The resilient parent whose quorum this repair satisfies.</param>
/// <param name="Required">
/// How many of <see cref="Candidates"/> must be brought healthy (a resilient
/// quorum needs one).
/// </param>
/// <param name="Candidates">
/// The names of the resilient siblings eligible to be repaired for the quorum.
/// The returned set restores one of them; any other would be equally minimal.
/// </param>
public sealed record QuorumChoice(
    string Parent,
    int Required,
    IReadOnlyList<string> Candidates);

/// <summary>
/// A pure, non-mutating diagnostic query layer over a <see cref="HealthTreeSnapshot"/>
/// (ADR-007, docs/adr/007-counterfactual-contributor-analysis.md). Every method
/// reasons about the captured snapshot value — it never touches live
/// <see cref="HealthNode"/> state, allocates no graph, and raises no events.
/// <para>
/// Node identity is by <see cref="HealthTreeSnapshot.Name"/>. The snapshot already
/// flattens cycles and diamonds into repeated leaves (see
/// <c>HealthNode.BuildTreeSnapshot</c>), so a node reachable via multiple paths is
/// treated as one entity: overrides and results are keyed by name, and every
/// occurrence moves together.
/// </para>
/// <para>
/// The re-fold shares its per-importance mapping with the live rollup through
/// <see cref="HealthContribution.Of"/>, so a counterfactual can never disagree with
/// production aggregation — including ADR-006's guarantee that an
/// <see cref="HealthStatus.Unknown"/> child is strictly non-gating.
/// </para>
/// <para>
/// <b>Intrinsic reconstruction and its one limitation.</b> A
/// <see cref="HealthTreeSnapshot"/> records only each node's <em>effective</em> status
/// (per ADR-002 there is a single, effective cache — no separate intrinsic field). The
/// analysis reconstructs each node's intrinsic status as the residual its recorded
/// children cannot explain. This is <em>exact</em> whenever a node's own intrinsic
/// status is <see cref="HealthStatus.Healthy"/> (the ADR-004 composite model) or is
/// strictly worse than every child's contribution (an unmasked probe failure — recovered
/// and reported by name). The only blind spot is a node whose own probe failure is
/// <em>masked</em> by an equal-or-worse child contribution: the two are indistinguishable
/// in the snapshot, so the analysis attributes that status to the children. Repairing the
/// children then appears to heal the node even though its own probe is still failing.
/// This is the theoretical limit of snapshot-only analysis, not a fold discrepancy.
/// </para>
/// </summary>
public static class HealthGraphAnalysis
{
    /// <summary>
    /// Re-folds <paramref name="tree"/> bottom-up with the nodes named in
    /// <paramref name="overrides"/> forced to the given statuses, and returns the
    /// resulting root status. Pure — <paramref name="tree"/> is unchanged.
    /// <para>
    /// A forced node contributes its override value regardless of its own subtree;
    /// every other node is re-aggregated from its dependencies exactly as the live
    /// graph would. Overrides are keyed by name, so a node reachable by multiple
    /// paths moves together.
    /// </para>
    /// </summary>
    /// <param name="tree">The snapshot to re-fold. Not mutated.</param>
    /// <param name="overrides">Node name → forced status. May be empty.</param>
    /// <returns>The root status under the hypothetical.</returns>
    public static HealthStatus WhatIf(
        HealthTreeSnapshot tree,
        IReadOnlyDictionary<string, HealthStatus> overrides)
    {
        if (tree is null) throw new ArgumentNullException(nameof(tree));
        if (overrides is null) throw new ArgumentNullException(nameof(overrides));

        var model = new FoldModel(tree);
        return model.Evaluate(tree.Name, overrides);
    }

    /// <summary>
    /// Returns the nodes currently gating the root at its effective status — those on a
    /// determining (arg-worst) path whose importance-mapped contribution equals the root
    /// status. Almost always leaves, but a composite that gates via its own failing probe
    /// is reported by name too. Nodes that are unhealthy but capped below the root status
    /// (e.g. an <see cref="Importance.Important"/> leaf under an
    /// <see cref="HealthStatus.Unhealthy"/> root) are excluded: they are not why the
    /// root is where it is. Returns an empty list when the root is
    /// <see cref="HealthStatus.Healthy"/>.
    /// </summary>
    /// <param name="tree">The snapshot to analyze.</param>
    public static IReadOnlyList<Contributor> Contributors(HealthTreeSnapshot tree)
    {
        if (tree is null) throw new ArgumentNullException(nameof(tree));

        if (tree.Status == HealthStatus.Healthy)
            return Array.Empty<Contributor>();

        var model = new FoldModel(tree);
        var found = new Dictionary<string, Contributor>(StringComparer.Ordinal);
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        model.CollectContributors(tree.Name, found, onPath);

        return found.Values
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns the smallest set of node repairs (each → <see cref="HealthStatus.Healthy"/>)
    /// that would bring the root to <paramref name="target"/> or better. The fold is
    /// monotone, so the problem is well-posed. Repairs are almost always leaves; a
    /// composite whose own probe gates the rollup is repaired at the composite.
    /// <para>
    /// With <see cref="Importance.Required"/> / <see cref="Importance.Important"/> /
    /// <see cref="Importance.Optional"/> edges only, the set is unique: to drop the
    /// root below <see cref="HealthStatus.Unhealthy"/> you fix exactly the nodes on a
    /// <see cref="Importance.Required"/> path that are <see cref="HealthStatus.Unhealthy"/>;
    /// <see cref="Importance.Important"/>-capped and <see cref="Importance.Optional"/>
    /// nodes are provably excluded. <see cref="Importance.Resilient"/> quorums
    /// introduce genuine choice (fix any one of several siblings); this method returns
    /// one minimal set and marks the choice on the affected <see cref="HealingStep"/>s.
    /// </para>
    /// Returns an empty list when the root is already <paramref name="target"/> or better.
    /// </summary>
    /// <param name="tree">The snapshot to analyze.</param>
    /// <param name="target">The status the root should reach (or improve past).</param>
    public static IReadOnlyList<HealingStep> MinimalHealingSet(
        HealthTreeSnapshot tree,
        HealthStatus target)
    {
        if (tree is null) throw new ArgumentNullException(nameof(tree));

        if (IsAtLeastAsGoodAs(tree.Status, target))
            return Array.Empty<HealingStep>();

        var model = new FoldModel(tree);
        var repairs = model.Heal(tree.Name, target, new HashSet<(string, HealthStatus)>());

        return Prune(model, tree.Name, repairs, target)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Drops repairs the rest of the set already covers, so no proper subset of the
    /// result reaches <paramref name="target"/>.
    /// <para>
    /// <see cref="FoldModel.Heal"/> composes each subtree's minimum independently, which
    /// is exact for <see cref="Importance.Required"/> / <see cref="Importance.Important"/>
    /// / <see cref="Importance.Advisory"/> trees but not across a shared node: a repair
    /// chosen because some other path <em>requires</em> that node can incidentally satisfy
    /// a <see cref="Importance.Resilient"/> quorum elsewhere, at which point the repair
    /// Heal picked for the quorum is dead weight. The interaction is global, so it is
    /// settled globally — by asking the same re-fold whether each step is still load
    /// bearing once the others are applied.
    /// </para>
    /// <para>
    /// Ordinal order, and a dropped step is restored the moment the fold says it was
    /// needed, so the result is deterministic and never loses soundness.
    /// </para>
    /// </summary>
    private static IEnumerable<HealingStep> Prune(
        FoldModel model,
        string root,
        Dictionary<string, HealingStep> repairs,
        HealthStatus target)
    {
        if (repairs.Count < 2)
            return repairs.Values;

        var kept = new Dictionary<string, HealingStep>(repairs, StringComparer.Ordinal);

        foreach (var name in repairs.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            kept.Remove(name);

            var overrides = new Dictionary<string, HealthStatus>(kept.Count, StringComparer.Ordinal);
            foreach (var remaining in kept.Keys)
                overrides[remaining] = HealthStatus.Healthy;

            if (!IsAtLeastAsGoodAs(model.Evaluate(root, overrides), target))
                kept[name] = repairs[name];
        }

        return kept.Values;
    }

    /// <summary>
    /// Recombines a flat, atomically-built <see cref="HealthReport"/> with a
    /// <see cref="HealthTopology"/> into the <see cref="HealthTreeSnapshot"/> the
    /// rest of this class consumes. Pure — touches no live graph state — and the
    /// safe reactive path to this layer (ADR-009): the report arrives per beat from
    /// <c>HealthGraph.StatusChanged</c>, the topology per structural change from
    /// <c>TopologyChange.Topology</c>, and for a quiescent graph
    /// <c>BuildTreeSnapshot(graph.GetReport(), graph.GetTopology())</c> is
    /// structurally equal to <c>graph.CreateTreeSnapshot()</c>.
    /// <para>
    /// <b>Contract: a projection of the report onto the topology.</b> The output
    /// covers exactly the topology's reachable set, walked pre-order in edge-list
    /// order with repeated names emitted as childless leaves — the same
    /// cycle/diamond flattening as <c>HealthNode.BuildTreeSnapshot</c>. Total in
    /// both mismatch directions: a topology name missing from the report (node
    /// removed after the topology was captured) is synthesized at
    /// <see cref="HealthStatus.Unknown"/> with an explanatory reason — ADR-006
    /// guarantees it can never gate an ancestor, and ADR-009's complete
    /// <c>TopologyChanged</c> signal guarantees the staleness resolves on the next
    /// topology emission. A report name missing from the topology (node added
    /// after capture) is outside the projection — it has no edges here and cannot
    /// affect a fold over this topology; use <see cref="FindOrphans"/> to detect
    /// such nodes.
    /// </para>
    /// </summary>
    /// <param name="report">Per-node statuses, keyed by name. Not mutated.</param>
    /// <param name="topology">The structure to project onto. Not mutated.</param>
    public static HealthTreeSnapshot BuildTreeSnapshot(HealthReport report, HealthTopology topology)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (topology is null) throw new ArgumentNullException(nameof(topology));

        var statuses = new Dictionary<string, HealthSnapshot>(
            report.Nodes.Count, StringComparer.Ordinal);
        foreach (var node in report.Nodes)
            statuses[node.Name] = node;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        return BuildTreeNode(topology.Root, topology, statuses, visited);
    }

    private static HealthTreeSnapshot BuildTreeNode(
        string name,
        HealthTopology topology,
        Dictionary<string, HealthSnapshot> statuses,
        HashSet<string> visited)
    {
        HealthStatus status;
        string? reason;
        IReadOnlyDictionary<string, string>? tags;

        if (statuses.TryGetValue(name, out var snapshot))
        {
            status = snapshot.Status;
            reason = snapshot.Reason;
            tags = snapshot.Tags;
        }
        else
        {
            // Topology predates the report (the node was removed in between).
            // Unknown is strictly non-gating (ADR-006) and transient here by
            // construction (ADR-009 §5): the next TopologyChanged resolves it.
            status = HealthStatus.Unknown;
            reason = $"'{name}' is in the supplied topology but not in the report; "
                + "topology predates report";
            tags = null;
        }

        if (!visited.Add(name))
        {
            // Already visited — emit a leaf to break cycles / diamonds, matching
            // HealthNode.BuildTreeSnapshot.
            return new HealthTreeSnapshot(
                name, status, reason, Array.Empty<HealthTreeDependency>(), tags);
        }

        var edges = topology.Edges.TryGetValue(name, out var found)
            ? found
            : Array.Empty<HealthTopologyEdge>();

        var children = new List<HealthTreeDependency>(edges.Count);
        foreach (var edge in edges)
        {
            children.Add(new HealthTreeDependency(
                edge.Importance,
                BuildTreeNode(edge.Name, topology, statuses, visited)));
        }

        return new HealthTreeSnapshot(name, status, reason, children, tags);
    }

    /// <summary>
    /// Returns the report nodes that <see cref="BuildTreeSnapshot"/> cannot place — those
    /// whose names are not reachable from <paramref name="topology"/>'s root.
    /// Non-empty when the report postdates the topology (a node was added in
    /// between); such nodes are outside the projection by contract (ADR-009) and
    /// transient once the consumer refreshes its topology on the next
    /// <c>TopologyChanged</c>. Empty when report and topology are aligned.
    /// </summary>
    /// <param name="report">Per-node statuses. Not mutated.</param>
    /// <param name="topology">The structure defining the projection. Not mutated.</param>
    public static IReadOnlyList<HealthSnapshot> FindOrphans(
        HealthReport report, HealthTopology topology)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (topology is null) throw new ArgumentNullException(nameof(topology));

        // The projection's domain is reachability from the root, not Edges keys —
        // an edge naming a node with no Edges entry is still enriched (as a leaf).
        var reachable = new HashSet<string>(StringComparer.Ordinal) { topology.Root };
        var stack = new Stack<string>();
        stack.Push(topology.Root);

        while (stack.Count > 0)
        {
            if (!topology.Edges.TryGetValue(stack.Pop(), out var edges))
                continue;

            foreach (var edge in edges)
            {
                if (reachable.Add(edge.Name))
                    stack.Push(edge.Name);
            }
        }

        var orphans = new List<HealthSnapshot>();
        foreach (var node in report.Nodes)
        {
            if (!reachable.Contains(node.Name))
                orphans.Add(node);
        }

        return orphans;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="status"/> is at least as good as
    /// (equal to or better than, i.e. not worse than) <paramref name="target"/> in rank.
    /// </summary>
    private static bool IsAtLeastAsGoodAs(HealthStatus status, HealthStatus target)
        => !status.IsWorseThan(target);

    /// <summary>
    /// The by-name model backing the analysis. Indexes every node in the snapshot to
    /// its richest occurrence (the one carrying the full subtree, since diamonds and
    /// cycles are unrolled into stub leaves), and reconstructs each node's intrinsic
    /// status as the residual the recorded fold cannot attribute to its children.
    /// </summary>
    private sealed class FoldModel
    {
        private readonly Dictionary<string, HealthTreeSnapshot> _definitions =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, HealthStatus> _intrinsic =
            new(StringComparer.Ordinal);

        public FoldModel(HealthTreeSnapshot root) => Index(root);

        private void Index(HealthTreeSnapshot node)
        {
            // Prefer the occurrence that carries the actual subtree. Cycle/diamond
            // stubs share a name but have no dependencies.
            if (!_definitions.TryGetValue(node.Name, out var existing)
                || (existing.Dependencies.Count == 0 && node.Dependencies.Count > 0))
            {
                _definitions[node.Name] = node;
            }

            foreach (var dep in node.Dependencies)
                Index(dep.Node);
        }

        /// <summary>
        /// The node's reconstructed intrinsic status: the recorded effective status
        /// if it is strictly worse than everything the recorded children contribute
        /// (so it can only have come from the node itself), otherwise
        /// <see cref="HealthStatus.Healthy"/> — attributing the status to the children,
        /// which is what makes it responsive to counterfactuals on those children.
        /// Computed from the original recorded snapshot; a fixed floor for the re-fold.
        /// </summary>
        private HealthStatus IntrinsicOf(string name)
        {
            if (_intrinsic.TryGetValue(name, out var cached))
                return cached;

            var def = _definitions[name];
            var childContribWorst = HealthStatus.Healthy;
            var hasHealthyResilient = HasHealthyResilient(def.Dependencies, dep => dep.Node.Status);

            foreach (var dep in def.Dependencies)
            {
                var contribution = HealthContribution.Of(
                    dep.Importance, dep.Node.Status, hasHealthyResilient);
                childContribWorst = HealthStatusExtensions.Worst(childContribWorst, contribution);
            }

            var intrinsic = def.Status.IsWorseThan(childContribWorst)
                ? def.Status
                : HealthStatus.Healthy;

            _intrinsic[name] = intrinsic;
            return intrinsic;
        }

        /// <summary>
        /// Re-folds the node identified by <paramref name="name"/> under
        /// <paramref name="overrides"/>. Memoized per call; cycles break to the
        /// recorded status, matching how <c>BuildTreeSnapshot</c> unrolls them.
        /// </summary>
        public HealthStatus Evaluate(
            string name, IReadOnlyDictionary<string, HealthStatus> overrides)
        {
            return Eval(name, overrides,
                new Dictionary<string, HealthStatus>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal));
        }

        private HealthStatus Eval(
            string name,
            IReadOnlyDictionary<string, HealthStatus> overrides,
            Dictionary<string, HealthStatus> memo,
            HashSet<string> onStack)
        {
            if (overrides.TryGetValue(name, out var forced))
                return forced;
            if (memo.TryGetValue(name, out var cached))
                return cached;

            var def = _definitions[name];
            if (!onStack.Add(name))
                return def.Status; // cycle break — the unrolled stub's recorded status

            var childStatuses = new HealthStatus[def.Dependencies.Count];
            for (var i = 0; i < def.Dependencies.Count; i++)
                childStatuses[i] = Eval(def.Dependencies[i].Node.Name, overrides, memo, onStack);

            var hasHealthyResilient = false;
            for (var i = 0; i < def.Dependencies.Count; i++)
            {
                if (def.Dependencies[i].Importance == Importance.Resilient
                    && childStatuses[i] == HealthStatus.Healthy)
                {
                    hasHealthyResilient = true;
                    break;
                }
            }

            var effective = IntrinsicOf(name);
            for (var i = 0; i < def.Dependencies.Count; i++)
            {
                var contribution = HealthContribution.Of(
                    def.Dependencies[i].Importance, childStatuses[i], hasHealthyResilient);
                effective = HealthStatusExtensions.Worst(effective, contribution);
            }

            onStack.Remove(name);
            memo[name] = effective;
            return effective;
        }

        /// <summary>
        /// Walks determining (arg-worst) edges from <paramref name="name"/>, collecting
        /// every leaf whose contribution reaches the root as the root status.
        /// </summary>
        public void CollectContributors(
            string name,
            Dictionary<string, Contributor> found,
            HashSet<string> onPath)
        {
            var def = _definitions[name];

            // The node gates through its own status when that status is its reconstructed
            // intrinsic — a graph leaf (intrinsic == status), or a composite whose own probe
            // (not any dependency) is a reason it sits where it does. Either way the frontier
            // cause is the node itself, reported by name.
            if (IntrinsicOf(name) == def.Status)
                found[def.Name] = new Contributor(def.Name, def.Status, def.Reason);

            var hasHealthyResilient = HasHealthyResilient(def.Dependencies, dep => dep.Node.Status);

            foreach (var dep in def.Dependencies)
            {
                var contribution = HealthContribution.Of(
                    dep.Importance, dep.Node.Status, hasHealthyResilient);

                // The child is arg-worst iff its contribution is exactly what makes
                // the parent's recorded status what it is.
                if (contribution != def.Status)
                    continue;

                if (!onPath.Add(dep.Node.Name))
                    continue; // guard cycles

                CollectContributors(dep.Node.Name, found, onPath);
                onPath.Remove(dep.Node.Name);
            }
        }

        /// <summary>
        /// The minimal set of leaf repairs (leaf name → <see cref="HealingStep"/>) that
        /// brings the node identified by <paramref name="name"/> to <paramref name="limit"/>
        /// or better. See <see cref="MinimalHealingSet"/> for the contract.
        /// </summary>
        public Dictionary<string, HealingStep> Heal(
            string name,
            HealthStatus limit,
            HashSet<(string, HealthStatus)> stack)
        {
            var result = new Dictionary<string, HealingStep>(StringComparer.Ordinal);
            var def = _definitions[name];

            if (IsAtLeastAsGoodAs(def.Status, limit))
                return result; // subtree already satisfies the limit — monotone, no repairs

            // A node whose own reconstructed intrinsic exceeds the limit can only be fixed
            // at the node itself — no dependency repair reaches it. Covers graph leaves
            // (intrinsic == status) and composites carrying their own failing probe.
            if (IntrinsicOf(name).IsWorseThan(limit))
                result[def.Name] = new HealingStep(def.Name, def.Status);

            if (def.Dependencies.Count == 0)
                return result; // a leaf has nothing below it to repair

            if (!stack.Add((name, limit)))
                return result; // cycle — no additional repair beyond the recorded floor

            var resilient = new List<HealthTreeDependency>();
            foreach (var dep in def.Dependencies)
            {
                switch (dep.Importance)
                {
                    case Importance.Required:
                        Merge(result, Heal(dep.Node.Name, limit, stack));
                        break;

                    case Importance.Important:
                        // Important caps Unhealthy at Degraded, so it only needs healing
                        // when the target is stricter than Degraded (Healthy / Unknown).
                        if (HealthStatus.Degraded.IsWorseThan(limit))
                            Merge(result, Heal(dep.Node.Name, limit, stack));
                        break;

                    case Importance.Advisory:
                        // Advisory caps Unhealthy at Degraded exactly like Important, so
                        // like Important it needs healing only for a target stricter than
                        // Degraded. But it also ABSORBS Unknown (ADR-008), so the child's
                        // own target is Unknown rather than the limit: an Unknown child
                        // already contributes Healthy, which satisfies any limit. Passing
                        // `limit` through here would demand repairs below an Advisory edge
                        // that the caller does not need — sound, but not minimal, which is
                        // the one thing this method promises.
                        if (HealthStatus.Degraded.IsWorseThan(limit))
                            Merge(result, Heal(dep.Node.Name, HealthStatus.Unknown, stack));
                        break;

                    case Importance.Optional:
                        break; // never contributes; never healed

                    case Importance.Resilient:
                        resilient.Add(dep);
                        break;

                    // No silent default (ADR-008), matching HealthContribution.Of and
                    // HealthStatusExtensions.Rank. Falling through this switch makes an
                    // edge behave exactly like Optional — never healed — so an unmapped
                    // member would silently drop repairs from the "minimal" set and the
                    // caller would apply them and not reach the target. That is how
                    // Advisory itself was missed until a generated topology caught it.
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(dep.Importance),
                            dep.Importance,
                            $"Unhandled {nameof(Importance)} value. A new member was added "
                                + $"without updating {nameof(HealthGraphAnalysis)}.{nameof(Heal)}; "
                                + "see ADR-008.");
                }
            }

            Merge(result, HealResilientGroup(resilient, limit, name, stack));

            stack.Remove((name, limit));
            return result;
        }

        private Dictionary<string, HealingStep> HealResilientGroup(
            List<HealthTreeDependency> resilient,
            HealthStatus limit,
            string parentName,
            HashSet<(string, HealthStatus)> stack)
        {
            var result = new Dictionary<string, HealingStep>(StringComparer.Ordinal);
            if (resilient.Count == 0)
                return result;

            if (limit == HealthStatus.Unhealthy)
                return result; // any contribution is already <= Unhealthy

            if (limit == HealthStatus.Degraded)
            {
                var anyUnhealthy = resilient.Any(
                    d => _definitions[d.Node.Name].Status == HealthStatus.Unhealthy);
                var anyHealthy = resilient.Any(
                    d => _definitions[d.Node.Name].Status == HealthStatus.Healthy);

                // With an existing healthy sibling, or no unhealthy sibling at all, the
                // group already caps to Degraded.
                if (!anyUnhealthy || anyHealthy)
                    return result;

                // No quorum yet. There are two independent ways to bring the group's worst
                // contribution down to Degraded, and neither dominates the other:
                //
                //   (a) Establish the quorum — restore ONE sibling all the way to Healthy,
                //       after which every unhealthy sibling caps at Degraded and the rest
                //       cost nothing.
                //   (b) Skip the quorum — bring every sibling to Degraded or better on its
                //       own, which the cap then leaves alone.
                //
                // (a) is one deep repair, (b) is several shallow ones; which is smaller
                // depends on the subtrees. Costing only (a) returns a set that heals but is
                // not minimal — e.g. a single resilient child over {Unhealthy, Unknown}
                // leaves, where (a) repairs both leaves to make the child Healthy and (b)
                // repairs just the unhealthy one to make it Unknown.
                var candidates = resilient
                    .Where(d => _definitions[d.Node.Name].Status != HealthStatus.Healthy)
                    .OrderBy(d => d.Node.Name, StringComparer.Ordinal)
                    .ToList();

                Dictionary<string, HealingStep>? viaQuorum = null;
                foreach (var candidate in candidates)
                {
                    var set = Heal(candidate.Node.Name, HealthStatus.Healthy, stack);
                    if (viaQuorum is null || set.Count < viaQuorum.Count)
                        viaQuorum = set;
                }

                var viaSiblings = new Dictionary<string, HealingStep>(StringComparer.Ordinal);
                foreach (var dep in resilient)
                    Merge(viaSiblings, Heal(dep.Node.Name, HealthStatus.Degraded, stack));

                // Ties go to (b): it carries no quorum ambiguity, so it is the more useful
                // answer to hand an operator when both cost the same.
                if (viaQuorum is null || viaSiblings.Count <= viaQuorum.Count)
                    return viaSiblings;

                var quorum = new QuorumChoice(
                    parentName,
                    1,
                    candidates.Select(d => d.Node.Name).ToList());

                foreach (var kvp in viaQuorum)
                    result[kvp.Key] = kvp.Value with { Quorum = quorum };

                return result;
            }

            // Target stricter than Degraded (Healthy / Unknown): the quorum cap is
            // insufficient, so every resilient child must reach the limit itself.
            foreach (var dep in resilient)
                Merge(result, Heal(dep.Node.Name, limit, stack));

            return result;
        }

        private static bool HasHealthyResilient(
            IReadOnlyList<HealthTreeDependency> deps,
            Func<HealthTreeDependency, HealthStatus> statusOf)
        {
            foreach (var dep in deps)
            {
                if (dep.Importance == Importance.Resilient
                    && statusOf(dep) == HealthStatus.Healthy)
                    return true;
            }
            return false;
        }

        private static void Merge(
            Dictionary<string, HealingStep> into,
            Dictionary<string, HealingStep> from)
        {
            foreach (var kvp in from)
            {
                // A repair carrying a quorum choice is a strictly weaker requirement
                // than an unconditional one; never let it overwrite the latter.
                if (into.TryGetValue(kvp.Key, out var existing)
                    && existing.Quorum is null)
                    continue;
                into[kvp.Key] = kvp.Value;
            }
        }
    }
}
