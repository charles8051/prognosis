---
id: ADR-012
status: proposed
governs:
  - HealthReportComparer.cs
  - HealthReport.cs
  - HealthNode.cs
  - Prognosis.Tests/Fuzzing/TopologyFuzzTests.cs
relates:
  - ADR-005
  - ADR-009
  - ADR-010
  - ADR-011
  - ADR-013
---

# ADR-012: The Report-Equality Contract — What Participates in Health-Report Change Detection

**Status:** Proposed
**Date:** 2026-07-31
**Drivers:** The library has two change-detection surfaces over `HealthReport` — a comparer that gates the report stream and a diff that feeds the transition stream — and they disagree about what a "change" is. That disagreement is unpinned, so it has been rediscovered and worked around **four** times in recent review, each time inside an ADR about something else. This ADR settles it once: what participates in report-change detection, why the two surfaces legitimately differ, and what an emitted `Reason` is allowed to contain.

## Context

### Two surfaces, two contracts, never written down

The core exposes change detection over `HealthReport` through two independent mechanisms that key on different fields:

| Surface | Consumers | Compares | Fires on a reason-only change? |
|---|---|---|---|
| `HealthReportComparer` (`Equals`/`GetHashCode`) | `HealthGraph.StatusChanged` gate; `Prognosis.Reactive`'s `ObserveHealthReport` / `PollHealthReport` via `DistinctUntilChanged` | per-node **`HealthSnapshot` records structurally** — so `Name`, `Status`, `Reason` (and `Tags`) | **Yes** |
| `HealthReport.DiffTo` | `Prognosis.Reactive`'s `SelectHealthChanges` (built directly on `DiffTo`) | per-node **`HealthStatus` only** | **No** |

Both are correct in isolation. Neither is wrong code. But together they mean a **reason-only change** — a node whose `Status` holds while its `Reason` moves — takes divergent paths:

- It **survives** `DistinctUntilChanged(HealthReportComparer)` and **fires `StatusChanged`** (`HealthGraph.cs`: emit when `previous is null || !HealthReportComparer.Instance.Equals(previous, report)`).
- It **emits nothing** from `DiffTo` / `SelectHealthChanges` (`DiffTo` records a `StatusChange` only when `prev.Status != curr.Status`).

The driving issue states the tension in one line: **churn to the subscribers that do not want it, silence to the one that would.** A report-stream subscriber gets an emission it may not have asked for; a transition-stream subscriber gets nothing, even though the explanation changed.

### The comparer's own two halves already disagree

Worse, `HealthReportComparer` is not internally coherent. Its `Equals` compares snapshots structurally (`other != svc`, the record `==`, over all members including `Reason`), but its `GetHashCode` **XORs `Name` and `Status` only** and deliberately drops `Reason`:

```csharp
nodeHash ^= svc.Name.GetHashCode() * 397 ^ svc.Status.GetHashCode();   // no Reason
```

This is a **valid** `IEqualityComparer<T>`: the hash key is a strict coarsening of the equality key, so `Equals(x, y)` still implies `GetHashCode(x) == GetHashCode(y)` (equal snapshots agree on `Name`+`Status`, hence hash equally). The contract holds. But it **misleads every reader who reasons from the hash.** Three separate review threads inferred, from "the hash excludes `Reason`," that "`Reason` does not participate in equality" — the exact opposite of what `Equals` does. A comparer whose two halves key on different fields is a correctness trap even when it is technically correct, and ADR-009 **copied this exact pattern** into `HealthTopologyComparer` "following the `HealthReportComparer` precedent." The precedent should be worth following.

### Four designs shaped by this one undecided contract

This ADR exists because the unpinned contract keeps distorting unrelated designs:

1. **The `Reason`-carrier for flap counts** (rejected). Carry a live flap count in `Reason`; because `Reason` participates in `Equals`, a changing count churns the report stream on every wave while `DiffTo` stays silent — signal to nobody, noise to everybody.
2. **The synthetic flap node** (rejected, ADR-011). A node whose `Reason` carries a live count fed back through the report stream never converges — `EmitStatusChanged` fires outside `_propagationLock` and recurses until two consecutive reports compare equal, which a live counter never does.
3. **ADR-010's per-second decay text.** `Decay` embeds `(int)age.TotalSeconds` in the reason and runs every evaluation, so an expired lease makes every report unequal to its predecessor forever: `StatusChanged` fires every wave, `SelectHealthChanges` emits nothing — precisely case 1, arrived at accidentally.
4. **ADR-011's flap-on-the-wire deferral** (ADR-011 OQ3). Whether flap state reaches `HealthSnapshot` is blocked *on this contract*, because a flap field would enter report equality.

