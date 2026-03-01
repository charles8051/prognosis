# ADR-002: Remove HealthNode.Evaluate — Cache-Only Evaluation Model

**Status:** Accepted
**Date:** 2026-06-22
**Drivers:** API simplification, single evaluation path, elimination of dead internal surface

## Context

After ADR-001 centralized the query API on `HealthGraph`, `HealthNode.Evaluate()` became `internal` but remained the recursive computation primitive. It served a different role from `Refresh()`:

| Method | Behavior |
|---|---|
| `Evaluate()` | Fresh recursive computation, no caching, no propagation, no observer notification |
| `Refresh()` | Re-evaluates intrinsic check, reads cached dependency values, caches result, propagates upward, fires `StatusChanged` |

In practice, consumers never need "quiet" evaluation. The three operations that cover all real use cases are:

1. **Read cache** → `HealthGraph.CreateReport()` — cheap, no re-evaluation.
2. **Point change** → `node.Refresh()` / `graph.Refresh(node)` — re-evaluates, propagates, notifies.
3. **Full poll** → `graph.RefreshAll()` — DFS bottom-up refresh of every node, rebuilds report, notifies.

`Evaluate()` is an orphan — it computes fresh but never caches, propagates, or notifies. Every call site that used `Evaluate()` actually wanted one of the three operations above but was forced to call `Evaluate()` because `_cachedEvaluation` could be `null` on nodes that hadn't been visited yet.

Additionally, `Evaluate()` introduced complexity:

- A dedicated `[ThreadStatic] HashSet<HealthNode> s_evaluating` for cycle detection during recursive evaluation.
- A `useCachedDependencies` parameter on `Aggregate()` to switch between "read cache" (propagation path) and "recurse fresh" (`Evaluate` path).
- A nullable `_cachedEvaluation` field requiring `?? Evaluate()` fallback at every read site.

## Decision

### 1. Remove `HealthNode.Evaluate()` entirely

The method, its `s_evaluating` cycle guard, and the `useCachedDependencies` parameter on `Aggregate()` are all removed.

### 2. Seed `_cachedEvaluation` in the constructor

```csharp
private HealthNode(string name, Func<HealthEvaluation> intrinsicCheck)
{
    Name = name;
    _intrinsicCheck = intrinsicCheck;
    _cachedEvaluation = intrinsicCheck();
}
```

At construction time there are zero dependencies, so the result is just the delegate's return value. `DependsOn()` immediately calls `Refresh()` which re-caches with the new dependency factored in. The field type changes from `HealthEvaluation?` to `HealthEvaluation` — it is never null.

### 3. `HealthGraph` constructor seeds the full tree

After attaching bubble strategies to all nodes, the constructor calls `_root.RefreshDescendants()` to ensure every node's cache reflects the complete dependency topology before the first `CreateReport()`.

### 4. `Aggregate()` always reads `_cachedEvaluation`

The `useCachedDependencies` parameter is removed. The only caller is `NotifyChangedCore()`, which is always invoked from paths with their own cycle detection (`BubbleChange` via `s_propagating`, `NotifyDfs` via `visited` set).

### 5. Remove `HealthGraph.Evaluate` and `HealthGraph.Refresh`

With `HealthNode.Evaluate()` gone, the graph-level `Evaluate(string)` / `Evaluate(HealthNode)` and `Refresh(HealthNode)` / `Refresh(string)` wrappers serve no purpose. Consumers already hold `HealthNode` references (they created the nodes or received them via `IHealthAware`). They call `node.Refresh()` directly when state changes — the graph observes the propagation automatically.

The remaining graph-level query API:

- `graph.CreateReport()` — read the cached report (cheap).
- `graph.RefreshAll()` — re-evaluate every node bottom-up, rebuild report, emit `StatusChanged`.
- `graph[name]` / `graph.TryGetNode(name)` — look up a node if needed.
- `graph.StatusChanged` / `graph.TopologyChanged` — observe changes.

## Consequences

### Positive

- **Single evaluation path.** Every computation goes through `NotifyChangedCore()` → `Aggregate()`. No dual "cached vs. fresh" branching.
- **Simpler `Aggregate()`.** Removed the `useCachedDependencies` parameter and the recursive `dep.Node.Evaluate()` fallback.
- **Non-nullable cache.** `_cachedEvaluation` is always populated. No `?? Evaluate()` fallbacks at read sites.
- **Less threading machinery.** `s_evaluating` (the evaluation cycle guard) is removed. Cycle detection remains handled by `s_propagating` (bubble) and `visited` sets (DFS).
- **Smaller `HealthGraph` surface.** `Evaluate` and `Refresh` wrappers removed — consumers call `node.Refresh()` directly, which is where the capability actually lives.

### Negative

- **Constructor calls the intrinsic check.** If the delegate captures state that isn't ready at construction time, the initial cached value may be inaccurate. However, it is immediately overwritten by the first `DependsOn()` → `Refresh()` or `HealthGraph` constructor → `RefreshDescendants()`, so the window is negligible.
- **Circular dependencies no longer produce `"Circular dependency detected"` from evaluation.** Cycles are still detected by `HealthGraph.DetectCycles()` and are safely handled (no stack overflow) by propagation guards. The runtime effect of a cycle is that the second visit is skipped during propagation, so cached values from the previous wave are read. This is a behavioral change for the circular dependency test.
- **Consumers must hold node references to refresh.** There is no `graph.Refresh("name")` convenience. In practice this is not a burden — nodes are created by the consumer or looked up via `graph["name"]`.
