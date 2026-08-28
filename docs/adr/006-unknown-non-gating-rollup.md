---
id: ADR-006
status: accepted
governs:
  - HealthStatus.cs
  - HealthNode.cs
  - Prognosis.Tests/HealthAggregatorTests.cs
relates:
  - ADR-002
  - ADR-005
  - ADR-007
  - ADR-008
  - ADR-009
  - ADR-010
  - ADR-011
---

# ADR-006: `Unknown` Is Strictly Non-Gating in the Rollup

**Status:** Accepted
**Date:** 2026-06-11
**Drivers:** Make the "not-yet-determined" startup state a documented, first-class guarantee so startup-health gates and run-signals can rely on it; prevent a future rank change from silently turning `Unknown` into a failure.

## Context

`HealthStatus` is ordered worst-last so that `Math.Max` / comparisons naturally pick the worst status (`HealthStatus.cs`):

```csharp
Healthy   = 0,
Unknown   = 1,
Degraded  = 2,
Unhealthy = 3,
```

`Unknown` is the **not-yet-probed startup state** — see `README.md`'s status table: `Unknown = Not yet probed (startup state)`. A node that has never run its intrinsic check (or a service that has not yet reported in) sits at `Unknown` until its first real evaluation.

The rollup in `HealthNode.Aggregate` (the rank-fold, `HealthNode.cs`) computes a parent's effective status from its intrinsic status plus each dependency's contribution, keeping the worst via `IsWorseThan`. Because `Unknown` ranks **below** `Degraded` and `Unhealthy`, an `Unknown` child can only ever raise a parent *to* `Unknown` — never to `Degraded` or `Unhealthy`. Walking the four `Importance` levels for an `Unknown` child confirms this:

| Importance | Contribution for an `Unknown` child | Effect on an otherwise-`Healthy` parent |
|---|---|---|
| `Required`  | `Unknown` (status passes through) | parent → `Unknown` |
| `Important` | `Unknown` (only `Unhealthy` is remapped, to `Degraded`) | parent → `Unknown` |
| `Optional`  | `Healthy` (dependency health is ignored entirely) | parent stays `Healthy` |
| `Resilient` | `Unknown` (the `Degraded` cap triggers only on `Unhealthy` siblings) | parent → `Unknown` |

So the non-gating property is **already true today** — but only *implicitly*, as a side effect of the rank ordering and the per-importance `switch` in `Aggregate`. Nothing names it as a guarantee, and only the `Required` and `Important` cases were pinned by a test; `Optional` and `Resilient` were uncovered.

Consumers are starting to depend on this property directly:

- **A consumer's startup warmup gate** treats an `Unknown` rollup as "still warming up," not "failed," and must not block or fail-fast on a subtree that is merely still probing.
- **A control plane's run-signal** already uses `HealthStatus.Unknown` as a *pending* value before the first real evaluation, and relies on a pending child not dragging an aggregate into `Degraded`/`Unhealthy`.

A silent change to the enum ranks (or to the `Aggregate` per-importance mapping) would break both consumers with no compile error and no failing test. This ADR closes that gap.

## Decision

**Pin the guarantee and test it.** `Unknown` is **strictly non-gating** in the rollup: a `Required`, `Important`, `Optional`, or `Resilient` dependency whose status is `Unknown` raises its parent **at most to `Unknown`** — never to `Degraded` or `Unhealthy`.

Concretely:

1. **This ADR is the named guarantee.** The non-gating property is now a documented invariant of `HealthNode.Aggregate`, not an incidental consequence of the enum's integer values.
2. **All four `Importance` × `Unknown` rows are pinned by tests.** `Prognosis.Tests/HealthAggregatorTests.cs`'s `Aggregate_PropagatesAccordingToImportance` theory covers every `Importance` value against an `Unknown` child, asserting the rolled-up status the table above prescribes (`Unknown` for `Required` / `Important` / `Resilient`; `Healthy` for `Optional`). The first two rows already existed; this ADR adds `Optional` and `Resilient`.
3. **A cross-reference points back here.** `HealthStatus.cs` references this ADR next to the `Unknown` member so the guarantee is discoverable from the type that defines the rank.

No code behavior changes. No new state, factory, or default is introduced — the guarantee is the *current* behavior, now made explicit and regression-protected.

### Alignment with prior ADRs

- **ADR-002 (single non-null cache, cache-only model).** This ADR adds **no new lifecycle state** — no `HasBeenEvaluated` bit, no pending sentinel, no second cache field. `Unknown` remains an ordinary `HealthStatus` value flowing through the one evaluation path (`NotifyChangedCore` → `Aggregate`), consistent with ADR-002's single non-null `_cachedEvaluation`.
- **ADR-005 (node tags — additive enrichment).** Like ADR-005, this is a strictly **additive** change: a doc, test rows, and a comment. It pins existing behavior and breaks no call site, following ADR-005's precedent of enriching the model without a parallel type or an opt-in surface.

## Consequences

### Positive

- **A named, regression-protected invariant.** "Startup `Unknown` never gates a parent" is now a contract with tests behind it, not folklore that happens to fall out of the enum ranks.
- **Safe foundation for startup-health gates.** A warmup gate and a pending-state run-signal can rely on an `Unknown` subtree staying non-failing while it probes, without each consumer re-deriving the property from the enum ordering.
- **Full `Importance` coverage.** All four importance levels against `Unknown` are exercised, closing the `Optional` / `Resilient` test gap.

### Negative / Trade-offs

- **A future "make `Unknown` gating" change is now a conscious act.** If someone later wants `Unknown` to escalate (e.g. reorder the enum so `Unknown` outranks `Degraded`, or special-case it in `Aggregate`), the pinned tests will fail and this ADR must be explicitly superseded. That is the intended cost: the property can no longer change silently.
- **The guarantee is coupled to the rank ordering.** The non-gating property is enforced by `Unknown < Degraded < Unhealthy` plus the `Aggregate` mapping. The tests guard the observable behavior, but the *mechanism* is the rank; anyone editing `HealthStatus` ranks must read this ADR first (the cross-reference in `HealthStatus.cs` exists for exactly this reason).
