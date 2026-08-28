---
id: ADR-009
status: proposed
amends:
  - ADR-001
governs:
  - HealthGraph.cs
  - HealthTopology.cs
  - TopologyChange.cs
  - Prognosis.Diagnostics/HealthGraphAnalysis.cs
  - Prognosis.Tests/HealthGraphTests.cs
  - Prognosis.Tests/HealthGraphAnalysisTests.cs
relates:
  - ADR-001
  - ADR-002
  - ADR-005
  - ADR-006
  - ADR-007
  - ADR-008
---

# ADR-009: Topology Is a First-Class Cached Artifact; the Tree Becomes a Projection of the Report

**Status:** Proposed
**Date:** 2026-07-29
**Drivers:** The topology-projection gap. ADR-007 added a pure query layer (`WhatIf` / `Contributors` / `MinimalHealingSet`) keyed on `HealthTreeSnapshot`, but no reactive entry point produces one — every stream yields `HealthReport`. The only path from a stream to the analysis layer is calling `CreateTreeSnapshot()` out of band from inside a `StatusChanged` handler, which races the propagation wave. Reviewing the proposed fix surfaced a second, prerequisite defect: `TopologyChanged` does not fire for every topology change, which silently breaks any consumer that caches topology.

## Context

### The gap

Every reactive surface — `HealthGraph.StatusChanged`, and `Prognosis.Reactive`'s `SelectHealthChanges`, `ForNodes`, both `PollHealthReport` overloads, `ObserveHealthReport` — emits `HealthReport`. `HealthTreeSnapshot`, the input type of the entire ADR-007 layer, has exactly one producer: `HealthGraph.CreateTreeSnapshot()`, an on-demand pull.

That pull is not safe from a subscriber:

- `RebuildReport()` runs inside `lock (_propagationLock)` (`HealthGraph.SerializedBubble`).
- `EmitStatusChanged(reportToEmit)` runs **after** the lock is released.
- `CreateTreeSnapshot()` takes **no lock**; `HealthNode.BuildTreeSnapshot` reads each node's `volatile _cachedEvaluation` directly.

Each individual read is atomic, so the failure is not a torn value but a torn *cross-node view*: a subscriber walking the tree while the next wave propagates sees a mix of pre- and post-wave statuses, disagreeing with the atomically-built report it was just handed. The window is widest during multi-fault churn — exactly when the analysis is wanted — and the failure is silent.

Compounding it, the `CreateTreeSnapshot()` doc comment claims it "evaluates the full graph." It evaluates nothing; it re-reads the same caches `RebuildReport()` reads. The comment makes the out-of-band capture look like a deliberate fresh evaluation, which is how it ends up in consumer code.

The insight driving the fix: a tree is **topology + statuses**, and the two change at completely different rates. Topology — the root name and, per node, its ordered child edges each carrying an `Importance` — changes only on structural mutation. Statuses are already carried, atomically and per beat, by `HealthReport.Nodes`. The tree is therefore recoverable by a pure function; nothing needs re-reading per beat.

### Prerequisite defect: `TopologyChanged` is an incomplete signal

The consumer recipe "hold a topology, refresh it on `TopologyChanged`" only works if every topology change raises the event. It does not. `HealthGraph.RefreshTopology` detects change by diffing the reachable **node set** (`fresh.SetEquals(current.Set)`). Three mutations change edges without changing the node set, and fire nothing:

| Mutation | Topology change | `TopologyChanged` today |
|---|---|---|
| `DependsOn` reaching a new node | node + edge added | fires |
| `RemoveDependency` orphaning a subgraph | nodes + edges removed | fires |
| `UpdateDependencyImportance` | edge `Importance` changes | **silent** |
| `RemoveDependency` of one diamond edge (node still reachable) | edge removed | **silent** |
| `ReplaceDependencies` preserving the reachable set | edges rewired | **silent** |

A consumer following the recipe holds a permanently stale topology after any of the silent three. An enrichment function would then decorate correct statuses onto wrong `Importance` edges, and `Contributors`/`WhatIf` would give confidently wrong answers — the exact silent-drift class ADR-007 §1 extracted `HealthContribution.Of` to prevent. Worse, a permanently stale topology makes the staleness placeholder (below) a **permanent** `Unknown`, which ADR-008 defines as a modelling defect. The totality contract is only sound if the refresh signal is complete, which is why both land in this one ADR.

### The totality question has two directions

`BuildTreeSnapshot` must be total. Topology and report are captured at (slightly) different times, so their name sets can disagree in either direction:

- **In the topology, absent from the report** — the node was *removed* after the topology was captured. The tree has a slot the report cannot fill.
- **In the report, absent from the topology** — the node was *added* after the topology was captured. The report has a status the tree has no edges for.

