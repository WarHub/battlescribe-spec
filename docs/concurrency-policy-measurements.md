# Concurrency policy measurements (Task 8) — and what Task 9 did with them

> ## ⚠ EVERY MEASUREMENT IN THIS DOCUMENT WAS TAKEN ON A LOCAL LANE.
>
> Every sweep below — both axes, both hardware classes — ran against a **frozen HAR file on local
> disk** or a **statically-served local site**. **None of them sent a single request to
> `newrecruit.eu`.** Their results therefore say nothing about `nr-live-conformance`, which drives a
> third party's production website, and whose pool is **not a throughput number**: it is a load limit
> (`ConcurrencyPolicy.ThirdPartyLiveLoadLimit = 2`), it is not swept, and it does not move up.
> Applying this document's numbers to that lane is exactly the defect **§9** exists to record and fix.

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
modified to run the §1–§6 campaign.**

> That sentence is scoped, and the scope matters: **later campaigns did patch source, and said so.**
> §7.1 adds a temporary `BSSPEC_CAMPAIGN_POOL` override inside `FixtureConcurrency` to sweep the
> context axis (there is no other way to vary a number the policy owns — which is the point of the
> policy), and §10.6 edits the constant before each build. Both are reverted; neither is in the tree.
> Read the blanket claim as belonging to §1–§6 only.

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

| CI lane | fixture → engine | was | **at §6** (the mirrored policy) | Δ |
|---|---|--:|--:|---|
| `nr-frozen` | `FrozenNrRosterFixture` → `newrecruit` | `NR_PARALLEL: 6` | **2** | ⚠️ **3× fewer** |
| `nr-editor-ui-frozen` | `FrozenNrGameDataUiFixture` → `newrecruit-ui` | `NR_PARALLEL: 6` | **4** | 1.5× fewer |
| `nr-live-conformance` | `LiveNrRosterFixture` → `newrecruit` | `NR_PARALLEL: 2` | **2** | **unchanged — but only by coincidence** |

> ⚠️ **THIS COLUMN IS HISTORY, NOT THE SHIPPED NUMBERS. Do not read a constant off it.** It records
> what the *mirrored* policy (`PoolSize: workers`) computed when §6 was written — which §7 measured and
> §8 replaced. **The shipped pools today are 4 / 16 / 2**; the §8.4 table is the current one. Every row
> here is wrong about the present, and the last one is wrong twice over — read its correction below.

> ⚠️ **CORRECTION (§9).** That last row's "unchanged" was true when written and stayed true only by
> **accident**: the mirrored policy happened to compute `ceil(4 × 0.375) = 2` for that lane, the same 2
> the deleted `NR_PARALLEL: 2` carried. The two numbers had nothing to do with each other. The `2` was
> a **deliberate load limit on a third party's live website** (`newrecruit.eu`); the `ceil(4 × 0.375)`
> was a process-axis constant fitted on a dev box. When §8 separated the axes, the coincidence broke
> and the live lane silently became **4** — a number fitted by sweeping a HAR file on local disk.
> **The constraint had been deleted along with the env var that carried it, and nothing noticed for
> two commits.** It now lives in `ConcurrencyPolicy.ThirdPartyLiveLoadLimit` and the lane is back at
> **2** — see §9, and do not read this table's last row as evidence that this lane is fine.

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

