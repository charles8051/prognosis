# Prognosis — Architecture Document

## Overview

Prognosis is a dependency-aware service health modeling library for .NET. It models the runtime health of a distributed system as a **directed acyclic graph** (DAG) where each node represents a service or component, and edges represent dependency relationships weighted by importance. The library computes an **effective health status** for every node by aggregating its own intrinsic check with the statuses of its dependencies, following configurable propagation rules.

### Design Philosophy

- **Graph-first modeling.** Health is not a flat list of checks — it is a topology. A cache failure that degrades auth which degrades checkout is a fundamentally different event than a direct checkout failure. Prognosis preserves this structure.
- **Zero-allocation steady state.** Core types use copy-on-write lists, `[ThreadStatic]` cycle detection, and volatile snapshot reads to avoid locking on the hot evaluation path.
- **Layered packaging.** The core library (`Prognosis`) targets .NET Standard 2.0 with no third-party dependencies beyond `System.Text.Json`. Reactive and DI integrations are separate opt-in packages.

---

## Package Structure

```
Prognosis.sln
├── Prognosis/                          # Core library (netstandard2.0; netstandard2.1)
├── Prognosis.DependencyInjection/      # M.E.DI integration (netstandard2.0; netstandard2.1)
├── Prognosis.Reactive/                 # System.Reactive extensions (netstandard2.0; netstandard2.1)
├── Prognosis.Tests/                    # Core unit tests (net10.0)
├── Prognosis.DependencyInjection.Tests/
├── Prognosis.Reactive.Tests/
├── Prognosis.Benchmarks/              # BenchmarkDotNet harness
├── Prognosis.Examples/                # Manual graph construction example
├── Prognosis.Examples.DependencyInjection/
└── Prognosis.Examples.Reactive/
```

### Dependency Graph Between Packages

```
Prognosis  ←──  Prognosis.DependencyInjection
    ↑               (+ M.E.DependencyInjection.Abstractions)
    │               (+ M.E.Hosting.Abstractions)
    │
    └───────  Prognosis.Reactive
                    (+ System.Reactive)
```

The core package has **no project references** to the extension packages. Extension packages reference only the core. This keeps the core deployable to any .NET Standard 2.0+ runtime without pulling in DI or Rx dependencies.

---

## Core Library (`Prognosis`)

### Type Map

| Type | Kind | Responsibility |
|---|---|---|
| `HealthNode` | Sealed class | Single node type for the health graph. Owns topology edges (`DependsOn`, `RemoveDependency`), intrinsic check invocation, aggregation logic, and upward propagation (`BubbleChange`). Created via `CreateDelegate` (custom health check) or `CreateComposite` (aggregation-only, always-healthy intrinsic). |
| `IHealthAware` | Interface | Marker for classes that participate in the graph. Single property: `HealthNode HealthNode { get; }`. |
| `HealthGraph` | Sealed class | Read-only materialized view of the graph. Entry point for reporting, evaluation, monitoring, and observables. |
| `HealthStatus` | Enum | `Healthy (0)`, `Unknown (1)`, `Degraded (2)`, `Unhealthy (3)`. Ordered worst-is-highest. |
| `Importance` | Enum | `Required`, `Important`, `Optional`, `Resilient`. Controls propagation rules on each edge. |
| `HealthEvaluation` | Record | `(HealthStatus Status, string? Reason)` pair. Implicit conversion from bare `HealthStatus`. |
| `HealthDependency` | Sealed class | Weighted edge: `(HealthNode Node, Importance Importance)`. |
| `HealthReport` | Record | Flat list of `HealthSnapshot` entries. Wire-friendly DTO with `DiffTo()` for change detection. |
| `HealthSnapshot` | Record | `(string Name, HealthStatus Status, string? Reason)` for a single node. |
| `HealthTreeSnapshot` | Record | Tree-shaped snapshot preserving dependency hierarchy. Ideal for nested JSON serialization. |
| `TopologyChange` | Sealed class | `(Added, Removed)` node lists emitted when the graph's structure changes. |
| `StatusChange` | Record | `(Name, Previous, Current, Reason)` describing a single node's status transition. |
| `HealthReportComparer` | Sealed class | `IEqualityComparer<HealthReport>` — order-independent, name-matched. Used to suppress duplicate emissions. |
| `HealthMonitor` | Sealed class | Timer-based poller. Calls `HealthGraph.RefreshAll()` on a configurable interval. Implements `IAsyncDisposable`. |
| `HealthStatusExtensions` | Static class | `IsWorseThan()` and `Worst()` comparison helpers. |

