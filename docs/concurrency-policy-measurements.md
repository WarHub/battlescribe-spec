# Concurrency policy measurements (Task 8) — and what Task 9 did with them

> ## ⚠ THIS DOCUMENT COVERS TWO DIFFERENT AXES. DO NOT MIX THEM.
>
> | | **Process axis** (§1–§6) | **Context axis** (§7)|
> |---|---|---|
> | Path measured | **CLI** — `bs-spec run --all` | **xUnit** — `dotnet test` |
> | What is replicated | an adapter **process family** (adapter + Node driver + own Chromium tree) | a browser **context** inside ONE shared browser + ONE shared Node driver |
> | Sized by | `ConcurrencyPlan.Workers` | `ConcurrencyPlan.PoolSize` |
> | Engine declares | `OversubscriptionFactor` (`k`): `newrecruit` 0.375, `newrecruit-ui` 1.0 | `ContextPoolSize`: **an absolute count, not a factor** — `newrecruit` 4, `newrecruit-ui` 16 |
> | Scales with CPU? | **yes** — `ceil(cpuCount × k)` | **NO.** The optimum is identical on 32 CPUs and on 4 (§7.4) |
> | Costs (measured) | `MemPerInstanceBytes`: **1.22–1.44 GiB** per worker | `MemPerContextBytes`: **163–225 MiB** per context |
> | Which one does CI run? | the `checks` / batch lanes | **every NewRecruit conformance lane** |
>
> `ConcurrencyPolicy.For` **used to** set `PoolSize: workers` — feeding **one number to both axes**.
> §7 measured how wrong that is for the context axis; **§8 records the code change that separated
> them** (issue #314). The two axes now have four separate declared facts and share no number.

**Status: measured.** On the dev box: `newrecruit-ui` roster (§1–§3) **and** `newrecruit` roster
(§5) are both swept, fitted, verdict-safety-checked and memory-measured; `battlescribe-ui`'s
`MemPerInstanceBytes` is measured (§4). **§7 measures the context axis on BOTH a 32-core dev box and
a 4-CPU/16 GiB Linux container** (the CI-runner class), for `newrecruit` and `newrecruit-ui`.
The 4-vCPU runner remains unmeasured **on the process axis** and `k` must not be inferred from the
dev box — see "What was not reached". `battlescribe` (non-UI) is also unmeasured
and therefore still declares `MemPerInstanceBytes = 0`, leaving it bound by
`ConcurrencyPolicy.UndeclaredMemoryWorkerCap` — which is exactly what that cap is for.

> **Note on §5 vs §6.** Two sections were written concurrently by two agents working this branch at
> the same time and both are kept: **§5** is Task 8's `newrecruit` measurement (its `k`, its cliff,
> its memory), **§6** is Task 9's transcription decisions. §5 landed *after* Task 9 was dispatched on
> the premise that `newrecruit` was unmeasured; Task 9 was corrected mid-flight and **§5's numbers
> are transcribed** (§6a) — including the deliberately-below-optimum `k = 0.375` and the two caveats
> that must travel with it.

See `.superpowers/sdd/task-8-concurrency-report.md` and `.superpowers/sdd/task-9-concurrency-report.md`
for the tasks' final reports.

This is the measurement campaign behind `ConcurrencyPolicy.For`
(`src/BattleScribeSpec.TestKit/Concurrency/ConcurrencyPolicy.cs`):

```
workers = clamp(min(ceil(cpuCount × k_engine),
                    floor(availableMemory × 0.8 / memPerInstance_engine)), 1, MaxParallel)

# ...and, ONLY while memPerInstance_engine == 0 (undeclared):
workers = min(workers, min(cpuCount, 8))
```

`k_engine` is `EngineProfile.OversubscriptionFactor`; `memPerInstance_engine` is
`EngineProfile.MemPerInstanceBytes`; the `0.8` is `ConcurrencyPolicy.MemoryHeadroomFactor` (Task 9 —
§6c). Both engine constants live in `src/BattleScribeSpec.TestKit/Engines/EngineRegistry.cs`. This
document is the measurement; Task 9 transcribed the fitted numbers into the registry.

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

## Finding 0: the undeclared-memory cap does not bind a `--policy workers=N` override

> Naming note: this cap was called `ProvisionalUnmeasuredMemoryCap` while this campaign ran. Task 9
> renamed it `UndeclaredMemoryWorkerCap` and kept it (see §6b) — the mechanism below is unchanged.

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

No headroom multiplier is baked into this number — this document reports what was *measured*, and
`EngineProfile.MemPerInstanceBytes` holds exactly this figure, unmodified. **Headroom is policy, not
measurement**, so Task 9 put it in the policy instead (`ConcurrencyPolicy.MemoryHeadroomFactor`,
§6c) rather than inflating an honest measured constant. The caveat directly below is why it exists at
all.

