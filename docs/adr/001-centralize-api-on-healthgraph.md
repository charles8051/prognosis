# ADR-001: Centralize Query, Report, and Observable API on HealthGraph

**Status:** Accepted
**Date:** 02-27-2026
**Drivers:** API simplification, single entry point for consumers, serializable concurrent propagation

## Context

`HealthNode` currently exposes a broad public surface that overlaps significantly with `HealthGraph`:

| Capability | `HealthNode` | `HealthGraph` |
|---|---|---|
| Single-node evaluation | `Evaluate()` | — |
| Flat report (subtree walk) | `CreateReport()` | `CreateReport()` (delegates to root) |
| Tree snapshot (subtree walk) | `CreateTreeSnapshot()` | `CreateTreeSnapshot()` (delegates to root) |
| Status observable | `StatusChanged` | — |
| Subtree refresh | `RefreshDescendants()` | `RefreshAll()` (delegates to root) |
| Upward propagation | `BubbleChange()` | — |

This creates a "two doors to the same room" problem — `graph.CreateReport()` literally calls `_root.CreateReport()`. Consumers must choose between equivalent paths, and the node-level methods silently scope to a subtree, which is confusing when the intent is to query the full graph.

Additionally, `_topologyCallback` is a single `volatile Action?` slot on each node. If guidance is to "create a `HealthGraph` at any subnode you care about," two graphs rooted at the same node silently overwrite each other's callback.

The per-node `StatusChanged` observable requires ~60 lines of observer plumbing (`_observers`, `_observerLock`, `_lastEmitted`, `StatusObservable`, `Unsubscriber`, `NotifyChangedCore` dispatch) on every node instance, even when no subscriber exists.

Finally, `BubbleChange()` uses `[ThreadStatic]` cycle detection, so two threads can propagate through the same graph concurrently. Their per-node `_cachedEvaluation` writes interleave, making any cached report at the graph level non-serializable across concurrent changes.

## Decision

### 1. `HealthGraph` becomes the sole query/observe surface

Remove from `HealthNode`'s public API:
- `Evaluate()` → internal (still used by `ToString()` within the assembly)
- `CreateReport()` → removed entirely
- `CreateTreeSnapshot()` → removed (internal `BuildTreeSnapshot` retained)
- `StatusChanged` → removed, along with all observer plumbing
- `BubbleChange()` → internal
- `RefreshDescendants()` → internal

Add to `HealthGraph`:
- `HealthEvaluation Evaluate(string name)` — single-node query by name
- `HealthEvaluation Evaluate(HealthNode node)` — single-node query by reference
- `IObservable<HealthReport> StatusChanged` — emits a full `HealthReport` when the graph's effective state changes
- `void NotifyChange(HealthNode node)` — public entry point that replaces direct `BubbleChange()` calls from consumers

`HealthNode` retains only: `Name`, `Dependencies`, `Parents`, `HasParents`, `DependsOn()`, `RemoveDependency()`, `ToString()`, and the static factory methods.

### 2. Callback hook for serialized propagation

Replace the `_topologyCallback` field with a `_bubbleStrategy` delegate:

```csharp
internal Action<HealthNode> _bubbleStrategy = static node => node.BubbleChange();
```

- `DependsOn()` and `RemoveDependency()` call `_bubbleStrategy(this)` instead of `BubbleChange()` directly.
- Before a graph is attached, the default lambda calls `BubbleChange()` lock-free.
- `HealthGraph` installs its own strategy on every discovered node during construction:

```csharp
foreach (var node in allNodes)
    node._bubbleStrategy = SerializedBubble;
```

- `SerializedBubble` wraps `BubbleChange()` + topology refresh + report rebuild under a single `_propagationLock`, serializing the entire propagation wave.
- `RefreshTopology()` installs the strategy on newly discovered nodes and resets it on removed nodes.

### 3. Cached `HealthReport` in `HealthGraph`

`HealthGraph` maintains a `volatile HealthReport? _cachedReport`:

- **Rebuilt** after every serialized `BubbleChange()` and after `RefreshAll()`.
- **Rebuild is cheap** — iterates the flat `NodeSnapshot.Nodes` set reading per-node `_cachedEvaluation` fields. No recursion, no delegate invocations.
- `CreateReport()` returns `_cachedReport ?? RebuildReport()`.
- `StatusChanged` emits the cached report only when it differs from the previous (using `HealthReportComparer`).
- `CreateTreeSnapshot()` is NOT cached — built on demand from per-node caches.

### 4. `HealthMonitor` simplification

`HealthMonitor.Poll()` calls `graph.RefreshAll()` which updates all per-node caches and rebuilds the graph-level cached report. The monitor's `ReportChanged` delegates to the graph's `StatusChanged`. The monitor's own observer plumbing is removed.

### 5. Rx extension surface reduction

Node-level overloads removed:
- `PollHealthReport(HealthNode, TimeSpan)`
- `ObserveStatus(HealthNode)`
- `ObserveHealthReport(HealthNode)`
- `CreateSharedReportStream(HealthNode, ...)`
- `CreateSharedObserveStream(HealthNode, ...)`

Graph-level overloads updated to use `graph.StatusChanged` instead of `graph.Root.StatusChanged`.

## Consequences

### Positive

