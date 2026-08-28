---
id: ADR-010
status: proposed
governs:
  - HealthNode.cs
  - HealthLease.cs
  - Prognosis.Tests/HealthLeaseTests.cs
relates:
  - ADR-002
  - ADR-004
  - ADR-006
  - ADR-008
  - ADR-009
  - ADR-011
  - ADR-012
---

# ADR-010: Leased Verdicts — Opt-In TTL Decay for Push-Fed Nodes

**Status:** Proposed
**Date:** 2026-07-30
**Drivers:** A July 2026 silent-failure sweep of a consuming application found the same defect independently reimplemented — or rather, independently *not* implemented — across ~8 of its health probes: a producer pushes verdicts into a node via a caching delegate, the producer dies, and the node reports the last verdict forever. Prognosis cannot detect this from the pull side, because the delegate answers every evaluation. Only the producer knows the data's age, and eight producers each had to remember a staleness guard; zero did.

> **Revision note (2026-07-31).** Amends the still-`proposed` ADR for three pre-implementation
> findings surfaced in review and by the ADR-011 consumer mapping:
> **the stage-one decay reason is band-quantized rather than emitting whole seconds, so a
> dead producer no longer churns the report stream every wave — §2, per the report-equality contract
> ADR-012 pins;
> **the wave-source dependency is promoted from a Negative/Trade-off bullet to an
> **adoption-blocking requirement** (§6), because the motivating consumer has no periodic graph wave;
> **the forward-compat worry that a future damping policy could suppress lease escalation is
> now resolved **structurally** by ADR-011 §7 (leases and policies are mutually exclusive per node),
> recorded in Alignment. No decision reverses; the two-stage decay, clock ownership, and target set
> are unchanged.

## Context

### The bug class: a probe that answers is not a probe that knows

`HealthNode`'s intrinsic check is a pull: `NotifyChangedCore` calls `_intrinsicCheck()` on every
wave, and `WithHealthProbe` / `ReplaceHealthProbe` install the delegate (ADR-004). That contract is
right for delegates that *compute* health when asked — a connection flag, a queue depth read. But a
recurring consumer shape feeds the node from the other direction: a background pump samples a
subsystem on its own cadence and the delegate merely returns the cache:

```csharp
// The push-fed pattern, ~8 times over in the sweep:
HealthEvaluation cached = HealthEvaluation.Healthy;
node.ReplaceHealthProbe(() => cached);

// somewhere else, a pump that can die:
while (true)
{
    cached = SampleSubsystem();          // throws every tick? wedges? cached freezes.
    await Task.Delay(_interval);
}
```

When the pump dies — throws every tick, wedges on I/O, or never starts — the node keeps reporting
the last verdict, or the `HealthNode.Create` `Healthy` default if it never sampled, forever. The
sweep's worst instance: a backlog-depth advisory node read `Healthy` *exactly when* its backing
SQLite ledger was unreadable — the failure mode the node exists to catch is the one that freezes it
in green.

Prognosis is structurally blind to this. It polls the delegate and gets a fresh-looking answer every
evaluation; "update time" is always now. The freshness of the *answer* says nothing about the
freshness of the *data* behind it. Only the producer knows its own cadence — which is why the fix
must be a declaration the producer makes (`ttl`) at the point where it wires the node, and why a
library that never hears from the producer again must be the party that notices.

The sweep's own conclusion is the requirement here: eight services each had to remember a
consumer-side staleness guard, and zero did. A helper convention ("wrap your cache in this pattern")
recreates the same eight opportunities to forget. A library-level node mode, where installing the
push surface *is* installing the guard, cannot be forgotten — the producer physically has no way to
push a verdict without also declaring its shelf life.

### What the library has to work with

- The intrinsic-check slot is a single `volatile Func<HealthEvaluation>` (`HealthNode._intrinsicCheck`),
  already swappable at runtime via `ReplaceHealthProbe`. `HealthNode` is sealed with no inheritance
  hierarchy (project convention), so "a new node subclass" is not on the table.
- `ReportStatus` is the existing push: it overwrites `_cachedEvaluation` with a skip flag and lets
  the *next* delegate evaluation naturally replace it. It is a one-shot interjection, not a standing
  verdict — exactly wrong for the pump shape, which needs the pushed verdict to persist *and* expire.
