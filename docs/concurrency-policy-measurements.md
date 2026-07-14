# Concurrency policy measurements (Task 8)

**Status: `newrecruit-ui` roster swept and fitted on the dev box. `newrecruit`, `battlescribe-ui`
and the 4-vCPU CI runner are NOT measured — see "What was not reached".** See
`.superpowers/sdd/task-8-concurrency-report.md` for the task's final report.

This is the measurement campaign behind `ConcurrencyPolicy.For`
(`src/BattleScribeSpec.TestKit/Concurrency/ConcurrencyPolicy.cs`):

```
workers = clamp(min(ceil(cpuCount × k_engine), floor(availableMemory / memPerInstance_engine)), 1, MaxParallel)
```

`k_engine` (`EngineProfile.OversubscriptionFactor`) and `memPerInstance_engine`
(`EngineProfile.MemPerInstanceBytes`) are unmeasured placeholders (`1.0` / `0`) in
`src/BattleScribeSpec.TestKit/Engines/EngineRegistry.cs` today. This document is the measurement;
Task 9 transcribes the fitted numbers into the registry.

## Hardware

| | Dev box (this campaign) | 4-vCPU GitHub runner |
|---|---|---|
| Logical processors | 32 | 4 |
| RAM | ~93.6 GiB (100,451,844,096 bytes) | — |
| OS | Windows 11 Pro | Linux (GH-hosted) |
| Measured here? | ✅ | ❌ — see "What was not reached" |

---

## Method — and the method that was WRONG

Read this before running another sweep. Two prior attempts burned hours and produced nothing, and
the method they were given is why.

### ❌ What NOT to do

```bash
# WRONG — do not copy this.
bs-spec compare --engine <e> --roster --filter "<set>" \
  --policy-a "workers=P" --policy-b "workers=P"
```

Two things are wrong with it:

1. **It costs 3× per data point.** `compare` runs an untimed warm-up pass **plus both arms**. Every
   level below therefore executed the 64-spec set *three* times. At P=1 that alone is ~56 minutes
   for one point. This is why the earlier sweeps never finished.
2. **With identical arms it cannot answer the question it appears to answer.** `workers=P` vs
   `workers=P` compares a configuration against *itself*. Its verdict-equality gate is then a
   **flakiness** check — it says "running this twice gives the same answers", which is worth
   something but is **not** "parallelism does not change conformance results". Nothing in a
   same-vs-same comparison tests parallelism at all.

### ✅ What to do (and what the fitted numbers below rest on)

**Timing sweep — one single-arm run per level:**

```bash
bs-spec run --all --engine newrecruit-ui --roster --filter "cost/,condition/" \
  --policy "workers=<P>"
```

`run --all` prints the trace summary (wall, p50/p95, peak `harness.resource.count` by kind) at the
end of the run — everything a sweep point needs, at 1× the cost instead of 3×.

**Verdict safety — ONE `compare`, at the extreme, testing serial against the knee:**

```bash
bs-spec compare --engine newrecruit-ui --roster --filter "cost/,condition/" \
  --policy-a "workers=1" --policy-b "workers=<knee>"
```

*Serial vs parallel* is the question that matters. If serial and the knee agree, the levels between
are very likely safe. Run it **once**, after the sweep.

### Disclosure: what this campaign actually executed

Honesty about provenance, because it changes how the numbers should be read.

- **P=1** was run with the correct single-arm method (`run --all --policy "workers=1"`).
- **P=4…48** were executed by a driver script using the **wrong** (`compare`, identical-arm) method
  above. It had been launched by a previous attempt; an attempt to kill it mid-campaign did not
  take, and it ran to completion before this was noticed.

The data is still sound, and is reported as-is rather than re-run, for one reason that turned the
mistake into an asset: **each level's two identical arms are two independent samples of the same
configuration.** That is a repeatability check the corrected single-arm method would *not* have
given, and at high `P` it turned out to matter a great deal (see "Run-to-run variance" below). Both
arms are therefore reported per level rather than averaged away. The verdict-equality result printed
at each level is reported below **only as a flakiness result**, which is all a same-vs-same
comparison can support — the real serial-vs-parallel check is a separate run.

### Peak RSS per instance

The harness has no built-in RSS metric (`harness.resource.count` is a concurrency gauge, not
memory). Sampled externally: a PowerShell loop polls `Win32_Process` + `Get-Process` every 3s and
totals working set across **both** per-worker process families —

