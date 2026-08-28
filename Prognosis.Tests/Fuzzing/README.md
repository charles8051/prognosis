# Topology fuzzing

Property-based tests that generate graph topologies and check the fold's laws against
them, instead of pinning the shapes we happened to think of.

Four files:

| File | Role |
|---|---|
| `TopologySpec.cs` | A pure, serializable description of a graph. Answers its own structural questions (reachability, cycles, optional-only nodes), materializes into a live `HealthGraph`, and round-trips through a one-line literal. |
| `TopologyGenerator.cs` | The shape zoo — 16 generators, 10 acyclic and 6 cyclic. |
| `TopologyShrinker.cs` | Greedy delta-debugging. A 20-node counterexample usually lands at 3–5 nodes. |
| `Fuzz.cs` | The driver: generate, check, shrink on failure, print a reproducible report. |
| `TopologyFuzzTests.cs` | The properties, plus the pinned regression corpus. |

## Running it

The default run is deterministic and fast — a constant seed, 250 cases per property, a
couple of seconds. It is part of `dotnet test`; nothing extra to invoke.

Two environment variables change that:

```bash
PROGNOSIS_FUZZ_SEED=12345 PROGNOSIS_FUZZ_CASES=20000 dotnet test Prognosis.Tests
```

The seed is constant by default on purpose. A clock-seeded fuzzer discovers a new
counterexample on somebody's unrelated PR and blocks it, which teaches everyone to
ignore the suite. Explore by choosing a seed deliberately.

## When it finds something

The failure prints the seed, the case index, the shape, the shrunk topology as Mermaid,
and a one-line literal:

```
Property 'healing-set-sound-and-minimal' failed.

  seed        20260822   (PROGNOSIS_FUZZ_SEED)
  case        37 of 250
  shape       bipartite
  generated   8 nodes, 16 edges
  shrunk to   4 nodes, 3 edges, cyclic: False

  Pin this counterexample in TopologyFuzzTests.Corpus:
      "bipartite=H>1A;H>2R,3R;U;D",
```

Paste that literal into `TopologyFuzzTests.Corpus` and it runs on every build regardless
of seed, through the same property bodies. `Corpus_TopologiesStillHold` applies whichever
properties are in scope for that topology (cyclic graphs skip the acyclic-only ones).

### The literal format

`shape=H>1R,2O;X;D>0S` — nodes separated by `;`, each an intrinsic-status letter
optionally followed by `>` and comma-separated `target`+importance-letter edges. Node 0
is the root.

- Status: `H`ealthy, `U`nknown, `D`egraded, unhealth`X`
- Importance: `R`equired, `I`mportant, `O`ptional, re`S`ilient, `A`dvisory

## The shape zoo

