# Concurrency policy measurements (Task 8)

**Status: `newrecruit-ui` roster timing/knee swept and fitted on the dev box; serial-vs-32-way
verdict safety confirmed; `MemPerInstanceBytes` now measured for both `newrecruit-ui` and
`battlescribe-ui`. `newrecruit` (non-UI) roster's `k` and the entire 4-vCPU CI runner remain NOT
measured — see "What was not reached".** See `.superpowers/sdd/task-8-concurrency-report.md` for
the task's final report.

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
memory). Sampled externally: a PowerShell loop polls `Win32_Process` every 2s and totals working
set across **all** per-worker process families discovered by walking the live process tree from
the measurement run's own root PID —

- the `bs-engine-host` adapter process (one per worker),
- the Playwright Node.js driver process (`node.exe`, one per worker — sits between the adapter and
  the browser; not anticipated when this method note was first written, found while measuring, see
  below), and
- the `chrome-headless-shell` tree (one per worker; itself main + GPU + network + renderer
  children, so "one instance" is the whole tree, not one PID).

Counting only the browser would **understate** `memPerInstanceBytes` — a worker cannot exist
without its adapter process and driver too.

**Tree-scoping, not name-matching, and why it is load-bearing here (not just tidy):** the sampler
enumerates `Win32_Process`, builds a parent→child map, and BFS-walks it from the measurement run's
own root PID, summing working set only over processes that are actual descendants of *that*
invocation. It does **not** match on process name globally. This was not a theoretical concern: a
**separate, unrelated Claude Code session was running its own Playwright-based tests against a
different project on this same machine during this measurement window**, spawning its own
`chrome-headless-shell` tree the whole time. A name-only sampler (`Get-Process -Name
chrome-headless-shell`) would have silently summed someone else's browsers into this number. Sampler
script: `.superpowers/sdd/campaign-logs/sample-mem.ps1` (gitignored, like the rest of this
campaign's scratch data); verified clean by cross-checking `adapterCount`/`chromeCount` in every
sample against the known worker count for the whole run (see below).

**Use a modest, single, fixed `P` — not the P=1-vs-`<knee>` verdict-safety run.** An earlier attempt
(commit `9018a31`, now superseded by this section) sampled *during* the `workers=1` vs
`workers=32` compare above and fit a line across whatever concurrency happened to be live at each
2–3s tick — which is not just P=1 and P=32, but also every transient in-between state while workers
were still starting up or shutting down (its own data shows samples at `workers ∈ {1, 8, 13, 32}`,
i.e. mid-ramp states from a run that was never actually running at 8 or 13 workers as a matter of
policy). Worse, it conflated two different things: `workers=1`'s single worker processes **all 64**
specs serially (so its managed heap grows across 64 cold starts), while each of `workers=32`'s
workers processes only 2 — so "memory per worker" was really measuring "memory as a function of how
many specs that worker has processed so far", not a per-instance constant. The corrected method
below runs **one dedicated, modest-P batch** (`P=8`, matching the sweep's own P=8 point for
cross-validation) and samples only *that* steady population of exactly 8 workers throughout — no
ramp-mixing, no re-running the expensive P=32 knee.

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

## 2. Verdict safety: serial vs the knee ✅ PASSED

```bash
bs-spec compare --engine newrecruit-ui --roster --filter "cost/,condition/" \
  --policy-a "workers=1" --policy-b "workers=32"
```

```
✓ Verdicts identical across 365 spec(s) (64 executed, 301 skipped).
  A wall: 1122.2s     B wall: 78.3s
```

**Running 32 workers in parallel does NOT change conformance results.** This is the check that
matters, and it is the one the identical-arm sweep above could never have made. Serial (`workers=1`)
and the knee (`workers=32`) produce **the same verdict for every one of the 64 executed specs** —
including the same 4 baseline failures, which is the specific thing that had to be confirmed: those
4 are genuine `newrecruit-ui` conformance failures (hidden-entry specs), not a parallelism artifact,
and parallelism neither hides them nor adds new ones. Since serial and P=32 agree, the levels
between are very likely safe.

Per-arm evidence that the two arms genuinely ran differently (i.e. the lever was connected, not a
false green):

| | Arm A (`workers=1`) | Arm B (`workers=32`) |
|---|---:|---:|
| wall | 1122.0s | 75.1s |
| p50 / p95 | 17132 / 18440ms | 20547 / 44083ms |
| peak `adapter-process` | 1 | **32** |
| peak `browser` | 1 | **32** |
| peak `browser-context` | 1 | **32** |

Log: `.superpowers/sdd/campaign-logs/nrui-verdict-w1-vs-w32.log`.

## 3. `MemPerInstanceBytes` for `newrecruit-ui` ✅ MEASURED (corrected)

> **This section supersedes commit `9018a31`'s 787 MB / 1 GiB figure.** That measurement sampled
> *during* the P=1-vs-P=32 verdict-safety compare above and is methodologically unsound for this
> purpose — see the "Peak RSS per instance" method note above for exactly why (ramp-state
> contamination, and conflating per-worker memory with per-worker spec count). It is left in this
> document's git history rather than scrubbed, in keeping with this campaign's practice of
> disclosing its own errors rather than quietly overwriting them.

**Measurement run (dedicated, not reused from any other point):**

```bash
bs-spec run --all --engine newrecruit-ui --roster --filter "cost/,condition/" --policy "workers=8"
```

Chosen deliberately as a **fixed P=8** — modest per the brief ("do NOT re-run P=32"), and it
reproduces the sweep's own P=8 timing almost exactly (150.0s here vs 149.1s/149.8s in section 1,
same 60 passed/4 failed), which cross-validates that this was a normal, representative run and not
an outlier. Sampled every 2s for the run's full 150s lifetime (63 samples), tree-scoped to this
run's own process descendants (see Method) so the concurrent unrelated session on this machine
could not contaminate the count.