That issue posed only the first and mislabelled it as the "added" case; the second direction is the one where silent omission is structurally forced, so both must be decided here.

## Decision

Six parts. Parts 1–4 make topology a published artifact symmetric with the report (this is the amendment to ADR-001); parts 5–6 define the pure projection on top.

### 1. `HealthTopology` — structure only, no statuses (core)

```csharp
public sealed record HealthTopologyEdge(string Name, Importance Importance);

public sealed record HealthTopology(
    string Root,
    IReadOnlyDictionary<string, IReadOnlyList<HealthTopologyEdge>> Edges);
```

Per-node edge lists preserve `HealthNode.Dependencies` order — that order is load-bearing (see §6). Identity is by `Name`, matching ADR-007's snapshot contract. The types live in core beside `HealthTreeSnapshot`, consistent with ADR-007's layering (snapshot types in core, analysis in `Prognosis.Diagnostics`).

Records with collection members do not get structural equality for free; change detection uses a dedicated `HealthTopologyComparer`, following the `HealthReportComparer` precedent.

### 2. Topology is cached and rebuilt inside the wave

`HealthGraph` maintains a `volatile HealthTopology? _cachedTopology` beside `_cachedReport`:

- **Seeded** in the constructor, alongside the initial node snapshot.
- **Rebuilt** inside `lock (_propagationLock)` whenever a wave may have changed edges — the same place `RefreshTopology` runs today. The rebuild is an O(edges) walk, the same order as `RebuildReport`'s O(nodes) walk the wave already pays.
- **Published** via `GetTopology()`, which returns `_cachedTopology ?? RebuildTopology()` — the exact `GetReport()` pattern. (The issue sketched this as `CreateTopology()`; it is named `GetTopology()` because it is a cached read, not a construction. `Create*` in this API means "walk and build now.")

Because the cache is built under the propagation lock and read via a volatile field, consumers get a wave-coherent value with **no consumer-callable lock** — the rejected alternative ("lock `CreateTreeSnapshot()`") stays rejected, and its concern never arises for `GetTopology()`.

### 3. `TopologyChanged` fires on any structural change

`RefreshTopology`'s change test becomes structural: compare the rebuilt `HealthTopology` against the cached one with `HealthTopologyComparer`. The node-set diff is retained only to compute `Added`/`Removed` and to attach/detach `_bubbleStrategy` on membership changes. All five mutation rows in the table above now fire.

`TopologyChange` gains the new value:

```csharp
public sealed class TopologyChange
{
    public IReadOnlyList<HealthNode> Added { get; }    // may be empty
    public IReadOnlyList<HealthNode> Removed { get; }  // may be empty
    public HealthTopology Topology { get; }            // new — the post-change topology
}
```

Consumers never capture topology out of band: the event *hands them* the value to feed `BuildTreeSnapshot`. **Semantic widening, stated plainly:** the event now fires for importance-only and edge-only changes, with empty `Added`/`Removed`. The doc comment — which today promises "each time nodes are added to or removed" — is rewritten to "fires on any structural change to the graph (nodes, edges, or edge importance)." Acceptable at 0.x; existing subscribers that only read `Added`/`Removed` see extra events with empty lists, never wrong data.

### 4. Emission ordering is pinned: topology before status

Within a single propagation wave, **`TopologyChanged` (when the topology changed) is observed before the wave's `StatusChanged`.** Today `NotifyTopologyObservers` fires *inside* `_propagationLock` while `EmitStatusChanged` fires outside it; both move outside the lock, emitted in that order. This also removes the existing hazard of invoking consumer callbacks under the propagation lock.

This ordering is the coherence guarantee the projection rests on: a consumer that updates its held topology on `TopologyChanged` provably holds the new topology by the time the matching report arrives. Cross-capture mismatch stops being a race in this library and becomes purely a consumer-side asynchrony artifact — transient by construction. Pinned by a test.

