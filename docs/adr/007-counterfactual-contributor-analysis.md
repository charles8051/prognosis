---
id: ADR-007
status: accepted
governs:
  - HealthNode.cs
  - HealthTreeSnapshot.cs
  - Prognosis.Diagnostics/HealthGraphAnalysis.cs
  - Prognosis.Tests/HealthGraphAnalysisTests.cs
relates:
  - ADR-002
  - ADR-004
  - ADR-006
  - ADR-009
---

# ADR-007: Counterfactual & Contributor Analysis over Health Snapshots

**Status:** Accepted
**Date:** 2026-07-18
**Drivers:** When a rollup is `Unhealthy` with several failing leaves, operators and downstream tooling need to answer questions the library cannot express today: *which* nodes are actually holding the rollup down, *would* repairing a given node change the verdict, and *what is the smallest set of repairs* that would. Today those questions require re-implementing the fold by hand against the topology. A real incident (below) turned on exactly this.

## Context

The rollup is a **worst-of fold**. `HealthNode.Aggregate` (`HealthNode.cs`) computes a parent's effective status from its intrinsic status plus each dependency's importance-mapped contribution, keeping the worst via `IsWorseThan`, and produces a **single** `HealthEvaluation(Status, Reason)` where `Reason` is the one worst-path leaf's reason. The importance mapping (see also ADR-006) is:

| `Importance` | Contribution of a failing child |
|---|---|
| `Required`  | child status passes through (`Unhealthy` → `Unhealthy`) |
| `Important` | `Unhealthy` is capped to `Degraded`; else passes through |
| `Optional`  | ignored (always `Healthy`) |
| `Resilient` | `Unhealthy` → `Degraded` **iff** a healthy resilient sibling exists (quorum); else passes through |

Two point-in-time captures exist:

- `HealthReport` (`GetReport()`) — `record HealthReport(HealthSnapshot Root, IReadOnlyList<HealthSnapshot> Nodes)`. `Root` is the single aggregated status + one worst reason; `Nodes` is a **flat** bag of `{Name, Status, Reason}` with **no edges and no importance**. This is the shape used for diffing (`DiffTo`), reactive pipelines (`StatusChanged`), and — in downstream consumers — the wire format sent to a control plane.
- `HealthTreeSnapshot` (`CreateTreeSnapshot()`) — the dependency tree **with** per-edge `Importance` and per-node status/reason. It carries the topology, but nothing consumes it for analysis.

The gap: **Prognosis exposes the forward fold and the raw tree, but no query layer that reasons over the fold.** Specifically there is no way to ask:

1. *"If node X were `Healthy`, what would the root be?"* — the only re-evaluation primitives (`ReportStatus`, `Refresh`) **mutate the live graph** and fire `StatusChanged`. They are destructive; there is no pure "evaluate this snapshot under a hypothetical" path.
2. *"Which nodes are determining the current verdict?"* — `Root.Reason` names one worst leaf; the flat `Nodes` list shows every status but not which are load-bearing.
3. *"What is the minimal set of repairs that would change the rollup?"*

### The intuition is usually wrong — which is why the library must answer it

A field unit reported the root `Unhealthy` with **two** `Unhealthy` leaves in `Nodes`: `Real.BackendApi` (wired `Required` under `Subsystem`) and `Real.Camera.Stream` (wired `Important` under `Subsystem`/`SecondarySubsystem`). Reading the flat report, both look equally culpable. They are not:

- **Repair the camera → root heals? No.** `Important` caps its contribution at `Degraded`; it was *never* why the root was `Unhealthy`. Repairing it changes the verdict by nothing.
- **Repair the API → root heals? Only to `Degraded`**, not `Healthy` — the camera still degrades `Subsystem`/`SecondarySubsystem` underneath.

So of two `Unhealthy` leaves, exactly one is load-bearing for the `Unhealthy` verdict, and repairing *even that* does not reach `Healthy`. None of this is derivable from `HealthReport` — you must know each edge's `Importance` and simulate the fold. With multiple **`Required`** unhealthy leaves it compounds: the root stays `Unhealthy` until *all* of them are fixed, and the report gives neither the count nor which leaves sit on a `Required` path.

### Downstream cost

The consumer's incident policy collapses a whole unit to one "unhealthy" incident keyed on the root rollup, in part **because Prognosis hands it one status and one reason.** And the flat `HealthReport` it ships north has already discarded `Importance` and topology, so even a willing control plane cannot reconstruct cause-granular incidents or a "would this repair help?" answer. The missing primitive is upstream, here.

## Decision

Add a **pure, non-mutating diagnostic query layer** over `HealthTreeSnapshot`. It reasons about a captured snapshot; it never touches live `HealthNode` state, allocates no graph, and raises no events. New surface lives in a `Prognosis.Diagnostics` namespace (static `HealthGraphAnalysis`), keeping the core node/graph API unchanged.

### 1. Extract the fold as a shared pure function

`Aggregate`'s per-importance contribution mapping is refactored into a single pure helper — `HealthContribution.Of(Importance, childStatus, hasHealthyResilientSibling) → HealthStatus` — that **both** the live `Aggregate` and the diagnostic re-fold call. This is a correctness requirement: a diagnostic fold that drifts from the real one gives confidently wrong answers. One source of truth, pinned by tests (the same discipline ADR-006 applied to the `Unknown` rows).

### 2. Counterfactual re-fold

```csharp
HealthStatus WhatIf(HealthTreeSnapshot tree, IReadOnlyDictionary<string, HealthStatus> overrides);
```