Every sample through the run's steady state read exactly `adapterCount=8`, `driverCount=8`,
`chromeCount=32` (8 workers × the 4-process Chromium tree) — confirming P=8 was honoured throughout
and nothing outside this run's own tree was being counted.

**Peak total (near end of batch — the dotnet adapters' managed heaps grow across the batch, so the
true peak is late, not at steady-state startup; early samples read only ~2.4 GB total):**

| process family | count | total @ peak | **per instance** |
|---|--:|--:|--:|
| `bs-engine-host` adapter (`dotnet.exe`) | 8 | 4,360,798,208 B | 545,099,776 B (≈520 MiB) |
| Playwright driver (`node.exe`) | 8 | 3,620,548,608 B | 452,568,576 B (≈432 MiB) |
| `chrome-headless-shell` tree | 32 | 4,410,413,056 B | 551,301,632 B (≈526 MiB) |
| **TOTAL** | **8 instances** | **12,391,759,872 B (≈11.54 GiB)** | **1,548,969,984 B (≈1.44 GiB)** |

> **The Playwright driver is a real, non-trivial cost (≈432 MiB/instance, ~29% of the total)** that
> the original method note (written before this measurement) did not anticipate — it only named the
> adapter and the browser tree. Omitting it would have understated `MemPerInstanceBytes` by roughly
> 30%, which is the same class of mistake the method note already warns about for browser-only
> sampling.

**Measured `MemPerInstanceBytes` for `newrecruit-ui` = 1,548,969,984 bytes (≈1.44 GiB / ≈1.55 GB).**
Raw log: `.superpowers/sdd/campaign-logs/nrui-mem-P8.log`; raw samples:
`.superpowers/sdd/campaign-logs/mem-nrui-P8.csv` (gitignored, same as the rest of this campaign's
scratch data).