- **Single entry point.** All query, report, and observable operations go through `HealthGraph`. The "what do I call and where?" question has one answer.
- **Clean separation.** `HealthNode` = topology building + intrinsic check definition. `HealthGraph` = querying, reporting, monitoring, observables.
- **Serializable propagation.** The `_bubbleStrategy` + `_propagationLock` pattern ensures each emitted `HealthReport` reflects a complete propagation wave — no partial updates from concurrent changes.
- **Reduced per-node overhead.** ~60 lines of observer plumbing removed from every `HealthNode` instance.
- **Rx extensions halved.** Removing node-level overloads cuts the extension class roughly in half.
- **Multicast-safe.** The `_bubbleStrategy` delegate replaces the single-slot `_topologyCallback`, so multiple graphs sharing nodes no longer overwrite each other's callback.

### Negative

- **Breaking change across all packages.** Touches `Prognosis` (core), `Prognosis.Reactive`, `Prognosis.DependencyInjection`, all test projects, all examples, and `README.md`.
- **`HealthGraph` materialization required for any query.** Consumers who previously called `node.Evaluate()` for a quick check must now have a `HealthGraph` reference. Mitigated by the fact that graphs are typically created once at startup.
- **`IHealthAware` consumers lose self-contained observability.** A service handing out its `HealthNode` can no longer offer direct subscriptions; subscribers need a `HealthGraph`. The `HealthNode` property on `IHealthAware` becomes a construction-only handle.
- **`ToString()` still triggers subtree evaluation.** `HealthNode.ToString()` calls the now-internal `Evaluate()`, so logging a node reference still walks its dependency subtree. This is intentional and documented.

## Alternatives Considered

### Eliminate `HealthGraph` entirely — pure node-centric API

The most minimal design: consumers create nodes, wire edges, and call methods directly on nodes. No `HealthGraph` type at all.

Nodes are self-sufficient for **computation** — `Evaluate()`, `CreateReport()`, `BubbleChange()`, and `RefreshDescendants()` all work by walking edges. A consumer who creates a root node and calls `Evaluate()` gets a fully functional health system with no graph object involved. This is how the construction phase works today: DI builds the entire topology before materializing a `HealthGraph`.

However, several capabilities require **state that no individual node possesses**:

| Capability | Why nodes can't self-serve |
|---|---|
| O(1) name lookup | A node knows its children and parents but not its siblings or distant descendants. Finding "Database" from the root requires an O(n) walk every time. A name index (`Dictionary<string, HealthNode>`) needs a stateful owner. |
| Topology change detection | Emitting "node X was added/removed" requires diffing the reachable set against a prior snapshot. Nodes only know their current edges — they don't store previous topology state. |
| Serialized propagation | A `_propagationLock` needs a single coordination point shared by all nodes in the graph. In a pure node topology there is no natural location for a shared lock. |
| Cached report | A report cache is scoped to "everything reachable from this root." The cache, the diff against the previous report, and the `StatusChanged` emission all need a single owner. |
| DI resolution | `GetRequiredService<???>()` needs a concrete type. You can't register a bare `HealthNode` distinctly as "the root." Multi-root scenarios (OpsView / CustomerView) need keyed wrapper types. |

Without `HealthGraph`, these capabilities would either not exist, move onto every `HealthNode` (bloating nodes with state most of them never use), or live on a separate utility type — which is `HealthGraph` by another name.

The relationship is analogous to `DbContext` over entities: the entities and relationships are the data structure; the context is the query layer that provides indexing, change tracking, and coordination. `HealthNode` is the data structure. `HealthGraph` is the query layer. Both are necessary; separating them is the right abstraction.

Rejected because the first thing a consumer building a health endpoint would do is write their own wrapper with a name index and a cached report — and that wrapper *is* `HealthGraph`.

### Keep `Evaluate()` public, only move reports and observables

Preserves the lightweight "check one node" path without requiring a graph. Rejected because it leaves an inconsistent boundary — "you can evaluate but not report" — and `HealthGraph.Evaluate(name)` covers the same use case with explicit scoping.

### Per-node `StatusChanged` that emits `(NodeName, HealthStatus)` tuples

Would preserve fine-grained per-node subscriptions. Rejected in favor of graph-level `HealthReport` emissions because: (a) the `DiffTo()` + `SelectHealthChanges()` Rx pipeline already provides per-node change events from a report stream, (b) it avoids maintaining two observable systems.

### Lock-free eventual consistency instead of `_propagationLock`

Accept that concurrent propagations may produce reports reflecting mixed states, corrected on the next propagation. Rejected because the serializable model is achievable at negligible cost (propagation is synchronous, sub-microsecond for typical graph sizes, and the 99% case is single-threaded).

## Implementation Plan

See the execution plan in the associated PR for step-by-step ordering. Summary:

1. Add `_bubbleStrategy` callback hook to `HealthNode`
2. Add serialized propagation + cached `HealthReport` to `HealthGraph`
3. Add `StatusChanged` observable and `Evaluate` overloads to `HealthGraph`
4. Internalize `HealthNode` query/notification surface
5. Simplify `HealthMonitor`
6. Rewrite Rx extensions
7. Update `HealthGraph<TRoot>` wrapper
8. Update all tests
9. Update all tests (Rx and DI)
10. Update examples and README
11. Verify build