**Caveat that must travel with this number:** `MachineProfile.AvailableMemoryBytes` is
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` — **total** physical memory (or a cgroup limit),
**not currently-free** memory. A bare `availableMemory / memPerInstance` would therefore leave zero
headroom for the OS and every other process on the box, *by construction*. A real 16 GiB laptop
already running a browser and an IDE has nothing like 16 GiB free. Compounding it, a sampled peak is
a **lower bound** — the true peak is at least 1,548,969,984 B, never less. See §6c.

### What the measured number does to this engine's plan

`ConcurrencyPolicy.For` applies `UndeclaredMemoryWorkerCap` (`min(cpuCount, 8)`) only while
`MemPerInstanceBytes == 0`. That is no longer true for `newrecruit-ui` — **the cap self-retires for
this engine**, and the real, memory-aware bound takes over. (It stays live for the engines that are
still undeclared; see §6b.)

Worked examples with the measured `k = 1.0` (§1), `MemPerInstanceBytes = 1,548,969,984`, and
`MemoryHeadroomFactor = 0.8`:

**Dev box** (32 cores, 93.6 GiB = 100,451,844,096 bytes, per "Hardware" above):

```
byCpu    = ceil(32 × 1.0)                                  = 32
byMemory = floor(100,451,844,096 × 0.8 / 1,548,969,984)    = 51
workers  = min(32, 51)                                     = 32
```

**32 — reproduces the empirically-found knee exactly, and the headroom factor costs nothing here**
(CPU binds with room to spare: 51 ≫ 32). Consistent with §1's finding that the P=48 degradation was
contention/thrashing, not memory exhaustion — the measured number is not just plausible, it is
self-consistent with the independently observed knee. **The harness is not slowed below
measured-optimal on the box the optimum was measured on.**

**16 GiB laptop** (≥ 8 cores; `AvailableMemoryBytes` ≈ 17,179,869,184 bytes — *total*, not free):

```
byMemory = floor(17,179,869,184 × 0.8 / 1,548,969,984)     = 8
workers  = min(byCpu, 8)                                   = 8    (memory binds on any ≥8-core laptop)
```

**8 workers, claiming ≈11.5 GiB of a nominal 16 GiB** and leaving ≈4.5 GiB for the OS, the page
cache and the IDE. Without the headroom factor the same box plans **11** (≈15.9 GiB — the entire
machine, on a peak figure that is itself a lower bound). Note the answer *numerically coincides*
with what the old blanket cap gave this laptop (`min(cpuCount, 8) = 8`) — but it is now derived from
a measured footprint and a stated safety margin rather than assumed, and it now moves correctly with
the box: the old cap is purely CPU-shaped and would hand the same `8` to a 64-core box with 4 GiB of
RAM (8 × 1.44 GiB ≈ 11.5 GiB ≫ 4 GiB → OOM), where the measured bound gives that box **2**.

**4-vCPU / 16 GB CI runner:**

```
byCpu    = ceil(4 × 1.0)                                   = 4
byMemory = floor(17,179,869,184 × 0.8 / 1,548,969,984)     = 8
workers  = min(4, 8)                                       = 4     (CPU binds)
```

**4** — replacing the hand-set `NR_PARALLEL: 6`. `k` on this hardware class is **not measured** (see
"What was not reached"), so this is a number we ship watching, not one we claim.

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

**What declaring this number does here: nothing, and that is correct.** `MemPerInstanceBytes`
becoming non-zero retires `UndeclaredMemoryWorkerCap`'s trigger for this engine too, but
`ConcurrencyPolicy.For` applies the engine's `MaxParallel` ceiling *after* the memory/CPU
computation (`workers = min(byCpu, byMemory)`, then clamped again to `MaxParallel`), so the worker
count stays `1` on every box, 16 GiB laptop or 93.6 GiB dev box alike — pinned by a test across four
machine profiles. Measuring this number was still necessary — it is the input the formula needs to
*prove* memory never binds here, not merely assume it — but it changes no externally-visible
behaviour.

---

## 5. `newrecruit` (non-UI) roster, dev box — Priority 2 ✅ MEASURED

Same 64 specs (`cost/,condition/`), same box. **Corrected method** throughout: single-arm
`run --all --policy "workers=P"`, **two reps per level** (section 1's variance finding says single
samples cannot be trusted). All 64 specs pass at every level. Noise here is **tiny** (≤5%, usually
<2%) — every conclusion below reproduces in both reps.

| P | rep 1 | rep 2 | **mean** | p50 | p95 |
|--:|--:|--:|--:|--:|--:|
| 1 | 151.9s | 152.1s | **152.0s** | 2349ms | 2395ms |
| 4 | 40.4s | 40.9s | **40.7s** | 2428ms | 3143ms |
| 6 | 29.2s | 29.3s | **29.2s** | 2508ms | — |
| 8 | 23.2s | 22.9s | **23.1s** | 2633ms | 3656ms |
| 10 | 20.2s | 20.4s | **20.3s** | 2647ms | 3749ms |
| 12 | 18.4s | 18.4s | **18.4s** | 2716ms | — |
| 14 | 16.5s | 16.4s | **16.5s** | 2782ms | 4184ms |
| **15** | 15.5s | 16.0s | **15.8s** ⭐ | 2672ms | 4358ms |
| **16** | 31.8s | 30.2s | **31.0s** 💥 | 2877ms | **16033ms** |
| 20 | 42.6s | 41.0s | **41.8s** | 3333ms | 28694ms |
| 24 | 50.7s | 50.9s | **50.8s** | 3933ms | 37468ms |
| 32 | 57.3s | 60.5s | **58.9s** | 14141ms | 45175ms |
| 48 | 82.4s | 75.3s | **78.9s** | 43593ms | 70033ms |

### The engines disagree — exactly as the design doc predicted

This is the finding the whole campaign exists for. The brief's Step 3 said: *"`nr-frozen` **degrades**
past P=6 while `nr-editor-ui-frozen` merely **plateaus**. Short CPU-bound specs die on contention;
long I/O-heavy ones tolerate oversubscription. If your data reproduces this, `k` is per-engine and
the spec is right."*

**It reproduces.** Same box, same specs, same day:

- **`newrecruit-ui`** (p50 ≈ 17.1s/spec — long, I/O-heavy) scales cleanly to **32 workers**, turning
  only at 48.
- **`newrecruit`** (p50 ≈ 2.4s/spec — **7× shorter**, CPU-bound) bottoms out at **15 workers**, then
  **falls off a cliff**, ending at P=48 *worse than it was at P=4*.

`k` is genuinely per-engine: **1.0 vs 0.375 — a 2.7× difference on identical hardware.** No single
global oversubscription factor could serve both.

### The cliff at P=16 — sharp, reproducible, and adjacent to the optimum

Not a gentle plateau. **P=15 → P=16 costs 1.97× (15.8s → 31.0s) for one extra worker**, in all four
runs (15.5/16.0 vs 31.8/30.2). p95 corroborates independently and more violently — **4358ms →
16033ms (3.7×)** — while p50 barely moves (2672 → 2877ms): the median spec is fine and a *tail* is
being starved. That is a contention cliff.

**It lands exactly on the physical core count.** This box is **16 physical / 32 logical** (2-way SMT).

> ⚠️ **A limitation of the policy model, not to be papered over.** `ConcurrencyPolicy.For` only sees
> `machine.CpuCount` = `Environment.ProcessorCount` = **32 logical**. The cliff tracks **16
> physical**. On this 2-way-SMT box physical = `CpuCount / 2`, so a `k` fitted here encodes "stay
> under physical cores" only *by accident of this box's SMT ratio*. On a non-SMT machine (logical ==
> physical — most cloud VMs, including the 4-vCPU GitHub runner) the same `k` sits somewhere else
> entirely relative to the cliff. The policy has no `PhysicalCoreCount` input to say this properly.

### Fitted `k` — measured optimum vs what should be transcribed

- **Measured optimum: P=15 → `k = 15/32 = 0.47`.**
- **Recommended transcription: `k = 0.375`** → `ceil(32 × 0.375)` = **12 workers** (18.4s).

**Why not transcribe the measured optimum.** It sits **one worker** below a **1.97× cliff**. A `k` of
0.47 puts every 32-logical-core box at 15 workers — one scheduling quirk, one stray background
process, one different SMT ratio from doubling the suite wall. `k = 0.375` costs **17%** against the
optimum (18.4s vs 15.8s — still **8.3×** over serial) and buys **3 workers of margin**. Choosing the
peak of a curve that falls off a cliff immediately to its right is not optimization, it is a trap.
The measured 0.47 is recorded so the choice is visible rather than silently rounded.

### Verdict safety ✅ PASSED

```bash
bs-spec compare --engine newrecruit --roster --filter "cost/,condition/" \
  --policy-a "workers=1" --policy-b "workers=15"
```

```
✓ Verdicts identical across 365 spec(s) (64 executed, 301 skipped).
  A wall: 152.7s     B wall: 15.9s