No headroom multiplier is added here — this document reports what was *measured*; how much
headroom to bank when transcribing into `EngineRegistry` is Task 9's call, not this task's (Task 9
should still read the caveat directly below about `AvailableMemoryBytes` being *total*, not *free*,
memory before deciding).

### What retiring the cap actually does — worked example

`ConcurrencyPolicy.For` applies `ProvisionalUnmeasuredMemoryCap` (`min(cpuCount, 8)`) only while
`MemPerInstanceBytes == 0`. That is no longer true for `newrecruit-ui` once Task 9 transcribes the
number above — **the cap can retire for this engine.**

Using the real formula with the measured `k = 1.0` (section 1) and `MemPerInstanceBytes =
1,548,969,984`:

**A hypothetical 16 GiB / 32-core laptop** (`AvailableMemoryBytes` ≈ 16 GiB = 17,179,869,184 bytes
— see the caveat below on what this field actually measures):

```
byCpu    = ceil(32 × 1.0)                         = 32
byMemory = floor(17,179,869,184 / 1,548,969,984)  = 11
workers  = min(32, 11)                            = 11    (MaxParallel = 0 → unlimited, no further clamp)
```

**11 workers.** That is *more* than the provisional cap gives this same laptop **today**
(`min(32, 8) = 8`) — the cap is purely CPU-shaped (`min(cpuCount, 8)`) and does not look at memory
at all, so it does not even correctly protect the box it exists to protect: a hypothetical 64-core
box with 4 GiB of RAM gets the same `8` from the cap today, which would still be enough to
overcommit that box's real, measured, memory bound (8 × 1.44 GiB ≈ 11.5 GiB > 4 GiB). The real
measured bound is a genuine memory-aware guard where the cap is a coarse CPU-shaped guess; retiring
the cap is a net improvement even on the small-memory box the cap was written to protect, not only
on the large dev box.

**Sanity check on the dev box itself** (93.6 GiB = 100,451,844,096 bytes, per "Hardware" above):

```
byCpu    = ceil(32 × 1.0)                            = 32
byMemory = floor(100,451,844,096 / 1,548,969,984)    = 64
workers  = min(32, 64)                               = 32
```

**Reproduces the empirically-found knee exactly.** On this box CPU binds before memory (64 ≫ 32),
consistent with section 1's finding that the P=48 degradation was contention/thrashing, not memory
exhaustion — the measured number is not just plausible, it is self-consistent with the independently
observed knee.

