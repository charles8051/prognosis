---
id: ADR-008
status: accepted
governs:
  - Importance.cs
  - HealthNode.cs
  - HealthEvaluation.cs
  - HealthStatusExtensions.cs
  - Prognosis.Generators/ServiceNodeDiscoveryGenerator.cs
  - Prognosis.Tests/HealthAggregatorTests.cs
relates:
  - ADR-006
  - ADR-007
  - ADR-010
  - ADR-013
---

# ADR-008: `Unknown` Is Transient by Contract; `Importance.Advisory` for Signals That May Not Exist

**Status:** Accepted
**Date:** 2026-07-23
**Drivers:** A single advisory leaf sitting at `Unknown` in steady state turned an entire deployment's reported health `Unknown` and — more seriously — silently disabled the consumer's "a lingering `Unknown` gates service" safety property for every other node in the graph. ADR-006's guarantee was correct and correctly implemented; the *unstated* half of the contract (that `Unknown` is transient) is what broke.

> **Acceptance note (promoted from `proposed`, 2026-07-31).** The library-side half of this ADR
> shipped in `v8.0.0-alpha` via
> the `Importance.Advisory` change (`feat: Importance.Advisory +
> Unknown(reason) + fail-loud totality (ADR-008)`): the normative transience contract,
> `Importance.Advisory`, `HealthEvaluation.Unknown(string reason)`, and fail-loud totality at
> `HealthStatusExtensions.Rank`, `HealthNode.Aggregate`, and `ServiceNodeDiscoveryGenerator`
> (`Prognosis.Generators`, added to `governs:` in this promotion so the fail-loud generator default is
> enforceable on future review — §2 is where its silent-`Required` hazard is spelled out). The
> contract is therefore binding, not under discussion. What accepting this ADR does **not** close is
> the **consumer-side half** — see the "Not sufficient alone" trade-off below; it is tracked in the
> consuming control plane's own repository.

## Context

### The incident

A field unit running a device-attached service reported its whole rolled-up health as `Unknown`.
The graph had about 60 nodes and looked like this:

```
health   Unknown (60 node(s))
  Device                     Unknown   Subsystem: Real.Inference.Liveness is Unknown
  Subsystem                  Unknown   Real.Inference.Liveness is Unknown
  Real.Inference.Liveness    Unknown
  ...all other ~57 nodes     Healthy
```

`Real.Inference.Liveness` is an **advisory** liveness leaf, newly added, wired
`DependsOn(node, Importance.Important)` under `Subsystem`. Its prober returned `Unknown` for
"nothing to probe" and "still warming" — treating `Unknown` as a benign no-signal floor. Every other
node was `Healthy`.

**Nothing in the library misbehaved.** `HealthNode.Aggregate` (`HealthNode.cs`) did exactly what
ADR-006 documents: `Importance.Important` remaps only `Unhealthy` (to `Degraded`); every other status
— including `Unknown` — passes through unchanged. The rolled-up reason string is
`Aggregate`'s no-reason fallback, `$"{dep.Node.Name} is {depEval.Status}"`.

### `Non-gating` is not `non-propagating`

ADR-006 pins a precise guarantee: an `Unknown` dependency raises its parent **at most to `Unknown`**,
never to `Degraded`/`Unhealthy`. That is a statement about *escalation*, not about *visibility*. The
ADR's own table says `Required` / `Important` / `Resilient` all take the parent **to `Unknown`**.

The two readings diverge at the operator's screen. A status that never escalates but always propagates
still replaces the root's `Healthy` with `Unknown` and installs itself as the root's stated cause. For
anyone reading a dashboard, "the unit is `Unknown`" is not a weaker claim than "the unit is
`Degraded`" — it is a differently-shaped one, and arguably a worse operator experience, because
downstream incident policies key on `Unhealthy` and therefore fire **nothing** for it. The condition is
maximally visible and minimally actionable.

