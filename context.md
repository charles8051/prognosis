# Prognosis — Project Context

Prognosis is a dependency-aware service health modeling library for .NET. It models service health as a directed graph where each node's effective status is computed from its own intrinsic check and the weighted health of its dependencies.

## Packages

| Package | Target | Purpose |
|---|---|---|
| `Prognosis` | netstandard2.0; netstandard2.1 | Core — graph modeling, aggregation, reporting, monitoring |
| `Prognosis.DependencyInjection` | netstandard2.0; netstandard2.1 | M.E.DI integration — assembly scanning, fluent builder, hosted monitoring |
| `Prognosis.Reactive` | netstandard2.0; netstandard2.1 | System.Reactive extensions — Rx polling, push-triggered reports, change streams |

Extension packages reference only the core. The core has zero project references outward.

## Core Types

- **`HealthNode`** — sealed, private constructors. Created via `CreateDelegate(name, healthCheck)`, `CreateDelegate(name)`, or `CreateComposite(name)`. Owns edges (`DependsOn`/`RemoveDependency`), aggregation, and upward propagation.
- **`HealthGraph`** — materialized read-only view of the graph. Entry point for `CreateReport`, `RefreshAll`, and observables (`StatusChanged`, `TopologyChanged`). Implements `IDisposable` to detach from nodes.
- **`HealthStatus`** — enum: `Healthy(0)`, `Unknown(1)`, `Degraded(2)`, `Unhealthy(3)`. Worst-is-highest.
- **`Importance`** — enum: `Required`, `Important`, `Optional`, `Resilient`. Controls how dependency failures propagate.
- **`HealthEvaluation`** — `(HealthStatus, string? Reason)` record. Implicit conversion from `HealthStatus`.
- **`HealthDependency`** — weighted edge: `(HealthNode, Importance)`.
- **`HealthReport`** / **`HealthSnapshot`** — flat report DTOs. `HealthReport.Root` carries the graph root's aggregated status. `DiffTo()` for change detection.
- **`HealthTreeSnapshot`** — tree-shaped snapshot preserving hierarchy for nested JSON.
- **`HealthMonitor`** — timer-based poller wrapping `HealthGraph.RefreshAll()`.
- **`IHealthAware`** — marker interface with a single `HealthNode` property.

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

## DI Integration

`services.AddPrognosis(...)` — scans assemblies for `IHealthAware`, reads `[DependsOn<T>]` attributes, builds a shared node pool lazily, wires edges, resolves roots. `PrognosisBuilder` provides the fluent API (`ScanForServices`, `AddDelegate<T>`, `AddComposite`, `MarkAsRoot`, `UseMonitor`).

## Conventions

- C# latest (14.0) with .NET Standard 2.0/2.1 downlevel targeting.
- Polyfills in `Polyfills/` (`IsExternalInit`, `ReferenceEqualityComparer`).
- All enums use `[JsonStringEnumConverter]`.
- Records and sealed classes preferred. No inheritance hierarchy on `HealthNode`.
- ADRs tracked in `docs/adr/`.
