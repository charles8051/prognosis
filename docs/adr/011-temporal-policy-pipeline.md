---
id: ADR-011
status: proposed
governs:
  - HealthNode.cs
  - HealthGraph.cs
  - TemporalPolicy.cs
  - NodeObservationHistory.cs
  - Prognosis.Tests/TemporalPolicyTests.cs
relates:
  - ADR-002
  - ADR-004
  - ADR-005
  - ADR-006
  - ADR-008
  - ADR-009
  - ADR-010
  - ADR-012
  - ADR-013
---

# ADR-011: Temporal Policies — Grace and Debounce as Library-Owned Node Behaviour, Flap as a Derived Observation

**Status:** Proposed
**Date:** 2026-07-31
**Drivers:** ADR-010 let time into the core as a read-only input and explicitly declined to be precedent for the rest ("future time-shaped proposals — flap damping, hysteresis, rate-limited transitions — must clear their own bar"). This is that bar. The immediate evidence is downstream: a consuming application has hand-built per-node temporal shells around Prognosis repeatedly, three of which re-derive the same deadline-nudge mechanism, because a health verdict in this library is a function of the present instant only.

> **Revision note (2026-07-31).** The first draft of this ADR proposed an open `IHealthPolicy`
> pipeline with a fixed grace-then-debounce order and a history latch derived from verdict
> history. Independent review found both central claims unsound: the field-proven composition
> in the consuming application runs **debounce then grace**, and the grace latch is a **domain
> liveness fact the library cannot derive** (a never-live device's raw verdict is a determined
> `Unhealthy`, so a verdict-derived latch trips immediately — and `HealthNode.Create` seeds
> `Healthy`, making it vacuous at construction). This revision closes those and the
> mechanism gaps found alongside them: library-owned typed policies instead of an open
> interface, a consumer-supplied liveness input, lease/policy mutual exclusion, a paired
> evaluation+history record swapped atomically, and a surfaced deadline so the consumer's
> nudge pump knows when to fire.

> **Amendment note (2026-07-31).** Two gaps the consumer-side pump design and a consumer-side
> policy mapping exposed after this ADR was proposed, both closed here in place:
> **§6 gains a `TemporalDeadlineChanged` notification — surfacing *the deadline value* is not
> enough, because a debounce **hold** installs a `PendingDeadline` without changing the effective
> evaluation, so the report compares equal and `StatusChanged` never fires; a pump armed on "nothing
> pending" then sleeps through the one deadline that matters and the debounce never gates.
> **(Producer-side grace fold)** §9 (new) promotes the single §7 rationale sentence — "a producer that
> wants its affirmed stream damped can damp before affirming" — to a named, exported pattern, because
> the consumer mapping showed a backend-API leaf needs it on day one (lease + producer-side grace,
> pinned by a consumer-side test) and without an exported grace core the "unified" solution leaks its
> first exception immediately. No prior decision reverses.

> **Amendment note (2026-08-08 — graph-wide defaults).** §10 (new) adds a graph-owned defaults bag,
> materialized into nodes at attach. The gap it closes is one the ADR created and never named: §1
> made policy strictly **per node**, which is right for the two-node case that motivated it
> (the peripheral-connection leaf of Context, "The worked example") and wrong at fleet scale, where every leaf wants the same debounce
> and nobody wants forty `WithDebounce` call sites. Consumers can approximate it today — loop
> `graph.Nodes`, subscribe `TopologyChanged` for late additions — but that approximation carries four
> defects the library is better placed to avoid (§10), including silently clobbering a node's own
> tuned policy and throwing on a leased node. No prior decision reverses: §1's per-node opt-in is
> still the model, §2's fixed order is untouched, and §7's exclusivity is *reaffirmed* — a default
> skips a leased node rather than throwing at it. Three sentences elsewhere in this ADR *are* falsified
> by §10 and are qualified in place rather than left standing — §1's "non-users pay nothing", the
> ADR-008 alignment bullet's "by construction", and the "Zero cost to non-users" consequence — following
> the precedent of amending the affected text where it lives.

## Context

### The library models an instant; every consumer needs a history

`HealthEvaluation` is `(HealthStatus Status, string? Reason)`. A node's verdict is whatever its probe answers right now. That is the correct model for a probe that computes health when asked, and it is why the library has stayed clock-free and exhaustively testable without one.

But the questions operators ask are temporal, and none are expressible against an instant:

- *"Has this device been gone long enough to matter, or is it re-enumerating?"*
- *"Is this leaf allowed to gate yet, or has it never been live?"*
- *"Is this subsystem flapping, or genuinely down?"*

Consumers answer these by building a parallel, clock-keyed structure alongside the graph and feeding its output back into a probe delegate. In one consuming application — an unattended, device-attached service whose graph is a few dozen leaves over USB peripherals and network dependencies — the per-node temporal shells accumulated into five distinct kinds:

| Shell | Temporal question | In scope here? |
|---|---|---|
| A cold-start grace, plus its policy object | may this node gate before it has ever been live? | **yes** — grace |
| A device-presence debounce, plus its gate | has this device been absent long enough? | **yes** — debounce |
| A grace refresh pump, a freshness sweep service, and the presence gate's one-shot timer | *(pure mechanism)* — a deadline needs something to nudge it | **partly** — §6 surfaces the deadline; the pump stays consumer-side |
| A family of freshness guards | is this cached verdict too old? | no — ADR-010's domain (leases) |
| A startup warmup latch and a subtree warmup gate | is the process as a whole still warming? | no — report-level serviceability gates, not per-node verdict transforms |

**Three** independent implementations of "a deadline needs something to nudge it" — the refresh pump, the sweep service, and the presence gate's one-shot re-arm — in one codebase is the diagnostic. The sweep service's own doc names the root cause:

> The health graph is **event-driven only** — `HealthGraph.ObserveHealthReport()` emits when something calls `Refresh()`, and nothing anywhere re-evaluates the graph on a timer. A leaf whose driver has died therefore emits nothing at all: without an external nudge, the staleness policy would be perfectly correct and never once evaluated.

### The worked example

One leaf — the connection status of a USB peripheral, call it `Peripheral.Connection` — gated an entire service on a **single 30 s sample**. That peripheral re-enumerates on the USB bus for 10–18 s, once or twice a day, on a free-running cycle unrelated to the probe cadence. Whether the absence paged an operator therefore depended on whether a probe tick happened to land inside the gap — roughly half did. The consumer-side fix was another bespoke debounce, correct in isolation and one more instance of the same shape.

That is the shape worth holding onto: a device legitimately absent for a bounded interval, a probe sampling too coarsely to distinguish that from a real failure, and a verdict model with no way to say "not yet."

### Two facts the design must respect (learned from the field code)

1. **The grace latch is a domain fact, not a verdict fact.** The consumer's grace fold takes the raw verdict *plus* an "is the node live right now" bit, where "live" means the device reached its driver-level `Active`/`Open` state. During the enumerated-but-not-yet-live window the raw verdict is a fully determined `Unhealthy`; that is precisely the verdict grace exists to suppress. No projection of verdict history can reconstruct this bit. The library must be *told*.
2. **Grace and debounce are disjoint by construction, and the proven order is debounce-then-grace.** In the field gate the grace applies only *before* the node's first live observation and the debounce only *after* it, and the consumer chains debounce first so that the grace's one-way live latch keeps advancing on the same observations.

### What ADR-010 established that this ADR inherits

- **Time enters as a value, not as scheduling.** Decay there is a pure function of ages.
- **The clock is injectable and monotonic** (`Func<long>` over `Stopwatch.GetTimestamp`), never wall-clock, because devices without a battery-backed RTC step their wall clock after boot.
- **An injected clock must be lock-free and side-effect-free** — it is read inside the propagation path.
- **Per-node temporal state is an immutable value swapped by reference** — the library's copy-on-write convention.

This ADR adds no new mechanism to that list; it generalizes the shape, with the corrections above.

## Decision

Ten parts. Parts 1–8 are the original decision; §6 gains the `TemporalDeadlineChanged` notification, §9 is the exported producer-side grace fold, and §10 is the graph-wide defaults bag.

### 1. Two library-owned typed policies, not an open interface

`HealthNode` gains two opt-in temporal policies, both implemented **inside the library**:

```csharp
public HealthNode WithDebounce(DebounceOptions options);   // absence/failure must persist before it gates
public HealthNode WithGrace(GraceOptions options);         // no gating before first-live, bounded by a deadline

public sealed record DebounceOptions(
    TimeSpan MinimumFaultDuration,              // a non-Healthy run shorter than this holds the prior effective status
    HealthStatus? HeldAs = null);               // optional: report Degraded instead of held-last-good during the window

public sealed record GraceOptions(
    TimeSpan Deadline);                          // past this, a never-live node gates on its raw merits (ADR-008 resolution path)
```

There is **no consumer-implementable policy interface in this ADR.** An open `IHealthPolicy` cannot be ordered by the library (an arbitrary implementation has no slot), cannot be verified pure (an instance field smuggles in per-policy state), and cannot be forced to carry a resolution deadline. Making the two policies library-owned dissolves all three problems at once: order is fixed because the library implements the chain; purity is real because the library wrote the functions; and `GraceOptions.Deadline` is a **required constructor parameter**, so a grace whose `Unknown` has no resolution path is unrepresentable — ADR-008's contract holds by construction, not by discipline. Consumer-extensible policies are a possible later ADR once the shape has survived contact with an implementation (see Open questions).

An unconfigured node has no policies and behaves **bit-for-bit as today** — identity is the default, and non-users pay nothing.

> **Qualified by §10 (2026-08-08).** "Unconfigured" now has two readings and this sentence is true only of the stricter one. A node is unconfigured iff *neither* it nor any graph it has ever been attached to configured a policy for it — §10e makes a materialized default node state that survives detach, so a node can be policied by a graph other than the one you are reading. The sentence holds exactly for a process that supplies no `TemporalDefaults` anywhere; it does not hold for "this node has no `With*` call" or "this graph has no defaults." Non-users in the strict sense still pay nothing.

### 2. Execution order: debounce, then grace — the field-proven composition

When both are configured, the chain is `raw → debounce → grace`. This is the field gate's order, kept for the field gate's reason: the two are disjoint by construction (grace acts only before first-live, debounce only after), and running debounce first lets the grace latch advance on the same observations. The order is library-internal and not configurable; there is nothing for a consumer to get wrong.

### 3. Liveness is a consumer-supplied input, not a derived one