Consumers compounded this by reading ADR-006's headline as "`Unknown` is safe." It is safe *inside the
fold*. It is not safe at the edges: the consumer's root health gate maps a **warm** `Unknown` root to
"do not serve," and the control plane's fleet rollup folds `Unknown` into the unit's status. Both are
legitimate consumer choices; neither is contradicted by ADR-006. But the library
never says "`Unknown` will be visible in your rollup and your consumers will act on it," so consumers
inferred the opposite.

### The unstated invariant: every `Unknown` in the field was transient

The other ~57 nodes avoid this **by construction, not by mechanism**. `HealthNode.Create` defaults a
node's intrinsic evaluation to `Healthy`; a node reaches `Unknown` only when a driver *explicitly* maps
it there. In the motivating consumer there were exactly two such mappers before the new leaf, and both
are transient by construction:

- device-connection probes mapping the pre-enumeration window — resolves when the device enumerates;
- a cold-start grace policy — resolves when the node goes live, or when its grace deadline passes.

Both keep `Unknown` **correlated with "has never yet produced a determined verdict."** That correlation
is precisely what the consumer's warmup predicate keys on:

```csharp
// IsWarm
!report.Nodes.Any(n => n.Status == HealthStatus.Unknown && !everDetermined.Contains(n.Name));
```

`Real.Inference.Liveness` was the first node whose `Unknown` was a **steady state**. It broke the
correlation, and the failure mode that followed is worse than the cosmetic one:

1. The node is never determined ⇒ `IsWarm` is **permanently false**.
2. The root health gate maps `Unknown ⇒ !isWarm ⇒ true`. The service keeps serving — but the rule
   *"once warm, a lingering `Unknown` gates"* is now **permanently disabled for the entire graph**. A
   device that genuinely never enumerates, which should have taken the unit out of service, silently
   cannot.
3. Startup verbose-capture never observes warmth and runs to its backstop on every boot.
4. The control plane's incident policy fires only on `Unhealthy`, so none of this paged anyone.

A permanently-`Unknown` node does not merely add noise. **It disarms the consumer's `Unknown` safety
net.** That is the finding that motivates this ADR.

### `Importance` cannot express "this signal may not exist"

The vocabulary conflates two independent questions:

| | *How bad is a **failure**?* | *Do I care about **indeterminacy**?* |
|---|---|---|
| `Required` | fatal | yes |
| `Important` | capped at `Degraded` | yes |
| `Optional` | ignored entirely | no |
| `Resilient` | fatal unless a healthy sibling | yes |

There is no level meaning **"a real failure should degrade me, but your *absence of signal* tells me
nothing about my own health."** That is exactly what an advisory liveness probe is, and it is the level
the new leaf needed. `Optional` gets the `Unknown` behavior right but throws away the failure signal too —
an `Unhealthy` `Optional` child is invisible, which is not what an advisory probe wants either.

Faced with those four, wiring it `Important` was the reasonable-looking choice. The gap is in the
vocabulary, not in the judgement of whoever wired it.

### Why "wire it `Optional`" would not have fixed the reported symptom

Worth stating because it is counterintuitive and it constrains the recommendation. The control plane's
health source folds the unit's **flat** `HealthReport.Nodes` list:

```csharp
var rollup = nodes.Aggregate(HealthStatus.Healthy,
    (worst, node) => HealthStatusExtensions.Worst(worst, node.Status));
```

Topology and `Importance` are both absent — at the time, the control plane referenced neither
`Importance` nor `HealthTreeSnapshot` anywhere. So it re-folds every node flat with worst-wins.
Re-wiring the leaf `Optional` would have kept the *unit's root* `Healthy` while the fleet view still
went `Unknown`, because the leaf is still in the flat list. This is the same discarded-`Importance` problem
ADR-007 identified for contributor analysis, now biting the rollup itself.

Consequence for this ADR: **a fix expressed purely as an `Importance` level cannot fully solve the
reported symptom until consumers fold with topology.** The library change is still right; it is just
not sufficient on its own.

## Decision

Four changes. The first is the contract; the rest close the gaps that let it be violated silently.