**Scope of the guarantee — per-wave only.** Moving emissions outside the lock trades away a property the old code had: topology notifications were previously emitted *under* the propagation lock and were therefore strictly ordered across waves. Now two *concurrent* waves — serialized inside the lock as A then B — can interleave their emissions as `T_B, R_B, T_A, R_A`, leaving a consumer holding the older pair until the next wave. This is accepted, stated rather than discovered: `StatusChanged` has had exactly this cross-wave semantics since ADR-001 (report emission was always outside the lock), propagation is single-threaded in the overwhelming case (ADR-001's own analysis), and the state self-corrects on the next wave. A total cross-wave order would require sequence-stamping both events — follow-up material if a consumer ever needs it, not this ADR.

### 5. `BuildTreeSnapshot` — a pure, total projection (Prognosis.Diagnostics)

```csharp
public static HealthTreeSnapshot BuildTreeSnapshot(HealthReport report, HealthTopology topology);
```

(The original sketch — and the `v8.0.0-beta.1` cut — named this `Enrich`. Renamed before `v8.0.0` stable: the contract below is a *projection* — shape-imposing, and lossy toward orphan report nodes — where "enrich" promises additive augmentation of the input; `Build*` is this codebase's construct-now naming, and the method deliberately parallels the internal `HealthNode.BuildTreeSnapshot` it replicates.)

Pure, allocation-only, never touches live nodes — an ADR-007 layer citizen. Statuses, reasons, and tags are looked up by name in `report.Nodes`. The reactive path to the whole ADR-007 layer is then:

```csharp
graph.ObserveHealthReport()
     .Select(r => HealthGraphAnalysis.BuildTreeSnapshot(r, topology))   // topology from TopologyChange.Topology
     .Select(HealthGraphAnalysis.Contributors);
```

**Contract: `BuildTreeSnapshot` projects the report onto the topology.** Its output covers exactly the topology's reachable set. The two mismatch directions:

- **Name in topology, absent from report** (node removed after capture): the node is synthesized at `HealthEvaluation.Unknown(reason)` — the ADR-008 §3 factory — with a reason naming the staleness (e.g. `"'X' is in the supplied topology but not in the report; topology predates report"`). ADR-006 guarantees this can never gate an ancestor or invent a culprit. ADR-008 demands every `Unknown` have a resolution path — here it is **mechanical**: §3 guarantees the topology mutation that caused the mismatch raised `TopologyChanged`, and §4 guarantees that event precedes the next report. The `Unknown` is transient by construction, not by luck. This compliance argument is why §3 is a prerequisite, not an independent fix.
- **Name in report, absent from topology** (node added after capture): the node is **outside the projection** — it has no edges in the supplied topology, so it cannot participate in a fold over that topology; omitting it does not silently alter the fold's meaning the way omission in the first direction would. It is not an error and not hidden: `HealthGraphAnalysis.FindOrphans(report, topology)` returns the report snapshots the topology cannot place, for consumers that want to alert on drift. Under §4's ordering this state is likewise transient.

Rejected totality answers, for the record: **throw** (not total; punishes the guaranteed transient window); **omit silently in the first direction** (changes the fold's meaning — the topology says the parent aggregates over a child the tree no longer shows); **a wrapper return record** carrying orphans (breaks drop-in composition with `Contributors`; the orphan query is separable and rarely wanted per beat).

### 6. Round-trip fidelity is pinned

For a quiescent graph, `BuildTreeSnapshot(graph.GetReport(), graph.GetTopology())` is **structurally equal** to `graph.CreateTreeSnapshot()` — pinned by tests over cyclic, diamond, and tagged graphs. Two contract points this forces:

- **Cycle/diamond rule.** `BuildTreeSnapshot` walks the topology pre-order DFS in edge-list order with a visited-by-name set; a repeated name is emitted as a childless leaf — replicating `HealthNode.BuildTreeSnapshot` exactly. Both occurrences of a repeated node carry the same status (lookup is by name), matching ADR-007's "override-by-name, both occurrences move together." This is why `HealthTopology.Edges` lists are ordered, and the rule is contract, not implementation accident. (Reference-visited and name-visited coincide because `HealthGraph` validates name uniqueness; nodes with duplicate names introduced after construction are outside contract — a pre-existing gap, unchanged here.)
- **Structural comparison in tests.** `HealthTreeSnapshot` is a record with collection members, so `Equals` is reference-based on them; the round-trip tests compare with a structural comparer (or serialized form), not `==`.

Tags round-trip via the report: ADR-005 threads `Tags` into every `HealthSnapshot`, and both `RebuildReport` and `BuildTreeSnapshot` null out empty tag sets identically. `HealthTopology` carries no tags — they are status-side data, delivered per beat.

Finally, the `CreateTreeSnapshot()` doc comment is corrected — it does not evaluate; it re-reads the same per-node caches as the report, unsynchronized — and now points reactive consumers at `BuildTreeSnapshot`. `CreateTreeSnapshot()` itself is retained unchanged for single-threaded/quiescent use (JSON endpoints, tests).

## Rejected alternatives

- **A `HealthTreeSnapshot` observable in `Prognosis.Reactive`.** Bakes per-beat full-tree materialization into the library when the tree is derivable; a consumer wanting only `Contributors` still pays for a whole tree every beat. `BuildTreeSnapshot` is the smaller primitive; a tree observable is a one-line `Select` on top.
- **Put `Importance` into `HealthReport`.** ADR-007 explicitly declined this ("no change to `HealthReport` or the wire shape"); it would tax every consumer that only wants statuses, and would prematurely make `Importance` a wire type on every heartbeat (see ADR-008 alignment below).
- **Lock `CreateTreeSnapshot()` against `_propagationLock`.** Fixes only the tearing; leaves the per-beat re-walk and the "no reactive input" gap, and puts a consumer-callable lock acquisition on the propagation path. Superseded by §2's approach: the *artifact* is built under the lock once per wave; consumers read a published value.
- **A topology version/fingerprint instead of structural events.** Cheaper per wave, but pushes the poll-and-diff burden onto every consumer and still needs §3's detection to bump the version — same cost, worse ergonomics.

## Alignment with prior ADRs

- **ADR-001 — amended, narrowly.** Two of its decisions are revised: `TopologyChanged`'s node-set-diff semantics (§3 widens to structural), and the §3 line "`CreateTreeSnapshot()` is NOT cached — built on demand from per-node caches" as the *only* tree path (a cached, wave-coherent topology now exists; `CreateTreeSnapshot()` itself keeps its uncached on-demand behavior). Everything else — `HealthGraph` as sole query surface, serialized propagation, cached report — is reinforced: `GetTopology()` is the report pattern applied to structure.
- **ADR-002 — no new lifecycle state.** `_cachedTopology` is a graph-level derived cache like `_cachedReport`; nothing new lives on `HealthNode`, and the single evaluation path is untouched.
- **ADR-005 — tags flow through.** Recovered from the report per beat; no parallel metadata channel.
- **ADR-006 — the safety floor for the staleness placeholder.** A synthesized `Unknown` can raise ancestors at most to `Unknown`; a stale topology can never invent a culprit.
- **ADR-007 — this is its missing input edge.** The scope boundary ("additive and pure; no change to the fold, `HealthReport`, or the wire shape") is kept verbatim, and the flagged "wire-format corollary" becomes satisfiable: a control plane can receive one `HealthTopology` per topology change and flat reports per beat, instead of a full tree per station per beat. ADR-007's masked-intrinsic blind spot applies identically to enriched trees — `BuildTreeSnapshot` reconstructs nothing ADR-007 didn't already; the limitation carries over unchanged.
- **ADR-008 — both halves honored.** The transience contract: the synthesized `Unknown` has a guaranteed mechanical resolution path (§3 + §4), satisfying "every `Unknown` MUST have a resolution path." The sequencing constraint: ADR-008 warned that `Advisory` had to land before the tree ships north; it did. This ADR now knowingly accepts the consequence ADR-008 predicted — **once `HealthTopology` crosses a wire, `Importance` is a wire type**, and any future `Importance` member becomes a staged multi-repo rollout with the same compatibility burden `HealthStatus` carries. That cost is inherited deliberately, and its fail-loud totality (no silent enum defaults) is the backstop that makes a missed site loud rather than wrong.

## Consequences

### Positive

- **ADR-007's layer is reachable from every reactive path.** The race is closed by construction — the only value crossing the impure boundary per beat is the atomically-built report; topology arrives coherently via its own event.
- **`TopologyChanged` becomes trustworthy.** Every structural mutation — including importance-only changes, previously silent — raises the event, with the new topology attached. Consumers stop reconstructing structure out of band.
- **Rate separation.** Structure is transmitted/materialized at topology rate; statuses at beat rate. Fleet-scale consumers dedupe structure and ship flat statuses per beat.
- **A pinned coherence guarantee** (topology-before-status emission) replaces an implicit, lock-straddling ordering, and consumer callbacks no longer run under the propagation lock.

### Negative / Trade-offs

- **Per-wave topology rebuild and comparison.** O(edges) work added to every wave even when edges didn't change. Same order as the existing report rebuild; measured, not assumed, before accepting cleverer invalidation.
- **`TopologyChanged` fires more often, sometimes with empty `Added`/`Removed`.** A semantic widening of a public event at 0.x. Subscribers reading only the lists see benign extra events; the doc comment is rewritten so the new contract is stated, not discovered.
- **Two artifacts to hold instead of one.** A consumer must combine a topology and a report stream. Mitigated by `TopologyChange.Topology` (no reconstruction) and the ordering guarantee (no re-sync logic); a convenience combinator in `Prognosis.Reactive` can be added later without new core surface.
- **The projection has a documented seam.** Report-only nodes are outside the tree by contract, discoverable via `FindOrphans` rather than visible in the projection itself. The alternative — a compound return type — was judged worse for the common composition path.
- **A future `Importance` member is now a wire migration** the moment any consumer ships `HealthTopology` north. Inherited from ADR-008 §2's deadline, with eyes open.