```csharp
public void MarkLive();   // one-way; idempotent; callable from any thread
```

`MarkLive` participates in the **same CAS loop over the §4 pair** as the wave path — it retries a swap of a history whose `HasEverBeenLive` is set, leaving `Effective` untouched — so it composes with a concurrent wave rather than racing it, and a separate latch field (which would reintroduce exactly the torn-pair problem §4 exists to rule out) never exists. It schedules nothing and triggers no wave: the live edge that prompts `MarkLive` is, in every known consumer, the same event that changes the raw verdict, so the consumer's existing `Refresh()` wiring carries the re-evaluation (the `tracker.StateChanged → probe.Refresh()` pattern).

The grace latch advances **only** when the consumer reports the underlying subsystem live — a device tracker's `Active` edge, a session's `Open`, whatever the domain means by it. The library never infers liveness from verdicts, because it cannot: a never-live node's raw verdict is a determined `Unhealthy`, and `HealthNode.Create` seeds `Healthy` before any probe runs, so every verdict-derived latch is either wrong or vacuous. `MarkLive` is the same input-shape as ADR-010's `Affirm`: a fact only the producer knows, declared at the only place the producer touches the library. A node with `WithGrace` and no `MarkLive` caller resolves anyway — that is what `Deadline` is for.

### 4. One paired record, one atomic swap, updated at the evaluation path

Per-node temporal state is a single immutable record:

```csharp
public sealed record NodeObservationHistory(
    HealthStatus LastRaw,
    TimeSpan CurrentRunStartedAt,        // wave time at which LastRaw last changed
    bool HasEverBeenLive,                // §3 latch
    TimeSpan? PendingDeadline,           // §6 — earliest instant a configured policy's answer can change
    IReadOnlyList<TimeSpan> Transitions); // raw transition instants; bounded, drop-oldest, library-fixed bound (32)
```

The node stores **one** volatile reference to an immutable `(HealthEvaluation Effective, NodeObservationHistory History)` pair — not two fields — so no reader can ever pair a new evaluation with an old history. Multi-writer paths exist (a node in two graphs propagates under two different `_propagationLock`s; the no-graph `BubbleChange` fallback holds no lock; `ReportStatus` writes outside any lock), so the update is a **CAS loop over the immutable pair**, not a blind store. Transitions are rare by definition — that is why they are worth recording — so contention on the loop is negligible.

The update site is `NotifyChangedCore`, with the other `_cachedEvaluation` writers named and handled rather than assumed away:

- **The constructor** seeds the pair to its initial value — empty history, the `Healthy` default evaluation — *without* running the chain. A build-time seed has no wave time to fold with, and no observer can see it before publication.
- **`WithHealthProbe`** writes directly today and never triggers a wave — and it may run against an *existing* node, so it does not re-seed. It CAS-swaps the pair's `Effective` to the new probe's immediate evaluation and **leaves the history untouched**: the history describes the node, not the probe (the same rule the Consequences section states for `ReplaceHealthProbe`). The chain is not applied — the pre-chain value is visible until the next wave, same as its today-behaviour, now stated.
- **`ReportStatus`** keeps its documented one-shot-interjection semantics: it writes the pushed evaluation directly and the next wave's evaluation replaces it. The interjection **bypasses the policy chain and does not enter the history** — it is an override by design, and two rapid pushes coalescing (second overwrites before the first wave) means push-heavy nodes undercount transitions. Both behaviours are stated limitations, consistent with ADR-010's treatment of `ReportStatus` as "the transient interjection".
- **`NotifyChangedCore`** — reached from every propagation path (`EvalInDependencyOrder`, `NotifyDfs`, `RefreshDescendants`, `RefreshAll`) — computes the raw evaluation, CAS-updates the history if the raw status changed, applies the chain, and swaps the pair.