### 1. State the transience contract (normative)

> **`Unknown` is a transient state. A node MUST NOT rest at `Unknown` in steady state.**
>
> `Unknown` means *not yet determined* — a node that has not completed its first evaluation, or whose
> driver has not yet reported. It does **not** mean "not applicable," "nothing to measure," "disabled,"
> or "no signal available." A node whose signal is structurally absent MUST resolve to a determined
> status — normally `Healthy` — not to `Unknown`.
>
> Every `Unknown` MUST have a resolution path: a first sample, an enumeration, a grace deadline, or a
> probe timeout. A node whose `Unknown` has no resolution path is a modelling defect.

This is what ADR-006 assumed and never wrote down. It is the half of the contract consumers needed.

### 2. Add `Importance.Advisory`

A fifth level, purely additive:

| Child status | `Advisory` contribution |
|---|---|
| `Healthy` | `Healthy` |
| `Unknown` | **`Healthy`** (absorbed — an advisory signal's indeterminacy is not the parent's) |
| `Degraded` | `Degraded` |
| `Unhealthy` | `Degraded` (capped, as `Important`) |

`Advisory` is `Important` that absorbs `Unknown`. It is the correct level for a probe that *observes*
something for operators without the parent's own health depending on the observation being available.

Additive **for existing edges**: no current edge changes behavior, no existing test changes, and every
current call site keeps its meaning. But "append an enum member" is not free, and an earlier draft of
this ADR claimed zero migration risk. That was wrong. Two `switch` sites over `Importance` exist in the
library and **both** must be updated in the same change:

| Site | Today | If `Advisory` is appended without updating it |
|---|---|---|
| `HealthNode.Aggregate` (`HealthNode.cs`) | `_ => HealthStatus.Healthy` | An `Advisory` edge contributes `Healthy` unconditionally — its `Degraded`/`Unhealthy` would be **silently swallowed** |
| `ServiceNodeDiscoveryGenerator` (`Prognosis.Generators`) | `_ => "Importance.Required"` | `[DependsOn("X", Importance.Advisory)]` **silently generates a `Required` edge** — the strictest level, the exact inverse of intent |