### Class Design

`HealthNode` is a **sealed** class with private constructors. Instances are created exclusively through static factory methods:

- `HealthNode.CreateDelegate(name, healthCheck)` — node with a custom intrinsic health check delegate
- `HealthNode.CreateDelegate(name)` — shortcut; intrinsic status is always `Healthy`
- `HealthNode.CreateComposite(name)` — aggregation-only node; health derived entirely from dependencies

`CreateDelegate` and `CreateComposite` are semantically distinct entry points but produce the same type. `CreateComposite` is simply sugar for a node whose intrinsic check always returns `Healthy`.

Fluent chaining via `DependsOn()` returns `HealthNode`:

```csharp
var app = HealthNode.CreateComposite("App")
    .DependsOn(auth, Importance.Required)
    .DependsOn(cache, Importance.Important);
```

---

## Key Architectural Concepts

### 1. Graph Topology — Copy-on-Write Edges

Nodes maintain two edge lists:
- `_dependencies` — outgoing edges (services this node depends on)
- `_parents` — incoming edges (services that depend on this node)

Both are stored as **volatile `IReadOnlyList<T>` references** updated via copy-on-write under a per-node lock (`_dependencyWriteLock` / `_parentWriteLock`). Readers never lock — they snapshot the volatile reference once and iterate safely.

```
       ┌─────────────┐        _dependencies         ┌──────────────┐
       │ AuthService  │ ─── Required ───────────────→│   Database   │
       │              │ ─── Important ──────────────→│    Cache     │
       └─────────────┘                               └──────────────┘
             ↑                                             │
        _parents (back-edge, auto-maintained)              │
                                                      _parents
```

`DependsOn()` and `RemoveDependency()` maintain both directions atomically and trigger propagation immediately.

### 2. Health Evaluation — Two-Pass Aggregation

`HealthNode.Aggregate()` is the core computation, running in two passes over a node's dependencies:

**Pass 1 — Read cached dependency evaluations:**
- Reads `_cachedEvaluation` from each dependency (always populated — seeded in constructor, maintained by propagation).
- Tracks whether any `Resilient`-marked sibling is healthy (needed for the resilience rule).

**Pass 2 — Compute effective status:**

| Importance | Propagation Rule |
|---|---|
| `Required` | Dependency status passes through unchanged |
| `Important` | `Unhealthy` is capped at `Degraded`; other statuses pass through |
| `Optional` | Always contributes `Healthy` (ignored) |
| `Resilient` | If ≥1 sibling `Resilient` dependency is healthy, `Unhealthy` is capped at `Degraded`. Otherwise behaves like `Required`. |

The worst status across all dependencies and the intrinsic check becomes the effective status. Reason strings chain from leaf to root:

```
Database.Latency: Degraded — Avg latency 600ms exceeds 500ms threshold
Database: Degraded — Database.Latency: Avg latency 600ms exceeds 500ms threshold
AuthService: Degraded — Database: Database.Latency: ...
```

### 3. Cycle Detection

Cycles in the dependency graph are handled safely at each propagation level. `BubbleChange()` uses a `[ThreadStatic]` `HashSet<HealthNode>` to prevent visiting the same node twice during upward propagation. `RefreshDescendants()` uses a local `visited` set for the same purpose during downward DFS walks.

`HealthGraph.DetectCycles()` performs a full DFS with gray/black coloring to enumerate all cycles as ordered name lists.

### 4. Propagation — The Bubble Strategy Pattern

When a node's state changes, the change must propagate **upward** through all ancestors so that parent evaluations are refreshed and any attached `HealthGraph` can rebuild its cached report.

This is managed through a **strategy delegate** on each node:

```csharp
internal Action<HealthNode>? _bubbleStrategy;
```

**Without a graph attached:** `_bubbleStrategy` is `null`. `BubbleChange()` falls back to a direct upward walk using `[ThreadStatic]` cycle detection — lightweight, lock-free.

**With a graph attached:** `HealthGraph` installs its `SerializedBubble` method via `+=` on every discovered node. Multiple graphs sharing a node each add their own callback (multicast delegate).

```
node.BubbleChange()
    ├── _bubbleStrategy is null?
    │   └── BubbleChangeCore()          ← direct upward walk (no graph)
    └── _bubbleStrategy is set?
        └── SerializedBubble(node)      ← graph-owned, under _propagationLock
            ├── BubbleChangeCore()      ← raw propagation
            ├── RefreshTopology()       ← detect added/removed nodes
            ├── RebuildReport()         ← rebuild cached HealthReport
            └── EmitStatusChanged()     ← notify observers (outside lock)
```