- the `bs-engine-host` adapter process (one per worker), and
- the `chrome-headless-shell` tree (one per worker; itself main + GPU + network + renderer
  children, so "one instance" is the whole tree, not one PID).

Counting only the browser would **understate** `memPerInstanceBytes`, because a worker cannot exist
without its adapter process too.

---

## Finding 0: the provisional cap does not bind a `--policy workers=N` override

`ConcurrencyPolicy.For` clamps `workers` to `min(cpuCount, 8)` when `MemPerInstanceBytes == 0`. It
does **not** clamp an override: `PolicyOverride.Apply` assigns `workers` directly, and
`EngineSelection.EffectivePlan` is `PlanOverride ?? ConcurrencyPolicy.For(...)` — the override
*replaces* the computed plan rather than layering onto it.

**Confirmed empirically at every level below.** Each sweep point reports `peak live resources` by
kind, and at `P > 8` those read `adapter-process: P`, `browser: P`, `browser-context: P` — 16, 24,
32, 48 — not 8. If the cap bound the override they would all read 8. It does not. **No source was
modified to run this campaign.**

## The spec set

**Filter `cost/,condition/` → 64 spec files**, all 64 executed (the `365 spec(s) — 301 skipped`
line in the `compare` logs counts the whole roster suite, of which the filter selects 64). Held
**fixed for every level** — mixing spec sets across a sweep makes the knee meaningless.

For scale: `newrecruit --roster` with *no* filter has **363 of 365** roster specs applicable. Three
older gitignored scratch logs under `.superpowers/sdd/campaign-logs/nr-roster-devbox-P{1,2,4}.log`
(survivors of a crashed prior attempt) used some **undocumented, different** filter yielding 69
specs. Their numbers (165.0s / 85.8s / 46.0s at P=1/2/4) are **not** spliced into anything below.

---

## 1. `newrecruit-ui` roster, dev box — Priority 1 ✅ MEASURED

64 specs (`cost/,condition/`), 32 logical processors.

| P | wall (arm A) | wall (arm B) | **wall (mean)** | speedup vs P=1 | p50 (A/B) | p95 (A/B) | peak adapter-process | peak browser | peak browser-context |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 1122.1s | *(single arm)* | **1122.1s** | 1.0× | 17115ms | 18446ms | 1 | 1 | 1 |
| 4 | 288.0s | 288.0s | **288.0s** | 3.9× | 17152 / 17171ms | 18420 / 18441ms | 4 | 4 | 4 |
| 8 | 149.1s | 149.8s | **149.5s** | 7.5× | 17262 / 17352ms | 18501 / 18667ms | 8 | 8 | 8 |
| 16 | 87.4s | 82.6s | **85.0s** | 13.2× | 17374 / 18500ms | 26601 / 20435ms | 16 | 16 | 16 |
| 24 | 69.0s | 73.3s | **71.2s** | 15.8× | 19493 / 17957ms | 22018 / 29479ms | 24 | 24 | 24 |
| **32** | **64.4s** | **48.4s** | **56.4s** ⭐ | **19.9×** | 22852 / 20157ms | 31310 / 22489ms | 32 | 32 | 32 |
| 48 | 76.7s | 58.9s | **67.8s** ⬆ | 16.6× | 44759 / 31763ms | 56657 / 33772ms | 48 | 48 | 48 |

Walls are the trace summary's own `run`-span wall. Raw logs:
`.superpowers/sdd/campaign-logs/nrui-roster-P*.log`.

### The knee is at P=32, and here is why it IS one

The brief's warning was that the prior 32-core sweep "stopped at P=16 *while still improving*",
which locates where a sweep ran out of patience, not where the engine stops scaling. This one did
not stop there:

1. **P=48 is slower than P=32 — in *both* independent arms**, not on average only:
   arm A `64.4s → 76.7s` (+19%), arm B `48.4s → 58.9s` (+22%). A degradation reproduced in two
   separate executions is not a sampling artifact.
2. **P=32 beats P=24 in both arms** (69.0 → 64.4; 73.3 → 48.4), so the minimum is not before 32
   either. The curve is bracketed on both sides.
3. **The per-spec latency corroborates the wall independently.** p50 is flat at ~17.1–17.4s from
   P=1 all the way to P=16 — i.e. up to 16 workers parallelism is nearly free, each spec still takes
   as long as it does alone. It then climbs (19.5s at P=24, 22.9s at P=32) and **explodes at P=48
   (44.8s — 2.6× the serial p50)**. At P=48 the box is thrashing: each spec takes far longer, and
   the extra workers no longer pay for it. That is the mechanism behind the wall turning around,
   observed rather than assumed.