Each was reasoned out locally and correctly. But four local answers to "what participates in report equality?" is three too many. The decision belongs here.

### The mechanics, verified against source

- **`Reason` participates in `Equals`** via record structural equality. Confirmed: `HealthReportComparer.Equals` uses `other != svc` on `HealthSnapshot`, a `record`.
- **`Reason` is excluded from `GetHashCode`.** Confirmed above.
- **`DiffTo` compares `Status` only.** Confirmed: the `StatusChange` it records still *carries* `curr.Reason`, but it is emitted only on a `Status` edge.
- **`Tags` participate in `Equals` — by reference identity, and reference-stable.** `HealthSnapshot.Tags` is `IReadOnlyDictionary<string, string>?`; record equality uses the default comparer for that type, which is **reference** equality (dictionaries do not override `Equals`). It does not churn, because `HealthGraph.RebuildReport` passes `node.Tags` **by reference** (`var tags = node.Tags.Count > 0 ? node.Tags : null;`) and `node.Tags` is immutable after `WithTags` (ADR-005) — the same instance every wave. So `Tags` currently contributes to equality but carries **zero change-signal** and is safe only by an accident of reference plumbing (§Decision 3 addresses this).

## Decision

Pin the report-equality key, make the comparer internally coherent, keep the transition stream deliberately distinct, and constrain what an emitted `Reason` may contain. Five sub-decisions (§3 narrows the key §1 pins, so it is a decision in its own right).

### 1. The report-equality key is `(Name, Status, Reason)` per node, plus the root — `Reason` stays significant

`HealthReportComparer` continues to treat a **reason-only change as a report change.** This is the deliberate call, and it is the load-bearing one, so the rationale is explicit.

The report stream (`StatusChanged`, `ObserveHealthReport`, `PollHealthReport`) answers **"has the operator-visible health picture changed?"**, where the picture is *a status together with its stated explanation*. A node that holds `Degraded` while its reason moves from `"queue depth 100"` to `"queue depth 5000"`, or from `"warming"` to `"backend refused"`, has changed that picture. Silencing it would drop real operator signal and would nullify the reason-bearing surfaces the rest of the substrate depends on — ADR-010's `StaleReasonPrefix` / `PendingReasonPrefix` markers, ADR-011's grace/debounce explanations. `Reason` is where the library tells a level-triggered consumer *why*, and the report stream is how that consumer hears about it.

The alternative — **excluding `Reason` from equality** — was considered and rejected (see Rejected alternatives). It would also close the churn, but by amputation: it collapses the report stream's key to `Status` alone, makes it identical in field-set to `DiffTo`, and permanently silences same-status explanation changes. The two streams would answer the same question, and the library would have no low-churn path for a reason to reach a report consumer at all.

### 2. Align `GetHashCode` with `Equals` — coherence over cleverness

`HealthReportComparer.GetHashCode` MUST hash the **same fields its `Equals` compares**: `Name`, `Status`, and `Reason` per node (plus `Root` and node count, as today). The current `Name`+`Status`-only hash is retired even though it is valid, because its validity is exactly the trap — a coarser-than-equality hash reads as "these fields are what matter" and is wrong.

```csharp
// intent, not final code:
nodeHash ^= HashCode.Combine(svc.Name, svc.Status, svc.Reason);   // matches Equals's key
```

This changes hash **values**, not the equivalence relation: any two reports that were equal remain equal and any two that were unequal remain unequal; only the bucket distribution changes. The `IEqualityComparer<HealthReport>` contract is fully preserved — `Equals` is authoritative, `GetHashCode` only has to agree with it, and it now does. Every in-repo consumer uses the comparer for exactly that (`DistinctUntilChanged`, the `StatusChanged` gate), so none is affected.

