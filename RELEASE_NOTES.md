# v5.0.0 Release Notes

**This is a major release with breaking changes.** The API surface has been redesigned around a single entry point (`HealthGraph`) for all query, reporting, and observable operations. `HealthNode` is now focused exclusively on topology building and health-check definition.

See [ADR-001](docs/adr/001-centralize-api-on-healthgraph.md) for the full rationale.

---

## ⚠️ Breaking Changes

### `HealthNode` public API reduction

The following members have been removed from `HealthNode`'s public surface and centralized on `HealthGraph`:

| Removed from `HealthNode` | Replacement on `HealthGraph` |
|---|---|
| `Evaluate()` | `graph.Evaluate(node)` / `graph.Evaluate("name")` |
| `StatusChanged` | `graph.StatusChanged` |
| `CreateReport()` | `graph.CreateReport()` |
| `CreateTreeSnapshot()` | `graph.CreateTreeSnapshot()` |
| `BubbleChange()` | `node.Refresh()` (public) / `graph.Refresh(node)` |
| `RefreshDescendants()` | `graph.RefreshAll()` |

### `HealthNode` class hierarchy consolidated

`DelegateHealthNode` and `CompositeHealthNode` subclasses have been removed. `HealthNode` is now a **sealed class** with private constructors. Create instances via static factory methods:

```csharp
// Before (v4.x)
var node = new DelegateHealthNode("Svc", () => HealthStatus.Healthy);
var comp = new CompositeHealthNode("Agg");

// After (v5.0)
var node = HealthNode.CreateDelegate("Svc", () => HealthStatus.Healthy);
var comp = HealthNode.CreateComposite("Agg");
```

### `HealthReport` slimmed

- `HealthReport.OverallStatus` — **removed**. Callers who need a single aggregate can derive it: `report.Nodes.Max(n => n.Status)`.
- `HealthReport.Timestamp` — **removed**. Callers who need timestamps can stamp their own envelope.
- `HealthReport.Services` — **renamed** to `HealthReport.Nodes`.

### Rx node-level extensions removed (`Prognosis.Reactive`)

All `HealthNode` overloads have been removed. Use `HealthGraph` equivalents:

| Removed | Replacement |
|---|---|
| `node.PollHealthReport(interval)` | `graph.PollHealthReport(interval)` |
| `node.ObserveHealthReport()` | `graph.ObserveHealthReport()` |
| `node.ObserveStatus()` | `graph.StatusChanged` |
| `node.CreateSharedReportStream(...)` | `graph.CreateSharedReportStream(...)` |
| `node.CreateSharedObserveStream(...)` | `graph.CreateSharedObserveStream(...)` |

### Rx method rename

- `SelectServiceChanges()` → `SelectHealthChanges()`

---

## ✨ New Features

### `HealthGraph` as the sole query/observe surface

`HealthGraph` is now the single entry point for evaluation, reporting, monitoring, and observables:

```csharp
var graph = HealthGraph.Create(root);

graph.Evaluate("Database");              // single-node query by name
graph.Evaluate(node);                    // single-node query by reference
graph.Refresh(node);                     // push change + propagate upward
graph.Refresh("Database");               // same, by name
graph.RefreshAll();                      // re-evaluate entire graph
graph.CreateReport();                    // flat report (cached after propagation)
graph.CreateTreeSnapshot();              // tree-shaped hierarchical snapshot
graph.DetectCycles();                    // upfront cycle detection
graph.StatusChanged.Subscribe(...);      // IObservable<HealthReport>
graph.TopologyChanged.Subscribe(...);    // IObservable<TopologyChange>
```

### `HealthNode.Refresh()` for push-based notifications

Services implementing `IHealthAware` can now push health changes directly without needing a `HealthGraph` reference:

```csharp
class MyService : IHealthAware
{
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("MyService", () => ...);

    public void OnConnectionLost()
    {
        // All attached graphs are notified immediately.
        HealthNode.Refresh();
    }
}
```

### `HealthGraph.TopologyChanged`

New `IObservable<TopologyChange>` that emits whenever nodes are added to or removed from the graph via `DependsOn` / `RemoveDependency`. Each emission carries `Added` and `Removed` node lists.