"Raw" at a composite means the post-`Aggregate` value (intrinsic folded with the children's *effective* evaluations, which is all `Aggregate` ever sees). Policies on composites are deferred — see Open questions — so in this ADR the chain runs on leaves only, where raw and intrinsic coincide.

> **Amendment note (2026-07-31).** The paragraph above claimed the CAS
> "covers the multi-writer paths," but the first implementation left **two** evaluation-path
> fields OUTSIDE the swapped record: the `§5` wave-time baseline (a non-volatile
> `Nullable<TimeSpan>` — a `bool`+`long`, not atomically read) and the one-shot
> `ReportStatus` bypass (a `volatile bool` consumed by a non-atomic check-then-clear). Under
> genuinely concurrent `NotifyChangedCore` for one node (a node in two graphs, per §5) they
> race: a torn timebase, and a one-shot double-consumed by two waves or lost between
> `ReportStatus`'s two writes. Not reachable through today's single-producer API (policies run
> on leaves, and a policied node is single-producer), but the blanket claim was false. **Both
> are now folded into the swapped record** — the state is `(Effective, History, Grace,
> LastWaveTime, SkipNextIntrinsic)`, `firstWave`/`chainNow`/`bypass` are derived from the
> observed snapshot INSIDE the CAS loop and persisted in the swapped-in value, and a retry
> re-derives them — so the "one atomic swap" is now literally true for every field the
> evaluation path reads or writes, and §4's multi-writer claim holds structurally rather than
> aspirationally. `LastWaveTime` is advanced as `max(observed, now)`, so it stays monotonic
> even when two graphs wave one node with different `now` values and a smaller-`now` wave wins
> a later CAS (a plain `now` overwrite would regress it and stale the §5 fallback timebase). It
> is excluded from the no-change fast-path test (a steady advance is a skipped no-op swap)
> except for the first-wave `null → non-null` establishment, which must persist so `firstWave`
> flips; this preserves the zero-allocation steady state §4 promises non-users. Falsified by two
> concurrent-shared-node tests (`HealthNodeConcurrencyTests`): one drives `bypasses > arms`
> against the pre-fix one-shot layout, the other drives a baseline regression against the plain
> overwrite.

### 5. Time: the graph owns the clock; one read per wave, threaded

`HealthGraph` gains an injectable monotonic clock (`Func<long>`, default `Stopwatch.GetTimestamp`, same constraints as ADR-010: lock-free, side-effect-free). `SerializedBubble` reads it **once at wave entry**, converts ticks to a `TimeSpan` since graph construction (the conversion lives at the graph boundary, in one place, using `Stopwatch.Frequency`), and threads that single `now` through the wave to every policy evaluation. History instants (`CurrentRunStartedAt`, `Transitions`) are recorded in the same timebase.

Stated honestly rather than assumed:

- **A wave is a pure function of (raw evaluations, wave timestamp, prior history).** Replay means replaying the wave *sequence* from an initial state — not replaying one recorded wave in isolation. That is still the property worth having: recorded inputs replay to identical outputs, byte for byte, per run.
- **The no-graph `BubbleChange` fallback has no clock, and wave time is the only timebase.** A policied node that has been waved at least once applies its chain with the *last* wave's `now` — deadlines cannot regress, and they fire on the next graph wave. A policied node that has **never** been evaluated in a graph wave has no timebase at all, so the chain is **inert (identity) until the first wave**, which also stamps `CurrentRunStartedAt`. (A zero-`now` fallback would be wrong in the dangerous direction: every debounce window already elapsed and every grace deadline already fired on first evaluation.) Temporal policies are, in practice, graph-scoped; the doc says so.
- **A node in two graphs propagates once per graph** (`_bubbleStrategy` is multicast), so one logical change produces two waves with two timestamps. The CAS in §4 makes the double history update safe; the second wave observes no raw change and records nothing.
- **ADR-010's lease clock is per-lease and per-evaluation today.** When both features are implemented, the wave-threaded `now` becomes the canonical instant for anything evaluated inside a wave, and the lease closure should take it rather than reading its own clock mid-wave. That reconciliation is an implementation note for whichever lands second; neither ADR's semantics change.

### 6. The deadline is surfaced, because the nudge stays the consumer's job

The chain computes, alongside the effective evaluation, the **earliest future instant at which its answer could change with no new observation** — a pending debounce window's end, a grace deadline. That lands in `NodeObservationHistory.PendingDeadline`, and the graph exposes the minimum over its nodes:

```csharp
public TimeSpan? NextTemporalDeadline { get; }   // null when no policy is pending
```

This is the generalization of the "gates at elapsed" field the consumer's presence gate built *specifically* so it knew when to re-pull — and it is what makes the consumer's pump possible at all: without it, a pump cannot know which nodes are pending or when, short of blind periodic refresh of everything forever. The library still schedules nothing ("no timers in the library", inherited from ADR-010); the refresh pump and its siblings remain the worked examples of the shell-side pump, but they can now be **one** pump keyed on one deadline instead of three mechanisms keyed on private state.

#### 6a. `TemporalDeadlineChanged` — the pump needs to know when the deadline *moves*, not just what it is

Exposing `NextTemporalDeadline` as a readable value is necessary but not sufficient. The consumer's pump has to learn when that value *changes* so it can re-arm its alarm — and the natural signal for "re-read the deadline," an emission on `StatusChanged`, is **silent in exactly the case the flagship feature depends on**:

1. A device goes absent. The tracker edge causes a wave.
2. The debounce policy **holds** the effective evaluation at last-known-good (§1: sub-threshold absence holds) and installs a `PendingDeadline` at absence-start + `MinimumFaultDuration`.
3. The effective evaluation is unchanged, so the rebuilt report compares **equal** to the cached one under `HealthReportComparer`, so `StatusChanged` never fires.
4. A pump that armed on "no deadline pending" and is now sleeping indefinitely **never learns a deadline appeared**. The debounce window elapses unwatched; the node never gates. The one deadline that had to fire is the one nothing woke up for.

Grace does not have this failure: its suppression *transitions* the effective status (`Healthy → Unknown`), which emits, so a pump can re-arm on any emission. **The silent case is precisely the debounce hold** — and it is the behaviour the whole section exists for (`Peripheral.Connection`, the ADR's worked example). Polling `NextTemporalDeadline` on a cadence masks the gap but defeats the entire purpose of surfacing a deadline: the pump degenerates back into the blind periodic sweep §6 exists to retire.

So the graph emits a distinct notification when its minimum pending deadline changes:

```csharp
public IObservable<TimeSpan?> TemporalDeadlineChanged { get; }   // fires only when NextTemporalDeadline moves
```

Its properties are deliberate:

- **Computed during the wave, emitted after `_propagationLock` is released.** The new minimum is *captured* inside the wave — one pass over the just-updated histories, in the same place the report is rebuilt and `NextTemporalDeadline` is recomputed — but the `OnNext` to subscribers is **deferred until the lock is released**, exactly as `EmitStatusChanged` already defers `StatusChanged` (the repo invariant: observer notifications fire outside `_propagationLock`). In the documented lock ordering `_propagationLock → _topologyLock → observer locks`, this channel sits at *observer locks*: its subscribers run after the wave completes, with no propagation lock held. Nothing about it is evaluated while a caller holds `_propagationLock`.
- **NOT a health emission.** It fires *even when the report compares equal*, which is exactly the debounce-hold case above; that is why it cannot ride `StatusChanged` and must be its own channel.
- **NOT carried in the report.** The deadline never enters `HealthReport`/`HealthSnapshot`, so it never touches the report-equality question ADR-012 pins — it is not a health fact and does not belong in the health picture. This keeps §6 orthogonal to ADR-012 by construction: the report answers "what is the health," this channel answers "when might a policy's answer next move."
- **"Changed" means the minimum *value* changed, and value is all that is compared.** The trigger is inequality of the `TimeSpan?` minimum between this wave's end and the last emitted value. Two things follow deliberately: (a) **provenance does not count** — if the node holding the minimum changes but the minimum instant is identical, the channel stays silent, because the pump needs *when* a policy answer can next move, not *which* node owns it (the pump re-reads all pending nodes at the deadline, §5's `RefreshAll` shape); (b) waves on one graph are **serialized** by `SerializedBubble`, so two waves never compute a minimum concurrently on the same graph — the "last emitted value" is read and updated inside the serialized wave, and the comparison is well-defined. A node in two graphs emits per-graph (each graph tracks its own minimum), matching §4's per-graph propagation.
- **A wave over unchanged nodes stays silent**, so a busy edge-driven graph does not spam the pump; the notification carries signal, not cadence.
- **Shared and replay-latest.** The channel is a single shared observable that **replays the current minimum on subscribe** (`BehaviorSubject`-like), not a cold per-subscriber stream. The pump's usage — subscribe once, read the current deadline, sleep, wake on each change — depends on this: a cold observable would leave a late subscriber blind to the deadline already pending at subscription time. Pinned here so an implementation does not regress it to Rx's cold default.

**Re-entrancy is bounded, and unlike §8's synthetic-node trap it cannot recurse unboundedly.** A subscriber whose wake-up triggers another wave on the same graph re-enters `SerializedBubble` *after* the lock is released (the emission is outside `_propagationLock`), and that re-entrant wave re-derives the deadline from current state. There is no feedback loop of the kind §8 rejects for a report-fed synthetic node: this channel is not health, does not enter report equality, and a re-entrant wave that finds an unchanged minimum emits nothing — so the recursion terminates the moment the deadline stops moving, which a correctly re-arming pump reaches in one step.

This is what lets a deadline-driven consumer pump **re-arm**: it subscribes once, reads the current deadline, sleeps until the surfaced instant, waves the graph, and re-reads — and whenever a hold silently installs, extends, or clears a deadline, this notification (not the report stream) is what moves its alarm. It is also the subscription a future deadline-aware `HealthMonitor` consumes: if the library ever grows the blessed shell loop (ADR-011 OQ5 / ADR-010 OQ3 — monitor-assisted deadlines), `TemporalDeadlineChanged` is the exact signal it listens to, so surfacing it now is a prerequisite for that phase, not a throwaway. The no-timers doctrine is intact throughout: the library still schedules nothing; it only tells the consumer's alarm clock that it moved.

### 7. Leases and policies are mutually exclusive per node

`Lease()` on a node with policies throws; `WithDebounce`/`WithGrace` on a leased node throws.

This is the structural resolution to the worry that a future damping policy could suppress lease escalation. A damping policy that could hold or soften a lease's stage-two escalation would be an indirect route to the configurability ADR-010 §3 refuses. The first draft exempted "lease-synthesized" verdicts from the chain, but that requires the choke point to *distinguish* an affirmed verdict from a decayed one — and there is no honest mechanism: the reason-prefix is a string convention a producer can forge (`Affirm(Unhealthy("lease-expired: …"))` is legal; ADR-010 does not police verdict content). Mutual exclusion needs no distinction at all, and no plausible node wants both: a lease guards a push-fed cache, a debounce shapes a live edge-driven signal. A producer that wants its affirmed stream damped can damp before affirming — it owns the fold on its side of the push. That escape hatch is not a hand-wave: it is a real, load-bearing migration path for the hardest consumer, so §9 makes it a named, exported pattern rather than a sentence.

### 8. Flap is a derived observation with a defined read surface

Flap tracking **reads the raw transition history and transforms nothing.** It is not a stage; if it sat downstream of the suppressors, a node that flaps constantly but is always suppressed would report zero flaps — hiding precisely the signal it exists to surface. Because §4 records raw transitions at the choke point, flap needs no mechanism of its own: it is a pure projection.

The read surface, so the observation exists somewhere (the first draft defined none):

```csharp
public (HealthEvaluation Effective, NodeObservationHistory History) Observe();   // on HealthNode; one volatile read

public static class FlapWindow
{
    public static int Count(NodeObservationHistory history, TimeSpan now, TimeSpan window);
}
```

Whether flap state reaches `HealthReport`/`HealthSnapshot` — and therefore the wire — is deferred (see Open questions): it would enter report equality, and the report-equality contract is now pinned by **ADR-012** (§1 pins the equality key as `(Name, Status, Reason)` per node; §4 keeps `DiffTo`/the transition stream `Status`-only; §5 forbids a live counter in a reason string, so a flap counter may not ride `Reason`). Per ADR-012 the honest home for flap on the wire is a *structured field* participating in the report key, not `Reason` — the same deferred structured-field question ADR-012 Open question 1 frames. Nothing here precludes that field later; `Observe()` makes the data readable today without touching the wire.

### 9. The producer-side grace fold: export the pure grace core for pre-`Affirm` use

§7 makes leases and policies mutually exclusive, and closes with the escape hatch a leased node uses when it *also* wants grace: "damp before affirming." The consumer mapping proved that hatch is not hypothetical — it is the **only** migration that preserves the hardest cohort's pinned behaviour — so the library must make it a first-class, exported pattern, not a thing each producer re-implements.

**The worked consumer.** A backend-API leaf stacks cold-start grace **and** a freshness guard: grace shapes what a ping's outcome *means* (a failure inside the warmup window is not yet gating), while freshness describes whether the ping *loop is still running* (a dead loop must be visible *even inside* the grace window). That composition is behaviourally pinned by a consumer-side test — cold-start grace applies inside the sample, freshness outside it. Under §7 it cannot be a policied lease. The mapping's resolution: the node takes a **lease**, and the consumer's ping service folds grace **producer-side** and `Affirm`s the grace-adjusted verdict. Grace-inside-the-sample and staleness-outside-it then fall out for free — the lease decays exactly when the pump stops affirming, and grace has already shaped each affirmed verdict before it was pushed.

**The requirement this places on the library.** That fold needs the *same* grace logic the `WithGrace` policy runs — one-way first-live latch, suppress-to-`Unknown`-before-live, bounded by a required deadline — but callable as a **pure function, before `Affirm`, with no node and no graph attached.** If the library does not export it, the consumer must either keep a private copy of grace for this one node — at which point "unified" leaks its first exception on day one, and the private copy drifts from the library's — or give up the pinned behaviour. Neither is acceptable, so:

> **Prognosis MUST export its grace core as a public, pure fold usable independently of a node.** The
> `WithGrace` policy (§1) and producers both call the same function; the policy is the in-graph
> caller, a leased producer is the pre-`Affirm` caller.

The shape, stated without over-specifying the implementation (the internals stay the library's to choose):

The exported surface is **two layers over one internal core**, and pinning that shape is what makes the "same function" guarantee mechanical rather than aspirational:

```csharp
// THE ONE CORE. Internal, pure, node-free. Both WithGrace (the policy path)
// and the public producer surface below delegate to THIS — there is no second
// grace implementation to drift. Divergence is structurally impossible because
// there is only one function; the ADR pins that both callers route through it.
internal static class GraceCore
{
    internal static GraceResult Apply(
        HealthEvaluation raw,    // freshly-sampled verdict
        bool isLiveNow,          // domain liveness bit (§3) — only the producer knows it
        GraceState state,        // one-way latch + deadline bookkeeping (immutable)
        TimeSpan now,            // monotonic clock read (ADR-010 §2 / §5 constraints)
        GraceOptions options);   // required-Deadline options (§1)
}

public readonly record struct GraceResult(
    HealthEvaluation Effective,  // Affirm() this
    GraceState Next);            // thread this into the next call

// LAYER 1 — the pure fold, for a producer that wants to own its own state
// (e.g. to persist/restore it). `now` defaults to the library's own monotonic
// clock so the honest path is the default path; passing a wall clock is
// possible but is opting out, not the easy path.
public static GraceResult ApplyGrace(
    HealthEvaluation raw, bool isLiveNow, GraceState state, GraceOptions options,
    TimeSpan? now = null);       // null => library Stopwatch.GetTimestamp conversion

// LAYER 2 — the recommended ergonomic surface. A thin stateful wrapper over the
// SAME GraceCore that OWNS the GraceState internally, so there is no caller-held
// state to drop, reset, or mis-thread. Returns just the verdict to Affirm().
public sealed class GraceMachine       // construct with GraceOptions
{
    public HealthEvaluation Update(HealthEvaluation raw, bool isLiveNow); // clock internal
}
```

Constraints that keep this honest and consistent with the rest of the ADR:

- **One core, structurally — not "we promise to keep them in sync."** The earlier draft leaned on prose ("literally the same function"); that is documentation, not a guardrail. The pin is now structural: `WithGrace` (the policy path) and both public surfaces delegate to the single `internal GraceCore.Apply`. There is no second implementation that *could* drift, so a future refactor that wanted node-contextual behaviour in the policy path would have to change the shared core (affecting both callers) or fork it (a visible, reviewable new function) — it cannot silently diverge. As belt-and-suspenders, the implementation should carry an equivalence test asserting `WithGrace` and `ApplyGrace` produce identical `GraceResult` for identical inputs, so even an accidental fork fails CI.
- **Two surfaces, one for each real need.** `GraceMachine` (Layer 2) is the **recommended** producer surface: it owns the `GraceState`, so the mis-thread footgun (`_state = ApplyGrace(...).Effective` — copying the verdict where `.Next` belonged, freezing the latch) is *structurally unrepresentable* — there is no `Next` for the caller to mishandle. `ApplyGrace` (Layer 1) stays for the producer that legitimately needs to hold and persist the state itself; it is the lower-level primitive `GraceMachine` is built from. Both are the same `GraceCore`. This is the middle ground between "keep grace private" and "export a raw pure fold and hope the producer threads it right" — the raw fold is available, but the state-owning wrapper is the one the docs point producers at.
- **Pure core, node-free.** No `HealthNode`, no history pair, no wave anywhere in `GraceCore`. `WithGrace` stores the returned `GraceState` in the node's history pair (§4); `GraceMachine` stores it in a private field; a Layer-1 caller stores it wherever it likes.
- **The clock is a stated constraint the default now makes easy, not a runtime-validated one.** `now` MUST come from a lock-free, side-effect-free, **monotonic** source (`Stopwatch.GetTimestamp`-derived, per ADR-010 §2 and §5). A `TimeSpan` cannot enforce that — a producer passing `DateTime.UtcNow.Ticks` satisfies the type while violating the discipline — and the library cannot check monotonicity or purity of an already-read value; this is the *same* limitation ADR-010 §2 accepts for its injected `Func<long>` ("stated in the doc comment, not validated at runtime; purity is not checkable"). What the ADR now **commits** (not merely "should offer") is that the sanctioned source is the **default**: `ApplyGrace`'s `now` is optional and defaults to the library's own `Stopwatch.GetTimestamp` conversion, and `GraceMachine.Update` reads it internally — so a producer gets the correct clock by writing *less* code, and supplying a wall clock is a deliberate opt-out rather than an easy slip. A producer that does opt out corrupts its own latch/deadline bookkeeping — a `GraceState` misuse the in-graph path structurally cannot suffer (Consequences names this as §9's deliberate cost, now shrunk to the Layer-1 opt-out path).
- **`GraceOptions.Deadline` stays required** here too, so a producer-side grace's `Unknown` still has a mechanical resolution path — ADR-008's transience contract holds for the pre-`Affirm` caller exactly as it does for the policy.
- **The producer owns the grace deadline; it is not surfaced through `NextTemporalDeadline`.** Because the node is *leased*, not policied (§7), its `GraceState.Deadline` lives on the producer's side of the push and never reaches the graph's `NextTemporalDeadline` — that field surfaces *policy* deadlines only. This is correct, not a gap: the producer already runs its own sampling loop (it is the thing calling `Affirm`), so it already owns a wake-up cadence and can consult its own `GraceState.Deadline` directly. The unified `NextTemporalDeadline` pump (§6) covers policied nodes; a producer-side grace fold is served by the producer's existing loop. There are two deadline owners by construction — the graph for policies, the producer for its own pre-`Affirm` grace — and §7's exclusivity is exactly what keeps them from overlapping on one node.
- **This is the *only* sanctioned lease+grace composition.** It does not reintroduce policies on leased nodes (§7 stands); grace runs entirely on the producer's side of the push, on the raw verdict, before the library ever sees it. The library sees only the already-grace-adjusted verdict a plain `Affirm` carries.

This is the one place the two features legitimately compose, and it composes *outside* the graph, by the producer's own hand, using a library primitive — which is exactly why it does not violate §7's per-node exclusivity.

### 10. Graph-wide defaults, materialized at attach, with explicit-wins provenance

§1 made temporal policy a per-node opt-in and never revisited the scaling question. At the two-node scale that motivated this ADR that is exactly right. At the scale the library is actually deployed — a graph of dozens of leaves whose USB and network devices all twitch the same way — "call `WithDebounce` on each leaf" is not a policy model, it is a checklist, and the failure mode is the node somebody forgot.

**What a consumer must write today, and why that is not good enough.** The graph already exposes everything needed to approximate this: `graph.Nodes` for the current set, and `TopologyChanged.Added` for nodes that appear later via `DependsOn`. So the approximation is a dozen lines. It carries four defects, and each one is a thing the library knows and the consumer does not:

1. **It clobbers.** `WithDebounce`/`WithGrace` overwrite unconditionally. A node with its own tuned window loses it to the blanket default whenever the default is applied second — which, for late-added nodes, is *always*, because the topology handler necessarily runs after the node was built.
2. **It throws on leased nodes.** §7 makes `WithDebounce`/`WithGrace` throw on a leased node, correctly, for an *explicit* call. A blanket default hitting the same wall turns one legal `Lease()` into a startup crash, so every consumer wraps the loop in a `catch (InvalidOperationException)` — swallowing the exception that §7 exists to raise.
3. **It cannot see provenance.** `IsTemporal` is `internal`, and no public member reports whether a slot is set or where it came from — so a consumer loop cannot ask "is this node already configured, and by whom?" It guesses. (Two things it *can* ask, which the first draft wrongly lumped in here: leaf-ness is public via `Dependencies`, and leased-ness is observable indirectly through ADR-013's `TemporalState.Staleness`, which is non-null exactly when a node is leased. The gap is provenance and a direct leased predicate, not all three.)
4. **It races the first wave.** Nodes attached in the constructor are policied only *after* `Create` returns, so the constructor's initial wave (`_root.RefreshDescendants(ElapsedNow())`) runs the identity chain and the deadline seed (`_temporalDeadline.Seed`) is computed against no policies — the graph publishes a first report and a first deadline that a defaults-configured graph should never have had.

So the graph owns the defaults:

```csharp
public sealed record TemporalDefaults(
    DebounceOptions? Debounce = null,
    GraceOptions? Grace = null,
    Func<HealthNode, bool>? AppliesTo = null);   // scope predicate; null => the §10 default scope

public static HealthGraph Create(HealthNode root, TemporalDefaults defaults);
public static HealthGraph Create(HealthNode root, Func<long> clock, TemporalDefaults defaults);

public TemporalDefaults? Defaults { get; }       // null when none were supplied
```

#### 10a. Materialized at attach — the two existing attach sites, and nowhere else

Defaults are **materialized into the node's policy fields at attach**, not consulted at evaluation time. There are exactly two attach sites and the hook goes in both: the constructor's `foreach (var node in allNodes) node._bubbleStrategy += SerializedBubble` and `RefreshTopology`'s `foreach (var node in added)` loop. Nothing else adds a `_bubbleStrategy` subscription, so these two are exhaustive.

The two sites have **different** safety arguments, and conflating them was the first draft's error:

- **The constructor site holds no lock at all.** It does not need one: it runs before the graph is published, before `_root.RefreshDescendants(ElapsedNow())` stamps the first wave time, and before `_temporalDeadline.Seed(...)` captures the first minimum. So materialization here is unobserved by definition, and it closes defect (4) — the initial wave and the seeded deadline both see the policies.
- **The late-add site is inside `_propagationLock`, and it runs *after* that wave, not before it.** `SerializedBubble` calls `origin.BubbleChange(now)` and only then `RefreshTopology()`, so the added-node loop executes with the wave already finished. The property §10 needs still holds, but for a narrower reason that must be stated rather than assumed: a node added by `DependsOn` is not in the propagation scope the preceding `BubbleChange` walked, so that wave never evaluated it. Its first evaluation is the next wave, by which time it is policied. **This is a real coupling to `EvalInDependencyOrder`'s scope check**, not a lock-ordering guarantee, and an implementation that changed when the added set is computed would break it silently.

Materializing rather than resolving is the load-bearing choice, and §4/§5 force it. A node may be attached to two graphs, but it has exactly **one** copy of the §4 state — one `GraceState` latch, one deadline anchor, one history — CAS-swapped by whichever graph waves it. (§4 calls this "the pair" and the concurrency-hardening note enumerates its five fields; the implementation names the record `EvaluationState`, and §10 uses that name where it needs to distinguish it from the `TemporalPolicySet` of §10c.) A resolve-at-wave-time default (`_grace ?? wavingGraph.Defaults.Grace`) would run **two different chains over one shared piece of state**: two graphs with different grace deadlines would take turns anchoring the same `DeadlineAt`, and the latch would be shared by two policies that disagree about what it means. Materializing collapses that to a single value on the node, where the conflict is *detectable* — see 10c.

#### 10b. Explicit wins, always, regardless of order

Each policy slot gains provenance:

```csharp
public enum TemporalPolicyOrigin { Unset, GraphDefault, Explicit }
```

- `WithDebounce`/`WithGrace` set the slot and latch it `Explicit`. This is unchanged behaviour plus a bit.
- A default fills a slot **only when it is `Unset`**, and marks it `GraphDefault`.
- A default never overwrites `Explicit` — so a node configured at its own construction site keeps its tuning no matter when the graph attaches it. Defect (1) becomes unrepresentable rather than order-dependent, which matters precisely because the ordering is not under the consumer's control for late-added nodes.
- A later `WithDebounce`/`WithGrace` **does** overwrite a `GraphDefault` slot and re-latches it `Explicit`. Overriding a default at runtime is the point; overriding an override is not.

The provenance is also the fix for defect (3)'s missing read surface, which this ADR should have shipped with §1 and did not:

```csharp
// on HealthNode
public bool IsLeased { get; }
public TemporalPolicyView<DebounceOptions> DebouncePolicy { get; }
public TemporalPolicyView<GraceOptions> GracePolicy { get; }

// Effective = Explicit ?? GraphDefault (null while leased); the losing contribution
// stays visible rather than being collapsed away.
public sealed record TemporalPolicyView<TOptions>(
    TOptions? Effective, TemporalPolicyOrigin Origin,
    TOptions? Explicit, TOptions? GraphDefault) where TOptions : class;
```

This is a read surface only — it is how a consumer, a test, or a diagnostic answers "why is this node damped," which under defaults is no longer answerable by reading the node's construction site. Nothing about it lets a consumer *reach into* the chain.

#### 10c. Conflicts throw at attach; leases are skipped, not thrown at; and both directions of the lease interaction are defined

Two rules, deliberately asymmetric, because the two situations are:

- **A second graph materializing a *different* default into a slot already marked `GraphDefault` throws at attach.** Identical defaults are idempotent and silent — the common shared-subgraph case costs nothing. A genuine disagreement is a consumer wiring error whose only alternative resolutions are silent and order-dependent, and the graph constructor already throws on exactly this class of ambiguity (`ValidateUniqueNames`). Making it unrepresentable is cheaper than debugging which graph won.

  **Compatibility is compared per slot, on the materialized value — never by `TemporalDefaults` record equality.** Whole-record equality would reject configurations that do not actually conflict: a graph defaulting `Debounce: X` and a graph defaulting `Debounce: X, Grace: Y` have unequal records, but their debounce materializations agree, and the second graph should be free to fill the still-`Unset` grace slot. The rule is therefore evaluated **slot by slot, for the node actually being attached**, and all four origin cases are enumerated so an implementation needs nothing but this paragraph:

  | Slot origin | Incoming default | Outcome |
  |---|---|---|
  | `Unset` | present | fill, mark `GraphDefault` |
  | `Unset` | absent | nothing |
  | `GraphDefault` | equal (by `DebounceOptions`/`GraceOptions` record equality), or absent | nothing |
  | `GraphDefault` | different | **throw** |
  | `Explicit` | any | **nothing — skip silently** (§10b: explicit wins, and it is not a conflict) |

  Scope enters only through node selection: a default whose `AppliesTo` excludes this node materializes nothing and therefore cannot conflict over it.

  **A leased node skips both slots** (below), and — the reciprocal §10 owes ADR-010 — **`Lease()` on a node carrying only `GraphDefault` or `Unset` slots succeeds, and the default becomes inert rather than being destroyed.** §7's throw is narrowed to `Explicit` slots only. Without this narrowing §10 would silently revoke a capability ADR-010 §1 grants ("callable at build time or at runtime"): a node attached to a defaulted graph would become permanently un-leasable. That is the same startup-crash defect (2) was written to remove, merely relocated to the other ordering — and it would falsify §10's own claim that a blanket default and a `Lease()` coexist in one graph. The narrowing is exactly symmetric with the skip-leased rule: a default names nobody, so a lease outranks it; an explicit `WithGrace` names the node, so it still collides.

> **Amendment note (2026-08-08 — retained sources).** An earlier draft of this
> subsection had `Lease()` **clear** the default slot, and had each slot store one materialized
> winner. Implementation showed that collapsing sources is what forces the ugliness: acquiring a
> lease had to silently mutate unrelated configuration, a default could never be revoked because
> no prior value survived to fall back to, and a graph's contribution was indistinguishable from
> the node's own. **Each slot now retains the explicit and graph-default contributions
> separately** and derives the effective policy as `Explicit ?? GraphDefault`, with a lease making
> both inert. Nothing about precedence, conflict detection, or §4's constraint changes — the chain
> still reads exactly one resolved value per slot, so a node in two graphs still runs one chain
> over one `GraceState` latch, which is what forced eager resolution in the first place. Two
> consequences are recorded rather than buried:
>
> - **Leases are deliberately NOT a precedence tier.** Modelling a lease as "the highest source
>   wins" would downgrade §7's designed error into a silent override. The exclusion stays at the
>   write side (an explicit policy still throws); the read side merely reports nothing in effect.
> - **Materialization is all-or-nothing per attach.** Retention raises the stakes on partial
>   application: a written default now *outlives the graph*, so a constructor that policied some
>   nodes and then threw would permanently mutate shared nodes with a bag nobody successfully
>   applied. Attach therefore runs in two phases — every `AppliesTo` predicate first (writing
>   nothing, so the commonest failure strands nothing), then the swaps, each recording the node's
>   prior set so a mid-apply conflict reverts them before rethrowing. The revert is best-effort by
>   construction and says so: if another graph has overwritten our contribution since, its value is
>   left alone. Retention is what makes even best-effort possible — under collapse-on-materialize
>   there was no prior value to restore. At the late-add site materialization also moved *ahead of*
>   subscription and the snapshot swap, so a rejected node is not left bubbling into a graph that
>   refused it.
> - **The sharp edge this does NOT fix, recorded plainly.** A *late* conflicting attach leaves the
>   dependency edge in place — `DependsOn` committed it before the wave, and the library does not
>   silently discard a caller's topology change — so the graph stays poisoned until the wiring is
>   fixed. The failure is asymmetric: `RefreshAll` walks the existing snapshot and is unaffected,
>   as is a refresh of the rejected node itself (the ordering above means it was never subscribed
>   to the graph that refused it), but any propagation that *reconciles topology* — refreshing the
>   graph's own root, for instance — re-attempts the attach and throws. Failing loudly on
>   a hard wiring error is defensible; failing loudly forever on some paths and not others is a
>   wart, and it is the strongest argument for eventually detecting conflicts before `DependsOn`
>   commits.
> - **Default-vs-default conflicts are detected only where no explicit policy has settled the
>   slot.** An interim revision of this note widened the rule to throw *even under* an explicit
>   value, reasoning that the retained layer must stay unambiguous for a future revocation (OQ7).
>   **That is reverted, and the reasoning was wrong twice over:** it traded a real,
>   available escape hatch for a hypothetical feature that does not exist and is deferred behind
>   two open questions, and the hatch it removed is the *only* remedy for a contested shared node
>   that requires no restructuring. An explicit policy is the node's own statement, it outranks
>   every default, and once present the graphs are arguing about a value neither can apply.
>   Stating it therefore settles the disagreement — one call, no rewiring, no second node, no
>   graph reconfiguration — and it is now what the conflict message leads with. The retained
>   default under an explicit policy is the first contribution, recorded for diagnostics only;
>   with the explicit value standing there is no right answer to pick between contested defaults
>   and no need to pick one. If revocation ever ships it decides for itself what a revoked slot
>   with contested defaults does, and falling back to unset is a fine answer.
>
>   This also changes the §10a late-attach story materially. A late attach rejected for a default
>   disagreement can now be **completed** rather than merely reported: settle the node's policy
>   and the next reconcile attaches it, so the graph un-poisons itself. The residual sharp edge
>   (the committed edge, and the throw on topology-reconciling propagation until the wiring is
>   settled) is unchanged, but its remedy is no longer restructuring.
  **The check and the fill must be one atomic operation, because the graph locks do not serialize this — and it must not be done with a lock.** `_topologyLock` is **per graph**, so two graphs attaching the same node concurrently take two *different* locks and serialize against nothing. A naive check-then-act would let both observe an `Unset` slot and both materialize: a race-dependent winner and, worse, *no* conflict exception in exactly the disagreeing case this rule exists to catch.

  The obvious repair — take the node's existing `_policyWriteLock` while materializing — **is a deadlock**, and the ADR states this explicitly so no implementation rediscovers it. `HealthNode.Lease` calls `Refresh()` *inside* `_policyWriteLock`, and `Refresh()` invokes `_bubbleStrategy` → `SerializedBubble` → `lock (_propagationLock)` → `RefreshTopology()` → `lock (_topologyLock)`. The established order is therefore **`node._policyWriteLock → graph._propagationLock → graph._topologyLock`**, and materializing under `_topologyLock → node._policyWriteLock` is its exact inversion. Two threads, one node shared by two graphs: T1 in `Lease()` holds the policy lock and blocks on graph B's propagation lock; T2 inside B's wave holds propagation and topology and blocks on the policy lock. Neither proceeds.

  So materialization is **lock-free, over one immutable record swapped by CAS** — the mechanism §4 already uses for the evaluation state, applied to the policy state:

```csharp
// One reference, swapped with Interlocked.CompareExchange. Read with Volatile.Read.
internal sealed record TemporalPolicySet(
    DebounceOptions? Debounce, TemporalPolicyOrigin DebounceOrigin,
    GraceOptions? Grace,       TemporalPolicyOrigin GraceOrigin,
    bool IsLeased);
```

  A materializing graph reads the current set, computes the successor per the slot rules below, and CAS-swaps; a lost CAS re-reads and re-decides, so the loser of a race always evaluates its rules against the winner's *materialized* values and the outcome collapses to the sequential case. Which graph wins is unspecified and irrelevant; *that a disagreement throws* is guaranteed either way, because the throw is decided on the observed set inside the loop.

  **`IsLeased` rides inside that same record, and this is load-bearing rather than tidy.** If leasing kept its own lock while defaults used a CAS, the two would not serialize at all: a concurrent `Lease()` and default-materialization could both observe a node that is neither leased nor policied and both succeed, leaving a node that is leased *and* policied — breaking §7's mutual exclusion structurally, which is precisely the guarantee §7 claims to make structural. Folding the leased bit into the CAS'd set makes that state unrepresentable. This is the same correction, for the same reason, that the concurrency-hardening amendment applied to §4: a field left outside the swap makes the atomicity claim false in exactly the multi-writer case the swap exists for. `Lease()` therefore participates in this CAS too, and its §7 check becomes part of the swap rather than a separate guarded read.
- **A leased node is skipped, silently.** §7 stands unamended for explicit calls: `WithGrace` on a leased node still throws, because the consumer named that node and asked for something impossible. A default names nobody. It is a statement about the nodes for which it makes sense, and a lease is the node's declaration that it is not one of them — the producer-side fold of §9 is that node's grace story. Throwing here would mean a blanket default and a single `Lease()` cannot coexist in one graph, which would make defaults unusable in exactly the mixed graphs that need them. Defect (2) resolves without a `catch` that swallows §7's real signal.

#### 10d. Scope: leaves by default, widened only deliberately

The default scope is **leaves** — nodes with no dependencies at attach time — because that is the scope §1 gave the chain, and OQ1 (policies on composites, where a debounced parent of debounced children double-damps) is still open. A defaults bag that quietly installed policies on every composite would resolve OQ1 by accident, in the double-damping direction, for every consumer at once.

`AppliesTo` widens or narrows it explicitly. The expected key is tags (ADR-005: tags are node identity, immutable, and therefore an honest scoping key — `n => n.Tags.ContainsKey("device")`).

**Two corrections to how the first draft framed this scope, because both were wrong in the consumer's favour.**

First, **leaves-only is a decision §10 is making, not a constraint the code enforces.** §1 and §4 say "the chain runs on leaves only" as an ADR-level scoping statement; nothing in `WithDebounce`, `WithGrace`, or `NotifyChangedCore` inspects `Dependencies`, and the chain will run on a composite's post-`Aggregate` verdict today if a policy is installed there. So §10d is not preserving an existing guardrail — it is supplying the only one there is.

Second, and following from it, **`AppliesTo: _ => true` is not the sanctioned escape hatch the first draft implied.** Saying "a consumer who wants composites can just widen the predicate" resolves OQ1 by predicate — on purpose and one keystroke away, rather than by accident for everyone, but resolved either way and with no ADR having decided the double-damping semantics. The honest statement is narrower: **a widened predicate materializes policy slots on composites, and what the chain then does with them is exactly the undecided behaviour of OQ1.** A consumer may write it; they are opting into an open question, not into a supported configuration, and §10b's read surface will report a policy whose composite semantics this ADR has not pinned. If composite policies are wanted as a feature, that is OQ1's job and it needs its own decision.

**The predicate runs inside the attach critical section, so it carries the same contract as the injected clock.** At the late-add site `AppliesTo` is invoked with `_propagationLock` **and** `_topologyLock` held (§10a); at the constructor site no lock is held, but the graph is unpublished and a predicate that blocks still hangs `Create`. It MUST therefore be **pure, non-blocking, and free of any call back into `HealthGraph` or `HealthNode` mutation** — reading `Name`/`Tags`/`Dependencies` is the intended use and is safe; taking a lock, doing I/O, waving a graph, or calling `DependsOn` from inside it is a lock-order violation or an attach-time deadlock. This is the *same* unvalidated-contract treatment ADR-010 §2 and §5 accept for the injected `Func<long>` clock, which is likewise read inside the propagation path and likewise cannot have its purity checked at runtime; the constraint is stated and documented, not enforced. Two things the ADR does pin rather than leave to the implementation:

- **Invoked exactly once per node per attach**, on the attaching graph's `added` set — never re-evaluated on a wave, so a predicate that is accidentally non-deterministic cannot make a node's policy flicker.
- **A throwing predicate fails the attach and propagates**, with the offending node named. Swallowing it would leave a graph whose scope silently differs from the one the consumer wrote. **But it is all-or-nothing only in the constructor**, where `ValidateUniqueNames` already establishes that shape and nothing has been mutated yet. At the late-add site there is no rollback and the ADR does not pretend otherwise: by then `DependsOn` has committed the edge and the parent back-reference, earlier nodes in the same `added` batch may already be `_bubbleStrategy`-subscribed and materialized, and `_snapshot` has not been swapped. A throwing predicate there leaves a half-attached graph. This is a genuine sharp edge of putting a consumer callback at that site, it is the strongest argument for the predicate being trivially pure, and it is why the same throw is a *validation* failure in one place and a *corruption* risk in the other.

#### 10e. A materialized default is node state, and it travels with the node

The consequence of §10a that must be stated rather than discovered: **once materialized, a `GraphDefault` is a property of the node, not of the graph that installed it.** A node attached first to a defaulted graph A and later also to an undefaulted graph B runs A's debounce in B as well, because B supplied no default and therefore made no statement — there is no "clear" operation, and inventing one would mean an undefaulted graph could silently strip a policy another graph deliberately installed.

Two clarifications, because this is the easiest part of §10 to get wrong in either direction:

- **In this case — B undefaulted — the end state is order-independent, and only its timing is not.** A-then-B and B-then-A both terminate with the node carrying A's default, because an undefaulted B never fills or clears a slot. What differs is *when* B's waves begin seeing it. So this is not the order-dependence §10b exists to eliminate; it is a scope question with a single deterministic answer.

  **This claim is scoped to an undefaulted B and does not generalize.** Where A and B supply *different* defaults for slots the node is in scope for, there is no successful terminal state in either order at all: whichever graph attaches second throws under §10c, which is the intended outcome and the whole point of the conflict rule. Where they supply *identical* defaults, or defaults touching disjoint slots, both orders succeed and agree. Those are the only three cases, and only the first two are order-independent in any useful sense — a reader must not carry "defaults are order-independent" across from this bullet to the conflicting case.
- **Detach does not un-materialize.** Consistent with the existing rule that graph detach/dispose leaves node state untouched, removing a node from A leaves the materialized default in place. `GraceState` latches and history survive detach today for the same reason; a policy slot is no different. **Follow the consequence through, because it is not only about sharing:** a node detached from A — or outliving a disposed A — that is later attached to a differently-defaulted graph B still throws under §10c, even though A is gone and nothing is shared at that moment. The conflict rule is over the *materialized value*, which has no memory of whether its installer still exists. `Lease()` is the sole operation that clears a `GraphDefault` (§10c); short of that, a node that has ever passed through a defaulted graph carries that default for the rest of the process.

**The rule this implies, stated normatively:** a node shared across graphs whose temporal defaults differ *in the slots that node is in scope for* is a wiring error — §10c throws on the conflicting case. A node shared between a defaulted and an undefaulted graph is **legal and means the node is defaulted in both**. A consumer that genuinely needs one node to run different policies in two graphs cannot have it, at any layer of this design (§4: one node, one `EvaluationState`, one latch), and should use two nodes.

One sharp edge, stated rather than engineered around: **leaf-ness is evaluated at attach and not revisited.** A node that is a leaf when attached and later grows dependencies keeps its materialized default, because by then the slot is filled and re-deriving scope on every topology change would mean silently *removing* a policy a node has been running under — a worse behaviour than keeping it. Consumers who build graphs bottom-up get the scope they see; consumers who attach a bare parent and fill it in later should scope with `AppliesTo`.

#### 10f. A grace default is legitimate, is not the safe one, and requires a wave source

Nothing here forbids `TemporalDefaults(Grace: …)`, and §1's required `Deadline` means a grace-emitted `Unknown` always carries a resolution *path* — a never-live node resolves at the deadline with no owner cooperation. That is weaker than "cannot violate ADR-008 however carelessly it is applied," which an earlier draft of this section claimed and which the rest of this section then contradicted. ADR-008's requirement is that a node MUST NOT **rest** at `Unknown` in steady state, and a deadline that is never reached is not a resolution path — it is a resolution path's parameter. A grace default over a graph nothing waves therefore *does* violate ADR-008, which is why the wave-source requirement below is a MUST and not advice. The Alignment bullet for ADR-008 is qualified accordingly. But the honest guidance belongs in the ADR and not only in the doc comment: grace suppresses to a non-gating `Unknown` until `MarkLive()`, and §3 is explicit that liveness is a **domain fact only each node's owner can supply**. A graph-wide grace default therefore asserts something about nodes whose owners may never have heard of it: every one of them sits non-gating for a full `Deadline` after construction, then gates on raw merits anyway. That is a real cost paid by every node in scope to protect the few whose owners call `MarkLive`.

**Debounce is the safe blanket default; grace is the deliberate one.** A grace default should be paired with a narrow `AppliesTo`.

**What resolves a node whose owner never calls `MarkLive`, stated explicitly:** the deadline does, and it needs no owner cooperation whatsoever. Once `now >= DeadlineAt`, the grace fold passes the raw verdict through **unchanged and gating** — an unmarked node is suppressed for exactly `Deadline` from its first fold and then reports its real failure. That is the whole reason §1 makes `Deadline` a required constructor parameter and not an option: a grace whose `Unknown` has no resolution path is unrepresentable, so "the owner never wired `MarkLive`" degrades to "this node is suppressed for `Deadline`, once, at startup," never to "this node is suppressed forever." The library requires nothing of the node owner here, which is deliberate — §3 establishes that liveness is a domain fact the library can never derive nor compel, so a design that *depended* on owner cooperation for its safety property would have no way to enforce it. The safety property rests on the deadline instead, and the deadline is mechanical.

The one thing that can defeat that is a graph nobody waves, which is why the MUST below is about the wave source and not about `MarkLive`.

**And "resolves mechanically" is conditional on a wave, which §10 makes normative rather than advisory.** ADR-008 compliance for a grace-emitted `Unknown` rests on the deadline being *reached*, and the library schedules nothing (Non-goals) — so a grace default over a graph nothing ever waves suppresses every in-scope leaf's real failure not for `Deadline` but **indefinitely**. That is the undriven-graph trap, already the ADR's documented coupling for a single policied node, except that a graph-wide default converts it from one node's problem into the whole graph's, and the failure is silent in the dangerous direction (a graph reporting non-gating `Unknown` looks calm). The existing machinery already detects this exactly — a defaulted graph has `HasTemporalNodes == true`, so `WarnIfTemporalWithoutWaveSource` fires — and §10 raises its status for this configuration:

> A graph configured with a grace default MUST be driven by a wave source (`RunMonitor()`, the DI `UseMonitor`, or a consumer pump on `NextTemporalDeadline`). A grace default without one is a misconfiguration, not a degraded mode.

The ADR stops at MUST rather than at a constructor throw for one reason: a wave source is attached *after* `Create` by construction (`AttachWaveSource` is called by the monitor's constructor, which takes the graph), so a graph cannot know at defaults-materialization time whether one is coming. Validating it at attach would reject the correct wiring order. The diagnostic that *can* see it already exists and already covers this case; what §10 adds is that for grace defaults it is checking a requirement rather than offering advice.

**Where the MUST stops being advisory: the DI path runs the check for you.** `WarnIfTemporalWithoutWaveSource` is a hook a consumer must remember to call, which is exactly the weakness a graph-wide default should not inherit. But the DI surface (OQ6) owns graph construction *and* the post-wiring moment — it is the one place that knows both that grace defaults were configured and whether `UseMonitor` was ever called. So `PrognosisBuilder.WithTemporalDefaults(…)` carrying a grace default MUST, when it is built (OQ6 — it does not exist yet, and this is a requirement on it, not a description of shipped behaviour), **run the diagnostic automatically at startup** with no consumer call site, and escalate it from a warning to a startup failure when nothing has attached a wave source. That would close the enforcement gap for the wiring path this ADR expects most consumers to use, at the only layer with enough information to close it. The hand-wired `HealthGraph.Create` path keeps the advisory diagnostic, because there the consumer has taken ownership of the wiring order and the library genuinely cannot tell "no monitor yet" from "no monitor ever."

## Non-goals

- **No timers or background work in the library.** Inherited verbatim from ADR-010. §6 surfaces *when*; the consumer still owns *waking up*.
- **No consumer-implementable policy interface.** §1's reasoning; revisit only after the two library policies have survived an implementation and a real consumer.
- **No new node kind and no decorator nodes.** `HealthNode` is sealed by project convention, and temporal policy is not a structural fact; it must not appear as topology. §10's defaults are scoped by a predicate over node identity, never inherited down dependency edges, for the same reason.
- **No per-subtree or hierarchical policy scopes (§10).** One defaults bag per graph, a predicate to scope it. Cascading scopes would make a node's effective policy a function of its position in the topology — the thing the bullet above forbids.
- **Not a replacement for ADR-010.** Leases are a verdict *source*; policies are *transforms* over a live signal. §7 makes them exclusive rather than composed.
- **No wire schema change.** Effective statuses and reasons ride fields that already exist. §6a's `TemporalDeadlineChanged` is an in-process notification channel, not a report/wire field — it is emitted precisely *because* it must not enter the report (ADR-012) — so it adds API surface without touching the heartbeat schema. §9's exported grace core is likewise a public function, not a wire type. **Qualified for §10:** the schema is still unchanged, but a graph-wide default changes how densely one already-existing optional field is *populated* — ADR-013's `HealthSnapshot.Temporal` goes from sparse to present on every in-scope leaf. No consumer needs a new parser; a consumer sized for sparse temporal data, or one treating `Temporal == null` as a signal rather than an absence, does see a behaviour change. See Consequences.

## Rejected alternatives

- **An open `IHealthPolicy` pipeline with library-fixed order (the first draft).** An arbitrary consumer implementation has no library-assignable slot, so "fixed order" and "consumer-supplied array" contradict; purity is unverifiable; and a deadline cannot be extracted from an opaque `Apply`. Typed library policies dissolve all three.
- **Deriving the grace latch from verdict history.** Cannot work: the suppressed verdict is a determined `Unhealthy`, and the `Create` default seeds `Healthy`. §3. The latch is a domain input.
- **Fixed grace-then-debounce order (the first draft).** Contradicts the only field-proven composition (the field gate runs debounce, then grace, for latch-advancement reasons) — and the two are disjoint by construction, which the first draft missed entirely.
- **Per-policy state slots.** Grows the node per policy and forces each policy author into their own persistence and threading story. One shared history, one pair, one swap.
- **Two separately-swapped volatile fields (evaluation, history).** Torn pairs: a reader can see a new evaluation against an old history. One reference to one immutable pair.
- **Flap state carried in `HealthNode.Tags`.** ADR-005 is explicit that tags are node *identity*, immutable after `WithTags`, with that immutability load-bearing for thread safety; its own trade-offs section directs dynamic metadata elsewhere. A mutable counter in tags is also invisible to `DiffTo`.
- **Flap state carried in `Reason`.** A standing counter on a healthy node inverts `Reason`'s documented meaning, and a changing counter lands in the report-equality trap (`Reason` participates in `HealthReportComparer.Equals`): a fresh `StatusChanged` on every wave while `SelectHealthChanges` emits nothing. Same defect class as the report-churn defect.
- **A synthetic flap node fed by the report stream.** A feedback cycle. `EmitStatusChanged` fires **outside** `_propagationLock` (the repo's documented invariant) and invokes `observer.OnNext` synchronously on the emitting thread — so an observer that writes a node re-enters `SerializedBubble` freshly, produces a new report, and emits again, recursing on the call stack with no lock involved. The recursion terminates only when consecutive reports compare equal; a synthetic node whose reason carried a live count never converges — unbounded synchronous recursion, invisible to any test over a static graph. It would also count its own transitions unless excluded from its own input. §8 avoids the class by never deriving state from the output stream.
- **Leaving graph-wide defaults to the consumer (`foreach (var n in graph.Nodes)` plus a `TopologyChanged` subscription).** It is a dozen lines and it is what consumers will write, which is the argument *for* the library owning it, not against: the four defects in §10 — clobbering explicit config, throwing on leased nodes, guessing at leaf-ness and provenance behind an `internal IsTemporal`, and missing the constructor's first wave and deadline seed — are all consequences of the consumer standing outside information the graph has. Three of the four cannot be fixed from outside the library at all.
- **Resolving defaults at evaluation time (`_grace ?? wavingGraph.Defaults.Grace`) instead of materializing at attach.** A node in two graphs has one `EvaluationState` (§4) and two potential defaults, so the two graphs would run different chains over one shared `GraceState` latch and deadline anchor, taking turns re-anchoring it. Materializing at attach (§10a) makes the same disagreement a detectable conflict that throws (§10c) rather than a state corruption that does not.
- **Defaults that override explicit per-node calls.** Inverts the word. The per-node call is the specific statement and must win; §10b makes it win regardless of attach order, which is the part a consumer-side loop cannot do.
- **Forbidding grace in a defaults bag, or requiring a non-null `AppliesTo` for it.** Tempting, because §10f concedes grace is the unsafe blanket default — but it declares the wrong thing illegal. A required predicate is satisfied by `_ => true`, so it buys a keystroke of friction and no safety; and grace-by-default is genuinely correct for a graph whose leaves are *all* owner-managed devices with `MarkLive` wiring, which is precisely the device-attached shape that motivated this ADR. The real hazard is not a wide grace scope, it is a grace scope with **no wave source**, which is a different condition, is detectable, and is where §10f puts its MUST.
- **Clearing a materialized default when a node is attached to an undefaulted graph.** Would let a graph that expressed no policy preference silently strip a policy another graph deliberately installed, and would make the outcome depend on attach order — reintroducing the exact non-determinism §10b exists to remove. §10e states the ownership rule instead: no default means no statement.
- **Applying defaults to composites by default.** Would resolve OQ1 by accident, in the double-damping direction, for every consumer simultaneously. §10d scopes to leaves and makes widening an explicit predicate the consumer writes.
- **Global per-node update-time stamping.** Rejected by ADR-010 for staleness and the reasoning holds here: evaluation instants measure the library's own polling. §4 stores *transition* instants, which are rare and meaningful.

## Alignment with prior ADRs

- **ADR-002 — single non-null cache.** The pair *replaces* `_cachedEvaluation` as the one cached value; there is no second evaluation cache. Honest accounting: the history half is library-owned per-node state derived from the node's own evaluations — a genuinely new kind of state on `HealthNode`, not "the same category as a probe's captured state". The trade is argued in Consequences, not hidden.
- **ADR-004 — probes are delegates on nodes.** Unchanged. The chain sits after the probe slot; §7 keeps the lease mode out of its path entirely.
- **ADR-005 — tags are identity.** Reaffirmed by not using them to *carry* temporal state. §10d does use them as the expected key for a defaults *scope* predicate, which is the same property read the other way: tags are immutable node identity, so scoping on them is stable for the node's lifetime and cannot drift under the graph.
- **ADR-006 — `Unknown` is non-gating.** Unamended. A grace-suppressed `Unknown` folds exactly as pinned.
- **ADR-008 — `Unknown` is transient.** `GraceOptions.Deadline` is required, so every policy-emitted `Unknown` carries a mechanical resolution path by construction — the contract ADR-010 §1 satisfied for leases, satisfied here for grace. **Qualified by §10f:** "by construction" covers the *representation* — a deadline-less grace is unrepresentable — not the *execution*. Reaching the deadline requires a wave, and the library schedules none, so an unwaved policied node rests at `Unknown` and violates ADR-008's actual requirement (MUST NOT rest at `Unknown` in steady state). For a single hand-configured node that is the undriven-graph trap and the consumer's own doing; a grace default makes it the whole graph's default posture, and it is the specific shape ADR-008 records as having disarmed a consumer's gating. Hence §10f's MUST.
- **ADR-009 — landed; this builds on it.** ADR-009's emission ordering and topology artifact are already on `main`. The chain runs inside the wave, upstream of everything ADR-009 reordered; `NextTemporalDeadline` follows `GetTopology()`'s cached-read pattern.
- **ADR-010 — leased verdicts.** Precedent for clock injection, purity constraints, and copy-on-write state; §5 notes the clock reconciliation, §7 the exclusivity. Complementary by construction: ADR-010 guards nodes whose producer may die; this ADR shapes nodes whose producer is alive but twitchy. §9 exports the grace core so a leased node can still get grace producer-side without violating §7. ADR-010's own alignment section records the reciprocal of §7 as its structural resolution of that worry.
- **ADR-012 — the report-equality contract.** ADR-012 §1 pins the report-equality key as `(Name, Status, Reason)` per node; §4 pins the transition stream (`DiffTo`) as `Status`-only and names the two streams as complementary; §5 is the `Reason`-content rule. §6a's `TemporalDeadlineChanged` is deliberately *outside* all of that: it is not a health emission and is not carried in the report, so it never enters ADR-012's report-equality key (§1) — the report answers "what is the health," the deadline channel answers "when might a policy's answer next move." ADR-012 §5's scope is the *content of an emitted `Reason`*, which the deadline channel does not have (it carries a `TimeSpan?`, not a health verdict), so §5 does not reach it either. And §8's flap-on-the-wire deferral now rests on ADR-012 rather than an open question: a flap counter may not ride `Reason` (§5), so the honest wire home is a structured field participating in the report key (§1), which ADR-012 frames and defers (its Open question 1). The two ADRs partition cleanly: ADR-012 governs what is in the report and how report-equality treats it; §6a governs a signal that is deliberately not in the report at all.

## Consequences

### Positive

- **The bespoke per-node shells collapse.** The cold-start grace and its policy, and the presence debounce and its gate, become `WithGrace` + `MarkLive` and `WithDebounce` registrations; the three deadline-nudge mechanisms can become one pump keyed on `NextTemporalDeadline`.
- **Flap becomes observable at all**, with a defined read surface (`Observe()`, `FlapWindow.Count`). Today nothing anywhere counts health transitions.
- **Torn evaluation/history pairs are unrepresentable** — one reference, one swap.
- **ADR-008's transience contract is mechanical** for policy-emitted `Unknown`s.
- **Zero cost to non-users.** No policies registered = identity; no wire, behaviour, or perf change. **Scoped by §10:** true for a process that configures no `TemporalDefaults` at all. Inside a defaulted process it is false by design and in three ways — an in-scope node is `IsTemporal`, flips its graph's `HasTemporalNodes`, and emits a non-null ADR-013 `Temporal` on every snapshot — including, per §10e, for a node whose *own* graph supplied no defaults.
- **The debounce hold becomes actionable, not silent (§6a).** `TemporalDeadlineChanged` closes the one hole that would have let a debounce window elapse unwatched — the flagship feature's load-bearing case — without putting a non-health signal into the report stream.
- **The lease+grace composition has a sanctioned, exported path (§9).** The hardest consumer cohort migrates without a private copy of grace and without losing its pinned inside-the-sample / outside-the-sample behaviour.
- **Policy scales to the size of a real graph (§10).** One defaults bag replaces N registrations, the forgotten-node failure mode goes away, and the four defects of the consumer-side loop go with it. `Lease()` and a blanket default coexist in one graph without a `catch`, **in both orderings** — lease-then-attach skips the node, attach-then-lease clears the materialized default (§10c), so ADR-010's runtime leasing survives.
- **§7's mutual exclusion becomes structural rather than checked (§10c).** Folding the leased bit into the CAS'd policy set makes "leased and policied" unrepresentable under concurrency, where today it is a guarded read that a concurrent writer could slip past. §10 tightens a guarantee §7 already claimed.
- **A node's temporal configuration is finally readable (§10b).** `IsLeased`/`DebouncePolicy`/`GracePolicy` with provenance answer "why is this node damped" — a question §1 left with no supported answer even before defaults existed, since `IsTemporal` is `internal`.

### Negative / Trade-offs

- **`HealthNode` gains genuinely new state.** The history is library-owned, derived from the node's own outputs, mutated on the evaluation path — the kind of addition ADR-006/ADR-009 alignment bullets celebrated avoiding. The mitigation is containment (one immutable pair, one CAS), not denial.
- **`NotifyChangedCore` is refactored, and the write-path carve-outs are load-bearing.** `ReportStatus` bypasses the chain and the history (documented one-shot semantics); coalesced pushes undercount transitions; `WithHealthProbe`'s no-wave direct write means a probe swapped without a subsequent `Refresh` leaves a pre-chain value visible until the next wave — same as its today-behaviour, now stated.
- **Two temporal concepts to teach.** Lease (my producer may die) versus policies (my signal is twitchy), made mutually exclusive so the choice is forced rather than compounded. The README owes the same triangle-drawing ADR-010 promised for probe modes.
- **Deadline latency is still the consumer's problem.** §6 tells the pump *when*; nothing in the library wakes up on its own. A policied node in a never-refreshed graph holds its pre-deadline answer — same coupling as ADR-010's, same documented requirement.
- **History lifecycle has sharp edges.** `ReplaceHealthProbe` (e.g. a real→mock swap) keeps the node's history — the latch and transitions describe the *node*, not the probe; a consumer swapping semantics should expect that and can be given a `ResetHistory()` if a real case demands it (open question). Graph detach/dispose leaves node state untouched, as today.
- **The `Transitions` bound (32, drop-oldest) is a judgment call.** Fixed and library-owned so replay is deterministic across implementations; 32 transitions at flap-relevant rates spans days. A node flapping faster than that saturates the window — and a saturated window is itself the signal.
- **A node's behaviour is no longer explained by its own construction site (§10).** This is the real cost of defaults and it is not fully mitigable: reading `HealthNode.Create("Peripheral").WithHealthProbe(…)` no longer tells you the node is debounced. The provenance read surface (§10b) makes it *discoverable*; it does not make it *local*. The trade is the standard one every defaults mechanism makes, taken knowingly here because the alternative failure — the leaf somebody forgot — is silent in the dangerous direction.
- **`HasTemporalNodes` becomes true for nearly every defaulted graph**, so `WarnIfTemporalWithoutWaveSource` starts firing for consumers it never fired for. That is correct — a defaulted graph genuinely does need a wave source, and a defaulted graph without one is that trap at graph scale rather than node scale — but it will read as new noise on upgrade, and `RunMonitor()` becomes effectively mandatory alongside defaults rather than merely blessed.
- **ADR-013's sparsity assumption weakens (§10).** `HealthSnapshot.Temporal` is populated when a node has a lease, a policy, or a recent flap, and is documented as sparse — "null for most nodes" is the premise behind putting it on the wire cheaply. A graph-wide default makes *every in-scope node* carry a `TemporalState` in *every* snapshot. Report-change detection is unaffected (ADR-013 keeps `Temporal` out of the comparer, so no equality churn and no report-churn regression), but heartbeat payload size and `.clef` volume grow roughly linearly in graph size where they used to grow in the number of interesting nodes. ADR-013 should be read as sizing for defaulted graphs, not sparse ones. The bound available to a consumer is the one §10 already provides — `AppliesTo` makes the growth linear in *in-scope* leaves rather than in graph size — and no payload budget, chunking, or size limit is specified here on purpose: such a limit belongs to ADR-013's field and to the transport that carries it, not to a policy-defaults section that would be guessing at both. What §10 owes is the sizing input, and that is now stated rather than left for a downstream consumer to discover.
- **Attach-time throwing is a new failure mode at `Create` (§10c/§10d).** Two graphs with disagreeing defaults over a shared subgraph now fail at construction, as does a throwing `AppliesTo` predicate. Intentional — the alternatives are silent and order-dependent — but it is a new way for a wiring change to break startup rather than degrade at runtime.
- **A shared node's policy is decided by whichever graph defaults it first, and never released (§10e).** Legal, deterministic, and documented, but it means "this graph has no defaults" does not imply "this graph's nodes have no policies." A consumer reading graph B's wiring cannot see the debounce B's shared leaf inherited from A; only the §10b provenance surface shows it. Two graphs needing genuinely different policies for one node is unsupportable at any layer (§4: one node, one `EvaluationState`) and needs two nodes.
- **`AppliesTo` is an unvalidated purity contract executed under a structural lock (§10d).** A blocking or re-entrant predicate deadlocks attach. This is the same class of unenforceable constraint ADR-010 §2 accepts for the injected clock — the precedent is real, but §10 adds a second one, and the two together mean a consumer can now hang graph construction from two different callbacks. Worse than the clock in one respect: a predicate that *throws* at the late-add site leaves a half-attached graph with no rollback (§10d), where the clock has no equivalent failure.
- **§10 forces a second CAS'd record onto `HealthNode` (§10c).** The node now carries two independently-swapped immutable records — `EvaluationState` (§4) and `TemporalPolicySet` — and the lock-free discipline that §4 needed for the evaluation path is now needed for the *configuration* path too, because the graph locks cannot serialize cross-graph attach and the node's own policy lock is on the wrong side of `Lease()`'s call into `Refresh()`. Defensible (it is the mechanism the library already uses, and it makes §7 structural) but it is a second concurrency-critical invariant on the hottest type, and the two records are not swapped together — an implementation must not assume a consistent view across both.
- **The late-add attach ordering is coupled to propagation-scope behaviour (§10a).** A node added by `DependsOn` is policied *after* the wave in the same critical section, and is safe only because `EvalInDependencyOrder`'s scope check means that wave never evaluated it. Nothing enforces that coupling; a change to when the added set is computed would silently reintroduce a window where a defaulted node is waved unpolicied.
- **§9 adds public grace surface, structurally tied to one internal core.** The grace core (`GraceCore`) is now the engine behind `WithGrace` *and* two public producer surfaces (`ApplyGrace`, `GraceMachine`); divergence is prevented mechanically (one internal implementation both callers delegate to, plus an equivalence test), not by a promise. It is still a genuine public-API commitment the in-graph-only design did not have. The residual misuse — a producer mis-threading caller-held `GraceState` — is now confined to the lower-level Layer-1 `ApplyGrace` opt-out; the recommended `GraceMachine` surface owns the state internally and makes that footgun unrepresentable, and the clock default makes the monotonic source the easy path. The remaining exposure is the deliberate, and now minimized, cost of not stranding the hardest consumer.

## Open questions

1. **Policies on composites.** Deferred; the chain runs on leaves in this ADR. A debounced parent of debounced children double-damps; whether composites ever need their own policies (rather than inheriting damped leaves) is unproven. §10d keeps the default scope at leaves so a defaults bag cannot resolve this by accident; whether the scope default should widen is part of whatever resolves this question.
2. **Consumer-extensible policies.** Only if the two library policies prove insufficient downstream; would need answers for ordering slots, purity, and deadline extraction that §1 deliberately sidesteps.
3. **Flap on the wire.** The report-equality contract is no longer the blocker — ADR-012 pins it and rules `Reason` out as flap's carrier (§5). What remains is the structured-field decision (a `HealthSnapshot` member that participates in the report key, per ADR-012) and a consumer that programmatically wants it; both are deferred with ADR-012. `Observe()` unblocks local consumption meanwhile.
4. **`ResetHistory()`.** Add only when a probe-semantics swap demonstrates a real need; a speculative reset API invites misuse (clearing a latch to re-enter grace).
5. **Monitor-assisted deadlines. — RESOLVED.** `HealthMonitor` now consumes
   `NextTemporalDeadline` and its `TemporalDeadlineChanged` re-arm signal, waking on
   `min(cadence, next-deadline)` and driving a `RefreshAll` wave — so the blessed shell loop
   this ADR anticipated exists, and consumers no longer hand-roll a deadline pump (`RunMonitor()`
   is the one-liner). One mechanism serves both features, as the question required: the graph's
   single `NextTemporalDeadline` is a min over BOTH policy pending-deadlines AND leased nodes'
   next-decay instants (ADR-010 §3), reconciled from the lease's `Stopwatch`-tick timebase into
   the wave `TimeSpan` timebase at the graph boundary (§5: the wave `now` is canonical). Cadence
   became optional: a purely deadline-driven graph needs none, while a drifting pull-probe (a
   change with no computable deadline) still needs the preserved poll path. This jointly resolves
   ADR-010 OQ3. The no-timers-in-the-core doctrine holds — the timer lives in the
   consumer-started monitor shell, where ADR-010 §6 always placed the wave source.
6. **DI surface.** `NodeConfigurator.WithDebounce/WithGrace` pass-throughs, plus a `PrognosisBuilder.WithTemporalDefaults(TemporalDefaults)` for §10 — the DI path is where a graph-wide default is most natural, since the builder already owns graph construction and the consumer never calls `HealthGraph.Create` itself. It also carries §10f's wave-source enforcement: a grace default configured through the builder runs `WarnIfTemporalWithoutWaveSource` automatically at startup and fails rather than warns when no wave source was ever attached, which is the only layer that can tell "no monitor yet" from "no monitor ever." Additive; follows once the core shape settles.
7. **Whether a default should be revocable (§10).** There is no `ClearDebounce`/`ClearGrace`, so a node in scope of a default cannot opt *out* except by explicitly setting a policy it does not want. Still deferred, but **retained sources (§10c) make it tractable in a way the original design did not**: because the graph-default contribution survives underneath an explicit one, "revoke the explicit value" now has a defined answer — fall back to the default — rather than being an unanswerable question about a slot with no history. What remains before this can ship is the lifecycle half: what a revocation does to an in-flight `GraceState` latch and a pending deadline, which is the same question OQ4 defers for `ResetHistory()`. Deferred with it. §10e's "a node that has ever passed through a defaulted graph carries that default for the rest of the process" is still the strongest argument that one of these two eventually needs a real answer; retention is the enabling step, not the answer.