```

Peak resources: arm A `adapter-process/browser/browser-context = 1`; arm B `= 15`. The arms genuinely
differed; parallelism changed no verdict.

### `MemPerInstanceBytes` for `newrecruit` ✅ MEASURED

Same method as section 3 (fixed **P=8**, tree-scoped to this run's own descendants, all **three**
per-worker families). Steady state read exactly `hosts=8, node=8, chrome=32` on every sample.

| process family | count | total @ peak | **per instance** |
|---|--:|--:|--:|
| `bs-engine-host` adapter (`dotnet.exe`) | 8 | 4,345 MB | 543 MB |
| Playwright driver (`node.exe`) | 8 | 3,016 MB | 377 MB |
| `chrome-headless-shell` tree | 32 | 2,660 MB | 332 MB |
| **TOTAL** | **8 instances** | **10,021 MB** | **1,253 MB (1,313,420,083 B ≈ 1.22 GiB)** |

**Measured `MemPerInstanceBytes` for `newrecruit` = 1,313,420,083 bytes (≈1.22 GiB).** Slightly
lighter than `newrecruit-ui`'s 1.44 GiB, as expected (no heavy SPA loaded in the page). Headroom
decision left to Task 9, same as section 3.

> **Two of my own earlier figures in this campaign were wrong and are retracted here.** An initial
> pass reported 787 MB/instance for `newrecruit-ui` and 680 MB/instance for `newrecruit`. Both
> sampled only `dotnet` + `chrome-headless-shell` and **missed the Playwright `node.exe` driver
> entirely** (~29–30% of the true total), and both were process-*name*-scoped rather than
> process-*tree*-scoped, so unrelated `node.exe` on the box (the agent harness runs its own) further
> polluted the count. Section 3's method — fixed P, tree-scoped, all three families — is the correct
> one, and both engines' numbers above are measured that way. Recording the retraction rather than
> quietly overwriting it: this is precisely the "browser-only understates it" trap the Method section
> warns about, and I fell into a variant of it.

---

## Summary — what Task 9 should transcribe

| engine | `MaxParallel` | measured optimum | **`OversubscriptionFactor` (k)** | **`MemPerInstanceBytes`** |
|---|---:|---:|---:|---:|
| `newrecruit-ui` | 0 | **P=32** / 32 cpus | **1.0** | **1,548,969,984** (≈1.44 GiB) |
| `newrecruit` | 0 | **P=15** / 32 cpus → 0.47 | **0.375** ⚠️ *deliberately below the optimum — see the cliff* | **1,313,420,083** (≈1.22 GiB) |
| `battlescribe-ui` | 1 | n/a (`k` moot) | *(unchanged)* | see section 4 |
| `battlescribe` | 0 | **NOT MEASURED** | **NOT MEASURED** | **NOT MEASURED** |

Both verdict-safety gates passed, so the parallelism these values unlock is conformance-neutral on
this box.

---

## 6. Task 9's decisions — what was transcribed, and the one judgment call

### 6a. Transcribed into `EngineRegistry`

| Engine | `MemPerInstanceBytes` | `OversubscriptionFactor` (k) |
|---|--:|--:|
| `battlescribe` | **0 — UNDECLARED** (never measured) | 1.0 (default; moot while undeclared) |
| `battlescribe-ui` | **1,055,391,744** (§4) | 1.0 (default; moot — `MaxParallel = 1`) |
| `newrecruit` | **1,313,420,083** (§5) | **0.375 — MEASURED** (§5: optimum P=15 → 0.47, fitted **below** it; 1.97× cliff at P=16) |
| `newrecruit-ui` | **1,548,969,984** (§3) | **1.0 — MEASURED** (§1: knee at P=32 / 32 cores; P=48 degrades) |

`newrecruit-ui`'s `k` was *already* 1.0 in the registry as an unmeasured default. The literal did not
change; **its status did.** It is now measured, and the code comment says so. It was previously right
only by luck, and a value that is right by luck is indistinguishable from one that is wrong until
someone measures it.

**The engines disagree by 2.7× and the policy now reproduces that.** On the same 32-core box the same
64 specs want **12 workers** under `newrecruit` and **32** under `newrecruit-ui`. A single global
oversubscription factor could not have served both; this is the per-engine `k` earning its existence.

**Two caveats ride with `newrecruit`'s `k = 0.375`** — both written at the constant in
`EngineRegistry.cs`, repeated here because they are the kind of thing a future reader "cleans up":

1. **It is deliberately below the measured optimum (0.47), and must stay there.** The cliff is
   brutally asymmetric: one worker *past* the peak costs **1.97×** (15.8s → 31.0s); one worker short
   costs a few percent. `ceil(32 × 0.375) = 12` buys three workers of margin for 17% of the optimum.
   A test (`Policy_NewRecruit_StaysLeftOfItsMeasuredCliff_OnTheBoxItWasMeasuredOn`) pins this so a
   future tweak cannot silently walk it over the edge.
2. **It is not portable, and the model cannot currently say so.** The cliff lands on the box's
   **physical** core count (16 of 32 logical — a 2:1 SMT box). Physically the optimum is *"one worker
   per physical core"*; 0.47 is not a property of the number, it is a property of this box's SMT
   ratio. `MachineProfile` only knows `Environment.ProcessorCount` (**logical**). On another 2:1 SMT
   box, 0.375 lands safely at-or-below physical cores. **On a non-SMT box it under-provisions by
   ~2×.** `newrecruit` is CPU-bound (p50 2.4s/spec) so it gains nothing from hyperthreads;
   `newrecruit-ui` is I/O-bound (p50 17.1s/spec) and scales past them happily — which is *why* `k` is
   per-engine. A `PhysicalCoreCount` input to `MachineProfile` is the real fix and is filed as a
   follow-up; it was not attempted here.

> **§5 is also the empirical vindication of §6b.** Had this task followed the plan and *deleted* the
> cap, `newrecruit` — which declared `MemPerInstanceBytes = 0` right up until §5 landed — would have
> fallen through to `byCpu` = 32 workers on this box. §5 measures that configuration at **58.9s
> against 23.1s at the capped 8**: a **2.55× regression**, straight over a cliff the plan did not
> know existed when it was written. The cap was not dead weight awaiting removal; it was holding an
> unmeasured engine back from a cliff nobody knew was there. **`battlescribe` is in exactly that
> position today** — unmeasured, cap-bound, cliff unknown.

### 6b. The cap was KEPT and PROMOTED, not deleted

The plan (Task 9, Step 1b) said to delete `ProvisionalUnmeasuredMemoryCap` on the reasoning that,
once every builtin declares a measured footprint, the `MemPerInstanceBytes == 0` gate can never fire
again and the cap becomes dead weight. **That premise turned out to be false**, so the step was not
followed:

- **`battlescribe` is still unmeasured** and declares `0`. One built-in is all it takes; and §5 is the
  proof that an unmeasured engine can hide a 1.97× cliff.
- `EngineRegistry.DefaultProfile` and the `engines.json` config path both let an engine register
  **without declaring `MemPerInstanceBytes` at all**, defaulting to 0. This harness is explicitly
  open to other engines — "every engine is measured" is a state it can never reach.

Deleting the cap would therefore have sent every undeclared engine straight back to unbounded
`cpuCount` workers on a big box — reintroducing the exact bug the cap was added to prevent. What
made the cap look provisional was never the mechanism; it was that *every* engine happened to be
unmeasured when it was written. The `== 0` gate means it **self-retires per engine, automatically,
with no code change** — which is precisely the property a permanent default wants.

So it was renamed **`UndeclaredMemoryWorkerCap`** and redocumented as what it permanently is: *the
conservative default for any engine that has not declared its per-instance memory footprint.*
**Declaring `MemPerInstanceBytes` is how an engine opts into full machine-width parallelism.** Until
it does, it gets `min(cpuCount, 8)` — slower than optimal, but it cannot OOM a laptop. That is the
right default for code we did not write and did not measure.

The two `xunit.runner.json` files **used** to be pinned to this same constant at
`maxParallelThreads: 8`. That link is now **cut**, for two reasons found in review:

- **The quantities have no shared meaning.** One is a memory-safety ceiling for engines that declare
  no footprint; the other is the test runner's own thread count. Pinning them together meant raising
  the engine cap would have silently re-sized the test host.
- **The literal `8` was a *raise*, not a cap, where it mattered most.** xUnit's default is
  `Environment.ProcessorCount`. On the 32-core dev box `8` capped (32 → 8), but on the **4-vCPU CI
  runner it doubled** collection parallelism (4 → 8) — an unmeasured increase in contention on the
  smallest, most memory-constrained machine in the fleet, shipped under a commit message that said
  the opposite.

The value is now a machine-relative multiplier, **`"0.5x"`**, declared and justified in
`ConcurrencyConfigurationDriftTests` (which still pins both files to it). It can never exceed xUnit's
own default on any box, and half is the honest half: xUnit's thread accounting covers only *its own
test threads*, while the tests spawn the things that actually consume the machine — JVMs, Playwright
Node drivers, Chromium trees — none of which xUnit can see. Verified live with `diagnosticMessages`:
**4-vCPU runner → 2 threads; this 32-core box → 16 threads.**

`ConcurrencyPlan.MaxParallelThreads` — a field with **zero consumers**, whose doc comment claimed it
governed xUnit — was deleted rather than left as decoration. xUnit reads that JSON before any of our
code runs, so no plan can govern it; saying so is better than pretending otherwise.

### 6c. The judgment call: `MemoryHeadroomFactor = 0.8`

With `MemPerInstanceBytes` non-zero, the memory bound goes **live** for the first time. As written it
computed `availableMemory / memPerInstance` — i.e. it planned to consume **100% of available
memory**. That is unsafe in two independent ways that point the same direction:

1. **A sampled peak is a lower bound.** The RSS figures above were sampled on a 2 s poll (and
   `ResourceMetrics` documents the same limitation about its own 2 s export interval). A spike
   shorter than the sampling period is invisible. The true peak is *at least* what was measured.
2. **"Available" is not "spare".** `AvailableMemoryBytes` is *total* physical memory (or a cgroup
   limit), not free memory. The OS, page cache, the parent CLI and the test host all live inside that
   number.

And **OOM is a cliff, not a gradient**: one worker too few costs a little wall-clock; one too many
kills the run. That asymmetry is the whole argument for a generous margin.

`ConcurrencyPolicy.MemoryHeadroomFactor = 0.8` — engines may claim at most **80% of available
memory**. It lives in the **policy, not the per-engine constant**, on purpose:
`MemPerInstanceBytes` must stay an honest measured number, and a safety margin folded into it would
be a lie that propagates into every future comparison against a re-measurement. Safety margin is
policy; policy lives in one tunable place.

**What it costs, checked against the measurement:** on the 32-core / 93.6 GiB dev box it costs
**nothing** — `byMemory` falls from 64 to 51, both still ≫ `byCpu = 32`, so `newrecruit-ui` still
resolves to **32**, exactly the measured knee. It costs `newrecruit` nothing either (byMemory 76 → 61,
both ≫ its `byCpu = 12`). **The headroom factor does not make the harness slower than measured-optimal
anywhere it was measured.** It binds only on memory-constrained boxes (the 16 GiB laptop: 11 → 8 for
`newrecruit-ui`), which is precisely where it should.

### 6d. What the policy now picks, per box — and what it replaces in CI

`ceil(cpuCount × k)` vs `floor(availableMemory × 0.8 / memPerInstance)`, whichever is smaller:

| box | `newrecruit` (k=0.375, 1.22 GiB) | `newrecruit-ui` (k=1.0, 1.44 GiB) | `battlescribe-ui` | `battlescribe` (undeclared) |
|---|--:|--:|--:|--:|
| 32-core / 93.6 GiB dev box | **12** (cpu binds; mem allows 61) | **32** (cpu binds; mem allows 51) | 1 | 8 (cap) |
| 16-core / 16 GiB laptop | **6** (cpu binds; mem allows 10) | **8** (**mem binds**; cpu allows 16) | 1 | 8 (cap) |
| 4-vCPU / 16 GB CI runner | **2** (cpu binds; mem allows 10) | **4** (cpu binds; mem allows 8) | 1 | 4 (cap → cpuCount) |

**The three `NR_PARALLEL` settings deleted from `ci.yml`, and what replaces them:**

| CI lane | fixture → engine | was | now | Δ |
|---|---|--:|--:|---|
| `nr-frozen` | `FrozenNrRosterFixture` → `newrecruit` | `NR_PARALLEL: 6` | **2** | ⚠️ **3× fewer** |
| `nr-editor-ui-frozen` | `FrozenNrGameDataUiFixture` → `newrecruit-ui` | `NR_PARALLEL: 6` | **4** | 1.5× fewer |
| `nr-live-conformance` | `LiveNrRosterFixture` → `newrecruit` | `NR_PARALLEL: 2` | **2** | **unchanged** |

⚠️ **`nr-frozen` 6 → 2 is the one to watch, and it is a genuine open risk, not a rounding.** CI's own
historical figure — `NR_PARALLEL: 6` was recorded as *measured optimal on the 4-vCPU runner* (48 s;
degrading past 6) — implies `k ≈ 1.5` for `newrecruit` **there**, while this box measured `0.375`
**here**: a 4× disagreement on the same engine. At least three things could explain it, and this
campaign cannot distinguish them:

- **`k` genuinely does not transfer across hardware classes.** Precisely the caveat "What was not
  reached" insists on: the 4-vCPU runner is **NOT measured**, and its `k` must not be inferred from a
  32-core box. This is the honest default reading.
- **The SMT gap (§6a, caveat 2).** `k = 0.375` encodes "one worker per *physical* core" on a 2:1 SMT
  box. Applied to a 4-vCPU runner it yields 2 — right if that runner is 2 physical cores × 2 threads,
  ~2× too low if its vCPUs are physical.
- **The two numbers may not measure the same quantity.** `NR_PARALLEL` sized *browser contexts in an
  in-process pool*; the sweep behind `k` sized *whole worker processes* (adapter + Node driver +
  Chromium tree each). A context is far cheaper than a process tree, so the optimum for one is not
  the optimum for the other — and the unified policy now feeds one number to both.

**This is shipped watching, not shipped assuming.** The plan's own gate (Task 9, Step 3) is that CI
lane wall-times must be **no worse** than the recorded baselines (`nr-frozen` 48 s). If `nr-frozen`
regresses, the fix is to measure `k` **on the runner** — the constant is wrong, not the architecture.

### The xUnit fixture path is bounded defensively, and it is still not measured

The third bullet above ("the two numbers may not measure the same quantity") is not just a caveat on
CI's `k` — it describes **the whole `dotnet test` path**, and every sweep in this campaign was
`bs-spec run` (the CLI), never `dotnet test`. Two things follow, and both are now written into the
code rather than only into this document:

1. **`PoolSize` is in a different unit from `Workers`.** A worker is a whole process family (adapter
   + Node driver + Chromium tree — that is what `MemPerInstanceBytes` measured); a fixture pool's
   element is an in-process browser **context** sharing one browser and one driver with its siblings.
   The policy feeds the same number to both. The error is conservative (it over-charges per context,
   so it cannot OOM) but it is an error.

2. **Nothing bounds the product across collections.** Real concurrency inside a conformance test is
   `Parallel.ForEachAsync(MaxDegreeOfParallelism = pool.Size)` *within a single `[Fact]`*, which
   xUnit's `maxParallelThreads` does not constrain at all; and collection fixtures live for the whole
   collection, so several pools can be alive at once. Uncapped, this 32-core box would ask for **32 /
   12 / 12** contexts across the three NR fixtures (≤ 56 live) where the pre-policy defaults were
   **5 / 5 / 10** (≤ 20).

`FixtureConcurrency.FixturePoolCap = 8` is the interim guard: worst case ≤ 24 live contexts, no lower
than the old frozen defaults, and it **does not bind on the 4-vCPU CI runner** (sized 2–4), so the CI
table above is unchanged by it. It is a defensive bound over an unmeasured path — *not* a fitted
constant, and not a substitute for the real fix, which is a shared budget the pools draw from
(**issue #314**). Sizing the xUnit path honestly needs a sweep of `dotnet test`, which this campaign
never ran.

> **§7 has now run that sweep.** Both statements above survive, but the framing "the error is
> conservative, so it can only over-provision" turns out to be **exactly backwards for CI**: on the
> 4-vCPU runner the policy *under*-provisions the pool, and the cost is a **2.0× wall-clock
> regression** on `nr-editor-ui-frozen`. `FixturePoolCap = 8` also binds the dev box, where it costs
> the `newrecruit-ui` lane **31%**. Read §7 before touching either number.

---

## What was NOT reached

Stated plainly, because inferring these from the dev box would be wrong:

- **The 4-vCPU CI runner — NOT MEASURED. `k` for CI is UNMEASURED and must NOT be inferred from this
  box.** This is no longer just a caution, it is *demonstrated*: the two engines' `k` on **this one
  box** differ by **2.7×** (1.0 vs 0.375) purely because their spec durations differ, and
  `newrecruit`'s cliff turns out to track **physical** cores while the policy only sees **logical**
  ones. A box with 8× fewer cores, a different SMT ratio and far tighter memory has no reason to land
  near either value. The design doc's own 4-vCPU observations (`nr-frozen` degrading past P=6,
  `nr-editor-ui-frozen` plateauing) are the only CI evidence in existence and are not a fitted `k`.
  **CI must be swept on CI.** This applies to the `MemPerInstanceBytes` figures too — per-instance RSS
  is plausibly closer to hardware-invariant than `k` (it is not a contention effect), but that has
  not been verified on the 4-vCPU class either.
- **`battlescribe` (non-UI, in-process IKVM) — NOT MEASURED.** Out of this campaign's scope. Its
  `MemPerInstanceBytes` is still `0`, so the undeclared-memory cap still governs it.
- **The gamedata domain — NOT MEASURED for `newrecruit`/`newrecruit-ui`.** Every sweep here is
  `--roster`. `k` may differ by domain; nothing here licenses assuming it does not.
- **Spec sets other than `cost/,condition/` — NOT MEASURED.** The knee is a property of the workload
  as much as the engine (it is set by spec *duration*, which is exactly what differs between the two
  NR engines). A suite with a different duration profile could knee elsewhere, especially for
  `newrecruit`, whose cliff is contention-driven.
- ~~`battlescribe-ui` `MemPerInstanceBytes` — NOT MEASURED.~~ **Now measured — see section 4.**
- **Headroom decisions — left to Task 9.** This document reports what was *measured*; whether to bank
  extra headroom before writing into `EngineRegistry` (given `AvailableMemoryBytes` is *total*, not
  *free*, memory) is flagged in sections 3 and 5 but deliberately not decided here.

---

# 7. The CONTEXT axis — the `dotnet test` path, measured

**Status: measured, on both hardware classes.** This section is a *separate campaign* from §1–§6 and
measures a **different quantity on a different code path**. Nothing in §1–§6 is revised or
contradicted by it — the two are not comparable numbers, and the single most important thing to take
from this document is that they were never the same number.

## 7.0 Why this is a different quantity (and why it is the one CI pays for)

`ConcurrencyPolicy.For` returns `PoolSize: workers` — the *same integer* for two consumers that share
no mechanism:

- **CLI / batch path** (`bs-spec run --all`): the parent spawns `Workers` adapter **processes**; each
  runs its specs strictly serially (`SpecSuiteRunner.cs`, `AdapterHandler.RunAsync`). `PoolSize` is
  **not even on the wire** — `EngineHostLocator` sends only `workers=`, `reuse-roster=`,
  `reuse-gamedata=`. §1–§6 swept *this*.
- **xUnit path** (`dotnet test`): there are **no worker processes at all**. Concurrency is the
  fixture's eagerly-created pool of browser **contexts**, sized by `FixtureConcurrency.PoolSizeFor()`
  and used as `MaxDegreeOfParallelism` inside a single `[Fact]`. `Workers` is read **nowhere** in
  `tests/Infrastructure/`. §7 sweeps *this*.

**Every NewRecruit CI conformance lane runs the xUnit path.** `nr-frozen`, `nr-ui-frozen`,
`nr-editor-frozen`, `nr-editor-ui-frozen` are all `dotnet test -p:TestProfile=...` (see `ci.yml`). The
axis that governs CI's wall-clock is the one that had never been measured.

The retired `NR_PARALLEL` env var sized **contexts on this axis** (`ci.yml` used 6/6/2). When the
policy replaced it, the mirror `PoolSize: workers` handed the *process-axis* `k` to the *context*
pool, and CI's pools became `nr-frozen` **6 to 2** and `nr-editor-ui-frozen` **6 to 4**. §7.5 shows
that this — and not anything else — is the measured cause of the observed CI regression.

## 7.1 Method

Deliberately **not** the §-Method recipe: `bs-spec compare` cannot help here, because it drives the
CLI path, **which has no pool at all**. There is no way to reach this axis from the CLI.

1. **Sweep the real lanes.** `dotnet test -p:TestProfile=nr-frozen` (engine `newrecruit`, fixture
   `FrozenNrRosterFixture`, **365 specs / 363 executed**) and `-p:TestProfile=nr-editor-ui-frozen`
   (engine `newrecruit-ui`, `FrozenNrGameDataUiFixture`, **113 specs / 112 executed**). Exactly what
   CI runs, with `NR_HEADLESS=true`.
2. **Forcing the pool size.** `FixtureConcurrency.PoolSizeFor` derives the pool from the policy, so a
   **temporary, local, uncommitted** env override (`BSSPEC_CAMPAIGN_POOL`) was added inside it for the
   duration of the campaign, together with a per-spec verdict/duration recorder. **This scaffolding was
   reverted before committing** — `git status` shows no source changes, and the tree was rebuilt clean.
   It was deliberately *not* named `NR_PARALLEL`: that knob is retired and mechanically gated by
   `ConcurrencyConfigurationDriftTests` (commit `2191e7e`), and nothing here reintroduces it.
3. **Two walls are recorded, and they are not the same.**
   - **`[Fact]` wall** — the `Parallel.ForEachAsync` execution only.
   - **`dotnet test` wall** — the whole invocation, **which is what CI actually pays**, and which also
     contains the fixture's pool construction. Contexts are created **serially** in a loop, each doing
     a full page load (+ HAR route + Pinia wait for `newrecruit`), so **init cost grows linearly with
     pool size**. The two walls disagree about the optimum, and `dotnet test` is the one that decides
     CI. It is the objective used below.
4. **Both hardware classes.** 32-core / 93.6 GiB Windows dev box, **and** a 4-CPU / 16 GiB Linux
   container modelling the GitHub runner.
5. **Bracketing.** Every knee is carried at least two — mostly four to six — levels past, and only
   called a knee once it gets *worse*.
6. **Hygiene.** Leftover `testhost` / `chrome-headless-shell` / Node-driver trees are reaped and
   **verified zero before every point**; a point that finds a survivor *aborts rather than reports*
   (this fired once; that sample was discarded and re-run). Runs are foreground, never `nohup`'d. No
   `--no-build` was trusted without first confirming a real rebuild — the pool size echoed back in each
   run's header is the proof the fresh dll is live.

### The 4-CPU container is a real 4-CPU box, not a CFS quota

This matters enough to state precisely, because the easy version of it is wrong. `podman run
--cpus=4` sets a **CFS quota** (`cpu.max=400000 100000`) but leaves `nproc` reporting **32** — so
Chromium and the .NET thread pool would still size themselves for a 32-core machine while being
throttled to 4 cores' worth of time. That is not the runner. `--cpuset-cpus` is not delegated under
rootless podman on WSL, so instead **the WSL VM itself was pinned to 4 processors / 16 GB**
(`.wslconfig`), giving a genuine `nproc=4`, `MemTotal ≈ 16 GiB` box. The original `.wslconfig` was
backed up and **restored byte-identically** afterwards.

## 7.2 `newrecruit` (`nr-frozen`) — 363 specs

**32-core dev box** (mean of *n* runs; the star marks the optimum):

| P | **`dotnet test` wall** | `[Fact]` wall | p50 | p95 | peak browser+driver RSS |
|--:|--:|--:|--:|--:|--:|
| 1 | 25.73 s | 20.24 s | 19 ms | 33 ms | 1 270 MiB |
| 2 | 19.25 s | 12.00 s | 17 ms | 28 ms | 1 611 MiB |
| **4** | **17.57 s** * *(n=3)* | 9.65 s | 19 ms | 36 ms | 2 218 MiB |
| **6** | **17.58 s** * *(n=3)* | 9.14 s | 17 ms | 37 ms | 2 664 MiB |
| 8 | 18.75 s *(n=3)* | 9.16 s | 19 ms | 35 ms | 2 997 MiB |
| 12 | 20.85 s (worse) | 9.00 s | 23 ms | 48 ms | 3 722 MiB |
| 16 | 22.45 s (worse) | 9.08 s | 29 ms | **1 120 ms** | 4 500 MiB |
| 24 | 25.81 s (worse) | 9.09 s | 42 ms | 1 290 ms | 6 199 MiB |
| 32 | 28.90 s (worse) | 9.12 s | 59 ms | 1 289 ms | 8 184 MiB |

**4-CPU / 16 GiB container:**

| P | **`dotnet test` wall** | `[Fact]` wall | p50 | p95 |
|--:|--:|--:|--:|--:|
| 1 | 29.51 s | 24.33 s | 24 ms | 38 ms |
| 2 | 19.81 s *(n=3)* | 14.28 s | 23 ms | 37 ms |
| **4** | **17.75 s** * *(n=3)* | 11.20 s | 34 ms | 67 ms |
| 6 | 18.50 s *(n=3)* | 10.93 s | 40 ms | 106 ms |
| 8 | 19.25 s *(n=3)* | 10.96 s | 47 ms | 149 ms |
| 12 | 21.34 s (worse) *(n=3)* | 11.12 s | 79 ms | **1 363 ms** |
| 16 | 23.15 s (worse) | 10.78 s | 103 ms | 1 513 ms |
| 24 | 26.67 s (worse) | 10.63 s | 161 ms | 4 437 ms |
| 32 | 31.36 s (worse) | 11.55 s | 220 ms | 4 565 ms |

**The knee is at P=4, and it IS one.** The `dotnet test` wall rises **monotonically for six
consecutive levels past it** on the container (6, 8, 12, 16, 24, 32) and five on the dev box, ending
**77% worse** than the optimum at P=32. Run-to-run spread at the knee is < 0.5%.

**Why it saturates so early:** the `[Fact]` wall **floors at ~9 s (dev) / ~11 s (container) from P=4
and never improves again** — 8x more contexts buys **zero** execution throughput. That floor is a
**serialization ceiling**, not a CPU limit: all N contexts share **one Chromium and one Playwright
Node driver**, and every CDP message funnels through that single driver process. Past P=4 the extra
contexts buy nothing and cost two things — linear pool-init time, and contention that inflates the
tail by **~40x** (p95: 33 ms to 1 289 ms). The per-spec work here is tiny (p50 **19 ms**), so the
driver round-trip *is* the workload.

## 7.3 `newrecruit-ui` (`nr-editor-ui-frozen`) — 112 specs

**32-core dev box:**

| P | **`dotnet test` wall** | `[Fact]` wall | p50 | p95 | peak browser+driver RSS |
|--:|--:|--:|--:|--:|--:|
| 1 | 245.62 s | 240.05 s | 1 340 ms | 4 967 ms | 824 MiB |
| 2 | 125.74 s | 119.59 s | 1 339 ms | 4 995 ms | 1 120 MiB |
| 4 | 66.86 s | 60.04 s | 1 324 ms | 4 914 ms | 2 389 MiB |
| 6 | 47.80 s | 40.25 s | 1 332 ms | 4 940 ms | 2 801 MiB |
| 8 | 37.63 s *(n=3)* | 30.40 s | 1 331 ms | 5 016 ms | 3 337 MiB |
| 12 | 30.22 s *(n=3)* | 22.84 s | 1 379 ms | 5 087 ms | 4 009 MiB |
| **16** | **28.81 s** * *(n=3)* | 21.35 s | 1 446 ms | 5 222 ms | 4 595 MiB |
| 24 | 29.39 s *(n=3)* | 20.10 s | 1 847 ms | 5 772 ms | 5 881 MiB |
| 32 | 30.36 s (worse) | 20.56 s | 2 242 ms | 6 478 ms | 7 077 MiB |
| 48 | 32.20 s (worse) | 20.96 s | 3 082 ms | 7 418 ms | 9 333 MiB |
| 64 | 36.99 s (worse) | 21.96 s | 3 987 ms | 8 671 ms | 11 566 MiB |

**4-CPU / 16 GiB container:**

| P | **`dotnet test` wall** | `[Fact]` wall | p50 | p95 |
|--:|--:|--:|--:|--:|
| 1 | 246.53 s | 241.17 s | 1 350 ms | 4 984 ms |
| 2 | 127.51 s | 122.30 s | 1 365 ms | 4 999 ms |
| 4 | 68.37 s | 62.69 s | 1 337 ms | 4 997 ms |
| 6 | 51.17 s | 45.52 s | 1 364 ms | 5 004 ms |
| 8 | 42.25 s | 36.30 s | 1 382 ms | 5 017 ms |
| 12 | 36.23 s *(n=3)* | 29.99 s | 1 590 ms | 5 253 ms |
| **16** | **34.22 s** * *(n=3)* | 27.47 s | 1 899 ms | 5 741 ms |
| 20 | 34.50 s *(n=3)* | 27.24 s | 2 277 ms | 6 609 ms |
| 24 | 35.45 s (worse) *(n=3)* | 27.83 s | 2 668 ms | 7 485 ms |
| 32 | 38.12 s (worse) | 29.48 s | 3 660 ms | 10 826 ms |
| 48 | 44.13 s (worse) | 33.23 s | 6 258 ms | 17 931 ms |

**The knee is at P=16 on BOTH boxes**, bracketed three-to-four levels past on each (dev: 24, 32, 48,
64 — ending 28% worse; container: 20, 24, 32, 48 — ending 29% worse). P=20 is inside noise of P=16;
P=24 upward is unambiguously worse.

This engine's specs are **~1.34 s each** and **latency-bound**, so oversubscription genuinely pays —
p50 stays flat (1 337 to 1 382 ms) all the way to **P=8 on a 4-CPU box, i.e. 2x oversubscribed**, and
the wall keeps falling to 16.

## 7.4 The headline: **the optimum does not depend on CPU count**

This is the finding, and it invalidates the *shape* of the model, not just its constants.

| engine | optimum, 32-core | optimum, 4-CPU | `pool / cpuCount` on 32-core | `pool / cpuCount` on 4-CPU |
|---|--:|--:|--:|--:|
| `newrecruit` | **4** (4–6 tie) | **4** | 0.125 | **1.0** |
| `newrecruit-ui` | **16** | **16** | 0.5 | **4.0** |

The optimal pool is **identical on both boxes**. Expressed as a *factor of `cpuCount`* the two
hardware classes disagree by **exactly 8x** — which is precisely the CPU ratio, i.e. the optimum did
**not move with CPU count at all**. A `ceil(cpuCount × k)` model cannot fit this: any `k` fitted on
one box is wrong by the full core-count ratio on the other.

**The cleanest single piece of evidence** — `newrecruit-ui` at P=1, the same 112 specs:

| | 32 logical CPUs | 4 CPUs | difference |
|---|--:|--:|--:|
| `[Fact]` wall | 240.05 s | 241.17 s | **+0.5%** |

An **8x reduction in CPU** produced a **0.5%** change in wall-clock. This workload is not CPU-bound in
any meaningful sense — it is bound by browser round-trip latency (`newrecruit-ui`) and by the shared
Playwright driver (`newrecruit`). **`cpuCount` is close to the wrong independent variable for this
axis.** The context-axis constant is an **absolute pool size per engine**, bounded by memory — not an
oversubscription factor.

For anyone who must have a "factor" to slot into today's model: on the CI runner it would be
`newrecruit` **k_ctx = 1.0** and `newrecruit-ui` **k_ctx = 4.0** — but they are *not* transferable to
another box, which is the whole point, and the honest transcription is a constant plus a memory bound.

## 7.5 What this costs CI today — and it explains the observed regression exactly

On the 4-vCPU / 16 GiB runner the shipped policy computes:

- `newrecruit`: `ceil(4 × 0.375) = 2`, memory bound 10 → **pool = 2**
- `newrecruit-ui`: `ceil(4 × 1.0) = 4`, memory bound 8 → **pool = 4**

Measured cost of those choices, on CI-class hardware:

| lane | pool | measured wall | vs measured optimum | vs retired `NR_PARALLEL=6` |
|---|--:|--:|--:|--:|
| `nr-frozen` | policy **2** | 19.81 s | **+11.6%** (opt. 4 = 17.75 s) | +7.1% (6 = 18.50 s) |
| `nr-editor-ui-frozen` | policy **4** | 68.37 s | **+99.8% — 2.0x slower** (opt. 16 = 34.22 s) | **+33.6%** (6 = 51.17 s) |

The `nr-editor-ui-frozen` lane at **+33.6% against the retired `NR_PARALLEL=6`** is, on its own, the
reported **~30% `nr-conformance` regression** (13.15 min vs 10.15 min). The measurement reproduces the
CI regression independently, and attributes it: **the policy under-provisioned the context pool.**

**Does the container's optimum agree with `NR_PARALLEL`'s historical 6?**

- `newrecruit`: **near enough.** Optimum 4; 6 measures 18.50 s vs 17.75 s — **4% off**. The old
  hand-tuned constant was close to right, and better than the policy's 2.
- `newrecruit-ui`: **no — 6 was far too LOW.** Optimum is **16**; 6 measures 51.17 s vs 34.22 s, i.e.
  the *old* value also left **50%** on the table. The policy then made it worse still. Nobody has ever
  run this lane near its optimum.

### What the caps do

`FixtureConcurrency.FixturePoolCap = 8` **does bind the dev box** (the policy asks 32/12 there, capped
to 8). On `newrecruit` that is harmless-to-helpful (8 → 18.75 s, vs 20.85 s uncapped at 12). On
`newrecruit-ui` it **costs 31%** (8 → 37.63 s vs 28.81 s at the optimal 16). Its docstring's claim
that it "can over-provision, not OOM" is true of *memory* and false of *time*.

## 7.6 Verdict safety — PASSED: every pool size, both engines, both boxes

This was the real risk on this axis: contexts share one browser, and NR-UI roster reuse has silently
changed six conformance verdicts in this project before. `bs-spec compare` **cannot test this** (no
pool on the CLI path), so the check is a direct **per-spec verdict-set diff** — the recorder captured
`pass` / `fail` / `xfail` / `pass-unexpected` / `skip` for **every spec by name**, and every pool size
was diffed against the **serial P=1 baseline**.

| box | engine | pools diffed vs P=1 | specs | result |
|---|---|---|--:|---|
| 32-core | `newrecruit` | 2, 4, 6, 8, 12, 16, 24, 32 | 365 | **IDENTICAL** |
| 32-core | `newrecruit-ui` | 2, 4, 6, 8, 12, 16, 24, 32, 48, 64 | 113 | **IDENTICAL** |
| 4-CPU | `newrecruit` | 2, 4, 6, 8, 12, 16, 24, 32 (x3 reps) | 365 | **IDENTICAL** |
| 4-CPU | `newrecruit-ui` | 2, 4, 6, 8, 12, 16, 20, 24, 32, 48 (x3 reps) | 113 | **IDENTICAL** |

**Zero divergent spec IDs anywhere**, up to 64 contexts (16x oversubscription on the dev box, and 12x
on a 4-CPU box). Every run: `newrecruit` 363 pass / 2 skip; `newrecruit-ui` 112 pass / 1 skip.
Contexts are genuinely isolated on both engines; **raising the pool to the measured optimum is
verdict-safe.**

## 7.7 Per-context memory — the slope

Sampled by walking the process tree **from the run's own root PID** (never by global process name —
the "Peak RSS per instance" note above explains why that distinction is load-bearing on this machine),
summing working set across the whole `testhost` -> Node driver -> Chromium tree. Fitted by least
squares over the pool sweep, so this is a **slope across many pool sizes**, not a two-point difference.

| box | engine | **per-context slope** | fixed baseline (P->0) | R² | pools fitted |
|---|---|--:|--:|--:|---|
| 32-core (Win) | `newrecruit` | **213.4 MiB / context** | 1 220 MiB | 0.9964 | 1...32 |
| 32-core (Win) | `newrecruit-ui` | **162.6 MiB / context** | 1 607 MiB | 0.9774 | 1...64 |
| 4-CPU (Linux) | `newrecruit` | **215.4 MiB / context** | 1 058 MiB | 0.9927 | 2...16 |
| 4-CPU (Linux) | `newrecruit-ui` | **224.9 MiB / context** | 1 310 MiB | 0.9834 | 2...16 |

`newrecruit`'s slope reproduces across OS and hardware to within **1%** (213.4 vs 215.4 MiB) — this
constant looks genuinely hardware-invariant, unlike `k`. Take **≈225 MiB/context** as the conservative
figure (the highest measured, on the Linux/CI class).

**A context is ~6-7x cheaper than a worker process**, which is exactly what the mirror gets wrong:

| | per **worker** (process axis, §3/§5) | per **context** (this axis) | over-charge factor |
|---|--:|--:|--:|
| `newrecruit` | 1 313 420 083 B (1.22 GiB) | ≈ 215 MiB | **5.8x** |
| `newrecruit-ui` | 1 548 969 984 B (1.44 GiB) | ≈ 225 MiB | **6.6x** |

Each context adds **exactly one Chromium renderer process** (measured: chrome procs = `P + 3`, the 3
being the shared browser main + GPU + network processes).

**Bounding the pool the way `MemPerInstanceBytes` bounds workers.** With a `MemPerContextBytes ≈
225 MiB` and the same `MemoryHeadroomFactor = 0.8`, a 16 GiB CI runner affords
`(16 × 0.8 − 1.3 GiB baseline) / 0.225 GiB ≈ 51` contexts. **Memory does not bind at the measured
optimum of 16** — confirmed directly: peak whole-container RSS at P=16 was **6.16 GiB**
(`newrecruit-ui`) and **6.70 GiB** (`newrecruit`) of 16 GiB. Pool 16 stays memory-safe down to an
~8 GiB box. The binding constraint on this axis is **contention, not memory** — the opposite of the
process axis.

## 7.8 What §7 did NOT reach

- ~~**No code was changed.**~~ **§8 changed it.** §7 was measurement only; the constants above are now
  transcribed into `EngineRegistry`, `ConcurrencyPolicy` no longer returns `PoolSize: workers`, and
  the model has gained the place to put a per-engine context constant that it lacked. See §8.
- **`nr-ui-frozen` and `nr-editor-frozen` — not swept.** The other two NR lanes run the same xUnit
  path and are presumably subject to the same effect, but were not measured. `battlescribe-ui` was not
  swept on this axis either.
- **Only the frozen (offline) suites.** Live-network lanes will have a different latency profile and
  therefore, on the reasoning in §7.4, plausibly a *different* absolute optimum.
- **The `[Fact]`-wall vs `dotnet test`-wall split is workload-dependent.** These suites are short
  (20–35 s at the optimum), so linear pool-init is a large fraction of the wall and it pulls the
  optimum *down*. A much longer suite would push the optimum toward the `[Fact]`-wall optimum (24+ for
  `newrecruit-ui`). The optima above are correct **for the suites CI actually runs**, which is what was
  asked.
- **Issue #314 (unbounded product across collections) is untouched** — §7 measures one pool at a time.

---

# 8. The axis separation — what §7 changed in the code (#314)

**Status: transcribed.** §7 measured the context axis and stopped. §8 is what was done with it. The
one-line summary: **`ConcurrencyPolicy.For` no longer computes `PoolSize` from `Workers`.** Each axis
now has its own declared facts on `EngineProfile`, its own bound in the policy, and its own tests.

## 8.1 The model, stated once

```
# PROCESS axis — CLI (`bs-spec run --all`): adapter processes. UNCHANGED.
workers = clamp(min(ceil(cpuCount × k_engine),
                    floor(availableMemory × 0.8 / memPerInstance_engine)), 1, MaxParallel)