`FixtureConcurrency.FixturePoolCap = 8` **was** the interim guard: worst case ≤ 24 live contexts, no
lower than the old frozen defaults, and it did not bind on the 4-vCPU CI runner (sized 2–4), so the CI
table above was unchanged by it. It was a defensive bound over an unmeasured path — *not* a fitted
constant, and not a substitute for the real fix, which is a shared budget the pools draw from
(**issue #314**). Sizing the xUnit path honestly needs a sweep of `dotnet test`, which this campaign
never ran.

> **§7 has now run that sweep, and §8.5 DELETED that cap. `FixturePoolCap` DOES NOT EXIST** —
> `FixtureConcurrency` has no second cap of any kind. The paragraph above is history. Its framing —
> "the error is conservative, so it can only over-provision" — turned out to be **exactly backwards
> for CI**: on the 4-vCPU runner the policy *under*-provisioned the pool, at a **2.0× wall-clock
> regression** on `nr-editor-ui-frozen`. The cap also bound the dev box, where it cost the
> `newrecruit-ui` lane **31%**. Read §7 and §8.5 before you reach for a round number again.

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

`ConcurrencyPolicy.For` **returned** `PoolSize: workers` when this section was written — the *same
integer* for two consumers that share no mechanism. §8 removed that mirror: the two axes are computed
independently now, and this section is the measurement that proved they had to be. The list below is
why one integer could never have served both:

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

## 7.5 What this cost CI under the MIRRORED policy — and it explains the observed regression exactly

> ⚠️ **HISTORIC NUMBERS. Do not read a current constant off this section.** These are what the
> *mirrored* policy computed — i.e. the regression §8 fixed. **The shipped pools today are
> `newrecruit` → 4 and `newrecruit-ui` → 16** (their measured optima, transcribed into
> `EngineRegistry`), and the live lane is held at **2** by `ConcurrencyPolicy.ThirdPartyLiveLoadLimit`.

On the 4-vCPU / 16 GiB runner the mirrored policy computed:

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

### What the caps did (the cap is DELETED — §8.5)

`FixtureConcurrency.FixturePoolCap = 8` **bound the dev box** (the mirrored policy asked 32/12 there,
capped to 8). On `newrecruit` that was harmless-to-helpful (8 → 18.75 s, vs 20.85 s uncapped at 12). On
`newrecruit-ui` it **cost 31%** (8 → 37.63 s vs 28.81 s at the optimal 16). Its docstring's claim that
it "can over-provision, not OOM" was true of *memory* and false of *time* — which is why §8.5 deleted it
rather than re-deriving it. **There is no `FixturePoolCap` in the tree.**

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
225 MiB` and the same `MemoryHeadroomFactor = 0.8`, a **16 GiB container** affords
`(16 × 0.8 − 1.3 GiB baseline) / 0.225 GiB ≈ 51` contexts. **Memory does not bind at the measured
optimum of 16** — confirmed directly: peak whole-container RSS at P=16 was **6.16 GiB**
(`newrecruit-ui`) and **6.70 GiB** (`newrecruit`) **of 16 GiB**. The binding constraint on this axis
is **contention, not memory** — the opposite of the process axis.

> ⚠️ **Note what that arithmetic subtracts — and what the code did not.** The `− 1.3 GiB baseline`
> above is the *intercept* column of the table, and this paragraph charged it while
> `ConcurrencyPolicy` **did not**: it charged `N × slope` against `0.8 × total` and counted the
> intercept nowhere. **A marginal slope consumed as a total charge** — the ninth instance of this
> branch's signature defect, and the one place where the doc was right and the code was wrong.
> On the real runner (**2 vCPU / 7.8 GiB**, §11.6 — *not* the 16 GiB container this section was
> measured in) the un-charged bound authorised `floor(6.24 / 0.220) = 28` contexts, which really cost
> `1.28 + 28 × 0.220 = 7.4 GiB` on a 7.8 GiB box. It was inert only because the shipped pool (16) sat
> below it — but **§10.2 records 16 / 20 / 24 as statistically tied on that runner**, so a 24 is a
> change these very measurements invite, and 24 did not fit.
>
> **Fixed (#317).** `EngineProfile.MemPoolBaselineBytes` now carries the intercept — per engine, from
> the *same* CI-class fit as the slope it is spent with (`newrecruit` 1058 MiB, `newrecruit-ui`
> 1310 MiB; do not mix fits, a slope from one regression and an intercept from another is not a line)
> — and the bound is `floor((0.8 × total − baseline) / slope)`. On the real runner that is **22** for
> `newrecruit-ui`: **16 still does not bind** (the §10.2 sweep's answer stands, untouched) and **24 is
> refused**. `MemoryHeadroomFactor`'s 20% was never able to absorb the intercept — 1.56 GiB of margin
> against a 1.3 GiB baseline — and it now has exactly one job again: what lives *outside* the pool's
> process tree (OS, page cache, build servers ≈ 1.37 GiB observed on the container, against that
> 1.56 GiB), plus the fact that a sampled peak is a lower bound. A margin spent twice is not a margin.
>
> Still open, and still the honest follow-up: **measure the peak ON the runner.** Everything above is
> fitted on the container.

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
| **2-vCPU / 7.8 GiB — the REAL GitHub runner** (§11.6) | **1** / **4** | **2** / **16** | 1 / **1** | **2** / **4** |
| 4-vCPU / 16 GiB *container* (the local model of CI) | 2 / **4** | 4 / **16** | 1 / **1** | 4 / **4** |
| 16-core / 16 GiB laptop | 6 / **4** | 8 / **16** | 1 / **1** | 8 / **4** |
| 32-core / 93.6 GiB dev box | 12 / **4** | 32 / **16** | 1 / **1** | 8 / **4** |

`W` = `Workers` (process axis, CLI). `P` = `PoolSize` (context axis, xUnit). **They differ on every
row for every browser engine, which is the entire point.**

> ⚠️ **The row that used to head this table said "4-vCPU / 16 GiB CI runner". THAT IS NOT THE CI
> RUNNER.** It is the local container that was used to *model* CI. The GitHub runner has now been
> measured, from inside a CI job: **`nproc: 2`, `MemTotal: 7.8 GiB`** (§11.6). Every "CI" row in this
> document that says 4-vCPU/16 GiB describes the model, not the machine — and the two disagree by 2×
> on cores and 2× on memory, on both axes of a document whose every number is machine-relative.
> Memory binds the pool on none of these boxes (the container affords ≈58 contexts), but the margin
> on the *real* runner is far thinner than this document claims — read §11.6 before you rely on it.

## 8.4 What CI gets back

| CI lane | fixture → engine | `NR_PARALLEL` (historic) | mirrored policy (the regression) | **now** |
|---|---|--:|--:|--:|
| `nr-frozen` | `FrozenNrRosterFixture` → `newrecruit` | 6 | 2 | **4** ⭐ measured optimum |
| `nr-editor-ui-frozen` | `FrozenNrGameDataUiFixture` → `newrecruit-ui` | 6 | 4 | **16** ⭐ measured optimum |
| `nr-live-conformance` | `LiveNrRosterFixture` → `newrecruit` | 2 | 2 | ~~4~~ → **2** (§9) |

**CI should end up faster than `main`, not merely back at parity**, because `NR_PARALLEL: 6` was
*itself* far below `newrecruit-ui`'s true optimum of 16 — that lane has never once been run near it
(§7.5: 6 costs 50%; the mirrored 4 costs 100%).

> ⚠️ **`nr-live-conformance` 2 → 4 was a DEFECT, and §9 reverts it.** What §8 shipped read: *"a
> side-effect worth watching… it is the one number here that no measurement covers."* That was the
> right instinct and the wrong conclusion. The missing measurement was not an oversight to be filled
> in later — **the 2 was never a throughput figure at all.** It was a load limit on `newrecruit.eu`, a
> website we do not own, chosen deliberately by the same commit that swept the frozen lanes
> (`7e65836`) and *declined* to apply its own sweep result here. Inheriting `newrecruit`'s declared
> pool of 4 doubled the traffic this harness puts on a third party, on the strength of a sweep of a
> HAR file. The lane is back at **2**, and the limit now has a name:
> `ConcurrencyPolicy.ThirdPartyLiveLoadLimit`.

## 8.5 `FixtureConcurrency.FixturePoolCap` — DELETED, not re-derived

It was `8`, and it was the wrong kind of constant: a round number defending an unmeasured path, with a
docstring claiming it "can over-provision, not OOM". True of memory, **false of time** — it was
capping `newrecruit-ui`'s measured optimum of 16 down to 8 and costing that lane **31%** on the dev
box (§7.5). A defensive bound that costs more than the thing it defends against is not a defence.

What replaces it is a real bound rather than nothing: the policy's own per-context memory bound
(`MemPerContextBytes` × `MemoryHeadroomFactor`) plus `MaxContexts`, the context axis's **own** declared
ceiling. (It was `MaxParallel` — the *process* ceiling — until that was recognised as the same
cross-axis mistake in miniature; see `EngineProfile`.) Unlike a magic 8, it tightens on a *small* box
(where the risk is) instead of on a big one.

**It does not widen issue #314's composed-bound gap.** The three NR pools ask for **2 + 4 + 16 = 22**
contexts across simultaneously-live collection fixtures — `LiveNrRosterFixture` **2** (held at
`ThirdPartyLiveLoadLimit`, *not* `newrecruit`'s declared 4), `FrozenNrRosterFixture` **4**,
`FrozenNrGameDataUiFixture` **16** — against the 8-cap's 24 and the pre-policy defaults' 20. A shared
budget the *local* pools draw from is still the real fix, and still #314's business.

> The **live** composed bound is a different quantity with a different owner, and it is now enforced
> rather than reasoned about: every fixture that opens a session on a third party's site draws it from
> `LiveLoadBudget`, which holds at most `ThirdPartyLiveLoadLimit` sessions **per host**. It had to be:
> the pooled and the sequential live NR fixtures are selected by the same `Engine=LiveNrRoster` filter,
> so `-p:TestProfile=nr-live` put **2 + 1 = 3** concurrent sessions on `newrecruit.eu`. Do not merge the
> two bounds — conflating a courtesy limit on someone else's server with a throughput budget on our own
> is exactly how the 2 was lost the first time.

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

And the tests §9 adds, against the defect §8 itself introduced (each verified red against the named
mutant, then green):

| test | mutant that makes it fail |
|---|---|
| `Policy_LiveLane_IsHeldAtTheLoadLimit_AndDoesNotTrackTheFrozenContextPool` | raise `ThirdPartyLiveLoadLimit` to the frozen pool's 4 — **the regression that shipped**. Also fails if the limit is derived from `ContextPoolSize` by any formula |
| `Policy_LoadLimit_IsAPropertyOfTheRemoteService_NotOfTheEnginesDeclaredPool` | compute the limit from the engine (`pool / 2`, `min(pool, cpu)`, …): three engines declaring 4 / 16 / 64 would stop all giving 2 |
| `Policy_LoadLimit_DoesNotScaleWithCpuCountOrMemory` | put **any** machine term in the clamp (`ceil(cpu × k)`, `min(cpu, limit)`, a memory bound) — a bigger runner is not consent. Also fails if the clamp misses the *process* axis (a 32-core box would plan 12 live workers) |
| `Policy_LoadLimit_DoesNotTouchLocalLanes` | apply the clamp unconditionally — `nr-editor-ui-frozen`'s pool would collapse 16 → 2, the mirror-image mistake |
| `Policy_DefaultLoadTarget_IsLocal` | flip the default to `ThirdPartyLive` (throttles every local lane) |
| `FixtureConcurrencyTests.PoolSizeFor_TheLiveLane_IsTheThirdPartyLoadLimit_NotTheFrozenPool` | the same, on the **real machine** through the fixture's own code path; also fails if `FixtureConcurrency` stops forwarding the load target |
| `ConcurrencyConfigurationDriftTests.LiveFixture_DeclaresThirdPartyLive_SoTheLoadLimitApplies` | change `LiveNrRosterFixture` to `LoadTarget.Local` — **a policy nobody invokes is a policy nobody has.** Also fails if a *local* fixture declares `ThirdPartyLive` |

## 8.8 What CI measured after the change

Run [29325721441](https://github.com/WarHub/battlescribe-spec/actions/runs/29325721441) (all 8 jobs
green) against the `main` baseline
[29239979347](https://github.com/WarHub/battlescribe-spec/actions/runs/29239979347) — the only
comparable sample (older runs predate the CI split #291).

**The pools applied, proven from telemetry** (`harness.pool.size` in the uploaded traces, on the real
runner): `FrozenNrRosterFixture` = **4**, `FrozenNrGameDataUiFixture` = **16**. Green was not taken as
evidence that the change was live; the tag is.

| step (lane) | pool: `main` → now | `main` | now | Δ |
|---|---|--:|--:|--:|
| Run NR conformance tests (`nr-live-conformance`) | 2 → **4** | 230 s | **145 s** | **−37%** |
| Full frozen NR Editor GameData UI (`nr-editor-ui-frozen`) | 6 → **16** | 101 s | **95 s** | **−6%** |
| Full frozen NR roster (`nr-frozen`) | 6 → **4** | 53 s | 54 s | +2% (flat) |

Job walls: `nr-conformance` **10.15 → 8.27 min (−18.5%)**; `thorough-conformance` 11.75 → 12.02 min
(**+2.3% — parity**, its −6 s win swamped by build/cache noise on a single baseline sample);
`thorough-ui-bs` 8.03/6.87 → 7.68/7.10 min (untouched by this change).

⚠️ **`nr-editor-ui-frozen`'s −6% is far short of the −33% §7.5 predicts for 6 → 16.** The likely cause
is §7's own surprise #3: pool construction is **serial** and grows linearly with P, and per-context init
on a real GitHub runner is dearer than on the WSL VM that modelled it — so init eats most of the
execution gain. **The true runner optimum is plausibly below 16 (8–12), and the model box was not the
runner after all.** 16 is *not* being nudged on the strength of one CI sample: it is measured, and it is
strictly better on the real runner than both values it replaces (the mirrored 4 and the historic 6).
The fix is the thing this document has now said three times — **sweep the context axis on the runner
itself**, which the `harness.pool.size` telemetry has just been shown to support there.

> 🛑 **The hypothesis in the paragraph above is WRONG, and §10 is the sweep that proves it.** It is left
> standing rather than rewritten, because the *refusal to act on it* was the right call and the record of
> why should survive. The sweep it demanded has now run **on the runner**: **pool 8 is 5.6% SLOWER than
> 16 and pool 12 is 3.8% slower** — nothing at or below 12 beats 16, and the true runner optimum is a
> **plateau at 16–20–24**. The constants do not move.
>
> The *mechanism* guessed at here is real (per-context init is ≈0.32 s and serial — §10.4), it is simply
> far too small to move this optimum: the execution gain from 4 → 16 contexts is −24%, against +4 s of
> init. The reason CI saw −6% instead of −33% is that **the runner is ~2.7× slower than the WSL container
> that modelled it** (92.4 s vs 34.2 s at pool 16), which makes this workload more CPU-bound and
> oversubscription worth less: the real 6 → 16 gain there is ≈10%, and −6% was one noisy sample of it.
> **The model box got the optimum right and the speed wrong** — and only the optimum is what the constant
> encodes. See §10.

## 8.9 What §8 did NOT reach

- **Verdict-safety was inherited, not re-run.** §7.6 diffed every pool size 1–64 against the serial
  baseline on both boxes and found **zero divergent specs** — that is what licenses raising the pools.
  §8 re-ran `nr-frozen` and `nr-editor-ui-frozen` at the new pools (4 / 16) on the dev box and both are
  green, but that is a check, not a new campaign.
- ~~**`nr-live-conformance`'s pool of 4 is unmeasured** (§8.4).~~ **Worse than unmeasured — it was
  wrong.** §8 filed this as a gap to be closed by a future measurement. It could not be: the number it
  replaced was never a throughput figure. See **§9**, which reverts the lane to 2.
- **`battlescribe` declares nothing on either axis** and takes both conservative defaults.
- **Issue #314's composed bound across collections is still open.** Unchanged by this work, and no
  longer masked by a cap that was costing more than it saved.

---

# 9. The live lane is not a lane — it is someone else's website

> **§7 and §8 measured the FROZEN lanes. Their results DO NOT TRANSFER to the live lane, and the
> reason is not that the live lane is unmeasured. It is that the live lane's number answers a
> different question.**

## 9.1 What happened

`nr-frozen` and `nr-live-conformance` resolve the **same engine** — `"newrecruit"`, the same
`EngineProfile`, the same `ContextPoolSize: 4`. One replays a HAR file off local disk. The other
drives **`newrecruit.eu`**, a live production website run by other people. Nothing in
`MachineProfile`, and nothing in `EngineProfile`, could tell those two apart, so the policy gave them
the same pool. It had no way not to.

The live lane's pool had been **2** for its entire history, set deliberately:

> *"The live nr-conformance lane stays at 2 — it drives the real newrecruit.eu, so parallelism there
> is a load question, not a throughput one."*
> — commit `7e65836`, 2026-07-12

Read what that commit **is**: it is a *sweep result*. It raised the frozen lanes 4 → 6 on measured
evidence and, in the same breath, **refused to apply itself to the live lane.** The 2 is not an
unmeasured number awaiting a sweep. It is *deliberately* unmeasured, because the thing it bounds is
not ours to optimize.

Then:

1. The concurrency model deleted `NR_PARALLEL` — correctly; it was a second place to decide a question
   the policy owns. But the **constraint** the variable carried had nowhere in the model to live, and
   was deleted with it.
2. For one commit it survived **by coincidence**: the mirrored policy computed `ceil(4 × 0.375) = 2`.
   The same 2, for unrelated reasons.
3. §8 separated the axes (#314). The coincidence broke. The live lane took `newrecruit`'s declared
   `ContextPoolSize: 4` — **fitted by sweeping `nr-frozen` (HAR replay, no network) on a 4-CPU
   container. Nothing in that sweep touched newrecruit.eu.**
4. CI measured the result and reported it as a **win**: `nr-live-conformance` 230 s → 145 s, −37%
   (§8.8). It was not a win. It was 85 seconds of our wall-clock bought with a doubling of the traffic
   we put on a stranger's server.

**This was the first change to that lane's concurrency in the repo's history, and nobody chose it.**

## 9.2 The fix: `LoadTarget`, and a limit that no sweep may raise

`ConcurrencyPolicy.For(machine, engine, loadTarget)` — a third input, because the question "who is on
the other end?" is not answerable from the machine or the engine, and cannot be defaulted safely.

| | asks | answered by |
|---|---|---|
| `OversubscriptionFactor`, `ContextPoolSize`, `MemPerContextBytes`, `MemoryHeadroomFactor` | *how fast can **this machine** go?* | measurement (§1–§8) |
| **`ThirdPartyLiveLoadLimit = 2`** | *how hard may we hit **a stranger's server**?* | **judgment. Never a sweep.** |

`LoadTarget.ThirdPartyLive` clamps **both axes** to 2 — contexts *and* worker processes — because the
remote host feels requests in flight and cannot see how we spawned them. `LoadTarget.Local` (HAR
replay, static local site, in-process IKVM, desktop app) is inert: the frozen lanes keep their measured
optima of 4 and 16 exactly.

`FixtureConcurrency.PoolSizeFor(engine, loadTarget)` has **no default** for the load target. Every
fixture must state who it is talking to. That a fixture had no way to say so is precisely how the
courtesy limit got deleted.

**Why it is not a fourth quantity to reconcile with the other three.** This branch's entire history is
numbers that got conflated because they shared a name or a mirror (`PoolSize: workers`;
`maxParallelThreads` pinned to a memory cap). `ThirdPartyLiveLoadLimit` differs in **kind**, not in
value, from `ContextPoolSize`. There is no exchange rate between them, and the next person to see a 2
sitting next to a 4 **will** want to bring them into line — the constant's own doc comment stops them,
in the code, where they will be standing.

## 9.3 What it costs, stated plainly

The live lane goes **145 s → ≈230 s** (the §8.8 numbers, run backwards). We pay that out of our CI
budget rather than out of someone else's bandwidth. **If this lane needs to be faster, make it send
fewer requests — not more of them at once.**

Two things make the 2 load-bearing rather than decorative:

- **Nothing else bounds it.** `grep -rE 'retry|backoff|throttl|rate.?limit|429|Task.Delay|Thread.Sleep'
  src/BattleScribeSpec.NewRecruit/` → **zero hits.** No pause between specs, no retry, no backoff, no
  429 handling. The pool size is the only brake this harness has.
- **`SequentialLiveNrRosterFixture` was launching a second browser against newrecruit.eu that no test
  used.** All 363 of its specs skip unless `NR_SEQUENTIAL=true` (nothing sets it), but the lane's
  filter still selected them, so xUnit built the fixture and it eagerly called
  `NewRecruitRosterEngine.CreateAsync` — a separate browser from the pool's, loading a third party's
  site for zero benefit. It is now lazy: the engine is created on first *use*.

## 9.4 What §9 did NOT reach

- **The CLI path does not declare its load target. — CLOSED, see §9.5.** `bs-spec run --all` with
  `NR_ENGINE_URL` set made the child engine host go live (`HostEngineFactory`), and the parent that
  computed the plan never asked — so that path was bounded only by `Workers` (`ceil(cpuCount × 0.375)`:
  **12** worker processes on the dev box, each with its own browser, against newrecruit.eu).
  `ConcurrencyPolicy.For` *accepted* the answer and clamped both axes when given it; the CLI simply never
  passed it. The design change this called for — **the engine declares which service it talks to** — is
  what §9.5 does.
- **The 2 itself is not, and will not be, measured.** That is the point. A sweep can tell you how fast
  newrecruit.eu answers 8 concurrent sessions. It cannot tell you whether we are entitled to ask.
- **This does not explain issue #318** (the `nr-conformance` crash). The evidence there is n=2 and the
  exit code (0) argues *against* resource exhaustion. §9 fixes a design defect on its own merits;
  #318 stays open. What §9 *does* give #318 is the thing it was missing: `nr-conformance` now uploads
  its telemetry (`if: always()`), so the next crash on that lane leaves a trace behind. It was the only
  lane in `ci.yml` without that step.

## 9.5 The CLI path: the engine declares which service it drives

**Status: fixed.** §9.4's first bullet, closed. Nothing in this section is a measurement — it is the
mechanism that decides *which* of the measured numbers a run is entitled to.

**The defect.** `bs-spec run --all --engine newrecruit` resolves the same `EngineEntry` and the same
`EngineProfile` whether the child will replay `newrecruit.har` off local disk or drive `newrecruit.eu`.
The only thing that differs is `NR_ENGINE_URL`, which `HostEngineFactory` reads and the *parent* — the
process that computes the plan and spawns the workers — did not. So both got the same machine-width
answer: **`ceil(32 × 0.375)` = 12 adapter processes, each with its own browser**, at a volunteer-run
website. On `main` that same path was **serial** (`--workers`, default 1). The 12× was nobody's decision.

**Why it could not simply read the variable.** `ConcurrencyPolicy` is a pure function of
`(MachineProfile, EngineProfile, LoadTarget)` and may never string-match an engine name — and
"is `NR_ENGINE_URL` set?" is meaningless for `battlescribe`, an in-process IKVM engine with no network
code, which that rule would throttle in any shell that happened to export the variable. The fact belongs
to the **engine**, so the engine states it:

| Engine | Roster endpoint | GameData endpoint |
|---|---|---|
| `battlescribe`, `battlescribe-ui` | `OnThisMachine` | `OnThisMachine` |
| `newrecruit`, `newrecruit-ui` | `FromUrlVariable("NR_ENGINE_URL")` | `OnThisMachine` |
| any `exec:`/`dotnet:` adapter | **undeclared** | **undeclared** |

`EngineSelection.LoadTarget` (CLI, at engine-resolution time — before a process is spawned) turns that
declaration plus the environment *the child will see* into the `LoadTarget` it hands
`ConcurrencyPolicy.For`. Three steps, one decision-maker each; the policy still knows nothing about
engine names.

**Per-domain, and that is what preserves the win.** `HostEngineFactory`'s *gamedata* switch never reads
`NR_ENGINE_URL` — the NR gamedata engine is always a frozen static dir — so a gamedata run keeps its full
measured worker count even in a shell that has the variable exported for live roster work. Pinned by
`ConcurrencyConfigurationDriftTests.HostEngineFactory_LiveEndpointRoutes_AreDeclaredByTheRegistry`, which
compares the endpoint variables the factory actually *reads*, per domain, against what the registry
*declares*: a live route the policy cannot see now fails the build.

**Fail-safe: only positive evidence buys `Local`.** `Undeclared` is the enum's zero value; an unparseable
URL resolves live; loopback and `file:` resolve local. An adapter we did not write is held to the load
limit and takes the machine's full width back with one line of `engines.json` — `"endpoint": "local"` —
the same bargain as `memPerInstanceBytes`, on the axis that costs a stranger rather than this box.

**And the limit holds against `--policy`.** The override's *base* plan is now computed for the load target
(so `--policy reuse-roster=on`, which says nothing about workers, cannot resurrect 12 of them through an
untouched `Workers` field), and an override that would *raise* the limit on a live engine is **refused**,
not silently clamped (#305). `ConcurrencyPolicy.ClampToLoadTarget` is the backstop for any other path that
builds a plan: a ceiling that only holds when nobody pushes on it is not a ceiling.

**What a CLI run gets on the 32-core dev box:**

| Run | Workers | Why |
|---|---:|---|
| `run --all --engine newrecruit` (frozen) | **12** | `ceil(32 × 0.375)` — the measured optimum, §5. Never touches the network. |
| `run --all --engine newrecruit-ui` (frozen) | **32** | `ceil(32 × 1.0)` — §3. |
| either, with `NR_ENGINE_URL` set | **2** | `ThirdPartyLiveLoadLimit`. Not measured, and not ours to measure. |
| `--gamedata`, with `NR_ENGINE_URL` set | **12 / 32** | The gamedata engine does not read that variable. |
| any unknown `exec:` adapter | **2** | Undeclared ⇒ assumed live. Declare `"endpoint": "local"` to opt out. |

**`NR_ENGINE_URL` is not a retired knob.** The knobs the concurrency model deleted (`NR_PARALLEL`,
`BS_UI_KEEP_ALIVE`, `BSSPEC_DISABLE_WARM_REUSE` — pinned by
`ConcurrencyConfigurationDriftTests.RetiredEnvironmentKnobs_*`) were each a *second answer* to a question
the policy owns: how parallel, and whether to reuse. This one answers a question the policy cannot ask and
has no other source for: **which server**. It does not set the worker count; the worker count is derived
from it, exactly once, by the one policy. The gate stays green, and it would go red if this reintroduced a
knob.

---

# 10. The context axis, fitted ON A REAL GITHUB RUNNER

**Status: measured, on the hardware CI actually runs.** §7 fitted this axis on a 32-core dev box and
in a 4-CPU/16 GiB WSL container built to model the runner. §8.8 then looked at one CI sample, found
`nr-editor-ui-frozen` had improved only **−6%** where §7 predicted −33%, and wrote down a hypothesis:

> *"pool construction is serial and per-context init is dearer on a real GitHub runner than on the WSL
> VM, so the true runner optimum is plausibly below 16 (8–12), and the model box was not the runner
> after all."*

It then — correctly — refused to act on it. **§10 tested it. The hypothesis is REFUTED.**

**On the runner, pool 8 is 5.6% SLOWER than 16, and pool 12 is 3.8% slower** — both distinguishable
from 16 with 95% confidence, both losing in 5–6 of 6 paired blocks. Nothing at or below 12 beats 16.
**The constants do not move. `newrecruit-ui` stays 16; `newrecruit` stays 4.** The WSL container
modelled the runner well enough, and that is a result worth writing down.

The *mechanism* in the hypothesis was real — per-context init **is** dearer on the runner (§10.4) — but
it is nowhere near large enough to drag the optimum below 16. The step from "init is dearer" to "the
optimum must be 8–12" was the part that skipped the measurement.

## 10.1 Why the obvious design failed, and what replaced it

The brief's design — a workflow matrix over `pool × repetition`, one job per level, ≥3 reps — was run
first: **39 jobs, all green, verdicts identical everywhere.** It cannot rank the levels, and the reason
is worth recording because it will bite the next person.

**GitHub hands out CPU models at random.** Across those 39 jobs the fleet served **AMD EPYC 7763, EPYC
9V45, EPYC 9V74, Intel Xeon Platinum 8573C and Xeon 6973P-C**, and they are not the same speed. The
within-level spread came out at **17–27%** — as large as *every* between-level difference in the sweep.
Pool 24 happened to draw three 7763s (the slowest of them); pool 4 drew none. Averaging that away needs
far more repetitions than blocking it away needs jobs.

**The fix is a blocked design: one job = one runner = one BLOCK containing every level.** Runner speed
then becomes a per-block constant and cancels exactly whenever levels are compared *within* a block.
Each block runs a discarded warm-up first (so no level pays the block's cold-cache cost), then every
level, with browser/`testhost` survivors reaped and **verified zero** before each point.

| design | jobs | within-level spread | can it rank the levels? |
|---|--:|--:|---|
| cold matrix (pool × rep, one level per job) | 39 | **17–27%** | **no** — swamped by runner CPU |
| blocked (every level on one runner) | 12 | **2–9%** | yes |
| blocked + clean Latin square (tie-break) | 6 | residual sd **2.3%** | yes, and position-balanced |

The cold matrix is not wasted: each of its timed runs is the *first* run in a fresh job, which is
exactly how CI pays for the step, so it supplies the honest absolute numbers and the verdict-safety
sample. It simply cannot be used to rank levels.

### The design defect in the first blocked run — owned, not buried

Its ordering was "rotate the level list by the block index, and reverse it on even blocks". Over an
**even** number of levels that **preserves position parity**: every level landed in odd slots only, or
in even slots only (verified from the recorded positions). Since later slots run ~1–3% faster
(page-cache warmth), the one comparison that straddles the two classes — **16 (even slots) vs 20 (odd
slots)** — was confounded with warmth, and two ways of reading the same data disagreed about it by
exactly the size of that confound.

It did **not** touch the finding that matters: *within each parity class independently*, every level at
or below 12 loses to the 16/20 region. But the 16-vs-20 tie needed its own run, so it got one. **Five
levels — an ODD count — with rotation only is a proper cyclic Latin square**: every level visits every
position, and the warmth trend cancels exactly. Confirmed from the data: position effects in the
tie-break are ≤1.5%, and the model-based and raw paired readings now agree.

## 10.2 `newrecruit-ui` (`nr-editor-ui-frozen`) — the answer

**Tie-break: 6 blocks, clean Latin square, 112 specs.** The quantity is the **`dotnet test` invocation
wall** (a stopwatch around the invocation), never job wall-clock — §8.8 was right that a ~6 s delta
inside a ~12 min job is unmeasurable. Effects below have block (runner) and position (warmth) removed
by an additive fit; the raw paired counts are given beside them so nothing rests on the model alone.

| pool | median wall | vs 16 (block+position removed) | 95% CI | faster than 16 in |
|--:|--:|--:|--:|--:|
| 8 | 97.9 s | **+5.6%** | [+3.1, +8.2] | 1 of 6 blocks |
| 12 | 96.0 s | **+3.8%** | [+1.2, +6.4] | **0 of 6 blocks** |
| **16** | **92.4 s** | — *(reference)* | — | — |
| 20 | 91.2 s | −0.8% | [−3.4, +1.8] | 5 of 6 blocks |
| 24 | 93.5 s | +0.7% | [−1.9, +3.3] | 2 of 6 blocks |

**The bottom of the curve is a PLATEAU (16–20–24), not a point.** Those three are statistically
indistinguishable from one another. 8 and 12 are distinguishable — and they are **worse**.

**The null result, stated plainly: 20 is nominally 0.8–1.0% faster than 16, and that is inside the
noise (its CI includes zero). It is not crowned.** 16 stays. It is already the measured optimum on two
other hardware classes, it sits in the flat bottom of the runner's curve, and nudging a cross-machine
constant to chase 1% on one machine is precisely the habit this branch exists to end.

Bracketing, honestly: 16 is bracketed **below** by 12 (+3.8%) and 8 (+5.6%) — measurably worse, two
levels deep. **Above**, it is bracketed by a plateau rather than a cliff: 24 is +0.7% here, and +2.3%
in the first blocked run's (internally clean) even-parity class. §7 already showed 32/48/64 degrading
hard on both other boxes. **A minimum sitting in a flat basin is a real finding, not a failure to find
a knee** — and it is the reason the exact value in 16–24 matters so little.

The wider first blocked run (levels 4–24, 6 blocks) puts the small pools where they belong:

| pool | 4 | 6 | 8 | 10 | 12 | 16 | 20 | 24 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|
| vs 16 (fit) | +16.4% | +10.7% | +3.8% | +5.2% | +1.2% | — | −3.5% \* | +2.3% |

\* the parity-confounded figure — see §10.1. The clean tie-break puts 20 at **−0.8%**, inside noise.

## 10.3 `newrecruit` (`nr-frozen`) — 4 is confirmed, and the rise past it is real

6 blocks, levels 2–16, 365 specs. A noisier engine than `newrecruit-ui` (residual sd **7.4%** against
2.3%), so its confidence intervals are correspondingly wide — which is itself the finding.

| pool | median wall | vs 4 (block+position removed) | 95% CI | reading |
|--:|--:|--:|--:|---|
| 2 | 48.9 s | −1.5% | [−9.9, +6.9] | within noise of 4 |
| **4** | **50.5 s** | — *(reference)* | — | **shipped** |
| 6 | 49.5 s | +2.3% | [−6.1, +10.7] | within noise of 4 |
| 8 | 51.8 s | −2.2% | [−10.6, +6.2] | within noise of 4 |
| 12 | 59.0 s | **+19.9%** | [+11.5, +28.3] | **distinguishable — worse** |
| 16 | 64.7 s | **+23.6%** | [+15.2, +32.0] | **distinguishable — worse** |

**2, 4, 6 and 8 are all within noise of each other; 12 and 16 are far worse.** The shipped **4** sits in
the middle of the flat region with two levels of margin before the rise. It stays.

A raw paired read of the same data appeared to favour 2 by 3.4% (it "won" 6 of 6 blocks) — that was the
position-parity confound of §10.1, and the position-balanced fit dissolves it to −1.5% ± 8.4%, a tie.
Recorded because it is exactly the sort of 3% "win" that a careless sweep would have shipped.

## 10.4 The mechanism — per-context init, measured on the runner

The `dotnet test` wall decomposes cleanly here, because each suite is a single `[Fact]`: the TRX gives
the `[Fact]` duration (the `Parallel.ForEachAsync` execution alone), and **invocation − `[Fact]`** is
test-host startup plus the fixture's **serial** pool construction. Both were recorded at every point,
so the hypothesis under test is measured rather than inferred.

| | `newrecruit-ui` | `newrecruit` |
|---|---|---|
| init at the smallest pool swept | 13.5 s (pool 4) | 14.0 s (pool 2) |
| init at the largest pool swept | 19.9 s (pool 24) | 31.6 s (pool 16) |
| **per-context construction cost** | **≈0.32 s** | **≈1.26 s** |
| `[Fact]` wall, small pool → optimum | 98.7 s → 75.3 s (4 → 16) | 34.9 s → 29.6 s (2 → 8) |
| `[Fact]` wall past the optimum | 74.9 s at 20 **and** 24 — **floors** | 33.1 s at 16 — **degrades** |

**§8.8's hypothesis was half right, and the half it got right is not the half it acted on.** Per-context
init *is* real, *is* serial and *is* linear in pool size — and a `newrecruit` context is **4× dearer to
build** than a `newrecruit-ui` one (1.26 s vs 0.32 s: the roster engine installs HAR route interception
and waits for Pinia on a real SPA, while the editor engine loads static files). That difference is
precisely *why* `newrecruit` wants 4 contexts and `newrecruit-ui` wants 16 — §7's conclusion, now
confirmed on the machine that pays for it.

But for `newrecruit-ui` the execution gain up to 16 (98.7 s → 75.3 s, **−24%**) dwarfs the init it buys
(13.5 s → 17.5 s, **+4 s**). Init does not drag that optimum below 16. Only past ~20, where the `[Fact]`
wall floors at ~75 s, does the ≈0.32 s/context stop paying for itself — which is why the curve turns
flat rather than falling further.

### So why did CI see −6% where §7 predicted −33%?

Because **the runner is ~2.7× slower than the container that modelled it** — pool 16 measures **92.4 s**
here against **34.2 s** in the WSL container, on the same 112 specs — and on slower cores this workload
is *less* latency-bound and *more* CPU-bound, so oversubscription buys less. The real 6 → 16 gain on the
runner is **≈10–11%** (104.5 s → 92.9 s in the wide blocked run), not 33%. The observed −6% was one noisy
sample of a genuine ~10% effect, not the signature of a wrong constant.

**The WSL container reproduced the runner's optimum correctly and its speed badly.** Only the optimum is
what the constant encodes — which is why "model the runner with a container" turned out to be a sound
method even though its wall-clock predictions were not transferable. That distinction is the lesson.

## 10.5 Verdict safety on the runner — PASSED, with one flake reported in full

Every pool level, in every run, on the real runner:

| engine | levels swept on the runner | timed runs | verdicts |
|---|---|--:|---|
| `newrecruit` | 2, 4, 6, 8, 12, 16 | 51 | **363 passed / 2 skipped / 0 failed — identical at every level** |
| `newrecruit-ui` | 4, 6, 8, 10, 12, 16, 20, 24 | 102 | **112 passed / 1 skipped / 0 failed — identical at every level** |

The pool was **proven live at every single point**, two independent ways: the fixture's own reported
size (`Pool size: N contexts`, read back from the constructed pool) and the harness's OTel telemetry
(peak `harness.resource.count` for `browser-context` = N). A point whose lever was not connected
**aborted rather than reporting** — an unforced sweep is not data.

**One divergence in 153 runs, and it is not a conformance divergence:**

- **Spec `constraint/constraint-create-and-fields`** — `newrecruit-ui`, first blocked run, block 3,
  **pool 6**, last position in its block.
- **`Setup error: Navigation to editor failed: Timeout 30000ms exceeded`** — a Playwright page load that
  did not finish in 30 s. The spec's assertions never ran. **No spec produced a different answer.**
- **It is not pool-dependent.** It landed at **pool 6**, a *low*-contention level, while pools 20 and 24
  were clean in every block, and pool 6 was clean in the other five blocks. A contention effect appears
  at the top of the range, not the bottom.
- Rate: **1 in 153** runs (≈0.7%). Excluded from the timing curves (a 30 s timeout sits inside its wall)
  and reported here rather than averaged away.

**No conformance verdict changed at any pool level on the runner.** §7.6's result — pools 1–64, both
engines, both other hardware classes, zero divergent specs — now holds on the runner as well.

## 10.6 What §10 did NOT reach

- **`nr-ui-frozen` and `nr-editor-frozen` — still not swept**, on any hardware. They run the same xUnit
  path and are presumably subject to the same effect. Nobody has measured them.
- **The live lanes were deliberately not touched.** A concurrency sweep is not a thing to point at a
  third party's website, and after §9 the live lane's pool is `LoadTarget`'s business, not this axis's.
- **Above 24 was not swept on the runner.** The right-hand bracket here is a plateau (16–24); the
  32/48/64 degradation is dev-box and container evidence, not runner evidence.
- **One workload, one spec set.** As §7 said: the optimum is a property of the workload's duration
  profile as much as of the engine's.
- **The scaffolding is gone.** The sweep ran on a scratch branch (`perf/context-axis-ci-sweep`) whose
  workflow forced the pool by patching the constant before the build (cold matrix) or by a temporary
  env-var override read (blocked runs, so one build could serve every level). Both were deleted with the
  branch. The retired-knob lint gate is green, and nothing in this section is reachable from `main`.

---

# 11. Final review — the two ceilings that were not ceilings, and the limit that had one enforcer

> **Nothing measured here.** §11 changed no constant that any sweep fitted. It fixed three places where
> a number *crossed a boundary and changed meaning on the way* — the failure family this whole document
> is about — and one place where a bound was enforced against one caller out of five.

**11.1 `--config-a NR_ENGINE_URL=…` vs `--config-a nr_engine_url=…` — the load limit evaporated on a
lowercased letter.** The CLI parent looked the endpoint variable up in its own
`Dictionary<string, string>(StringComparer.Ordinal)`; the child reads it out of the environment the OS
hands it, which is **case-insensitive on Windows**. So the parent computed `LoadTarget.Local` and planned
`ceil(32 × 1.0) = 32` browsers while the child went **live** — 32 adapter processes at `newrecruit.eu`,
and the "held to 2 concurrent sessions" banner never printed. The fix is not a comparer swap (hard-coding
`OrdinalIgnoreCase` merely moves the bug to Linux, where the variable genuinely *is* a different one):
`AdapterProcess.ComposeChildEnvironment` composes the child's environment through
`ProcessStartInfo.Environment` — **the dictionary the OS itself defines** — and the load target is read
back out of *that*. One value, one source, no second implementation of "what does this variable name
mean" left to drift.

**11.2 `MaxParallel` was clamping the context axis.** It is a ceiling on adapter **processes** — on the
protocol wire, in `docs/adapter-guide.md`, applied by `RunBatch.ClampWorkers` to the worker count — and
`ConcurrencyPolicy` was also using it to clamp `PoolSize`. Justified as *"battlescribe-ui runs one JVM,
and that is as true of a context pool as of a worker process"*: true of that engine, and a
generalization of one engine's coincidence into a cross-axis rule. `{"maxParallel": 2,
"contextPoolSize": 4}` — "don't run more than 2 of my processes", exactly what the protocol says that
field means — silently halved a measured pool. **The context axis now has its own declared ceiling,
`MaxContexts`** (`engines.json`: `maxContexts`), and `battlescribe-ui` declares **both** 1s. Same shape
as `PoolSize: workers`, pointing the other way. *No number is shared between the axes any more.*

**11.3 The live load limit governed one fixture out of five.** `ThirdPartyLiveLoadLimit` calls itself
"the only thing standing between a 363-spec conformance run and a volunteer-run website"; four fixtures
opened sessions at `newrecruit.eu` / `giloushaker.github.io` without asking it anything, and the drift
gate **forbade** them from declaring `ThirdPartyLive` (it asserted `LiveNrRosterFixture` was the only
file allowed to). The gate was enforcing the gap. Concretely: `-p:TestProfile=nr-live` selects
`Engine=LiveNrRoster`, which is *both* the pooled collection (2 contexts) and the sequential one (1
engine), and xUnit runs collections in parallel ⇒ **3 concurrent sessions**, 50% over a limit this
document forbids raising by 1 for a measured speed-up. **`LiveLoadBudget`** now holds at most
`ThirdPartyLiveLoadLimit` sessions **per host** (two third parties are not one third party), every live
fixture draws from it, and the gate is a biconditional: a fixture reads an endpoint URL variable **iff**
it reserves from the budget.

**11.4 CI announced an in-process adapter as someone else's website.** `--engine
"battlescribe=dotnet:…/bs-reference-adapter.dll"` is a *launchable* connectable, and a launchable's
metadata was looked up in `engines.json` alone — a file this repo does not have. So it resolved to an
**undeclared** endpoint, the fail-safe fired, and every `checks` run printed *"Load target: third-party
live service — held to 2 concurrent sessions"* for an IKVM engine with **no network code at all**, at
half the width the runner affords. A launchable that claims a name we ship now inherits that engine's
*declaration* (never its verdict: `newrecruit=exec:…` still derives from `NR_ENGINE_URL` and still fails
safe). An adapter under an **unknown** name is still undeclared, and undeclared is still live.

**11.5 And the tests now run.** `tests/BattleScribeSpec.Cli.Tests` — which holds *every* gate on the
CLI's load target — had **never been executed by CI**: all fifteen `dotnet test` steps named the other
project. `EveryTestProject_IsRunBySomeCiStep` enumerates `tests/**/*.csproj` and requires each to appear
in a `dotnet test` command line. A gate nobody invokes is a gate nobody has, and that was true of the
gates protecting the number at the top of this document.

## 11.6 THE CI RUNNER IS NOT THE MACHINE THIS DOCUMENT DESCRIBES — measured

Every constant here is machine-relative (`ceil(cpuCount × k)`, `maxParallelThreads: "0.5x"`,
`MemoryHeadroomFactor × availableMemory`), this document calls CI "the 4-vCPU / 16 GiB CI runner"
throughout — and **nobody had ever asked the runner.** A `Runner profile` step now does, in the
`checks` job, on every run. First reading:

```
nproc:    2
MemTotal: 7.8 GiB
```

**2 cores, not 4. 7.8 GiB, not 16.** The "4-vCPU / 16 GiB" box is the *local container* that was used
to model CI (§7.4 says so plainly; §6d and §8.3 then relabelled it "CI runner", and that is the
conflation). What follows from the real numbers:

- **`bs-spec run --all` on the runner gets HALF the workers this document states**, on every engine:
  `newrecruit` **1** (not 2), `newrecruit-ui` **2** (not 4), `battlescribe` **2** (not 4).
- **`maxParallelThreads: "0.5x"` yields 1 thread there, not the 2**
  `ConcurrencyConfigurationDriftTests` states in its remarks. (Benign — 1 is a valid, conservative
  thread count — but the stated yield is wrong, and it is stated as a measurement.)
- **The context-pool memory margin is much thinner than §7.7 claims.** Pool 16 peaked at 6.16 GiB on
  the 16 GiB container: on a 7.8 GiB runner that is ~79% of *total* memory. The policy still computes
  16 (the memory bound gives ≈28), and `nr-editor-ui-frozen` does pass — but "memory does not bind"
  was established on a box with twice the RAM.

**How this was found — and it is the same lesson as everything else in §11.** It was not found by
reading. `tests/BattleScribeSpec.Cli.Tests` had never been executed by CI (§11.5); the first run that
executed it went **red**, on an assertion that hardcoded `ThirdPartyLiveLoadLimit` as an expected
worker count. The code was right — the limit is a *ceiling*, and on a 2-core box `ceil(2 × 0.375) = 1`
is already under it. The test had encoded the 32-core machine it was written on, and so, it turns out,
had the document.

**Nothing is re-tuned here on the strength of this.** Fitting constants against a freshly-inferred
machine profile is the exact sin this document exists to record. The measurement is now printed on
every CI run; re-deriving §6d/§7.5/§8.3's CI column against the real runner is the follow-up, and it
should be done by *sweeping the runner*, not by arithmetic.