**Caveat that must travel with this number:** `MachineProfile.AvailableMemoryBytes` is
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` — **total** physical memory (or a cgroup limit),
**not currently-free** memory. `availableMemory / memPerInstance` therefore leaves zero headroom for
the OS and every other process on the box by construction. The 16 GiB worked example above is
computed the same way the policy itself computes it (matching what the reader will actually see if
they run the policy on such a box), but a real 16 GiB laptop already running a browser and an IDE
has less than 16 GiB free — Task 9 should weigh that when deciding whether to transcribe the raw
measured value or add headroom.

---

## 4. `MemPerInstanceBytes` for `battlescribe-ui` ✅ MEASURED

`MaxParallel = 1` for `battlescribe-ui`, so `k` (`OversubscriptionFactor`) is moot — the engine can
never run more than one instance regardless of what the formula computes. Only the one-JVM peak RSS
needed measuring.

**Measurement run:**

```bash
bs-spec run --all --engine battlescribe-ui --gamedata --filter "entry/,export/"
```

No `--policy` override — the default plan already resolves to `workers=1` (the engine's own
`MaxParallel` ceiling), so this is the policy's real, unmodified default behaviour for this engine.
54/54 specs passed, wall 159.8s (matches the 159.7s recorded in `docs/warm-reuse.md` for this same
spec set almost exactly, confirming warm-reuse fired normally and this was a representative run: one
JVM alive for the whole batch, not 54 cold starts). Sampled every 2s for the full run (76 samples),
same tree-scoped method as above.

The BS app and its automation agent share **one JVM** (`bs-ui-java-agent` is loaded via
`-javaagent` into the same `java` process that runs the Roster/Data Editor — see
`BsRosterApp.cs`/`BsGameDataUiEngine.cs`), so "one instance" here is genuinely one process, not a
tree.

**Peak total:**

| process family | count | total @ peak | |
|---|--:|--:|--:|
| adapter (`dotnet.exe`, `bs-engine-host`) | 1 | 80,945,152 B (≈77 MiB) | |
| JVM (`java.exe`, app + agent) | 1 | 974,446,592 B (≈929 MiB) | |
| **TOTAL** | **1 instance** | **1,055,391,744 B (≈1006.6 MiB / ≈0.98 GiB)** | **= per instance, directly (MaxParallel=1)** |

**Measured `MemPerInstanceBytes` for `battlescribe-ui` = 1,055,391,744 bytes (≈0.98 GiB / ≈1.06
GB).** Raw log: `.superpowers/sdd/campaign-logs/bsui-mem-gamedata.log`; raw samples:
`.superpowers/sdd/campaign-logs/mem-bsui-gamedata.csv`.

**What retiring the cap does here: nothing, and that is correct.** `MemPerInstanceBytes` becoming
non-zero retires `ProvisionalUnmeasuredMemoryCap`'s trigger for this engine too, but
`ConcurrencyPolicy.For` applies the engine's `MaxParallel` ceiling *after* the memory/CPU
computation (`workers = min(byCpu, byMemory)`, then clamped again to `MaxParallel`), so the worker
count stays `1` on every box, 16 GiB laptop or 93.6 GiB dev box alike. Measuring this number was
still necessary — it is the input the formula needs to *prove* memory never binds here, not merely
assume it — but it changes no externally-visible behaviour.

---

## What was NOT reached

Stated plainly, because inferring these from the dev box would be wrong:

- **The 4-vCPU CI runner — NOT MEASURED. `k` for CI is UNMEASURED and must NOT be inferred from this
  box.** The two hardware classes demonstrably disagree: the design doc records `nr-frozen`
  *degrading* past P=6 on the 4-vCPU runner while `nr-editor-ui-frozen` merely plateaus. This box
  shows `newrecruit-ui` scaling cleanly to 32. A `k` fitted here says nothing about a 4-vCPU
  container, where 4 browsers already saturate the box and memory is far tighter. This applies to
  **every** number in this document, including the two `MemPerInstanceBytes` figures — RSS per
  Chromium/JVM instance is plausibly closer to hardware-invariant than `k` is (it is not a
  contention effect), but that has not been verified on the 4-vCPU class either.
- **`newrecruit` (non-UI) roster — NOT MEASURED.** The other parallelising engine. Its `k` is
  expected to differ from `newrecruit-ui`'s (short CPU-bound specs vs long I/O-heavy ones is
  precisely the axis the design doc predicts engines will disagree on), so it must be swept on its
  own, with its own fixed filter. A background sweep attempt for this (`nr-sweep-correct.sh`,
  single-arm method, two reps per level — the *right* method this time) was found already running
  when this task's memory-measurement work began; it was stopped deliberately, uncompleted, to get
  a clean box for the RSS sampling below (a concurrent heavy job would have contaminated the process
  tree it needed to isolate). Its partial logs (`nr-roster-P1-r1.log` and earlier) are incomplete
  and were not used for anything in this document. `newrecruit`'s `k` remains open.
- ~~`battlescribe-ui` `MemPerInstanceBytes` — NOT MEASURED.~~ **Now measured — see section 4.**
- **`newrecruit-ui`'s `MemPerInstanceBytes` headroom decision — left to Task 9.** This document
  reports the raw measured figure (1,548,969,984 B); whether to bank extra headroom on top before
  writing it into `EngineRegistry` (given `AvailableMemoryBytes` is total, not free, memory) is
  flagged in section 3 but deliberately not decided here.
