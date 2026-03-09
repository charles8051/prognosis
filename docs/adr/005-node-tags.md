# ADR-005: Node Tags — Static Metadata on HealthNode

**Status:** Accepted  
**Date:** 2025-07-01  
**Drivers:** Real-world need to associate environment, ownership, and service metadata with health nodes for richer reporting, logging, and alerting.

## Context

Health nodes in production systems carry implicit contextual metadata that is currently unrepresentable in the model:

| Metadata | Examples |
|---|---|
| Environment | `env=prod`, `region=us-east-1` |
| Ownership | `owner=platform-team`, `tier=critical` |
| Versioning | `version=2.4.1`, `deploy-id=abc123` |
| Classification | `category=database`, `sla=99.9` |

Without a first-class metadata mechanism, users work around this by embedding metadata in node names, maintaining external dictionaries keyed by name, or building parallel data structures. All approaches lead to drift and duplication.

A common alternative request is a distinct `RichHealthReport` type that pairs status snapshots with metadata. That surface split would require callers to opt into a different API, break `DiffTo`, add a parallel Rx stream, and impose extra complexity in the DI and Reactive packages — for no structural benefit over enriching the existing snapshot types directly.

## Decision

### Shape

Add a `Tags` property to `HealthNode` typed as `IReadOnlyDictionary<string, string>`:

- **String keys and values** — covers all real metadata cases, serializes to JSON without custom converters, requires no type-erasure, and avoids `object?` boxing.
- **Non-null, empty by default** — callers never need a null-check; nodes with no tags behave identically to today.
- **Immutable after the `WithTags` call** — tags describe a node's *identity*, not its runtime state. Nodes are created once and live for the process lifetime. There is no use case for mutating tags at runtime. This eliminates all threading concerns for the tags field entirely.

### API

```csharp
// Fluent — chains with WithHealthProbe, matches existing style.
HealthNode.Create("AuthService")
    .WithHealthProbe(check)
    .WithTags(new Dictionary<string, string>
    {
        ["env"]   = "production",
        ["owner"] = "platform-team",
        ["tier"]  = "critical",
    });

// Read.
node.Tags["owner"]; // "platform-team"
```

`WithTags` returns `this` for fluent chaining, consistent with `WithHealthProbe`.

### Propagation into snapshots

`Tags` is threaded into `HealthSnapshot` and `HealthTreeSnapshot` as an optional trailing constructor parameter defaulting to `null`. `null` and an empty dictionary are treated identically by consumers — the parameter is nullable purely to keep serialized output clean (no `"tags": {}` noise on nodes with no tags).

`HealthGraph.RebuildReport` and `HealthNode.BuildTreeSnapshot` each read `node.Tags` and pass it through. No logic or aggregation is performed on tags — they are copied as-is from the node.

### No new report type

`HealthReport`, `HealthSnapshot`, and `HealthTreeSnapshot` are already the right abstraction. Enriching them is strictly additive. `DiffTo` continues to diff on `HealthStatus` only — tag mutations are not health state transitions.

### DI integration

`NodeConfigurator` in `Prognosis.DependencyInjection` gains a `WithTags` pass-through that delegates to `HealthNode.WithTags`.

## Consequences

### Positive

- **Additive, non-breaking.** New optional parameter on record constructors; existing call sites compile unchanged.
- **Single API.** `GetReport()`, `CreateTreeSnapshot()`, and all Rx streams carry metadata automatically — no opt-in required.
- **Clean JSON output.** `IReadOnlyDictionary<string, string>` serializes as a plain JSON object; no extra converters needed.
- **No threading complexity.** Tags are write-once at construction; no lock, no volatile, no copy-on-write needed.
- **DiffTo unaffected.** Status-change diffing is unrelated to metadata; no behavior change.

### Negative / Trade-offs

- **String-only values.** Structured values (e.g., a nested object) cannot be represented. In practice, all known metadata use cases are scalar strings; structured values belong in an external registry, not on a health node.
- **No runtime mutation.** Tags cannot be changed after `WithTags`. Any scenario requiring dynamic metadata should model it as a separate node or as the `Reason` field on a `HealthEvaluation`.