- The core reads **no clock anywhere**. `HealthMonitor` owns cadence via `Task.Delay`; no
  `DateTime`/`DateTimeOffset`/`TimeProvider` appears in the library. Whatever this ADR adds must not
  scatter wall-clock reads through the pure evaluation path.
- ADR-006 pins `Unknown` as strictly non-gating; ADR-008 pins `Unknown` as transient-by-contract
  ("a node MUST NOT rest at `Unknown`; every `Unknown` MUST have a resolution path") and supplies
  `HealthEvaluation.Unknown(reason)`. Both constrain the decay target, and §4 below leans on both.

## Decision

Introduce an opt-in **leased-verdict mode** for a node: the producer pushes verdicts with
`Affirm(verdict)`, each push renews a lease, and when the lease expires without re-affirmation the
node's evaluation **decays in two stages** — `Unknown` at `ttl`, then a gating status (default
`Degraded`) at `ttl + escalateAfter`. Six parts — the sixth is an adoption requirement, not an
implementation detail.

### 1. Shape: a third installation mode for the existing probe slot

Not a new node kind, not a decorator. `Lease()` installs a library-owned closure into the same
`_intrinsicCheck` slot that `WithHealthProbe` / `ReplaceHealthProbe` fill, and hands the producer a
`HealthLease` — the push surface:

```csharp
// on HealthNode — callable at build time or at runtime, like ReplaceHealthProbe:
public HealthLease Lease(HealthLeaseOptions options);

public sealed record HealthLeaseOptions(
    TimeSpan Ttl,
    TimeSpan? EscalateAfter = null,        // default: Ttl (escalation at 2×Ttl total age)
    HealthEvaluation? Escalated = null,    // default: Degraded(...); closed set {Degraded, Unhealthy} (§3)
    Func<long>? Clock = null);             // default: Stopwatch.GetTimestamp; lock-free/side-effect-free (§2)

public sealed class HealthLease
{
    /// <summary>Stable machine-checkable marker carried by every decayed evaluation (§4).</summary>
    public const string StaleReasonPrefix = "lease-expired: ";

    /// <summary>Stable marker carried by the seeded never-affirmed evaluation (§4).</summary>
    public const string PendingReasonPrefix = "lease-pending: ";

    /// <summary>Stores the verdict, renews the lease, and propagates via Refresh().
    /// Throws InvalidOperationException if this lease has been detached.</summary>
    public void Affirm(HealthEvaluation evaluation);

    public TimeSpan Ttl { get; }
    public TimeSpan EscalateAfter { get; }
}
```

The push-fed pattern becomes:

```csharp
var lease = node.Lease(new HealthLeaseOptions(Ttl: TimeSpan.FromSeconds(90)));

while (true)
{
    lease.Affirm(SampleSubsystem());     // push + renewal, one call — the guard can't be skipped
    await Task.Delay(_interval);
}
```

Everything downstream of the slot is untouched: `NotifyChangedCore` still calls the intrinsic check,
`Aggregate` still folds, `_cachedEvaluation` stays the single evaluation cache (ADR-002). The graph
does not know or care that a node is leased; staleness is computed *inside* the check, at evaluation
time, like any other intrinsic answer.