The generator case is the serious one and it is the same defect class this ADR flags in `Rank` (§4):
a positional `impVal switch` over `0..3` with a silent default, so a new member does not fail — it
quietly becomes something else, and the something else is maximally gating. Any consumer using the
attribute + generator discovery path (ADR-003's replacement for `IHealthAware`) would get a health
graph that does not match its own source. The motivating consumer is not exposed today because it
wires edges through the fluent `DependsOn(node, Importance)` API rather than the attribute — but that
is luck, not design.

So the implementation must, in one change: add the `Aggregate` arm, add `4 => "Importance.Advisory"`
to the generator, and harden **both** defaults to fail loudly rather than substitute a value. Adding the
member without the generator arm is worse than not adding it at all.

Consumers switching over `Importance` will also see a new unhandled case, and the cost depends on
*which* `switch` they wrote:

- A **`switch` expression** missing an arm is warning **CS8509**, not a compile error, and this repo
  sets no `TreatWarningsAsErrors` — so the build still succeeds. If an `Advisory` edge actually reaches
  the unhandled arm at runtime, it throws
  `System.Runtime.CompilerServices.SwitchExpressionException`.
- A **`switch` statement** missing a `case` produces **no diagnostic at all** — it falls through to
  `default`, or does nothing. No warning, no exception, no signal.

The statement form is the dangerous one, and it is worth stating plainly because it inverts the
reassurance: a consumer gets *less* warning the more permissively they wrote their switch. That is the
same hazard as the two library defaults in the table above, just relocated into consumer code — which
is why the guidance is to fail loudly at the defaults rather than to rely on the compiler noticing.

### 3. Add `HealthEvaluation.Unknown(string reason)`

`HealthEvaluation` offers `Healthy`, `Unhealthy(reason)`, `Degraded(reason)` — and no `Unknown` factory.
An `Unknown` node therefore structurally cannot carry a reason, which is why the incident's rollup read
`"Real.Inference.Liveness is Unknown"` with no explanation. `Unknown` is second-class in the
construction API while being first-class in the fold. Adding the factory costs three lines and makes
every future `Unknown` self-describing.

### 4. Make `HealthStatusExtensions.Rank` total-by-failure, not total-by-accident

```csharp
_ => int.MaxValue,   // current fallback
```

An enum member added without updating `Rank` silently ranks **worse than `Unhealthy`** and would
propagate as the worst status everywhere — the exact silent-breakage class ADR-006 exists to prevent.

Replace it with an explicit `throw`. An earlier draft offered "or map to `Unknown`" as an alternative;
that is struck, because it fails the very goal of this item — mapping an unrecognized status to a
*plausible* value is silent substitution, the same defect in a friendlier disguise. The whole point is
that the next status addition must be impossible to miss. Fail loudly, in one way, with no option.

### Explicitly rejected: a distinct `NotApplicable` / `NoSignal` status

Considered and rejected. It models the domain more honestly than `Advisory` does, but the migration
cost is severe and lands mostly on consumers who did nothing wrong:

- `HealthStatus` is serialized with `JsonStringEnumConverter` and crosses a device → host-agent →
  control-plane wire **on every heartbeat**. A new member is a **breaking wire change**: older readers
  fail to deserialize. The rollout would have to be ordered across three repositories and two
  over-the-air update trains.
- Every consumer `switch` over `HealthStatus` — the root health gate, the warmup predicate, the
  control plane's health source and its incident policy — silently gets a new unhandled case. Most
  have a `_ => false` or worst-wins default that would quietly do the wrong thing.
- The `Rank` fallback above makes the failure mode *maximally* bad until every site is updated.

**Is that argument fair, given this ADR proposes a new `Importance` member?** The objection is
reasonable and deserves an explicit answer, because `Importance` carries the *same*
`[JsonConverter(typeof(JsonStringEnumConverter))]` attribute as `HealthStatus`, and
`HealthTreeDependency` (`HealthTreeSnapshot.cs`) has an `Importance Importance` member — so `Importance`
is unambiguously *serializable*.

The asymmetry that justifies the different verdict is **what actually crosses the wire today**:

| | On the wire today? |
|---|---|
| `HealthStatus` | **Yes** — every `HealthSnapshot` in every heartbeat carries one. |
| `Importance` | **No.** `HealthReport`/`HealthSnapshot` carry no importance at all; only `HealthTreeSnapshot` does, and at the time nothing shipped it — no consumer referenced `Importance` or `HealthTreeSnapshot`. |

So a new `HealthStatus` member breaks live deserializers immediately; a new `Importance` member reaches
no deserializer at all. The rejection stands, but on this narrower ground rather than "enums are
wire-serialized" in general.

> **Correction (2026-08-13). The right-hand column above is now false, and the window
> this section describes as open has closed.** `HealthTreeSnapshot` — and therefore `Importance` —
> ships north today and is parsed by a real consumer: the control plane's tree view folds an
> importance-annotated tree (reading `HealthTreeDependency.Importance` directly), and a sibling
> formatter renders ADR-013's `TemporalState`. The "no consumer references `Importance` or
> `HealthTreeSnapshot`" claim, made twice in this ADR, was accurate on 2026-07-23 and is stale. Two consequences, and neither invalidates any decision here — both are
> about what a *future* change costs:
>
> - **`Advisory` landed inside the window; a sixth `Importance` member would not.** Adding one now is
>   the staged multi-repo rollout this section rejects for `HealthStatus`, not the free append that
>   §2 costed. The sequencing argument was right and the deadline it named was real — it simply
>   already elapsed.
> - **The asymmetry that justified rejecting a `NotApplicable` *status* now applies to `Importance`
>   too.** The rejection is therefore stronger than when written, not weaker.
>
> The consumer-side half also moved: the control plane now folds with topology via
> `HealthGraphAnalysis` rather than the flat worst-wins health source this ADR analyses, so the
> "Not sufficient alone" trade-off below is substantially addressed. `HealthTreeSnapshot` still drops
> `TemporalState`, which is a separate ADR-013 gap.

**This created a sequencing constraint, and it was the reason to land `Advisory` sooner rather than
later.** *(Historical as of 2026-08-13 — the deadline described here has passed; see the Correction
above. `Advisory` landed inside the window. Do not plan a further `Importance` addition from this
paragraph: the current rollout consequence is that a sixth member is a staged multi-repo rollout,
not a free append.)* This ADR also argues that consumers *should* start folding with topology, which
means shipping `HealthTreeSnapshot` — and its `Importance` — north. Once that
happens, `Importance` becomes a wire type and gains exactly the compatibility burden `HealthStatus`
has now. Adding `Advisory` **before** the tree ships north is free; adding it after is the same
staged, multi-repo rollout this section rejects. The window was open then.

If a genuine `NotApplicable` is wanted later, it should be a separate ADR with a staged
wire-compatibility plan, and `Advisory` does not block it.

### Explicitly rejected: changing `Important` to absorb `Unknown`

This was the incident's most tempting fix and it is wrong. `Important` is the most-used level in both
consumers; making it swallow `Unknown` would convert every genuine "this dependency stopped reporting"
into silence, across the whole graph, to fix one mis-modelled node. It also directly contradicts
ADR-006's pinned table. ADR-006 stands unamended.

## Consequences

### Positive

- **The contract is complete.** ADR-006 said what `Unknown` will not do; ADR-008 says what a node
  owner must not do. Together they are actionable without reading `Aggregate`.
- **The right level exists.** Advisory probes stop having to choose between "poisons the rollup"
  (`Important`) and "invisible even when broken" (`Optional`).
- **No existing behavior moves.** Every current edge, call site, and test keeps its meaning; the
  factory and the doc are purely additive. This is *not* the same as "zero migration risk" — see the
  trade-off below.
- **The next status addition fails loudly** instead of ranking worse than `Unhealthy`.

### Negative / Trade-offs

- **A fifth `Importance` level to teach.** `Important` vs `Advisory` is a real but subtle distinction.
  Mitigated by the README table and by naming that matches how people already describe these nodes.
- **`Advisory` absorbs a real blind spot.** A genuinely-wedged advisory probe now reports `Healthy` to
  its parent. That is the deliberate trade: the node stays individually visible in the flat report and
  in `Prognosis.Diagnostics`, it just stops speaking for its parent. Node owners who *want* the
  indeterminacy to surface should keep `Important`.
- **Not sufficient alone.** *(Historical as of 2026-08-13 — largely resolved; see the Correction in
  "Explicitly rejected: a distinct `NotApplicable` / `NoSignal` status". The control plane now ships
  and folds the tree via `HealthGraphAnalysis` in its tree view, so the flat worst-wins re-fold
  described below is no longer how the fleet rollup reaches an operator, and the consumer-side
  tracking issue should be read for its current state rather than as an open blocker. Retained because it records why the library-side
  fix was insufficient on its own at the time.)* Until consumers fold with topology + `Importance` (ADR-007's tree, shipped
  north) rather than flat worst-of, an `Advisory` edge does not stop a leaf from moving a fleet
  rollup. This is the **consumer-side half of the contract, tracked separately**: the concrete blocker
  is the control plane's health source re-folding the flat `HealthReport.Nodes` list worst-wins
  (topology and `Importance` both discarded — see "Why 'wire it `Optional`' would not have fixed the
  reported symptom" above), tracked in that consumer's own repository. Until it lands, an
  `Importance`-level fix in this library cannot fully resolve the reported symptom no matter how a leaf
  is wired. This ADR does not claim to close it.
- **The enum addition must be landed atomically with both `switch` sites.** Not "zero risk": a partial
  implementation that adds the member without the `ServiceNodeDiscoveryGenerator` arm silently emits
  `Required` edges for `Advisory` attributes — strictly worse than the status quo. The migration cost is
  small but it is not nil, and it is concentrated in exactly one place that is easy to miss (§2).
- **A deadline, in effect.** `Advisory` is cheap only while `Importance` stays off the wire. Shipping
  the tree north first would convert this from an additive change into a staged multi-repo rollout.

## Amendment (2026-08-13): the scope of `Advisory`, and applicability is not the library's

Two questions were put to this ADR a fortnight after acceptance: whether §1 left `Importance.Advisory`
with anything to do, and whether §1 moved the ambiguity it claimed to remove — a node now reports
`Healthy` both when it measured something good and when it never looked. Both are answered here so
neither is re-litigated from the summary.

### `Advisory` is narrower than §2 implied, but ADR-010 widened it back

The observation is correct: §1 forbids resting at `Unknown`, and `Advisory` differs from `Important`
in exactly one cell — the `Unknown` row. So a node that honours §1 sees the two levels behave
identically **except while an `Unknown` is in flight**, and `Advisory` is worth choosing only for a
window the contract guarantees is temporary.

That is a real narrowing and it explains the zero adoption in the motivating consumer (on
`8.0.0-beta.5`, 233 `Importance` edges, none `Advisory`). Its one candidate probe says so in its own
words: a role-binding evaluator that is contract-bound never to report `Unknown`, so *"the levels are
behaviourally identical here."* **Non-adoption is a correct reading of the contract, not an
oversight** — which is the point of recording it.

But the inference "therefore `Advisory` is near-vestigial" does **not** follow, because ADR-010
enlarged the most important transient window after this ADR was written. A leased node whose producer
stops affirming decays to `Unknown` for the whole stage-one window between `Ttl` and
`Ttl + EscalateAfter` (`HealthLease.Decay`), and a never-affirmed lease sits at the
`PendingReasonPrefix` seed until its first affirmation. Both satisfy §1 — they have a resolution path,
the escalation deadline — and both can last minutes. Over that window the one differing cell is live
and load-bearing: an `Important` edge to a decaying lease takes its parent to `Unknown`; an `Advisory`
edge does not.

**Guidance.** Prefer `Advisory` for an edge to a *leased* observational node, where stage-one decay is
an expected steady occurrence rather than a startup artefact. For an unleased probe that honours §1,
`Important` and `Advisory` are indistinguishable and `Important` is the conventional choice.

### Rejected: applicability as non-folding evaluation metadata

The proposal was a `HealthEvaluation.NotApplicable(reason)` that contributes `Healthy` to the fold
(leaving ADR-006/008 and every existing edge untouched) but stays distinguishable in the snapshot, so
"measured, good" and "never looked" stop sharing one value. It is rejected. The overloading is real,
but it is **not the library's to disambiguate**, and the evidence is that the disambiguation already
exists a layer up, in better shape than this library could produce.

**Two categories, two different answers.** The proposal treats "structurally absent signal" as one
thing. It is two, and conflating them is what makes a single library field look necessary:

- **(A) Mocked or fallen-back** — the service runs a mock backend, by configuration or by the
  recovery arm. Static or shell-observed, and the category the motivating role-binding mock path
  falls in.
- **(B) Real-but-absent** — the service is configured real and the hardware simply is not there.
  Dynamic, not knowable from configuration.
- **(C) Configured-off outside the switchable catalog** — a node that exists and reports `Healthy`
  for a feature disabled by some flag that is not a `UseMockX` catalog entry. Neither of the two
  channels below covers this; see the concession at the end of this section.

Category (A) is answered by the simulation channel below. Category (B) is answered by the graph
itself, and this is the more important half: **a real service whose hardware is absent is
eventually another node's fault, not an unrepresented one.** In the motivating consumer a camera's
absence lands on `Real.Camera` (the device-connection probe) — which is exactly why the role-binding
evaluator stays silent on that path, in its own words because *"a role with no device is already the
connection probe's business, and saying anything here would double-report its fault under a second
name."* Category (B) is therefore not invisible; it is visible **as a fault on
the node that owns it**, which is the correct place.

Precisely, though: §1 gives that node a *resolution path*, not the immediate absence of an
`Unknown`. A leased connection probe genuinely passes through a bounded `Unknown` interval — the
never-affirmed seed, or stage-one `HealthLease.Decay` between `Ttl` and `Ttl + EscalateAfter` — before
it reaches its verdict, and on an `Advisory` edge that interval is absorbed rather than propagated,
so an absent device can be briefly hidden **from the parent rollup**. Two bounds keep that from
undermining the argument, and both are pre-existing ADR-008 properties rather than new claims: the
interval is bounded by the escalation deadline (after which the lease escalates to a *gating* status,
which `Advisory` does not absorb), and the leaf stays individually visible in the flat report
throughout — `Advisory` stops a node speaking for its parent, it does not hide the node. A consumer
that needs the indeterminacy to reach the parent immediately keeps `Important`, per §2's trade-off.

**Applicability is also encoded structurally, in node identity.** The motivating consumer's camera
composites carry `Mock.Camera` / `Mock.SecondaryCamera` or `Real.Camera` / `Real.Camera.Stream` as
*distinct nodes*, swapped as a dependency profile at runtime. Which names appear under a composite
**is** the applicability marker, it needs no new field, and it reaches every consumer already — in
the flat report as node names and in `HealthTreeSnapshot` as topology (ADR-009), which the control
plane folds. A consumer reading `Mock.Camera` in a report knows the role is simulated without
consulting anything else.

**And for category (A), it is a first-class, wire-borne, cross-repository channel — deliberately outside health.**
The consumer platform carries a per-service *simulation report* for any workload: pushed from the
workload to the host agent over IPC, folded into the heartbeat as a simulated-services field, landed
as a simulation facet, and rendered in the operator UI's own section. Its doc comment draws the
boundary this proposal would cross: *a deliberately mocked bench box is neither unready nor
unhealthy, just not real, so this must not feed either.* That is a decision already made, shipped,
and covered by its own tests. All three surfaces said to be blind carry **category (A)** through it:

| Surface | Already carries category-(A) applicability via |
|---|---|
| in-process | the resolved simulation posture — the configured mock-flag set, unioned with a runtime fallback registry |
| northbound heartbeat | the heartbeat's simulated-services field, per service, on every beat |
| structured log | a startup audit row per mocked service, carrying provenance and sanction |

For the motivating instance this is exact, not approximate: the flags the role-binding evaluator reads
are catalog flags, so the mapper ships those very roles north on the same heartbeat as the health
report, keyed by a name the operator UI already maps. The join is available at **both** ends — the
device's own knowledge is not privileged over the control plane's.

The consumer's channel is also **strictly richer** than a library flag could be. It distinguishes
configured-mock from fell-back-to-mock-against-config, carries provenance and sanction, and treats
`null` (never reported) as distinct from empty (affirmatively all-real). A boolean-ish
`NotApplicable(reason)` on a health node collapses all of that into one bit plus a free-text string.

Three further reasons the library is the wrong home:

1. **A parallel channel could disagree with the graph.** Since category (B) is already another node's
   verdict and category (A) is already the composite's topology, an applicability field on a *third*
   surface introduces two representations of one fact that can drift — a node marked applicable while
   its connection probe says the device is gone, or the reverse. The existing encodings cannot
   disagree with the rollup, because they *are* the rollup.
2. **It cannot ride `TemporalState`.** ADR-013's field is temporal by design — every member derives
   from a clock, and §3 excludes it from `HealthReportComparer` *because* it varies. Applicability is
   static; putting it there would mean a node flipping applicable→not-applicable emits **no report
   change**, which is precisely the transition an operator must see (a camera that stopped
   enumerating). It would also be invisible on the tree path the control plane actually renders,
   which drops `TemporalState` entirely. Any future adoption is a sibling field, never this one.
3. **`WithTags` (ADR-005) is the zero-cost approximation and is unused.** Tags are static, reach both
   `HealthSnapshot` and `HealthTreeSnapshot`, are already excluded from equality, and cost one fluent
   call and no library change. The motivating consumer calls `WithTags` **zero** times. A need that has not
   motivated the free mechanism does not justify a wire-schema addition.

**Concession: category (C) is genuinely uncovered.** The simulation report is keyed to the switchable
catalog (the `UseMock*` flags) plus shell-observed fallbacks. A node that exists, reports `Healthy`,
and is inert because of a flag *outside* that catalog has no wire-borne applicability record — not in
the heartbeat, not in the simulation facet, and not in the structured startup audit. The claim that all structural non-applicability is already available is therefore true of (A)
and (B) and **false in general**, and this rejection should not be read as asserting otherwise.

It does not change the verdict, for three reasons. No such node was found in either consumer today,
so the category is presently hypothetical rather than observed — unlike (A) and (B), which are both
live. A node omitted from the graph entirely raises no ambiguity at all, since nothing reports
`Healthy` on its behalf. And where it does arise, the proportionate fix is the one already available
and unused: a `WithTags` entry naming the disabling flag, which reaches both snapshot types with no
schema change. If (C) becomes common enough that per-node tagging is unmanageable, that is a reason
to revisit — and it is written into the reconsider-if trigger below.

**Migration hazards, costed honestly** (§2's discipline: enumerate before, not after):

| Site | Hazard if applicability is added to `HealthEvaluation` / the snapshots |
|---|---|
| `HealthNode.Unchanged` (`HealthNode.cs`) | `HealthEvaluation` record equality **is** the CAS loop's meaningful-change test. A new member enters the hottest method's change detection; a mis-specified parent fold swaps state every wave — the ADR-012 churn class |
| `HealthNode.Aggregate` | Must decide a *parent's* applicability (all children N/A ⇒ parent N/A?). "Non-folding" does not answer this; leaving it implicit is exactly §2's silent-default hazard |
| `HealthReportComparer` | In the equality key ⇒ touches the explicit triple ADR-012 §2/§3 and ADR-013 §3 pinned. Out of it ⇒ an applicability flip is silent. Neither is free |
| `HealthTreeSnapshot` | Now genuinely on the wire (see the Correction above), so this is a staged rollout across the library → the device service → the platform SDK → the control plane, over two over-the-air update trains |
| `Prognosis.Diagnostics` (ADR-007) | `MinimalHealingSet`/`Contributors` would name an N/A node as a repairable culprit unless taught otherwise — and a consumer-side culprit-parity test pins server culprits against the UI fold, so a divergence breaks a cross-repository test |

**§1 stands unamended.** Resolving a structurally-absent signal to `Healthy` is still correct: it
keeps `Unknown` correlated with "not yet determined", which is what makes consumers' `Unknown`-gating
safety properties armable at all. The residual observability cost is accepted, because it is paid
elsewhere and paid better.

**Reconsider if** a consumer needs applicability *joined to graph topology* in a way name-keyed
correlation cannot serve — concretely, if `HealthGraphAnalysis` must exclude N/A nodes from a healing
set to stop naming un-actionable culprits, or if a workload's applicability is per-node rather than
per-switchable-service so the simulation report's vocabulary cannot express it — which is category (C)
arriving in volume, past the point where per-node `WithTags` is manageable. None holds today.

### Consumer guidance (non-normative)

- Audit every node that can report `Unknown` for a resolution path. Seed a driver-backed node's cached
  verdict to a **determined** value so it is fail-safe if the driver never starts or stops ticking.
- On a probe timeout, **hold the previous verdict** rather than publishing `Unknown`.
- Consider a graph-level regression test asserting no node rests at `Unknown` once the graph has settled.
- Treat "a node has been `Unknown` for longer than its expected resolution window" as its own alertable
  condition. Today `Unknown` is the one rollup status no incident policy fires on, which is how this
  incident stayed un-paged for as long as it did.