**Fitted `k` for `newrecruit-ui` = 32 workers ÷ 32 logical processors = `1.0`.**

> ⚠️ **`k = 1.0` is the same number the registry currently holds as an unmeasured placeholder. It is
> now a measured value that happens to coincide — on *this* box.** Do not read the coincidence as
> "the placeholder was fine all along". It was never measured, and on the 4-vCPU CI runner it still
> is not (see "What was not reached"). The value being right here says nothing about there.

### Run-to-run variance is large at high P — and the flawed method is why we know

The two identical arms per level are two samples of the same configuration, and their spread is the
run-to-run noise:

| P | arm A | arm B | spread |
|--:|--:|--:|--:|
| 4 | 288.0s | 288.0s | 0% |
| 8 | 149.1s | 149.8s | 0.5% |
| 16 | 87.4s | 82.6s | 5.5% |
| 24 | 69.0s | 73.3s | 6.2% |
| 32 | 64.4s | 48.4s | **25%** |
| 48 | 76.7s | 58.9s | **24%** |

Noise is negligible below P=16 and becomes **large (~25%) at P≥32**, exactly where the box is
saturated and the run is at the mercy of OS scheduling. Consequences, stated plainly:

- The **P=48 degradation is robust** — it reproduces in both arms and in p50, so the *existence* of
  the knee is not in doubt.
- The **exact location** of the minimum is softer than the mean column suggests. P=24 and P=32
  overlap within noise on arm A (69.0 vs 64.4). What is solid is that the optimum lies in the
  **24–32 band** and that **48 is past it**. `k = 1.0` (i.e. 32) is the best-supported point in that
  band — it wins in both arms — but a `k` of 0.75 (24 workers) would cost little and sit further
  from the cliff.
- A single-sample sweep at these levels **would not have revealed this at all**. The corrected
  method is right about cost, but anyone using it at P≥32 should run each point twice.

### What the cap is costing today

The provisional cap pins `newrecruit-ui` to 8 workers. Measured cost of that, on this box:

**149.5s (P=8) vs 56.4s (P=32) — the cap is costing 2.65×** (2.3× on arm A alone). That is the prize
for retiring it, and retiring it requires `MemPerInstanceBytes`, below.

### Baseline verdicts

The P=1 baseline is **60 passed / 4 failed** of 64. Those 4 failures are this spec set's baseline
conformance verdicts for `newrecruit-ui` (hidden-entry specs the NR UI genuinely cannot satisfy:
`selectChildEntry`/`selectEntry` against entries not visible in the UI) — **not** a parallelism
artifact. The question the serial-vs-parallel check must answer is whether P=32 produces the *same*
4.

### Flakiness (NOT a parallelism-safety result)

Every level's `compare` reported **"Verdicts identical across 365 spec(s) (64 executed, 301
skipped)"**. Because both arms were the *same* `workers=P`, this establishes only that **the harness
is deterministic when run twice at the same parallelism level** — at every level from 4 to 48. It
does **not** establish that parallelism preserves verdicts. That is a separate run, below.

## 2. Verdict safety: serial vs the knee

```bash
bs-spec compare --engine newrecruit-ui --roster --filter "cost/,condition/" \
  --policy-a "workers=1" --policy-b "workers=32"
```

**IN FLIGHT — the result, and the fitted `MemPerInstanceBytes` sampled during it, land here.**

---

## What was NOT reached

Stated plainly, because inferring these from the dev box would be wrong:

- **The 4-vCPU CI runner — NOT MEASURED. `k` for CI is UNMEASURED and must NOT be inferred from this
  box.** The two hardware classes demonstrably disagree: the design doc records `nr-frozen`
  *degrading* past P=6 on the 4-vCPU runner while `nr-editor-ui-frozen` merely plateaus. This box
  shows `newrecruit-ui` scaling cleanly to 32. A `k` fitted here says nothing about a 4-vCPU
  container, where 4 browsers already saturate the box and memory is far tighter.
- **`newrecruit` (non-UI) roster — NOT MEASURED.** The other parallelising engine. Its `k` is
  expected to differ from `newrecruit-ui`'s (short CPU-bound specs vs long I/O-heavy ones is
  precisely the axis the design doc predicts engines will disagree on), so it must be swept on its
  own, with its own fixed filter.
- **`battlescribe-ui` `MemPerInstanceBytes` — NOT MEASURED.** `MaxParallel = 1`, so `k` is moot
  there, but the one-JVM peak RSS still needs measuring.