**Coexistence and lifecycle** (last-write-wins, matching `ReplaceHealthProbe`'s existing semantics):

- `Lease()` supersedes any installed probe delegate and detaches any previously attached lease.
- A later `WithHealthProbe` / `ReplaceHealthProbe` call reverts the node to pull mode and detaches
  the lease. A detached lease's `Affirm` **throws** — a silent no-op would recreate the
  silent-failure class inside the guard itself, the exact defect shape ADR-008 §4 forbids.
- `ReportStatus` on a leased node keeps its one-shot semantics unchanged: the override is consumed
  by the next wave's skip flag, then the leased evaluation resumes. `Affirm` is the durable push;
  `ReportStatus` stays the transient interjection.
- Lease state is a single volatile reference to an immutable `(Verdict, AffirmedAtTimestamp)` pair,
  swapped on `Affirm` — the library's copy-on-write convention; readers never lock.

`Lease()` seeds the node to `Unknown(PendingReasonPrefix + "awaiting first affirmation")` and
starts the clock immediately. The seed reaches `_cachedEvaluation` synchronously, by the same
mechanism `ReplaceHealthProbe` already uses: `Lease()` installs the closure into the slot and calls
`Refresh()` before returning, so the wave that `Refresh()` triggers evaluates the closure (which
returns the seed while `age ≤ Ttl`) and writes it through the normal `NotifyChangedCore` path —
observer notifications firing outside `_propagationLock` as the architecture rules require. There
is no window after `Lease()` returns in which a report can still show the `Create` `Healthy`
default. A producer that *never starts* therefore escalates on the same schedule as one
that dies mid-run — this closes the sweep's "never sampled, reports the `Create` `Healthy` default
forever" instance, and the seeded `Unknown` is ADR-008-compliant because its resolution path
(first `Affirm`, or mechanical escalation at `Ttl + EscalateAfter`) is guaranteed by construction.

### 2. Clock ownership: pure core over ages, monotonic timestamps at the shell

The decay decision is a pure function — no clock read, no node state:

```csharp
// HealthLease internals — the functional core, table-testable without a graph:
internal static HealthEvaluation Decay(
    HealthEvaluation lastAffirmed,
    TimeSpan age,                    // now − lastAffirmedAt, both from the injected clock
    TimeSpan ttl,
    TimeSpan escalateAfter,
    HealthEvaluation escalated)
{
    if (age <= ttl)
        return lastAffirmed;

    if (age <= ttl + escalateAfter)
    {
        // ADR-012 §5: an emitted Reason must be stable between meaningful
        // changes, never a per-wave telemetry channel. Band `age` to whole
        // multiples of `ttl`, so the string changes only when `age` crosses a
        // whole multiple of `ttl` — not on every evaluation. (The entry into
        // this stage, at `age` first exceeding `ttl`, is itself a status
        // transition Healthy/last-good -> Unknown, which legitimately emits;
        // thereafter the reason is stable within each ttl-wide band.) The
        // earlier `(int)age.TotalSeconds` differed on essentially every wave and
        // defeated HealthReportComparer suppression: a single
        // expired lease made every report unequal to its predecessor forever,
        // firing StatusChanged each wave while SelectHealthChanges (DiffTo,
        // Status-only) stayed silent. `age.Ticks` and `ttl.Ticks` are both long,
        // so the quotient is long without a cast; the guard avoids div-by-zero
        // for a degenerate zero ttl.
        var ttlBands = ttl.Ticks > 0 ? age.Ticks / ttl.Ticks : 1L;
        return HealthEvaluation.Unknown(
            $"{StaleReasonPrefix}no affirmation for over {ttlBands} ttl "
            + $"(ttl {(int)ttl.TotalSeconds}s)");
    }

    return escalated;
}
```

The band index (`ttlBands`) is the only age-derived quantity in the reason, and it advances only
when `age` crosses a whole multiple of `ttl` — so a stalled producer emits one *stable* reason string
per band, not a fresh one per wave. Two adjacent waves inside the same band produce byte-identical
reports and are suppressed by `HealthReportComparer` exactly as intended. This is the ADR-012 §5
content rule applied at the one place this ADR emits varying data; the coarse band is the sanctioned
interim until a structured staleness field lands (Open question 1). A consumer-side freshness policy
already band-quantizes its staleness reasons for this exact reason — it is **consumer-proven prior
art**, not a novel scheme, and retiring that policy onto leases (ADR-011 consumer mapping, cohort 4)
depends on the library reproducing the property.

The impure shell is the closure `Lease()` installs into the probe slot:

```csharp
() =>
{
    var s = _state;                                        // volatile read, immutable pair
    var age = ElapsedSince(s.AffirmedAtTimestamp, _clock()); // Stopwatch-tick arithmetic
    return Decay(s.Verdict, age, Ttl, EscalateAfter, _escalated);
}
```

"Now" comes from an injectable `Func<long>` on `HealthLeaseOptions` defaulting to
`Stopwatch.GetTimestamp` — **monotonic, not wall-clock**, deliberately:

- The library reads no wall clock today, and this ADR keeps it that way. Embedded devices are exactly
  the environment where wall time steps: a box with no battery-backed RTC gets NTP after boot and jumps hours forward
  (instantly and spuriously expiring every lease) or backward (a lease that never expires).
  `Stopwatch.GetTimestamp()` is immune to both and available on netstandard2.0 with zero new
  dependencies.
- Consequence: decayed reasons report **age**, as a `ttl`-band ("no affirmation for over 2 ttl"),
  not a wall-clock "stale since T" — a monotonic clock has no epoch. Age is also the more actionable
  number on a dashboard, and the band keeps it stable between meaningful changes (ADR-012 §5).
- Tests inject a fake `Func<long>` and step it; no `Task.Delay`, no real time. (`TimeProvider` via
  the `Microsoft.Bcl.TimeProvider` package would model this equally well —
  `TimeProvider.GetTimestamp()` has the same semantics — but costs a new package dependency on the
  core for one long-returning function. Recorded as an open question, not taken.)
- **An injected clock MUST be lock-free and side-effect-free**, the same constraint probe delegates
  already carry: the closure runs inside the propagation wave, so `_clock()` is invoked while
  `_propagationLock` is held. A clock that acquires any lock creates a lock-ordering hazard against
  the documented ordering (`_propagationLock` → `_topologyLock` → observer locks) — e.g. a
  logging decorator around `Stopwatch.GetTimestamp` whose sink lock is also reachable from a thread
  waiting on propagation can deadlock the wave. Stated in the `Clock` doc comment, not validated at
  runtime (purity is not checkable).

Decay is **observed at evaluation time only**. No timers, no threads, no scheduled callbacks in the
library: a leased node's staleness is noticed by whatever already evaluates the graph — a
`HealthMonitor` tick, any propagation wave, `RefreshAll`. Detection latency is therefore bounded by
the poll interval. This is the existing division of labor (`HealthMonitor` owns cadence; nodes own
evaluation) applied unchanged, and it is the right trade for the motivating consumer, which already
polls. The corollary is a documented requirement: **a leased node in a never-polled, never-refreshed
graph will not visibly decay** — see Consequences.

### 3. Decay target: two stages, and the second one gates — *the load-bearing decision*

| Age since last affirmation | Evaluation | Gates ancestors? |
|---|---|---|
| `age ≤ Ttl` | last affirmed verdict, unchanged | as affirmed |
| `Ttl < age ≤ Ttl + EscalateAfter` | `Unknown("lease-expired: …")` | never (ADR-006) |
| `age > Ttl + EscalateAfter` | `Escalated` — default `Degraded("lease-expired: …")` | yes |

Each single-stage option fails a constraint this repo has already pinned:

- **Decay to `Unknown` only** is semantically honest — the library genuinely does not know the
  subsystem's state — but ADR-006 makes `Unknown` strictly non-gating, so a dead pump would trade
  one silent state (stale `Healthy`) for another (resting `Unknown` that never escalates, never
  pages — ADR-008's incident showed `Unknown` is the one status no incident policy fires on).
  Worse: a permanently dead producer would make the *library itself* park a node at `Unknown` in
  steady state, violating the transience contract ADR-008 imposes on node owners. The library
  should not ship a feature whose failure mode breaks its own normative contract. The consumer
  experience ADR-008 records — one never-determined `Unknown` disarming warmth gating graph-wide —
  is consumer-side, but it is a preview of what resting `Unknown`s do in practice.
- **Decay straight to `Degraded`** gates and pages, but claims knowledge the library lacks — one
  missed tick from a slow producer becomes an instant health downgrade, and "producer is late" and
  "subsystem is degraded" collapse into one state on every dashboard. It also erases the honest
  intermediate that lets an operator distinguish a blip from a death.
- **A fully configurable decay target** hands the decision back to eight call sites — the exact
  "each service had to remember, zero did" failure this ADR exists to remove. A consumer could
  configure the guard back into silence.

Two stages compose the honest answer and the safe answer in sequence: `Unknown` first, because
"signal lost" is what is actually known, and it is the correct dashboard state for a producer that
is merely late; escalation second, mechanically, so the `Unknown` cannot rest — its resolution path
(re-affirmation or the escalation deadline) satisfies ADR-008 *by construction*, not by producer
discipline. ADR-006 is not amended: the stage-one `Unknown` folds exactly as ADR-006 pins, and the
escalation to a gating status happens at the node's own evaluation, not in the rollup.

Bounded configurability, validated at `Lease()` (throw on violation, no silent clamp):

- `Escalated.Status` must be in the closed set `{Degraded, Unhealthy}` — severity of "my producer
  is dead" is genuinely node-specific (a deposit-backlog advisory is not a safety interlock). It
  may **not** be `Healthy` or `Unknown`, which exhausts `HealthStatus`: *whether* staleness
  eventually gates is the library-level guarantee and is not configurable. (`Importance.Advisory`
  is an edge weighting, not a status, and cannot appear here.)
- `Ttl` and `EscalateAfter` must be non-negative, and their sum must not overflow:
  `EscalateAfter ≤ TimeSpan.MaxValue − Ttl`, so `Decay`'s `ttl + escalateAfter` comparison is
  always well-defined (an overflowed negative sum would silently make stage one unreachable).
  `EscalateAfter = TimeSpan.Zero` is legal — a node that wants immediate gating on expiry can have
  it; the `Unknown` stage collapses away by explicit choice rather than by a separate mode.

The producer may `Affirm(Unknown(...))` — the library does not police verdict content — but
ADR-008's transience contract applies to the producer's choice exactly as it does for pull probes.

### 4. Wire visibility: a stable reason marker, no new wire field

`HealthEvaluation.Unknown(reason)` / `Degraded(reason)` already flow through `HealthReport`,
`HealthSnapshot`, and the ADR-009 tree projection — a decayed verdict is visible on every existing
surface with zero schema change. What consumers need beyond visibility is *distinguishability*: a
control plane must tell "stale producer" apart from "genuinely unknown" without string-guessing.

Every evaluation *synthesized by decay* — both stages, including a consumer-supplied `Escalated`,
whose reason the library prefixes — carries `HealthLease.StaleReasonPrefix` (`"lease-expired: "`),
a `public const` so consumers compare against the constant rather than a folklore string. This is
deliberately the modest 8.0 answer: a first-class structured field (e.g. a `Staleness` marker on
`HealthSnapshot`) would put a new member on the wire on every heartbeat — precisely the compat
burden ADR-008 documented for wire enums and ADR-009 inherited with eyes open for `Importance`.
The reason-prefix convention costs nothing now and does not preclude a structured field later
(recorded as an open question).

All three lease-emitted evaluation shapes are const-anchored — no folklore strings anywhere in the
surface: the seeded never-affirmed state carries `PendingReasonPrefix` (`"lease-pending: "`), and
both decay stages carry `StaleReasonPrefix`. The two prefixes are distinct because the states are
operationally distinct — "this node has never heard from its producer" (normal during startup) and
"this node's producer went silent" (never normal) — so a control plane can match each without
parsing, and the three phases are distinguishable on a dashboard end to end.

### 5. Versioning: land in the 8.0 line, before stable

This targets the current `8.0.0-beta.x` stabilization line, not 8.1:

- **Purely additive.** One new type (`HealthLease` + options), one new method on `HealthNode`. No
  existing member changes signature or behavior; no `HealthStatus`/`Importance` member is added; no
  switch site anywhere gains a case (the ADR-008 §2 hazard class does not apply). The wire shape is
  untouched — decayed statuses and reasons ride fields that already exist.
- **The window argument, again.** ADR-008 landed `Advisory` inside the 8.0 window because additive
  surface is cheap before stable and expensive after. Same logic here: the consumer's silent-failure
  remediation is the reason this feature exists, that consumer takes the 8.0 update train, and
  deferring to 8.1 leaves eight known-unguarded probes unguarded through an entire release cycle for
  no compensating stability gain.
- **Compat story:** none needed. A consumer that never calls `Lease()` compiles and behaves
  bit-for-bit identically. Prerelease-to-prerelease, no binary-compat promise is in force; from
  `8.0.0` stable onward the new surface is covered by the normal SemVer promise.

### 6. Adoption requirement: a leased graph MUST have a wave source — *not a trade-off, a precondition*

Decay is observed at evaluation time only (§2): the library schedules nothing, and a leased node
decays only when *something already evaluating the graph* runs its closure. The first draft filed the
consequence under Negative/Trade-offs. That was too soft. Restated as a normative adoption
requirement, because the motivating consumer does **not** satisfy it by default and a migrated probe
that silently never decays is *worse* than the unguarded one it replaces — it looks guarded.

> **A graph containing leased nodes MUST be driven by a wave source whose cadence is at least as fast
> as the tightest `Ttl` in the graph.** Absent such a source, a lease's decay is never evaluated: the
> stale verdict persists, the escalation never fires, and the guarantee this ADR exists to provide is
> void. Adopting `Lease()` without a wave source is a modelling defect, the leased-node analogue of
> ADR-008's "an `Unknown` with no resolution path."

The requirement is unmet by the motivating consumer as it stands. That graph is **edge-driven only**:
a search of its source for `HealthMonitor`, `RefreshAll(`, and `PollHealthReport` across non-test C#
returns no health-related hit. Waves come from device-tracker, facade, and
peripheral-heartbeat `Refresh()` calls — incidental, not guaranteed, and they thin out exactly
overnight when the machine is quiet and a dead producer is least likely to be noticed by a human,
which is empirically also the window when its USB peripherals re-enumerate. Critically, the one
periodic health mechanism that consumer has — a freshness sweep service — is **guard-scoped, not
graph-scoped**: it refreshes registered freshness guards, not the graph, so it is not a wave source in
this sense. A lease dropped into that graph would decay only by luck.

**This does not weaken the "no timers in the library" doctrine — it relocates the timer to where one
already belongs.** The library still schedules nothing; the *consumer* runs the wave loop it already
needs for polling. Two worked shell examples, both already present in that consumer:

- **A grace refresh pump** exists *specifically* because cold-start grace deadlines had no wave to
  ride otherwise — its own doc says so. It is precedent that a consumer of a deadline-bearing node
  already accepts owning the nudge loop; a lease is the same shape.
- **The freshness sweep service** already refreshes every guard on a floor cadence. The lease
  migration's wave source is that service *widened to wave the graph* (a `RefreshAll()` or
  equivalent) at a cadence covering the tightest `Ttl` — not new machinery, an existing loop given the
  graph as its target. The ADR-011 consumer mapping treats this as the near-term unified pump's floor
  cadence.

The clean long-term answer — `HealthMonitor` learning the earliest upcoming lease deadline and
scheduling an off-cycle tick — stays Open question 3 (shared with ADR-011 OQ5); until it lands, the
consumer-run wave source is the adoption gate, and the consumer must add one as part of its
remediation **before** the eight probes migrate, not discover it after.

## Non-goals

- **Global update-time timestamping is explicitly rejected** (and was considered before this
  design). Stamping every `_cachedEvaluation` write proves the wrong thing: a polled caching
  delegate is *called* every evaluation, so its "update time" is always fresh even when the data
  behind it is dead — the observable measures the library's own polling, not the producer's
  liveness. And a delegate that computes live (`() => _pool.Available > 0`) needs no staleness
  machinery at all; taxing every node with a timestamp to guard the push-fed minority inverts the
  cost. Staleness is a property only the producer can declare, per node, opt-in.
- **No timers or background work in the library.** Decay is evaluated, not scheduled (§2). Stated
  as doctrine, deliberately: this ADR is the first to let time into the core, and it lets time in
  *only as an input to a pure decay function, never as scheduling*. Future time-shaped proposals
  (flap damping, hysteresis, rate-limited transitions) must clear their own bar — this ADR is not
  precedent for a clock that *does* things, only for one that is *read*.
- **No policing of pull probes.** A `WithHealthProbe` delegate that returns a stale cache is still
  expressible — the fix for that shape is *migrating it to a lease*, not a library heuristic that
  guesses which delegates cache.
- **No async probe surface.** Orthogonal; unchanged.

## Rejected alternatives

- **A consumer-side helper convention** (a stale-guard wrapper class in the consumer, or a
  documented pattern). Recreates the eight-times-forgotten guard; the sweep is the empirical
  refutation. The guard must be structurally inseparable from the push surface.
- **A new node kind or `HealthNode` subclass.** `HealthNode` is sealed by design ("no inheritance
  hierarchy" — project convention); a second node type would fork every downstream surface
  (report, tree, topology, generators) for what is at bottom a probe-slot behavior.
- **A decorator node wrapping the real one.** Changes topology — the graph gains a synthetic node,
  names become load-bearing in two places, and `HealthNames`/generator output no longer match the
  consumer's mental model. Staleness is not a structural fact; it should not appear as structure.
- **`maxAge` on `WithHealthProbe` (pull + age check).** For a pull delegate the library has no
  data-age observable to compare against `maxAge` — calling the delegate *is* the update. This is
  the global-timestamping non-goal wearing a per-node hat.
- **`Affirm` without TTL (push-only mode), TTL optional.** An optional TTL is a forgettable TTL;
  the entire point is that the shelf-life declaration is mandatory at the only place the producer
  touches the library.
- **Per-lease expiry timers that `Refresh()` the node.** Tightens detection latency below the poll
  interval, but puts threading, disposal, and re-entrancy machinery into the core and breaks the
  existing "cadence lives in `HealthMonitor`" division. If sub-poll latency is ever needed, a
  monitor-side awareness of the next lease deadline is the right home (open question), not
  per-node timers.
- **Single-stage decay (either target) and fully-configurable targets.** Rejected in §3.

## Alignment with prior ADRs

- **ADR-002 — single non-null cache, one evaluation path.** No second evaluation cache and no
  lifecycle bit on `HealthNode`: `_cachedEvaluation` remains the only evaluation cache, written by
  the same `NotifyChangedCore` path. The lease's `(verdict, timestamp)` pair is producer-input
  state captured by the installed closure — the same category as any probe delegate's captured
  state, just owned by the library so the guard is unforgeable.
- **ADR-004 — probes are delegates on nodes.** The lease is the third installation mode for the
  slot ADR-004 created; `ReplaceHealthProbe`'s runtime-swap semantics extend to it unchanged.
- **ADR-006 — unamended, and load-bearing twice.** Stage-one `Unknown` folds under exactly the
  pinned non-gating table (a late producer never fails an ancestor), and the *insufficiency* of
  that same guarantee for a resting state is the argument that forces stage two.
- **ADR-008 — the contract this feature operationalizes.** Every `Unknown` this feature can emit
  has a mechanical resolution path (first `Affirm`, re-affirmation, or the escalation deadline) —
  transient by construction, not by producer discipline. Stage one uses the §3 `Unknown(reason)`
  factory; the fail-loud detached-`Affirm` and the validated `Escalated` target follow §4's
  no-silent-substitution ethos. The consumer guidance ADR-008 closed with ("treat a node `Unknown`
  longer than its resolution window as alertable") becomes a library primitive.
- **ADR-009 — no new surface needed.** Decayed evaluations ride the report and the tree projection
  as ordinary statuses with reasons; `BuildTreeSnapshot` and the topology artifact are untouched.
- **ADR-011 — escalation-suppression is closed structurally, not by a forward-compat clause.**
  This ADR's §3 refuses to let `Escalated.Status` be `Healthy` or `Unknown`, so
  *whether* staleness eventually gates is a library-level guarantee and not configurable. That review asked
  whether a *future* transform-style temporal policy (debounce, hysteresis, flap damping) could reach
  around that guarantee by holding or softening the stage-two escalation on a node that carried both a
  lease and a policy. It cannot: **ADR-011 §7 makes leases and policies mutually exclusive per node** —
  `Lease()` on a policied node throws and `WithDebounce`/`WithGrace` on a leased node throws. There is
  no node on which a policy can observe, let alone suppress, a lease-synthesized escalation, so the
  collision it flagged is unrepresentable rather than merely discouraged. The forward-compat wording
  it proposed for this ADR is therefore unnecessary; the constraint lives where the two features
  actually meet, in ADR-011 §7. (ADR-011 also records this as its own resolution.)
- **ADR-012 — the report-equality contract.** The §2 band-quantization of the decay reason is
  ADR-012 §5's content rule applied at this ADR's one varying-data emission: an emitted `Reason` must
  be stable between meaningful changes, so age is banded to `ttl` multiples rather than emitted per
  second. The §4 reason-prefix convention (`StaleReasonPrefix` / `PendingReasonPrefix`) is likewise
  the interim answer ADR-012 sanctions until a structured staleness field lands (Open question 1,
  shared with ADR-012's deferred field).

## Consequences

### Positive

- **The guard cannot be forgotten.** Declaring the TTL is the same call that obtains the push
  surface; the eight-probe failure class is structurally unexpressible for lease consumers.
- **Dead-producer states become visible, then actionable.** "Producer late" (`Unknown`, honest,
  non-paging) and "producer dead" (`Degraded`/`Unhealthy`, gating, pages through existing policies)
  are distinct, ordered, and machine-distinguishable via `StaleReasonPrefix`.
- **Never-started producers fail safe.** The seeded `Unknown` + escalation schedule closes the
  "reports the `Create` `Healthy` default forever" instance.
- **The core stays clock-free where it matters.** One injectable monotonic timestamp source at the
  shell; the decay decision is a pure function with table tests.
- **Zero cost to non-users.** No behavior, wire, or perf change for graphs that never lease.

### Negative / Trade-offs

- **Detection latency is the poll interval, and the wave source is an adoption precondition.** A
  leased node in a never-evaluated graph never visibly decays. This is no longer filed as a mere
  coupling: it is promoted to the normative **adoption requirement in §6** — a leased graph MUST have
  a wave source at least as fast as the tightest `Ttl`, which the motivating consumer does not satisfy
  by default. See §6 for the requirement and its worked shell examples.
- **A third probe mode to teach.** `WithHealthProbe` (pull), `ReportStatus` (one-shot interjection),
  `Lease`/`Affirm` (standing push with expiry). The README's probe section must draw this triangle
  clearly, or consumers will keep reaching for the caching-delegate pattern out of habit.
- **Escalation can false-positive.** A producer that is alive but slower than its declared TTL
  (GC pause, I/O stall, debugger) gates its parent. That is the designed trade — the producer
  declared the cadence — but tuning `Ttl`/`EscalateAfter` too tight converts blips into pages.
  Guidance: `Ttl` ≥ 2–3× the sampling interval.
- **Warmth-gate interplay is consumer-visible.** A leased node sits at `Unknown` until its first
  `Affirm`, which consumer warmth logic of the kind ADR-008 records will see. Unlike that incident
  this `Unknown` always resolves mechanically, but consumer-side warmth semantics should be
  re-checked when the eight probes migrate.
- **The reason prefixes are a string convention.** Stable and const-anchored (`StaleReasonPrefix`,
  `PendingReasonPrefix`), but still strings on the wire where a structured field would be crisper —
  deferred, with the wire-compat reasoning in §4.

## Open questions

1. **Structured staleness on the wire.** Should `HealthSnapshot` eventually carry a first-class
   marker (e.g. `Staleness: Fresh | Expired | Escalated`) instead of the reason prefix and the
   band-quantized age (§2)? Wire change; would need the staged-rollout treatment ADR-008 described.
   ADR-012 pins the contract such a field would satisfy (it participates in the report-equality key,
   not the transition key) and sanctions band-quantization as the interim; this is the same deferred
   structured-field question ADR-012 Open question 1 owns. Deferred until a control plane actually
   consumes the distinction programmatically.
2. **`TimeProvider` vs `Func<long>`.** Adopting `Microsoft.Bcl.TimeProvider` on the core would
   align with the BCL's blessed abstraction at the cost of a new dependency; the `Func<long>`
   keeps the core dependency-light. Revisit if the core ever takes `TimeProvider` for another
   reason.
3. **Monitor-assisted expiry latency. — RESOLVED.** `HealthMonitor` now learns the
   earliest upcoming lease deadline and wakes on it (not just a fixed poll interval), tightening
   decay detection to the deadline itself rather than the cadence. A leased node surfaces its next
   decay instant (`AffirmedAtTimestamp + Ttl`, then `+ Ttl + EscalateAfter`), and the graph folds
   it — reconciled from the lease's `Stopwatch`-tick timebase into wave time — into the single
   `NextTemporalDeadline` the monitor mins over, jointly with ADR-011's policy deadlines. This is
   the clean answer §6 anticipated: the library still schedules nothing in its core, but the
   consumer-started monitor shell (`RunMonitor()`) now owns the nudge, so §6's consumer-run wave
   source is satisfied by a single blessed call instead of a hand-rolled loop. Cadence stays
   optional; a graph with drifting pull-probes still uses the preserved poll path. Resolves jointly
   with ADR-011 OQ5.
4. **DI surface.** `NodeConfigurator.WithLease(...)` for the `Prognosis.DependencyInjection`
   builder path, and whether the returned `HealthLease` should be resolvable by the owning service.
   Additive; follows once the core shape settles.
