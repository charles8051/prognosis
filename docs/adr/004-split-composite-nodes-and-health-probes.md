# ADR-004: Split Composite Nodes and Health Probes into Distinct Concepts

**Status:** Proposed
**Date:** 2025-01-21
**Drivers:** API clarity, eliminate dual-modeling ambiguity, single-responsibility node roles

## Context

`HealthNode` currently allows any combination of intrinsic health check (`WithHealthProbe`) and dependency edges (`DependsOn`) on a single node. This creates two equivalent ways to model the same scenario:

| Approach | Code |
|---|---|
| Intrinsic probe + dependency | `Create("Auth").WithHealthProbe(check).DependsOn(db, Required)` |
| Pure aggregation over probe node | `Create("Auth").DependsOn(authProbe, Required).DependsOn(db, Required)` |

Both produce the same runtime health, but the duality makes the API harder to reason about. Users must decide which pattern to use, and mixing both within a single graph creates inconsistent topology shapes.

In practice, existing examples already separate these roles informally:

| Node | Role | Intrinsic check? | Children? |
|---|---|---|---|
| `DatabaseService` sub-nodes (connection, latency, pool) | Probe | Yes | No |
| `DatabaseService.HealthNode` | Composite | No | Yes |
| `EmailProvider` | Probe | Yes | No |
| `AuthService` | Composite | No | Yes (Database, Cache) |
| `Application` | Composite | No | Yes (AuthService, NotificationSystem) |

The pattern is clear: leaf nodes measure health, interior nodes aggregate it. The API does not enforce this separation.

## Decision

### Conceptual model

Introduce two distinct node roles:

- **Probe** — a leaf node with an intrinsic health check. Produces a single health output. Has no children. Represents a measurable health signal (connectivity, latency, pool saturation, etc.).
- **Composite** — a named point of interest with no intrinsic health. Derives its health entirely from its dependencies. Consumes one or more health inputs (probes or other composites) and provides a single aggregated health output.

### Enforcement layer

Enforce the distinction at the **DI builder layer** (`PrognosisBuilder`), not at the core `HealthNode` type:

```csharp
// Leaf — intrinsic check, no children.
public ProbeConfigurator AddProbe(string name) { ... }
public ProbeConfigurator AddProbe<TService>(
    Func<TService, HealthEvaluation> healthCheck) where TService : class { ... }

// Aggregation point — children only, no intrinsic check.
public CompositeConfigurator AddComposite(string name) { ... }
```

`ProbeConfigurator` exposes `WithHealthProbe<T>(...)` but **not** `DependsOn(...)`.
`CompositeConfigurator` exposes `DependsOn(...)` but **not** `WithHealthProbe(...)`.

Both configurators produce a standard `HealthNode` internally — the split is a builder-time constraint, not a runtime type hierarchy.

### Core `HealthNode` remains unified

The core type keeps its current shape. Advanced users wiring nodes manually (without the DI builder) retain full flexibility. This avoids a breaking change to the core library and keeps the propagation/aggregation engine simple.

### Deprecation of `AddNode`

`AddNode(string)` (which returns a `NodeConfigurator` allowing both probe and dependency configuration) is deprecated in favor of the explicit `AddProbe` / `AddComposite` methods. It may be retained as an escape hatch for edge cases.

### Source generator updates

`ServiceNodeDiscoveryGenerator` would classify discovered nodes:
- Classes with a `HealthNode` property created via `Create(name).WithHealthProbe(...)` and no `[DependsOn]` attributes → emitted as `AddProbe`.
- Classes with a `HealthNode` property and `[DependsOn]` attributes but no `WithHealthProbe` → emitted as `AddComposite`.
- Classes with both → emitted as `AddNode` (deprecated path) with an analyzer warning suggesting the split.

## Consequences

### Positive

- **Eliminates modeling ambiguity.** One way to model each scenario — probes measure, composites aggregate.
- **Self-documenting topology.** Reading a builder configuration immediately reveals which nodes are measurement points vs. aggregation points.
- **Compile-time guardrails.** `ProbeConfigurator` and `CompositeConfigurator` have disjoint APIs — calling `DependsOn` on a probe is a compile error.
- **Aligns with existing practice.** The examples already follow this pattern informally; this makes it explicit.
- **Non-breaking at the core.** `HealthNode` is unchanged. Only the DI builder layer adds new types.

### Negative

- **Larger builder API surface.** Two new configurator types plus new `PrognosisBuilder` methods.
- **Does not prevent misuse at the core level.** Users bypassing the builder can still call both `WithHealthProbe` and `DependsOn` on the same node. This is by design — the core is intentionally flexible.
- **Migration cost.** Existing `AddNode` call sites need to be reclassified as `AddProbe` or `AddComposite`. Mitigated by the deprecation path.
- **"Both" case requires an extra node.** A service with its own health check AND dependencies (e.g., `AuthService` with a connectivity probe plus Database/Cache edges) must be modeled as a composite depending on its own probe node. This adds a node but makes the topology more explicit.

## Alternatives Considered

### Enforce at the core type level (sealed subclasses or runtime guard)

Split `HealthNode` into `HealthProbe` and `CompositeNode` (or throw at runtime if both `WithHealthProbe` and `DependsOn` are called). Rejected because it would be a breaking change across every package, test, and example, and it removes legitimate flexibility for advanced scenarios.

### Do nothing — document the recommended pattern

Add guidance to README and XML docs recommending the probe/composite split without enforcing it. Rejected because documentation-only guidance is easily ignored, and the ambiguity remains a source of confusion for new consumers.

### Enforce via analyzer only

Add a diagnostic that warns when a node has both an intrinsic check and dependencies. Less invasive than type-level enforcement but provides no compile-time API guidance. Rejected as insufficient — the goal is to make the right thing easy, not just warn about the wrong thing.

## Implementation Plan

1. Add `ProbeConfigurator` and `CompositeConfigurator` types to `Prognosis.DependencyInjection`.
2. Add `AddProbe` and `AddComposite` methods to `PrognosisBuilder`.
3. Deprecate `AddNode` with `[Obsolete]` pointing to the new methods.
4. Update `ServiceNodeDiscoveryGenerator` to emit `AddProbe` / `AddComposite` where possible.
5. Add analyzer diagnostic for nodes with both probe and dependencies (informational).
6. Migrate examples to use the new API.
7. Update tests.
8. Update `context.md` and `README.md`.
