---
id: ADR-013
status: proposed
governs:
  - HealthSnapshot.cs
  - TemporalState.cs
  - HealthGraph.cs
  - HealthReportComparer.cs
relates:
  - ADR-005
  - ADR-008
  - ADR-009
  - ADR-010
  - ADR-011
  - ADR-012
---

# ADR-013: A Structured Temporal-State Snapshot Field — Staleness, Flap, and Deadline on the Wire

**Status:** Proposed
**Date:** 2026-07-31
**Drivers:** The substrate has grown per-node temporal state in three places — lease freshness/age (ADR-010), flap counts (ADR-011 §8), and pending policy deadlines (ADR-011 §6) — and every one of them is **invisible to the snapshot surfaces.** A full `HealthSnapshot` today carries only `(Name, Status, Reason, Tags)`, and ADR-012 §5 forbids stuffing the varying values (an age, a count) into `Reason`. So there is no sanctioned path from the state the library now computes to the two consumers that serialize it: the northbound heartbeat (`HealthReport`) and a device-side flight-recorder `.clef` capture (which embeds the same serialization). A multi-day investigation into a flapping peripheral is the poster child — a flap count in the snapshot would have made that diagnosis a glance. This ADR resolves the structured-field open question the substrate has deferred in three documents at once: ADR-010 §4 / OQ1 (staleness on the wire), ADR-011 §8 / OQ3 (flap on the wire), and ADR-012 OQ1.

## Context

### The data exists; the wire cannot see it

After ADR-010 (leases) and ADR-011 (policies) land, each node may carry:

- **Lease staleness** — whether an affirmed verdict is fresh, in its stage-one `Unknown` decay, or escalated, plus which `Ttl`-band the age has reached (ADR-010's prefix taxonomy: `PendingReasonPrefix`/`StaleReasonPrefix`, band-quantized per ADR-012 §5).
- **Flap** — how many raw status transitions the node has recorded in a recent window (ADR-011 §8, `NodeObservationHistory.Transitions` + `FlapWindow.Count`).
- **Policy phase** — whether the node is currently holding a raw fault under a debounce window, or suppressing a not-yet-live verdict under a grace window, and when that hold/suppression is scheduled to resolve (ADR-011 §6, `PendingDeadline`).

All of it is readable *in-process* — `HealthNode.Observe()`, `HealthLease` state, `FlapWindow.Count` — and none of it crosses a snapshot. A control plane, a heartbeat aggregator, or a `.clef` reader sees only the effective status and its reason. The reason is deliberately band-stable (ADR-012 §5), so the *precise* age, the *exact* flap count, and the *time-to-deadline* are gone by the time the picture leaves the process.

### Why a reason string cannot be the carrier

ADR-012 §5 is normative and settled: an emitted `Reason` is an explanation of the current status and MUST be stable between meaningful changes; it MUST NOT embed a continuously- or monotonically-varying value (an age in seconds, a live counter, a wall-clock instant, a running total). A flap count is a live counter; a time-to-deadline counts down every wave; a lease age climbs monotonically. Every one of them is exactly the shape §5 forbids, because putting it in `Reason` — which participates in `HealthReportComparer.Equals` (ADR-012 §1) — makes every wave's report unequal to the last and fires the report stream on nodes whose health did not change (the report-churn defect class). ADR-012 §5 names the sanctioned home in the same breath: *"continuously-varying data belongs in a structured field on the snapshot."* This ADR builds that field.

### Why now, in Phase 2, and not later

Three reasons, in order of weight:

1. **The producing code is being written now.** Phase 2's ADR-011 implementation refactors `NotifyChangedCore` — the hottest method in the library — to compute and store `NodeObservationHistory` per node. Reading that history plus lease state at `RebuildReport` time to populate a snapshot field is nearly free while the code is open. Retrofitting it after is a *second* pass over the hottest method, for no benefit.
2. **A wire-schema addition is cheapest before stable.** The library is on `8.0.0-beta`. Adding a `HealthSnapshot` member crosses the heartbeat schema (ADR-008's wire analysis), and that staged-rollout cost is far lower on a beta than after `8.0.0`. Doing it in the same phase that introduces the data it carries avoids a gratuitous second schema bump.
3. **The consumer demand is already on file.** The peripheral-flap and freshness investigations wanted exactly this data at a glance, and a control-plane rollup change already on file would be the first consumer to parse it. The field can ship now and be *consumed* later without a further schema change, because it is additive and sparse.

## Decision

Add one sparse, optional, structured field to `HealthSnapshot`, populate it in `RebuildReport`, and keep it out of report-change detection.

### 1. `HealthSnapshot` gains `TemporalState? Temporal`

```csharp
public sealed record HealthSnapshot(
    string Name,
    HealthStatus Status,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    TemporalState? Temporal = null);      // NEW — null when the node has no lease/policy/flap state
```

`Temporal` is **null** for a node that carries no lease and no policy and has not flapped — which is the overwhelming majority of nodes in a healthy graph, so a quiescent report pays **negligible** additional wire cost: with null-omission serialization the member is omitted entirely (zero bytes), and even without it a null member is a few bytes (`"Temporal": null`) and never a populated object (see §2 "Wire encoding" — null-omission is what the implementation configures to make the omission real). It gains content only when something is actually stale, flapping, or held — which is precisely the moment a consumer wants it. Sparseness is what makes "always serialize it" affordable: there is no north/diagnostic split to maintain, because a stale or flapping node is exactly what you want to send north anyway.

### 2. `TemporalState` is structured and numeric, never a varying string

```csharp
public sealed record TemporalState(
    StalenessMarker? Staleness = null,   // Fresh | Expired | Escalated — lease taxonomy (ADR-010); null when not leased
    int? TtlBand = null,                 // Ttl-band the lease age reached; 0 fresh, >=1 expired, null when not leased or escalated
    int FlapCount = 0,                   // raw status transitions in the library flap window (ADR-011 §8); >= 0
    bool InDebounceHold = false,         // a debounce policy is currently holding a raw fault (ADR-011 §1)
    bool InGraceWindow = false,          // a grace policy is currently suppressing a not-yet-live verdict (ADR-011 §3)
    TimeSpan? PendingDeadline = null);   // time from this capture until the node's next policy deadline; null when none

public enum StalenessMarker { Fresh, Expired, Escalated }
```

Every member is a number, an enum, or a bool. This is the structured field ADR-012 §5 points to — a numeric field is fine where a varying reason string is not. Specific choices:

- **`Staleness` mirrors ADR-010's prefix taxonomy**, derived at report-build time from the node's effective `(Status, Reason-prefix)`. The full mapping the implementation follows (§4 references it):

  The single decision is **does the effective reason start with `StaleReasonPrefix`?** — every row keys on that, so no `(status, prefix)` combination is left unpinned:

  | Node is leased? | Reason starts with `StaleReasonPrefix`? | Effective status | `Staleness` | `TtlBand` |
  |---|---|---|---|---|
  | no | — | — | `null` | `null` |
  | yes | no (affirmed verdict, or `PendingReasonPrefix` seed) | any (`Healthy`/`Degraded`/`Unhealthy`/**`Unknown`**) | `Fresh` | `0` |
  | yes | yes | `Unknown` | `Expired` | band ≥ 1 |
  | yes | yes | `Degraded`/`Unhealthy` | `Escalated` | `null` (past banding) |

  `Staleness` is `null` only for a non-leased node, so a reader distinguishes "not leased" from "a fresh leased node." The second row deliberately covers **`Unknown` with no `StaleReasonPrefix`** — a producer that affirmed a bare `Unknown` verdict within `Ttl`, or the never-affirmed `PendingReasonPrefix` seed — as `Fresh`: the marker keys strictly on the stale prefix, and only the library's own decay synthesizes that prefix (ADR-010 `HealthLease.Decay`), so an `Unknown` without it is an affirmed/seeded verdict, not a stale one. `TtlBand` is `null` past escalation because ADR-010 bands only the stage-one `Unknown` window; the escalated reason carries no band to mirror. **Documented lossy point:** `Fresh` deliberately covers both *affirmed-and-within-`Ttl`* and *seeded-pending (never affirmed)* — both are operationally not-yet-stale. These have different `Reason` prefixes (none vs `PendingReasonPrefix`); a consumer that must distinguish the two consults `Reason`. The brief fixes the enum at `Fresh | Expired | Escalated`, so this ADR documents the collapse rather than adding a fourth marker; a later ADR may split `Fresh`/`Pending` if a consumer needs it.
- **`TtlBand`** carries the same band the reason string is quantized to (ADR-012 §5) as a raw integer. It is **`0` for a fresh leased node** (band-symmetric: a consumer reading "current band, default 0" needs no null special-case), **`>= 1` in the `Expired` stage**, and **`null` in two cases: a non-leased node** (no lease band applies) **and an escalated node** (ADR-010 bands only the stage-one `Unknown` window, so past escalation there is no band to mirror). A consumer therefore reads `TtlBand` together with `Staleness`: `null` band + `null` staleness = not leased; `null` band + `Escalated` = past banding.
- **`FlapCount`** is `FlapWindow.Count` over the node's raw transition history for a **library-fixed window** — fixed for the same reason the `Transitions` bound is fixed (ADR-011 Consequences): deterministic, implementation-independent reads. It is a **non-negative** count (`int` for CLS-compliant interop; documented as `>= 0` on the field, since `FlapWindow.Count` never returns negative). The window is a library constant, documented on `TemporalState`.
- **`PendingDeadline`** is stored **relative to the capture instant** (`node.PendingDeadline - captureNow`), not as an absolute wave-time, so a `.clef` reader hours later reads "this node was ~12 s from gating when captured" without needing the graph's construction epoch. It is the per-node deadline; the graph-level minimum is surfaced separately as `HealthGraph.NextTemporalDeadline` (ADR-011 §6) and is **not** duplicated here.

**The nullable/non-nullable split follows one rule:** a member is nullable when *absence carries meaning distinct from its default* — `Staleness`/`TtlBand`/`PendingDeadline` are `null` to mean "this dimension does not apply to this node" (not leased / no pending deadline), which a consumer must be able to tell apart from a real zero. A member is non-nullable when *the default IS the no-signal value* — `FlapCount = 0`, `InDebounceHold = false`, `InGraceWindow = false` each mean "nothing happening" at their default, so a null would be redundant. This keeps a populated `Temporal` compact and unambiguous.

**Wire encoding.** The library's snapshot types are plain records serialized by the consumer's serializer (System.Text.Json in-repo, per `Prognosis.csproj`); `TemporalState` follows suit. Its enum/bool/int members are encoder-stable. Two points the *implementation* pins rather than this doc assuming:
- The **"zero cost when null" claim assumes null-omission serialization** (`JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }` or the equivalent on whichever encoder a path uses). With default System.Text.Json a null member is written as `"Temporal": null` (~17 bytes), not omitted — cheap, but not zero. The heartbeat and `.clef` paths must use the same encoder configuration for the sparse-cost and old-reader claims to hold; the implementation configures null-omission on the snapshot serializer.
- `PendingDeadline`'s `TimeSpan` encoding is **encoder-dependent** (System.Text.Json emits an ISO-8601 duration string; ticks-based encoders emit a long). The implementation pins the encoding to the same convention the rest of `HealthReport` already uses on each path; a cross-encoder consumer treats it under ADR-008's staged-rollout rules, not as a guaranteed-identical byte shape.

### 3. `Temporal` is excluded from report-change detection — the load-bearing constraint

**`Temporal` MUST NOT participate in `HealthReportComparer.Equals` / `GetHashCode`.** It is excluded exactly like `Tags` (ADR-012 §3). This is the whole reason the field is safe: it carries continuously-varying numbers (a flap count that ticks, a deadline that counts down, an age band that climbs), and a varying value in the equality key is precisely the per-wave churn ADR-012 was written to eliminate — every wave's report would compare unequal to the last, firing `StatusChanged` on nodes whose health did not change, while `SelectHealthChanges` (status-keyed) stayed silent. That is the report-churn defect, re-entered through a new field.

The varying numbers ride along for **point-in-time readers** — a diagnostic snapshot, a periodic heartbeat, a `.clef` frame — but they never drive the change-detection stream. Change detection stays exactly where ADR-012 pinned it: the report stream keys on `(Name, Status, Reason)` per node plus the `Root` and node count (ADR-012 §1); the transition stream keys on `Status`. A real status change still emits via `Status`; flap and staleness are **level-read** off the field, not edge-triggered.

Concretely, this requires **no comparer change beyond the explicit-triple keying ADR-012 §2/§3 already prescribe**: because `HealthReportComparer` compares snapshots on the explicit `(Name, Status, Reason)` triple rather than the record `==`, a new `HealthSnapshot` member does not enter equality. That explicit-triple comparer landed with the leased-verdicts implementation ("comparer coherence (ADR-012)"), so in the current tree `SnapshotKeyEquals`/`SnapshotKeyHash` already key on the triple and `Temporal` is excluded by construction. This does **not** rest on a reader's memory: the sequencing that makes it true is ADR-012 §Status-and-merge-order (the comparer coherence lands with the substrate implementation, which it did), and adding `Temporal` to `HealthSnapshot` is safe only *after* that landed — which it has. Were the two ever reordered (a `Temporal` field added while a comparer still used record `==`), `Temporal` would enter the key and churn the stream — exactly the report-churn defect.

This ADR **amends ADR-012 §3** to add `Temporal` to the stated exclusion list (`Tags` and `Temporal`), so the intent is recorded and no future refactor reverts to record equality and folds `Temporal` back into the key. `HealthReportComparer.cs` is in this ADR's `governs` set because ADR-013 **governs the comparer's continued adherence** to the explicit-triple exclusion (it constrains the code path, it does not change it here). The mechanical guard is a **regression test that is tracked-and-deferred, not yet in the tree**: it lands with the field population (PR C) and is an **explicit merge gate for that PR** — two `HealthSnapshot` values with identical `(Name, Status, Reason)` but differing `Temporal` must compare equal under `HealthReportComparer.Instance.Equals` and hash equally, and a regression to record `==` must fail it. This doc records the requirement; PR C is where a reviewer verifies the test exists and pins the exclusion. Until PR C lands, the exclusion rests on the explicit-triple comparer plus author/reviewer discipline, which is exactly why the test is a stated gate rather than a claim that it already runs.

### 4. Populated at `RebuildReport`, from state that already exists

`HealthGraph.RebuildReport` builds each `HealthSnapshot`. It gains a per-node `TemporalState?` computed from:

- the node's `NodeObservationHistory` (ADR-011 §4) — `HasEverBeenLive`, `PendingDeadline`, `Transitions` (for `FlapCount`), and the in-hold / in-grace phase the chain recorded;
- the node's lease state (ADR-010) — whether it is leased, and the staleness marker + band derived from its effective evaluation's status and reason prefix;

both of which exist after the lease and policy implementations. The capture instant is the wave's threaded `now` (ADR-011 §5) when the rebuild happens inside a wave, or a fresh clock read when `GetReport()` rebuilds outside one — the same clock the rest of the substrate reads. A node with neither lease nor policy state and no transitions in the flap window yields `null`, preserving sparseness.

A consumer's diagnostic snapshot and heartbeat carry the field automatically once `HealthReport` serializes it — **no consumer change is needed for the data to appear.** Display, and control-plane parsing to *use* it, is later work; this ADR only makes the data present and correct on the wire.

## Non-goals

- **No consumer of the field in this ADR.** The field is produced and serialized; parsing/display is downstream and does not gate this.
- **No change to report-change detection semantics.** The report stream and transition stream key on exactly what ADR-012 pinned; `Temporal` is inert to both.
- **Not a second deadline surface.** The graph-level `NextTemporalDeadline` and its `TemporalDeadlineChanged` notification (ADR-011 §6/§6a) remain the pump's signal. `TemporalState.PendingDeadline` is a *per-node, point-in-time* value for readers, not a scheduling channel.
- **No wire field for producer-side grace (ADR-011 §9).** A leased node's producer-side `GraceState` lives on the producer's side of the push and never reaches the graph; it is out of the snapshot's scope by the same §7 exclusivity that keeps it off `NextTemporalDeadline`.

## Rejected alternatives

- **Carry the varying data in `Reason`.** Forbidden by ADR-012 §5 and the direct cause of the report-churn class. This ADR exists because that door is closed.
- **A separate diagnostic-only snapshot type, north-suppressed.** Considered and rejected: sparseness already makes the heartbeat cost negligible, and a stale/flapping node is exactly what you want north. A north/diagnostic split would add a schema and a suppression rule to save bytes that a null field already saves.
- **Include `TemporalState` in report equality (the naive record-`==` path).** This is the trap ADR-012 §3 guards against — a live count/age in the key churns the report stream every wave. Excluded by construction and by the §3 amendment.
- **Defer to a post-stable release.** Rejected on cost: the producing code is open now, and a beta wire addition is far cheaper than a post-`8.0.0` one. Waiting buys a second pass over the hottest method and a second schema bump.
- **A flap *rate* (transitions per unit time) instead of a windowed count.** A rate is a derived float that invites its own churn and precision debates; a windowed integer count over a fixed window is deterministic, matches `FlapWindow.Count` exactly, and is trivially thresholdable by a consumer.

## Alignment with prior ADRs

- **ADR-005 — tags are identity.** `Temporal` sits beside `Tags` in the snapshot and shares its equality treatment (excluded), but is the opposite kind of data: `Tags` is immutable identity carrying zero change-signal; `Temporal` is live derived state carrying *only* varying signal. Both are correctly out of the equality key, for symmetric reasons.
- **ADR-008 — `Unknown` is transient / wire compatibility.** The field is a new `HealthSnapshot` member and therefore crosses the heartbeat schema — the addition ADR-008's analysis governs. It is additive and sparse (null-by-default). "An old reader ignoring an unknown member is unaffected" holds unconditionally only for tolerant encoders (System.Text.Json ignores unknown members by default); a **schema-pinned** encoder (Protobuf, MessagePack-with-a-fixed-map, a hand-rolled reader) treats an unknown member per its own rule, which is exactly the staged-rollout compatibility question ADR-008 owns — deploy readers that tolerate the member before writers emit it. This ADR does not claim to sidestep that; sparseness minimizes the *cost*, ADR-008's rollout discipline governs the *safety*, and landing on beta minimizes the blast radius. `Staleness` reflects ADR-010's resolution taxonomy, which itself satisfies ADR-008's transience contract.
- **ADR-010 — leased verdicts.** `Staleness`/`TtlBand` are the structured form of ADR-010's decay taxonomy — the "staleness on the wire" its §4/OQ1 deferred. The band here equals the band ADR-010's reason string is quantized to (ADR-012 §5), so the number and the text agree by construction.
- **ADR-011 — temporal policies.** `FlapCount`, `InDebounceHold`, `InGraceWindow`, and `PendingDeadline` are the wire projection of `NodeObservationHistory` (§4) and the chain phase (§1/§3). This ADR is the resolution of ADR-011 OQ3 (flap on the wire): flap reaches the snapshot as a numeric field, never `Reason`. The graph-level `TemporalDeadlineChanged` channel (§6a) stays a separate in-process signal — deliberately not in the report — consistent with keeping scheduling off the report stream.
- **ADR-012 — the report-equality contract.** This ADR is the first-class structured field ADR-012 OQ1 framed and §5 pointed to. It amends §3's exclusion list to `Tags` and `Temporal`, and relies on §3's explicit-triple comparison so the exclusion holds without new comparer code. The two ADRs remain partitioned: ADR-012 governs what is in the report and how equality treats it; this ADR adds a member that is in the report but, by ADR-012's own rule, inert to equality.

## Consequences

### Positive

- **The substrate's temporal state becomes visible on the wire** — staleness, flap, and policy phase reach every snapshot consumer (heartbeat, `.clef`) with no per-consumer plumbing. Multi-day flap investigations become a glance.
- **Three deferred open questions close at once** (ADR-010 OQ1, ADR-011 OQ3, ADR-012 OQ1) with one field and one §3 amendment.
- **Zero cost to quiescent graphs.** A node with no lease/policy/flap yields `null`; a healthy report is byte-unchanged.

> **Amendment note (2026-08-08).** The bullet above, the "additive **and sparse**"
> justification in "Why now" (reason 3), and the sparseness argument this ADR uses to reject a
> separate diagnostic-only snapshot type all rest on the same premise: that most nodes yield `null`.
> **ADR-011 §10 (graph-wide temporal defaults) makes that premise conditional.** A graph configured
> with a `TemporalDefaults` bag materializes a policy into every in-scope node, and
> `BuildTemporalState`'s sparsity guard (`!leased && !hasGrace && !hasDebounce && flapCount == 0`)
> is then false for all of them — so a defaulted graph emits a `TemporalState` per in-scope leaf on
> every snapshot, and "a healthy report is byte-unchanged" is simply false for it. Nothing about this
> ADR's *decision* changes: the field is still optional, still additive, still out of report equality
> (§3), so there is no churn and no schema change and no consumer needs a new parser. What changes is
> the **sizing**: payload and `.clef` volume grow with in-scope leaves rather than with the number of
> temporally interesting nodes, and a consumer treating `Temporal == null` as a *signal* rather than
> an absence sees a behaviour change. Two consequences for readers of this ADR: size it for defaulted
> graphs, and read the rejection of the separate diagnostic snapshot type as resting on an argument
> that a defaulted deployment weakens. ADR-011 §10d's `AppliesTo` predicate is the available bound.
- **No comparer risk.** The exclusion is structural (explicit-triple comparison, ADR-012 §3), so the field cannot reintroduce churn even if a consumer serializes it into a diff.
- **Cheapest possible timing.** Populated in the same `NotifyChangedCore`/`RebuildReport` pass ADR-011 already touches; wire addition lands on beta.

### Negative / Trade-offs

- **A wire-schema addition, however sparse.** `HealthSnapshot` grows a member; serializers and any schema-pinned consumer must tolerate it. Mitigated by additive-and-optional shape and beta timing (ADR-008).
- **The flap window is a library constant, not a per-node knob.** A node flapping slower than the window looks quiet; one flapping much faster saturates the count. This mirrors the fixed `Transitions` bound (ADR-011) — determinism over configurability — and a consumer that needs a different window can still read `Observe()` locally.
- **`Temporal` duplicates data available via `Observe()` in-process.** Intentional: `Observe()` serves the local reader, the snapshot field serves the wire reader. They share the same source (`NodeObservationHistory` + lease state), so they cannot disagree.
- **`Staleness` is derived from the effective evaluation's reason prefix and status**, not a first-class lease flag on the snapshot. This couples the marker to ADR-010's prefix constants; those are already public, machine-checkable markers (`StaleReasonPrefix`/`PendingReasonPrefix`), so the coupling is to a stable contract, not folklore.

## Open questions

1. **A consumer-tunable flap window.** Deferred until a consumer needs a window other than the library default; `Observe()` + `FlapWindow.Count` already allow a local reader to pick its own.
2. **Whether `Staleness` should be a first-class lease-emitted field** rather than derived from the reason prefix at report-build time. Deferred; the derived form is correct and adds no lease-side state, and a first-class field can supersede it without changing the wire shape (the enum stays).
3. **Control-plane display consumption.** Out of scope here — this ADR only guarantees the data is present and correct; using it is a later phase with its own gate.
