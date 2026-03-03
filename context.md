# Prognosis — Project Context

Prognosis is a dependency-aware service health modeling library for .NET. It models service health as a directed graph where each node's effective status is computed from its own intrinsic check and the weighted health of its dependencies.

## Packages

| Package | Target | Purpose |
|---|---|---|
| `Prognosis` | netstandard2.0; netstandard2.1 | Core — graph modeling, aggregation, reporting, monitoring |
| `Prognosis.DependencyInjection` | netstandard2.0; netstandard2.1 | M.E.DI integration — fluent builder, service node registration, hosted monitoring |
| `Prognosis.Reactive` | netstandard2.0; netstandard2.1 | System.Reactive extensions — Rx polling, push-triggered reports, change streams |
| `Prognosis.Generators` | netstandard2.0 | Source generators + analyzers — auto-generates `HealthNames` constants, validates `DependsOn` edge references at compile time |

Extension packages reference only the core. The core has zero project references outward.

## Core Types

- **`HealthNode`** — sealed, private constructors. Created via `Create(name)` with optional `.WithHealthProbe(healthCheck)`. Legacy `CreateDelegate` and `CreateComposite` factory methods are deprecated. Owns edges (`DependsOn`/`RemoveDependency`/`ReplaceDependencies`), aggregation, upward propagation, and external status reporting (`ReportStatus`). `ReplaceDependencies` atomically swaps the full dependency set for switchable-service scenarios.
Entry point for `GetReport`, `RefreshAll`, and observables
- **`HealthStatus`** — enum: `Healthy(0)`, `Unknown(1)`, `Degraded(2)`, `Unhealthy(3)`. Worst-is-highest.
- **`Importance`** — enum: `Required`, `Important`, `Optional`, `Resilient`. Controls how dependency failures propagate.
- **`HealthEvaluation`** — `(HealthStatus, string? Reason)` record. Implicit conversion from `HealthStatus`.
- **`HealthDependency`** — weighted edge: `(HealthNode, Importance)`.
- **`HealthReport`** / **`HealthSnapshot`** — flat report DTOs. `HealthReport.Root` carries the graph root's aggregated status. `DiffTo()` for change detection.
- **`HealthTreeSnapshot`** — tree-shaped snapshot preserving hierarchy for nested JSON.
- **`HealthMonitor`** — timer-based poller wrapping `HealthGraph.RefreshAll()`.

## Aggregation Rules

| Importance | Rule |
|---|---|
| `Required` | Status passes through unchanged |
| `Important` | `Unhealthy` capped at `Degraded` |
| `Optional` | Ignored |
| `Resilient` | If ≥1 healthy sibling, `Unhealthy` → `Degraded`; otherwise passes through |

## Threading Model

- **Edge reads** — lock-free volatile snapshots of copy-on-write lists.
- **Edge writes** — per-node locks (`_dependencyWriteLock` / `_parentWriteLock`).
- **Cycle detection** — `[ThreadStatic]` hash sets (propagation).
- **Propagation** — `HealthGraph._propagationLock` serializes one wave at a time. Observer notifications fire outside the lock.
- **Lock ordering** — `_propagationLock` → `_topologyLock` → (observer locks independent).

## Propagation

- **Push path:** `node.Refresh()` → `_bubbleStrategy` → `SerializedBubble` (under lock) → `BubbleChange` → `NotifyChangedCore` → `RefreshTopology` → `RebuildReport` → `EmitStatusChanged`.
- **Poll path:** `HealthMonitor` tick → `graph.RefreshAll()` → DFS `NotifyChangedCore` on every node → `RebuildReport` → `EmitStatusChanged`.
- **Report path:** `node.ReportStatus(eval)` → writes `_cachedEvaluation` directly, sets skip flag → `Refresh()` → `NotifyChangedCore` uses cached value as intrinsic (skips delegate) → propagates upward → next poll/refresh clears the override naturally.

## DI Integration

`services.AddPrognosis(...)` — registers service nodes via `AddServiceNode<T>`, defines new nodes via `AddNode`, wires edges, resolves roots. `PrognosisBuilder` provides the fluent API (`AddServiceNode<T>`, `AddNode`, `MarkAsRoot`, `UseMonitor`). The `NodeConfigurator` returned by `AddNode` provides `.WithHealthProbe<T>(...)` and `.DependsOn(...)`. The generator emits `AddDiscoveredNodes()` which calls `AddServiceNode<T>` for every class with a public `HealthNode` property and wires `[DependsOn]` attribute-declared edges.

## Source Generators

- **`HealthNodeNameCollector`** — incremental generator. Scans `HealthNode.Create("name")` / `CreateDelegate("name")` / `CreateComposite("name")` calls, emits `HealthNames` static class with `const string` fields.
- **`ServiceNodeDiscoveryGenerator`** — incremental generator. Scans classes with public `HealthNode` properties, reads `[DependsOn]` attributes, emits `AddDiscoveredNodes()` extension on `PrognosisBuilder`. Only emits when `Prognosis.DependencyInjection` is referenced.
- **`DependsOnEdgeAnalyzer`** — diagnostic analyzer. Validates `DependencyConfigurator.DependsOn("name")` arguments against discovered node names. Reports `PROGNOSIS001` for unknown references.

## Conventions

- C# latest (14.0) with .NET Standard 2.0/2.1 downlevel targeting.
- Polyfills in `Polyfills/` (`IsExternalInit`, `ReferenceEqualityComparer`).
- All enums use `[JsonStringEnumConverter]`.
- Records and sealed classes preferred. No inheritance hierarchy on `HealthNode`.
- ADRs tracked in `docs/adr/`.