The one usage this *does* perturb is a consumer that treats `GetHashCode(report)` as a **stable content digest across processes** — logging it to an aggregator, or persisting it to compare a report seen by an old process against one seen by a new process during a rolling upgrade. That is a misuse, not a supported contract: `GetHashCode` is not collision-free (distinct reports can share a hash, so it never was a sound change-detector), it is documented not to be stable across .NET runtimes or app-domain restarts, and the type offers `Equals`/`DiffTo` for the change-detection those consumers actually want. So the exposure is real but narrow — bounded to a hash-as-digest anti-pattern — rather than "nil"; the honest claim is that no *supported* use breaks, and the digest misuse should migrate to `Equals`/`DiffTo` regardless. The payoff is that the comparer stops lying to its readers, and `HealthTopologyComparer` (ADR-009) inherits a precedent worth copying.

### 3. `Tags` (and `Temporal`) do not participate in report-change detection

The equality key is `(Name, Status, Reason)` — **`Tags` and `Temporal` are excluded.** `Equals` and `GetHashCode` should compare snapshots on those three fields explicitly rather than via the record `==` that silently drags the non-participating members in.

> **Amendment (2026-07-31, ADR-013).** The exclusion list, originally `Tags` alone, now reads **`Tags` and `Temporal`**. ADR-013 adds a sparse structured `TemporalState? Temporal` field to `HealthSnapshot` carrying continuously-varying data (lease-staleness band, windowed flap count, in-hold/in-grace flags, pending-deadline-relative-to-capture). §5 of this ADR is explicit that continuously-varying data belongs in a structured field rather than `Reason` — and the same reasoning that keeps such data out of `Reason` keeps it out of the equality key: a live count or age in the key reintroduces exactly the per-wave churn this ADR fought. So `Temporal` rides the report for point-in-time readers but is **excluded from report-change detection**, structurally identical to `Tags`. The exclusion holds *provided* the comparer keys on the explicit `(Name, Status, Reason)` triple rather than the record `==` — the coherence change §2/§3 of this ADR prescribe. That change landed with the leased-verdicts implementation (its commit records "comparer coherence (ADR-012)"), so the current `HealthReportComparer.SnapshotKeyEquals`/`SnapshotKeyHash` already compare on the explicit triple and a new `HealthSnapshot` member does not enter equality — note that this post-dates and supersedes the §Context/§Status-and-merge-order narrative above, which was written before that code landed and described the then-current record-`==` state. This amendment records the intent so no future refactor "helpfully" reverts to the record `==` and folds `Temporal` (or `Tags`) back into the key; the regression test that pins it lands with the ADR-013 field population (PR C). See ADR-013 for the field's shape and rationale.

Tags are node *identity* (ADR-005): a set of labels (environment, owner, region), not a health signal. ADR-005 documents them as immutable **after** `WithTags` — its immutability is about the tag *contents* not being mutated in place. It does **not** follow that a tag value can never change on a live node: `WithTags` sets `_tags` and returns the *same node*, so the API does permit replacing a live node's whole tag dictionary. Whether `WithTags` should itself be constrained to build-time — closing that affordance — is an **ADR-005 question this ADR neither answers nor depends on**. ADR-012 makes the narrower, self-standing call that tags do not participate in *health-report change detection*, because tags carry no health signal; that holds however ADR-005 later resolves the `WithTags` affordance.

Tags participate in equality today by *reference identity* — record equality uses the default comparer for `IReadOnlyDictionary`, which is reference equality — and `RebuildReport` reuses the one `node.Tags` reference every wave, so an unchanged node's tags compare equal wave to wave. That is a property of the current plumbing, not of the contract, and it buys only two hazards:

- **The latent churn trap.** A future refactor that rebuilt a node's tag dictionary per wave (a copy, a projection, an enrichment) would make **every tagged node's report churn on every wave**, silently — the precise failure class this ADR exists to eliminate, re-entering through the one field nobody is watching.
- **The `WithTags`-replacement edge, stated honestly.** If a consumer *does* replace a live node's tags via `WithTags`, the next wave rebuilds the node's snapshot with the new dictionary reference, which compares unequal, so **`StatusChanged` fires carrying an otherwise health-identical report.** That is a real emission a consumer could be keying on — and it is exactly the wrong emission: a label edit is not a health change, but it currently reads as one on the health stream. Excluding tags does change this behaviour, and this ADR does not pretend otherwise (see Migration); it holds that firing the *health* stream on a *tag* edit is a latent defect, not a feature worth preserving.