Re-folds `tree` bottom-up with the named nodes forced to the given statuses. Pure; `tree` is unchanged. Answers question 1 directly (`WhatIf(tree, { ["Real.Camera.Stream"] = Healthy })` on the incident above returns `Unhealthy` — proving the camera is not the cause).

### 3. Contributor set

```csharp
IReadOnlyList<Contributor> Contributors(HealthTreeSnapshot tree);
```

Returns the leaves **currently gating the root at its effective status** — those on a determining (arg-worst) path whose importance-mapped contribution equals the root status. Distinguishes a leaf that *gates the current rollup* from one that is unhealthy-but-capped (e.g. an `Important` leaf under an `Unhealthy` root). This is the structured, multi-culprit replacement for the single `Root.Reason`.

### 4. Minimal healing set

```csharp
IReadOnlyList<HealingStep> MinimalHealingSet(HealthTreeSnapshot tree, HealthStatus target);
```

The smallest set of **leaf** repairs (each leaf → `Healthy`) that brings the root to `target` or better. The fold is **monotone** — improving any leaf can only improve or hold each ancestor — so the problem is well-posed:

- With `Required` / `Important` / `Optional` only, the set is **unique and O(nodes)**: to drop the root below `Unhealthy` you must fix exactly the leaves whose `Required`-path contribution is `Unhealthy`; `Important`-capped leaves are provably excluded from the `Unhealthy`-healing set (they can only produce `Degraded`), and `Optional` leaves never appear.
- **`Resilient` quorums introduce genuine choice** (fix *any k of n* siblings), so a smallest healing set need not be unique. The contract: `MinimalHealingSet` returns *one* minimal set and marks quorum decision points on the affected `HealingStep`s, rather than pretending the answer is unique. Callers wanting the full determining frontier use `Contributors`.

### Scope and boundaries

- **Additive and pure.** No change to the live-graph API, the fold *semantics*, `HealthReport`, or the wire shape. `ReportStatus`/`Refresh` remain the only mutators; this layer never mutates.
- **Snapshot-in, value-out.** Inputs are `HealthTreeSnapshot` (identity is by `Name`; the snapshot already flattens cycles/diamonds into repeated leaves per `BuildTreeSnapshot`). Overrides and results are keyed by `Name`; the "same node reachable by two paths" case is documented as override-by-name (both occurrences move together), matching how the snapshot presents them.
- **Wire-format corollary (out of scope, flagged).** Remote consumers (e.g. a control plane generating incidents) can only use this if the importance-carrying tree — or a compact `Contributors` projection — is transmitted, since `HealthReport` drops `Importance`. That is a change in the *consuming* repos, not this library, but it is the reason this ADR exists and is recorded here so the two land coherently. *(ADR-009 later made this satisfiable: structure ships once per `TopologyChanged` as `HealthTopology`, flat reports ship per beat, and `HealthGraphAnalysis.BuildTreeSnapshot` recombines them — which is also the safe reactive entry point into this layer.)*

### Alignment with prior ADRs

- **ADR-002 (single non-null cache).** No new lifecycle state on `HealthNode`; the diagnostic layer reads snapshots, not live cache fields.
- **ADR-004 (split composite nodes and probes).** Contributor/healing analysis is most useful precisely because leaves are split from composites; this layer reports on the leaf frontier that split created.
- **ADR-006 (`Unknown` strictly non-gating).** The extracted `HealthContribution.Of` is the *same* mapping ADR-006 pins, so the non-gating guarantee for `Unknown` holds identically inside `WhatIf`/`MinimalHealingSet` — a counterfactual can never invent gating an `Unknown` child would not cause live.

## Consequences

### Positive

- **The three operator questions become API calls**, not hand-simulation of the fold against topology only humans currently hold.
- **Cause-granular diagnostics upstream of every consumer.** `Contributors` gives the real multi-culprit frontier the single `Root.Reason` throws away; it is the primitive a control plane needs for per-cause incidents and "would this repair help?" triage.
- **One fold, guaranteed.** Extracting `HealthContribution.Of` removes the risk that a diagnostic re-fold silently disagrees with production aggregation, and shrinks `Aggregate` to a thin loop over a pinned helper.

### Negative / Trade-offs

- **`Resilient` makes minimal-healing non-unique.** The honest contract (return one minimal set + mark quorum points) is less tidy than a single canonical answer, but the alternative — pretending uniqueness — would mislead exactly when the graph is most complex. Documented, not hidden.
- **A second consumer of the fold mapping to keep in lockstep.** Mitigated by making it literally one shared function with test rows; still, anyone changing importance semantics now updates one helper and its pinned table (the ADR-006 precedent).
- **Snapshot identity is by `Name`.** Diamonds where one node is reachable by multiple paths move together under an override. This matches the existing `HealthTreeSnapshot` presentation but should be called out to callers who assume per-edge independence.
- **Intrinsic status is reconstructed, not recorded — one masked blind spot.** A `HealthTreeSnapshot` carries only each node's *effective* status (per ADR-002 there is a single, effective cache; adding an intrinsic field would be new lifecycle state). The layer reconstructs a node's intrinsic status as the residual its children cannot explain. This is exact when the intrinsic is `Healthy` (the ADR-004 composite model) or is *strictly worse* than every child's contribution — an unmasked probe failure, which is recovered and reported by name (the gating/repair unit is a node, not necessarily a leaf). The single blind spot is a node whose own probe failure is *masked* by an equal-or-worse child contribution: the two are indistinguishable in the snapshot, so the status is attributed to the children. Making this exact would require either recording intrinsic status in the snapshot (new lifecycle state, contra ADR-002) or re-running probes at analysis time (impure); both are rejected. The limitation is documented on `HealthGraphAnalysis` and pinned by a test.