### `HealthGraph.DetectCycles()`

Performs a full DFS with gray/black coloring and returns every cycle as an ordered list of node names. Returns empty when the graph is acyclic.

### `HealthTreeSnapshot` — hierarchical JSON output

New `CreateTreeSnapshot()` on `HealthGraph` returns a tree-shaped snapshot whose nesting mirrors the dependency topology — ideal for JSON serialization where hierarchy should be visible.

### `HealthEvaluation` factory methods

```csharp
HealthEvaluation.Healthy                        // static singleton
HealthEvaluation.Unhealthy("connection refused") // convenience factory
HealthEvaluation.Degraded("high latency")        // convenience factory
```

### `ForNodes()` Rx filter

Filter a `StatusChange` stream to specific node names:

```csharp
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .SelectHealthChanges()
    .ForNodes("Database", "Cache")
    .Subscribe(...);
```

### Shared nodes across multiple graphs

Nodes can now be shared across multiple `HealthGraph` instances. Each graph receives independent propagation notifications via multicast delegates:

```csharp
var shared = HealthNode.CreateDelegate("SharedDB", () => ...);
var graph1 = HealthGraph.Create(root1.DependsOn(shared, Importance.Required));
var graph2 = HealthGraph.Create(root2.DependsOn(shared, Importance.Required));
// Both graphs react when shared node's health changes.
```

### `HealthGraph<TRoot>` parity

The typed DI wrapper now forwards `Refresh(HealthNode)`, `Refresh(string)`, `RefreshAll()`, `StatusChanged`, and `Evaluate(string)`.

### `HealthStatusExtensions`

New extension methods: `IsWorseThan(HealthStatus)` and `Worst(HealthStatus, HealthStatus)`.

---

## 🐛 Bug Fixes

- **`Nodes` property no longer exposes mutable collection.** Previously returned the internal `HashSet<HealthNode>` typed as `IEnumerable<HealthNode>` — callers could cast to `ICollection` and corrupt graph state. Now returns an immutable `HealthNode[]` snapshot.
- **`RefreshTopology` race condition fixed.** Concurrent `DependsOn`/`RemoveDependency` calls could race through topology refresh, with the last writer silently dropping an update. Topology updates are now serialized under `_topologyLock`.
- **Atomic topology publish via `NodeSnapshot`.** Replaced two separate volatile fields (`_allNodes`, `_nodesByName`) with a single volatile `NodeSnapshot` reference, preventing torn reads where a thread could observe a stale set paired with a fresh index.

---

## ⚡ Performance

- `ToString()` now prefers `_cachedEvaluation` when available, avoiding a full subtree walk for nodes that have already been visited by a propagation wave.
- `HealthMonitor` internals simplified — observer plumbing removed; `ReportChanged` delegates directly to `graph.StatusChanged`.
- Serialized propagation via `_propagationLock` ensures each emitted `HealthReport` reflects a complete, consistent wave — no partial updates from concurrent changes.

---

## 📦 Packages

| Package | Version | Target |
|---|---|---|
| `Prognosis` | 5.0.0 | .NET Standard 2.0 / 2.1 |
| `Prognosis.Reactive` | 5.0.0 | .NET Standard 2.0 / 2.1 |
| `Prognosis.DependencyInjection` | 5.0.0 | .NET Standard 2.0 / 2.1 |

## Migration Guide

1. **Replace subclass constructors** with `HealthNode.CreateDelegate(...)` / `HealthNode.CreateComposite(...)`.
2. **Route all queries through `HealthGraph`**: `node.Evaluate()` → `graph.Evaluate(node)`, `node.CreateReport()` → `graph.CreateReport()`, etc.
3. **Replace `node.StatusChanged`** with `graph.StatusChanged`.
4. **Replace `node.BubbleChange()`** with `node.Refresh()` (or `graph.Refresh(node)`).
5. **Update Rx pipelines**: use `graph.PollHealthReport(...)` / `graph.ObserveHealthReport(...)` instead of node-level overloads. Rename `SelectServiceChanges()` → `SelectHealthChanges()`.
6. **Remove `OverallStatus` / `Timestamp` usage** from `HealthReport` consumers.
7. **Rename `report.Services`** to `report.Nodes`.