#   ...and, ONLY while memPerInstance_engine == 0 (undeclared):
workers = min(workers, min(cpuCount, 8))          # UndeclaredMemoryWorkerCap

# CONTEXT axis — xUnit (`dotnet test`): browser contexts in a fixture pool. NEW.
pool    = clamp(min(contextPoolSize_engine,       # an ABSOLUTE measured count; 0 → 4 (default)
                    floor(availableMemory × 0.8 / memPerContext_engine)), 1, MaxParallel)
```

**Note what is absent from the second formula: `cpuCount`.** That is the finding of §7.4, not an
oversight — the optimum is *identical* on a 32-core box and a 4-CPU container, and `newrecruit-ui` at
pool=1 takes 240.05 s on 32 CPUs vs 241.17 s on 4 (an 8× CPU cut costs 0.5%). Contexts share one
Chromium and one Playwright driver; they contend on **that driver**, and no number of cores relieves
it. `ceil(cpuCount × k)` cannot express this and must not be reintroduced here.

Both axes share exactly two things — `MemoryHeadroomFactor = 0.8` and `MaxParallel` — because those
are properties of the *machine* and of the *engine's hard ceiling*, not of either axis.

## 8.2 The declared facts — two per axis

`EngineProfile` (`src/BattleScribeSpec.TestKit/Concurrency/EngineProfile.cs`), all four also settable
from `engines.json`, all four rejected at load if negative:

| field | axis | `newrecruit` | `newrecruit-ui` | `battlescribe-ui` | `battlescribe` |
|---|---|--:|--:|--:|--:|
| `MemPerInstanceBytes` | process | 1,313,420,083 | 1,548,969,984 | 1,055,391,744 | **0 (undeclared)** |
| `OversubscriptionFactor` | process | 0.375 | 1.0 | *(moot)* | *(moot)* |
| **`ContextPoolSize`** | **context** | **4** | **16** | 0 → `MaxParallel` clamps to 1 | **0 (undeclared)** |
| **`MemPerContextBytes`** | **context** | **225,863,270** (215.4 MiB) | **235,824,742** (224.9 MiB) | 0 | 0 |

The per-context figures are §7.7's least-squares slopes, taking the **larger (Linux/CI-class)** of the
two boxes in each case — the conservative direction, and CI is the machine that has to survive it.
They are **~6× smaller** than the per-process figures; charging a context at a process's rate is
exactly what the mirror did.

**`ConcurrencyPolicy.UndeclaredContextPoolSize = 4`** is the default for an engine that declares no
pool size. It is the *smaller* of the two measured optima: exactly right for one measured engine,
merely slower — never degraded — for the other, and memory-trivial (4 × 225 MiB ≈ 0.9 GiB). The
asymmetry justifies the low end: past the optimum this axis degrades hard (**+77%** at pool 32 for
`newrecruit`, six consecutive worsening levels), while below it you only leave throughput on the
table.

## 8.3 What each box now gets

| box | `newrecruit` W / **P** | `newrecruit-ui` W / **P** | `battlescribe-ui` W / **P** | `battlescribe` W / **P** |
|---|---|---|---|---|
| 4-vCPU / 16 GiB CI runner | 2 / **4** | 4 / **16** | 1 / **1** | 4 / **4** |
| 16-core / 16 GiB laptop | 6 / **4** | 8 / **16** | 1 / **1** | 8 / **4** |
| 32-core / 93.6 GiB dev box | 12 / **4** | 32 / **16** | 1 / **1** | 8 / **4** |

`W` = `Workers` (process axis, CLI). `P` = `PoolSize` (context axis, xUnit). **They differ on every
row for every browser engine, which is the entire point.** Memory binds the pool on none of these
boxes (a 16 GiB runner affords ≈58 contexts); it starts binding below ≈8 GiB.

## 8.4 What CI gets back

| CI lane | fixture → engine | `NR_PARALLEL` (historic) | mirrored policy (the regression) | **now** |
|---|---|--:|--:|--:|
| `nr-frozen` | `FrozenNrRosterFixture` → `newrecruit` | 6 | 2 | **4** ⭐ measured optimum |
| `nr-editor-ui-frozen` | `FrozenNrGameDataUiFixture` → `newrecruit-ui` | 6 | 4 | **16** ⭐ measured optimum |
| `nr-live-conformance` | `LiveNrRosterFixture` → `newrecruit` | 2 | 2 | **4** ⚠️ see below |

**CI should end up faster than `main`, not merely back at parity**, because `NR_PARALLEL: 6` was
*itself* far below `newrecruit-ui`'s true optimum of 16 — that lane has never once been run near it
(§7.5: 6 costs 50%; the mirrored 4 costs 100%).

⚠️ **`nr-live-conformance` 2 → 4 is a side-effect worth watching.** It shares the `newrecruit` engine
with `nr-frozen`, so it inherits the declared pool of 4. §7 swept only the **frozen** suites; a live
lane has a different latency profile, and 4 concurrent contexts is more traffic to a third-party
service than 2. It is still below the pre-policy code default for that fixture (5), and the sweep says
the shared Playwright driver saturates at 4 regardless — but it is the one number here that no
measurement covers.

## 8.5 `FixtureConcurrency.FixturePoolCap` — DELETED, not re-derived

It was `8`, and it was the wrong kind of constant: a round number defending an unmeasured path, with a
docstring claiming it "can over-provision, not OOM". True of memory, **false of time** — it was
capping `newrecruit-ui`'s measured optimum of 16 down to 8 and costing that lane **31%** on the dev
box (§7.5). A defensive bound that costs more than the thing it defends against is not a defence.

What replaces it is a real bound rather than nothing: the policy's own per-context memory bound
(`MemPerContextBytes` × `MemoryHeadroomFactor`) plus `MaxParallel`. Unlike a magic 8, it tightens on a
*small* box (where the risk is) instead of on a big one.

**It does not widen issue #314's composed-bound gap.** The three NR pools now ask for 4 + 4 + 16 =
**24** contexts across simultaneously-live collection fixtures — exactly what the 8-cap permitted
(8 + 8 + 8 = 24). A shared budget the pools draw from is still the real fix, and still #314's business.

## 8.6 The other mirror: `--policy workers=N`

`PolicyOverride` used to assign `PoolSize` from `workers=N` too ("which mirrors it"). It no longer
does: `workers=` sets the process axis and nothing else. There is deliberately **no `pool=` key** —
every command that parses `--policy` is a CLI command, the CLI path has no pool, and `PoolSize` is not
on the protocol wire, so a `pool=` flag would be accepted, forwarded, and inert. That is the
silently-dropped flag #305 forbids. (It is also why §7's campaign had to reach its axis with a
temporary env var: `--policy workers=` could not.)

## 8.7 The tests that would have caught the original bug

In `tests/Features/ConcurrencyPolicyTests.cs` unless noted. Each was verified to fail against a
deliberately reintroduced version of the defect (the mutant is named):

| test | mutant that makes it fail |
|---|---|
| `Policy_PoolSize_IsIndependentOfCpuCount` | restore `PoolSize: workers` — **the original bug**. Also fails on *any* `cpuCount` term in the pool computation: the shape is what was wrong, not just the constant |
| `Policy_TheTwoAxes_MoveIndependently_ProcessWithCpu_ContextNotAtAll` | restore the mirror (pool assertions), **or** delete the `cpuCount` term from the *worker* computation "for symmetry" (worker assertions) |
| `Policy_PoolSize_OnTheCiRunner_IsTheMeasuredOptimum` | the mirror (gives 2 / 4), or `NR_PARALLEL`'s old 6 |
| `Policy_PoolSize_IsBoundedByMemory_OnASmallMemoryBox` | drop the pool's memory bound (plans the full 16 on a 2 GiB box), or drop `MemoryHeadroomFactor` (plans 9 instead of 7) |
| `Policy_BattlescribeUi_StaysAtOneWorker_OnEveryProfile` | drop the `MaxParallel` clamp on the pool — it would take the undeclared default of 4 |
| `Policy_UndeclaredContextPoolSize_GetsTheConservativeDefault` | fall back to the worker count for an undeclared engine — the 64-core box would plan 64 contexts |
| `Policy_NegativeContextDeclarations_AreTreatedAsUndeclared` | gate the pool on `!= 0` instead of `> 0` |
| `EngineRegistryTests.NegativeContextAxisDeclarations_AreRejectedAtLoad` | delete either `Validate` check |
| `FixtureConcurrencyTests.PoolSizeFor_IsThePolicysAnswer_UnmodifiedByAnyFixtureLevelCap` | re-add any fixture-level cap that binds on the running machine (e.g. the old 8, against a plan of 16) |
| `PolicyOverrideTests.Workers_OverridesTheProcessAxisOnly_NotThePool` | restore `poolSize = parsedWorkers` |

## 8.8 What CI measured after the change

Run [29325721441](https://github.com/WarHub/battlescribe-spec/actions/runs/29325721441) (all 8 jobs
green) against the  baseline
[29239979347](https://github.com/WarHub/battlescribe-spec/actions/runs/29239979347) — the only
comparable sample (older runs predate the CI split #291).

**The pools applied, proven from telemetry** ( in the uploaded traces, on the real
runner):  = **4**,  = **16**.

| step (lane) | pool:  → now |  | now | Δ |
|---|---|--:|--:|--:|
| Run NR conformance tests () | 2 → **4** | 230 s | **145 s** | **−37%** |
| Full frozen NR Editor GameData UI () | 6 → **16** | 101 s | **95 s** | **−6%** |
| Full frozen NR roster () | 6 → **4** | 53 s | 54 s | +2% (flat) |

Job walls:  **10.15 → 8.27 min (−18.5%)**;  11.75 → 12.02 min
(**+2.3% — parity**, its −6 s win swamped by build/cache noise on a single sample).

⚠️ **'s −6% is far short of the −33% §7.5 predicts for 6 → 16.** The likely cause is
§7's own surprise #3: pool construction is **serial** and grows linearly with P, and per-context init on
a real GitHub runner is dearer than on the WSL VM that modelled it — so init eats most of the execution
gain. **The true runner optimum is plausibly below 16 (8–12).** 16 is not being nudged on one CI sample;
the fix is the thing this document has now said three times — **sweep the context axis on the runner
itself**, which the  telemetry has just been shown to support there.

## 8.9 What §8 did NOT reach

- **Verdict-safety was inherited, not re-run.** §7.6 diffed every pool size 1–64 against the serial
  baseline on both boxes and found **zero divergent specs** — that is what licenses raising the pools.
  §8 re-ran `nr-frozen` and `nr-editor-ui-frozen` at the new pools (4 / 16) on the dev box and both are
  green, but that is a check, not a new campaign.
- **`nr-live-conformance`'s pool of 4 is unmeasured** (§8.4).
- **`battlescribe` declares nothing on either axis** and takes both conservative defaults.
- **Issue #314's composed bound across collections is still open.** Unchanged by this work, and no
  longer masked by a cap that was costing more than it saved.