So tags carry **zero health change-signal**, and their equality participation buys only a reference-identity hazard and a spurious tag-edit emission. Excluding tags from the key removes both by construction, and does so **independently of the ADR-005 mutability question** — this is not the comparer papering over an unenforced identity contract, but a health-stream keying on health, which tags are not. The behaviour that changes is the loss of that spurious `WithTags`-then-wave emission — a correctness improvement, flagged in Migration rather than buried.

### 4. The transition stream stays `Status`-keyed — the divergence is intentional and named

`DiffTo` / `SelectHealthChanges` continue to compare `HealthStatus` only, and this is now a **stated contract, not an accident.** A `StatusChange` is by definition a status *edge* `(Previous → Current)`; a reason-only change has no edge to report. `DiffTo` already attaches `curr.Reason` to each edge it emits, so the current explanation rides every real transition — but a same-status reason move is, correctly, not a transition.

The two streams therefore answer two genuinely different questions, and the contract makes the split explicit:

| Stream | Surface | Key | Reason-only change | Question |
|---|---|---|---|---|
| **Report** (snapshot, level-triggered) | `HealthReportComparer` → `StatusChanged`, `ObserveHealthReport`, `PollHealthReport` | `(Name, Status, Reason)` per node + `Root` | **emits** | "has the visible picture changed?" |
| **Transition** (edge-triggered) | `DiffTo` → `SelectHealthChanges` | `Status` per node | **silent** | "which nodes crossed a status edge?" |

A consumer that needs "same status, new explanation" subscribes to the **report** stream. A consumer that needs status edges subscribes to the **transition** stream. Neither is retrofitted onto the other; the surfaces are documented as complementary, and `SelectHealthChanges`'s XML doc should say so plainly so nobody expects reason-change events from it.

### 5. Normative: what an emitted `Reason` may contain — the obligation that kills the churn at its source

Making `Reason` change-significant places a duty on whatever produces one. This is the rule that dissolves designs 1–3 above, and it is normative.