`SerializedBubble` holds `_propagationLock` for the entire wave, ensuring each emitted `HealthReport` reflects a complete, consistent propagation — no partial updates from concurrent changes.

### 5. Topology Tracking — `NodeSnapshot`

`HealthGraph` maintains a volatile `NodeSnapshot` containing:
- `HashSet<HealthNode> Set` — for O(1) membership tests
- `Dictionary<string, HealthNode> Index` — for name-based lookup
- `HealthNode[] Nodes` — for indexed iteration during report building

After every propagation, `RefreshTopology()` re-walks the graph from the root, computes a fresh node set, diffs against the current snapshot, and:
- Subscribes new nodes to the graph's bubble strategy (`+=`)
- Unsubscribes removed nodes (`-=`)
- Emits a `TopologyChange` to `TopologyChanged` observers

### 6. Reporting

Two report shapes serve different consumers:

| Shape | Type | Use Case |
|---|---|---|
| **Flat** | `HealthReport` → `List<HealthSnapshot>` | Wire-friendly DTO, diff-based change detection, Rx pipelines |
| **Tree** | `HealthTreeSnapshot` → nested `HealthTreeDependency` | JSON serialization where hierarchy should be visible |

`HealthReport.DiffTo()` compares two reports by name and produces `StatusChange` records for nodes whose status changed, appeared, or disappeared.

`HealthReportComparer` is order-independent and used by `HealthGraph` to suppress duplicate `StatusChanged` emissions and by Rx operators like `DistinctUntilChanged`.

### 7. Observables (Built-in)

`HealthGraph` exposes two `IObservable<T>` properties using raw BCL types (no System.Reactive dependency):

| Observable | Payload | Fires When |
|---|---|---|
| `TopologyChanged` | `TopologyChange` | Nodes added to or removed from the graph |
| `StatusChanged` | `HealthReport` | The effective health report changes (after propagation) |

Observer subscription/unsubscription is lock-protected with copy-on-write snapshot dispatch. Notifications are emitted **outside** the propagation lock to avoid deadlocks.

### 8. Polling — `HealthMonitor`

`HealthMonitor` provides a simple timer-based polling loop:

```
Start() → spawns PollLoopAsync → every interval:
    graph.RefreshAll()
        → DFS from root, leaves first
        → NotifyChangedCore() on each node (re-evaluates intrinsic + cached deps)
        → RebuildReport()
        → EmitStatusChanged() if report changed
```

`Poll()` triggers a single immediate cycle. `ReportChanged` delegates to `graph.StatusChanged`.

---

## Extension: Dependency Injection (`Prognosis.DependencyInjection`)

### Registration Flow

```csharp
services.AddPrognosis(health => {
    health.ScanForServices(typeof(Program).Assembly);
    health.AddDelegate<ExternalService>(...);
    health.AddComposite("Aggregator", ...);
    health.MarkAsRoot("Aggregator");
    health.UseMonitor(TimeSpan.FromSeconds(30));
});
```

### Graph Materialization

1. **Assembly scanning** — discovers all concrete `IHealthAware` implementations, registers them as singletons, and reads `[DependsOn<T>]` attributes for edge declarations.
2. **Node pool** — built lazily on first graph resolution. A shared `Dictionary<string, HealthNode>` holds all nodes from scanned services, composites, and delegates.
3. **Edge wiring** — attribute-declared and configurator-declared edges are resolved by name against the pool and wired via `DependsOn()`.
4. **Root resolution** — zero roots auto-detects the single parentless node. One root registers a plain `HealthGraph` singleton. Multiple roots register keyed `HealthGraph` instances and optionally `HealthGraph<TRoot>` wrappers.

### Key Types

| Type | Responsibility |
|---|---|
| `ServiceCollectionExtensions` | `AddPrognosis()` entry point. Orchestrates scanning, pool building, root registration. |
| `PrognosisBuilder` | Fluent builder collecting scan assemblies, composite definitions, delegate definitions, and root declarations. |
| `DependencyConfigurator` | Fluent edge declaration API used inside `AddComposite` and `AddDelegate` callbacks. |
| `DependsOnAttribute<T>` | Declarative edge: `[DependsOn<DatabaseService>(Importance.Required)]` on `IHealthAware` classes. |
| `HealthGraph<TRoot>` | Typed wrapper enabling generic resolution (`sp.GetRequiredService<HealthGraph<TRoot>>()`) for multi-root scenarios. |
| `PrognosisMonitorExtensions` | `UseMonitor()` registers `HealthMonitor` + `IHostedService` adapter for automatic start/stop with the host. |