**Acyclic** — chain (deep recursion), star (widest single fold), caterpillar, binary tree
with forward cross edges, layered DAG, random DAG, transitive tournament (every edge a
DAG can have), bipartite mesh, shared-leaf fan-in ("everything depends on the one
database"), diamond ladder (linear in nodes, *exponential* in unrolled tree paths).

**Cyclic** — self-loop, simple cycle, figure-eight, cycle with chords, hairball, DAG with
back edges. Cycles are a tolerated state in this library (`DetectCycles` reports them
rather than the graph rejecting them), so they are generated deliberately.

Shapes are picked round-robin by case index, so every shape gets even coverage and a
failing case index always maps to the same shape and the same seeded `Random`.

Importance is uniform over the five levels, except that a node has a 1-in-5 chance of
having *all* its edges made `Resilient`. Uniformly random importance almost never
produces two resilient siblings — which is exactly the case the quorum rule is about.

## The properties

**Live engine, every shape including cyclic** — every public query is total (no throw, no
hang, no stack overflow); the report covers exactly the reachable set; the topology
preserves edge order and importance; `DetectCycles` agrees with ground truth; the tree
snapshot expands each name at most once; re-folding a settled graph changes nothing; reason
chains are bounded by the node count and do not grow across waves; the fold is monotone in
every node; `Unknown` is strictly non-gating (ADR-006); optional-only subgraphs are inert;
`Advisory` is never stricter than `Important` (ADR-008).

**Diagnostic re-fold, acyclic** — `WhatIf` with no overrides reproduces the live root;
`WhatIf` never *overstates* the root under masked probes; the projected tree equals the
live tree when quiescent (ADR-009).

**Diagnostic re-fold, acyclic + leaf failures only** — the regime where snapshot-only
intrinsic reconstruction is provably exact, so the analysis can be held to exact
agreement with the live engine: `WhatIf` predicts the engine exactly under leaf
counterfactuals; `MinimalHealingSet` is sound and irredundant; the contributor frontier
is load-bearing and converges.

Every one of these compares against the **live engine** — materializing a graph per
counterfactual — not against a second model of the fold. A property that only checked the
analysis against itself would agree with its own bugs.

## What it found

Four defects on the first run, all since fixed. The counterexamples are pinned in
`TopologyFuzzTests.Corpus` under `found-`, and reverting either fix makes those entries
fail — the corpus is load-bearing, not decorative.

**Three in `MinimalHealingSet`** (`Prognosis.Diagnostics/HealthGraphAnalysis.cs`):

1. `Heal`'s switch over `Importance` had no `Advisory` case and no `default`, so an
   Advisory edge silently behaved like `Optional` and was never healed — the returned set
   left the root `Degraded`. The switch now carries both, matching the "no silent default"
   guard `HealthContribution.Of` and `HealthStatusExtensions.Rank` already use (ADR-008).
2. Advisory *absorbs* `Unknown`, so a child under an Advisory edge only needs to reach
   `Unknown`, not the limit. The first version of that fix over-healed.
3. A `Resilient` group can reach `Degraded` two ways — establish the quorum, or fix every
   sibling — and only the first was costed. Separately, across a shared node a repair
   chosen for a `Required` path can satisfy a quorum elsewhere for free, making the
   quorum repair dead weight; that interaction is global, so it is settled globally by an
   irredundancy prune over the same re-fold.

**One in `HealthNode.Aggregate`** — the reason chain spliced in a back edge's cached
reason, so on a cycle it gained a full lap per wave:

```
TopologySpec.Parse("cycle=H>1R;H>0R,2R;U")     // n0 ⇄ n1, and n1 also sees n2 (Unknown)

pass 0: Unknown — "n1: n0: n1: n2: n2 intrinsic Unknown"
pass 1: Unknown — "n1: n0: n1: n0: n1: n2: n2 intrinsic Unknown"
pass 2: Unknown — "n1: n0: n1: n0: n1: n0: n1: n2: n2 intrinsic Unknown"
```

The status was stable; only the string grew, without bound — and because
`HealthReportComparer` carries `Reason` in its equality key (ADR-012 §1), every wave then
looked like a change, so a cyclic graph emitted on `StatusChanged` every beat forever.
ADR-012 §5, as amended 2026-08-22, makes bounded composition normative.

`Aggregate` now cuts the chain at a back edge, and prefers an equally-bad dependency that
can explain itself over a cycle peer whose chain had to be cut — so the culprit outside the
cycle still gets named:

```
pass 0: Unknown — "n1: n2: n2 intrinsic Unknown"
pass 1: Unknown — "n1: n2: n2 intrinsic Unknown"
```

This is provably a no-op on an acyclic graph: both wave walkers evaluate a node's
dependencies before the node itself, so nothing is ever on the stack unless an edge runs
backwards. `ReasonChains_AreBoundedPaths` asserts the invariant directly rather than its
symptom, and `RefreshAll_IsIdempotent` runs over `AllShapes` as the acceptance test.

What the cut guarantees is that each node appears at most once as a *hop* and the chain is
bounded by the node count — not that the full name sequence is a simple path. The terminal
may repeat a hop, which is what closing a cycle looks like: on `A →R C →R B →R A`, A reports
`"C: B: A is Unhealthy"`. That terminal carries a status rather than a nested chain, so it
ends the chain and a lap can be entered at most once. Pinned as
`found-cycle-terminal-repeats-hop`.