A **meaningful change** — the thing a new `Reason` is *allowed* to reflect — is a change a human operator reads *differently*: the status flipped, the root cause changed (`"queue depth 100"` → `"backend refused"`), or the explanation crossed from one prefix-anchored class to another (ADR-010's `"lease-pending: …"` → `"lease-expired: …"`). It is **not** a value that ticks on its own while the operator's reading is unchanged (an age counting up, a running total, a flap count incrementing). ADR-010's `StaleReasonPrefix`/`PendingReasonPrefix` scheme plus band-quantization is the canonical taxonomy: the *prefix class* and the *band* are meaningful; the raw age between band crossings is not. The sharp, mechanical test is the negative one in the rule below (no continuously-varying value); "meaningful" names the positive space that leaves.

> **An emitted `Reason` is an *explanation of the current status*, and MUST be stable between meaningful changes. It is not a telemetry channel.**
>
> A `Reason` MUST NOT embed a continuously- or monotonically-varying value — an age in seconds, a live counter, a wall-clock instant, a running total — that changes while the status's *meaning* does not. Such a value defeats report-equality suppression: it makes every wave's report unequal to the last, firing the report stream on nodes whose health did not change, and it is invisible to the transition stream, which keys on status.
>
> Continuously-varying data belongs in a **structured field** on the snapshot (deferred; ADR-010 OQ1 for staleness, ADR-011 OQ3 for flap), or, until such a field exists, MUST be **quantized to a band** so the string is stable between band crossings — a coarse bucket a human reads the same way for the whole band.

Under this rule the four problem designs resolve without special-casing:

- **ADR-010's decay reason** must quantize `age` to a band (a multiple of `Ttl`) rather than emit whole seconds. ADR-010 is amended to require this, citing this ADR. A consumer-side freshness policy's band-quantization is the consumer-proven prior art.
- **Flap counts** may not live in `Reason` at all (a live counter is the forbidden shape); they belong in a structured read surface (`Observe()` / `FlapWindow`, ADR-011 §8) or a future wire field, never in the equality-participating string.
- **The synthetic flap node** is doubly excluded: it both carries a live count in `Reason` and feeds the output stream back as input.

### Status and merge order

This ADR is `proposed`. Its normative language (`MUST`/`should`) states the contract to be adopted, matching the house style of ADR-008/010/011; it is not a claim that the code already behaves this way. The code changes it prescribes — `HealthReportComparer.GetHashCode` hashing `(Name, Status, Reason)`, `Equals`/`GetHashCode` comparing those three fields explicitly rather than via record `==`, and the `SelectHealthChanges` XML-doc note (§4) — are **not** in this doc PR; they land with the substrate implementation phase (the comparer sits on the ADR-010/011 implementation path), where the test updates named in Migration land in the same change. The two amendments this contract gates — ADR-010's band-quantized decay reason and ADR-011 OQ3's flap-on-wire wording — are sibling doc PRs. Recommended order: this ADR merges first (it is the contract the others cite); the comparer code change follows in the implementation phase. Nothing here should be read as approving a silent behaviour change to `HealthReportComparer` ahead of that implementation.

## Consequences

### Positive

- **The contract is pinned once.** "What participates in report equality?" has one answer — `(Name, Status, Reason)` for the report stream, `Status` for the transition stream — and future ADRs inherit it instead of re-deciding it.
- **The comparer stops misleading its readers.** `Equals` and `GetHashCode` key on the same fields; reasoning from the hash now gives the right answer, and `HealthTopologyComparer` gains a coherent precedent.
- **A latent tag-churn trap is removed** before it can bite: report-change detection no longer depends on tag-dictionary reference plumbing.
- **`Reason` becomes a disciplined, low-churn signal channel.** A meaningful explanation change reaches report consumers; a per-wave wiggle cannot, because the content rule forbids the wiggle at the source.
- **The churn gets a principled fix** (quantize, don't amputate) and ADR-011's flap-on-wire question gets its blocking contract.

### Negative / Trade-offs

- **`HealthReportComparer` changes behaviour in two small ways.** `GetHashCode` values change (equivalence relation unchanged — no observable effect for correct consumers), and `Tags` stop participating in equality. The latter is a behaviour change only for a consumer that today mutates a node's tags in place and expects the report stream to notice — which ADR-005 already forbids (tags are immutable identity), so no supported usage regresses. Stated rather than hidden.
- **The two-stream split is a permanent teaching burden.** "Report stream fires on reason changes; transition stream does not" is a real distinction consumers must learn. Mitigated by the table above and by `SelectHealthChanges`'s doc stating it. The alternative (collapse them) was rejected as losing capability.
- **The `Reason`-content rule is a convention the library cannot enforce.** A producer *can* still stuff a timestamp into a reason; nothing throws. This is the same class as ADR-010's reason-prefix convention — const-anchored discipline, not a compiler check. The mitigation is that the two library producers that emit varying data (lease decay, and any future policy explanation) are themselves governed by ADRs that now cite this rule.
- **No structured field yet.** This ADR pins the contract and defers the first-class staleness/flap fields to their own ADRs (ADR-010 OQ1, ADR-011 OQ3). Until then, band-quantization is the sanctioned workaround, which is coarser than a structured value would be.

### Migration

Enumerated against every known `HealthReportComparer` / `DiffTo` consumer, in-repo and by usage shape, rather than asserted:

- **The `StatusChanged` gate** (`HealthGraph.SerializedBubble` / refresh path, via `HealthReportComparer.Instance.Equals`): behaviour changes in exactly two ways, both intended — a same-status reason move now emits *only when the reason meaningfully changed* (band-crossing, not per wave: this is the report-churn fix), and a `WithTags`-then-wave no longer emits a health-identical report (§3). No other report drives a different emission decision.
- **`ObserveHealthReport` / `PollHealthReport`** (`Prognosis.Reactive`, via `DistinctUntilChanged(HealthReportComparer.Instance)`): identical to the above — they share the one comparer. Nothing else in `Prognosis.Reactive` calls the comparer.
- **Transition-stream consumers** (`SelectHealthChanges` → `DiffTo`): no change — `DiffTo` was and remains `Status`-keyed; this ADR does not touch it.
- **Direct `Equals` callers** (tests, e.g. `HealthReportComparerTests`; any sibling extension package): a same-`(Name, Status, Reason)` pair that previously differed only by a distinct-but-structurally-equal `Tags` reference now compares **equal**. No supported production path constructs such a pair (report snapshots reuse the node's one `Tags` reference); tests asserting the old Tags-sensitivity would need updating alongside the implementation, which is expected for a comparer contract change and lands in the same PR.
- **Direct `GetHashCode` callers using it as a cross-process content digest**: the value changes (§2). This is a hash-as-digest misuse — not collision-free, not runtime-stable — and should move to `Equals`/`DiffTo`; no supported use breaks.

Because a full external-consumer audit is not possible from this repo, the claims above are scoped to *in-repo consumers and usage shapes*, not to a proof that no downstream code anywhere relies on the old Tags-sensitivity or a specific hash value. The two behaviours that observably change (`WithTags`-then-wave emission, hash values) are named explicitly so a downstream owner can check their own code against them.

## Rejected alternatives

- **Exclude `Reason` from `HealthReportComparer` entirely** (the bluntest option considered). The bluntest fix for the churn: it closes the report churn and unifies the comparer's two halves in one stroke. Rejected because it collapses the report stream's key to `Status`, making it identical to `DiffTo` and permanently silencing same-status explanation changes — a Degraded node whose *cause* changed would emit nothing on any surface. It also strands ADR-010's reason markers and forecloses `Reason` as a signal channel the substrate deliberately uses. The larger blast radius (every report consumer expecting reason updates loses them) is the concrete cost the issue itself flagged. Band-quantization achieves the churn goal without the amputation.
- **Unify the two streams onto one key.** Either the report stream drops to `Status` (previous item) or the transition stream gains `Reason`. Giving `DiffTo` reason-sensitivity would invent a "reason-only transition" with no `(From → To)` status edge — a `StatusChange` that changed no status — which is incoherent for an edge-triggered surface. The streams answer different questions; forcing one key onto both destroys one of the answers.
- **A first-class structured staleness/flap field on `HealthSnapshot`, now.** The honest long-term home for varying data, and the right way to carry age or flap counts. Deferred, not rejected: it is a wire-schema change on `HealthSnapshot` (it crosses the heartbeat, per ADR-008's wire analysis) and needs a consuming control-plane feature to justify the compatibility cost. This ADR pins the *contract* that such a field would satisfy; ADR-010 OQ1 and ADR-011 OQ3 own the field itself. Band-quantization is the interim.
- **Leave the comparer's hash coarser than its equality** ("it is technically valid"). Rejected: validity is precisely why it deceives. Four review threads misread it; a fifth would too. Coherence is worth more than the marginal hash-distribution difference.
- **Make the `Reason`-content rule advisory only.** Rejected as too weak to do its job: the rule exists specifically to make the report-churn fix mandatory and to forbid the reason-carrier anti-pattern by contract rather than by case-by-case review. An advisory version would let the same design return a sixth time.

## Alignment with prior ADRs

- **ADR-005 — tags are identity.** Reaffirmed and leaned on: because tags are immutable identity, excluding them from report-change detection loses no signal and removes a reference-identity hazard.
- **ADR-008 — wire compatibility.** The deferral of a structured field inherits ADR-008's reasoning: a new `HealthSnapshot` member crosses the heartbeat and carries the staged-rollout burden. Band-quantization stays entirely within existing fields.
- **ADR-009 — the comparer precedent.** ADR-009's `HealthTopologyComparer` was written "following the `HealthReportComparer` precedent." §2 makes that precedent coherent (hash matches equality), so the copy inherits soundness rather than the split-halves trap.
- **ADR-010 — leased verdicts.** §5's content rule is the contract behind the report-churn fix: the decay reason must band-quantize `age`. ADR-010 is amended in place to require it and to cite this ADR. Stage transitions themselves carry status changes and are unaffected; only the within-stage age text was the churn source.
- **ADR-011 — temporal policies.** Unblocks OQ3 (flap on the wire): flap state may not ride `Reason` (§5), and a first-class field is the deferred structured-field question this ADR frames. `Observe()` / `FlapWindow` remain the local read surface meanwhile. ADR-011's separate `TemporalDeadlineChanged` signal is deliberately *outside* this contract — it is not carried in the report and never enters report equality — which is consistent with §4's stance that not every temporal signal belongs on the report stream.

## Open questions

1. **The structured staleness/flap field. — Resolved by ADR-013.** When a control plane programmatically consumes staleness or flap, a first-class `HealthSnapshot` member (e.g. `Staleness`, `FlapCount`) supersedes band-quantized reason text. Owned jointly by ADR-010 OQ1 and ADR-011 OQ3. ADR-013 adds exactly this member (`TemporalState? Temporal`) and resolves the question. **Clarification of the original phrasing:** "it would participate in the report key, not the transition key" meant the field belongs on the *report surface* — the snapshot the report stream carries — as opposed to the edge-triggered transition stream (`DiffTo`), which stays `Status`-only. It did **not** mean the field participates in report-*equality*: because the field carries continuously-varying data, it is **excluded** from `HealthReportComparer.Equals`/`GetHashCode` exactly like `Tags` (§3, as amended), which is precisely what stops it reintroducing the per-wave churn this ADR fought. So the field is *in the report the stream carries* but *inert to the equality that gates the stream* — the two senses of "key" that the original one-liner elided. ADR-013 §3 states this exclusion normatively.
2. **Should the report stream ever expose a reason-diff?** A consumer might want "these nodes changed status, and these changed only their reason" as one enriched stream. Not needed by any current consumer; the two-stream split covers the known cases. Revisit only with a concrete consumer.

## Amendment (2026-08-22): §5 binds the aggregator, not only the probe

§5 was written as an obligation on whoever *produces* a `Reason` — a probe author, `HealthLease.Decay`,
a policy explanation. Property-based fuzzing over generated topologies found the one producer the rule
never named: **`HealthNode.Aggregate` itself**, the fold that *composes* reasons out of other reasons.
In the failing graph every probe obeyed §5 perfectly — each emitted a constant string — and the report
churned on every wave anyway, because the composition manufactured an unbounded value out of bounded
inputs. The rule is extended here to bind composition, so the next composition surface does not reopen it.

### The violation §5 did not anticipate

`Aggregate` nests the worst dependency's reason as `"<dep>: <its reason>"`. Across a **back edge** the
dependency's cached evaluation is a wave old and, on a cycle, already contains *this* node's previous
reason — so the chain gains a full lap of the cycle every wave, without bound:

```
// n0 ⇄ n1, and n1 also depends on n2 (Unknown)
wave 0: Unknown — "n1: n0: n1: n2: n2 intrinsic Unknown"
wave 1: Unknown — "n1: n0: n1: n0: n1: n2: n2 intrinsic Unknown"
wave 2: Unknown — "n1: n0: n1: n0: n1: n0: n1: n2: n2 intrinsic Unknown"
```

Every status is stable. Only the string grows — and it is exactly the shape §5 forbids, *"a value that
ticks on its own while the operator's reading is unchanged."* It lands exactly where §5 predicted such a
value would hurt: under §1 the report is unequal to its predecessor forever, so `StatusChanged` and every
`DistinctUntilChanged(HealthReportComparer)` consumer fire on **every wave, forever**, while `DiffTo`
stays silent. Churn to the subscribers that do not want it, silence to the one that would — the §Context
sentence, reached by a route §Context did not enumerate.

This is a **fifth** design distorted by this contract, and the first with no producer at fault. Cases 1–4
were each a producer putting a varying value into a string. Here every input was conformant and the
composition was not, which is precisely why the rule as written could not catch it: nobody broke it.

It is reachable in production rather than theoretical. Cycles are a **tolerated** state in this library —
`HealthGraph.DetectCycles` reports them rather than the graph rejecting them, and `BubbleChange` documents
handling them — so a graph that accidentally closes a loop does not fail loudly; it quietly emits forever
and grows a string for the lifetime of the process.

### Normative: a composed reason is bound by §5 exactly as an emitted one

> **A `Reason` produced by *composition* is bound by §5 exactly as one produced by a probe. The composed
> chain MUST be bounded by the topology: its length may not depend on how many waves have run.**
>
> Concretely for `HealthNode.Aggregate`: a dependency reached by a **back edge** — one still on the
> current wave's evaluation stack — MUST NOT have its chain spliced in. It is cut to the flat
> `"<name> is <status>"` form, which carries a status rather than a nested chain and therefore
> terminates the chain.

The cut is the same one the library already makes twice elsewhere, so it inherits an established
precedent rather than inventing one: `HealthNode.BuildTreeSnapshot` reduces a revisited node to a
childless stub, and `HealthGraph.DetectCyclesDfs` stops at a gray node. Both wave walkers
(`NotifyDfs`, `EvalInDependencyOrder`) now carry the gray set of a standard cycle-detecting DFS for it.

**What the cut guarantees, stated precisely.** Each node appears at most once as a *hop* (a `"name: "`
prefix), and because the cut form embeds a status rather than a nested chain it always terminates, so a
lap can be entered at most once and the chain is bounded by the walk depth. That bound is what discharges
§5; it is asserted directly rather than through its symptom.

**What it deliberately does not guarantee** is that the full sequence of names is a *simple path*. The
terminal may repeat a hop, because that is what closing a cycle looks like: on
`A →Required C →Required B →Required A`, A reports `"C: B: A is Unhealthy"` — read as "A is unhealthy
because C, because B, because it comes back around to A." That is the honest report for a cyclic
dependency, and suppressing it would remove the only signal that the cycle is what closed the loop. The
weaker invariant is the intended one; the stronger phrasing was an error in the first draft of this work
and is corrected here so no future reader treats the terminal repeat as a defect.

**Acyclic graphs are unaffected, by construction.** Both walkers evaluate a node's dependencies before the
node itself, so a dependency can only be on the stack if an edge runs backwards. No DAG output changes —
including the several existing tests that assert exact reason strings.

### Why this belongs in this ADR rather than in a commit message

§1 makes `Reason` change-significant, and §5 is the obligation that makes §1 affordable: the report stream
can key on an explanation only because explanations are stable between meaningful changes. An aggregation
that composes conformant producers into a non-conformant result defeats §1 with nobody having broken a
rule as written — which means the obligation cannot live only on producers. Any surface that *derives* a
reason from other reasons (a future enrichment, a projection, a re-fold) inherits it, and this amendment
is what makes that inheritance explicit instead of leaving the next such surface to rediscover the churn.

### What pins it

Found and now guarded by property-based tests over generated topologies
(`Prognosis.Tests/Fuzzing/`), which run cyclic shapes deliberately — self-loops, figure-eights, cycles
with chords, hairballs:

- `RefreshAll_IsIdempotent` over every shape: a settled graph re-folds to an equal report.
- `ReasonChains_DoNotGrowAcrossWaves`: no node's reason moves once the fold has settled.
- `ReasonChains_AreBoundedPaths`: hops are distinct, and the whole chain is bounded by the node count —
  the assertion that forbids growth directly, since a chain accumulating laps must exceed it.

Verified byte-identical across waves on the shrunk counterexamples and clean across roughly 72,000
generated topologies, about a third of them cyclic. Two are pinned in the regression corpus:
`found-cycle-reason-lap` (the growth above) and `found-cycle-terminal-repeats-hop` (the terminal repeat,
raised in review).

Note that a cyclic graph converges over several waves rather than immediately — a wave reads dependencies
from cache, so information crosses one back edge per wave. That is inherent to folding a cycle from cached
values and is not a §5 concern: the fold reaches a fixpoint and stays there, which is the property that
matters. The defect was that it never reached one at all.

### Rejected alternatives

- **Cap the chain at a fixed depth.** Bounds the string, but picks an arbitrary constant and gets the
  content wrong either way: truncating from the head discards the culprit, which is the informative end;
  truncating from the tail prints a path that is not the real one. It also states nothing about what a
  reason chain *is*, so it papers over the invariant rather than pinning it.
- **Exclude `Reason` from the equality key.** Would also stop the emission storm, and is already rejected
  by this ADR's own §Rejected alternatives for the same reason: it collapses the report key to `Status`,
  making it identical to `DiffTo`, and permanently silences same-status explanation changes. The bug is in
  the reason, not in the key.
- **Make the full name sequence a simple path.** Requires per-evaluation name provenance — a set threaded
  through `HealthEvaluation`, which is a public record, or inspection of the composed string — for no
  functional gain. The harm is unbounded growth, which the cut removes; the terminal repeat is informative
  rather than defective.