---

## Extension: Reactive (`Prognosis.Reactive`)

Provides idiomatic System.Reactive operators as an opt-in layer over the core's `IObservable<T>`:

| Method | Input | Output | Description |
|---|---|---|---|
| `PollHealthReport(interval)` | `HealthGraph` | `IObservable<HealthReport>` | Timer-driven; calls `RefreshAll()` each tick, emits on change |
| `ObserveHealthReport()` | `HealthGraph` | `IObservable<HealthReport>` | Push-triggered via `StatusChanged`, no polling delay |
| `SelectHealthChanges()` | `IObservable<HealthReport>` | `IObservable<StatusChange>` | Diffs consecutive reports, emits per-node changes |
| `ForNodes(names)` | `IObservable<StatusChange>` | `IObservable<StatusChange>` | Filters to specific node names |
| `CreateSharedReportStream(interval, strategy)` | `HealthGraph` | `IObservable<HealthReport>` | Multicasted (`RefCount` or `ReplayLatest`) |
| `CreateSharedObserveStream(strategy)` | `HealthGraph` | `IObservable<HealthReport>` | Multicasted push-triggered variant |

---

## Threading Model

| Concern | Mechanism |
|---|---|
| Edge reads (`Dependencies`, `Parents`) | Volatile snapshot of copy-on-write `IReadOnlyList<T>` — lock-free |
| Edge writes (`DependsOn`, `RemoveDependency`) | Per-node `_dependencyWriteLock` / `_parentWriteLock` |
| Propagation cycle detection | `[ThreadStatic] HashSet<HealthNode>` — no cross-thread contention |
| Propagation serialization | `HealthGraph._propagationLock` — one wave at a time per graph |
| Topology updates | `HealthGraph._topologyLock` inside propagation lock |
| Observer dispatch | Snapshot observer list under dedicated locks, dispatch outside all locks |
| `_cachedEvaluation` reads | `volatile` field — safe for torn-read avoidance on reference types |
| `_snapshot` (NodeSnapshot) reads | `volatile` field |

### Lock Ordering

```
_propagationLock
  └── _topologyLock
        └── (observer locks are independent — used only for list mutation)
```

Observer notification always happens **after** releasing `_propagationLock` to prevent subscriber code from re-entering the graph under the lock.

---

## Data Flow Diagram

### Push Path (state change → report emission)

```
Service state changes
        │
        ▼
node.Refresh() / node.BubbleChange()
        │
        ▼
_bubbleStrategy dispatches to graph's SerializedBubble
        │
        ▼  (under _propagationLock)
BubbleChangeCore(): re-evaluate node + walk up parents
        │
        ▼
RefreshTopology(): diff node sets, subscribe/unsubscribe new/removed nodes
        │
        ▼
RebuildReport(): iterate all nodes, read _cachedEvaluation
        │
        ▼  (release _propagationLock)
EmitStatusChanged(): notify StatusChanged observers
```

### Poll Path (timer tick → report emission)

```
HealthMonitor timer tick
        │
        ▼
graph.RefreshAll()
        │
        ▼  (under _propagationLock)
root.RefreshDescendants(): DFS, NotifyChangedCore() on each node
        │
        ▼
RebuildReport()
        │
        ▼  (release _propagationLock)
EmitStatusChanged() if report changed
```

---

## .NET Compatibility

| Target | Polyfills |
|---|---|
| .NET Standard 2.0 | `IsExternalInit` (for `init` properties/records), `ReferenceEqualityComparer` (missing from BCL) |
| .NET Standard 2.1 | `IsExternalInit` only |

The `Polyfills/` directory in the core and extension projects provides these shims. `LangVersion` is set to `latest` (C# 14.0 features used with downlevel targeting).

---

## Design Decisions

Architectural decisions are tracked in `docs/adr/`:

| ADR | Summary |
|---|---|
| [001 — Centralize API on HealthGraph](adr/001-centralize-api-on-healthgraph.md) | Moved query, report, and observable APIs from `HealthNode` to `HealthGraph`. Introduced `_bubbleStrategy` for serialized propagation. Eliminated per-node observer plumbing. |
