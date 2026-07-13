# Harness Concurrency & Reuse Model (Spec 2 of 2)

**Status:** proposed
**Follows:** Spec 1 — *Harness telemetry* (`2026-07-12-harness-telemetry-design.md`), merged as #306. Its measurable inputs and its `bs-spec compare` verdict rail are what make this spec possible.
**Does not include:** expanding the `nr-ui-frozen` lane to its full spec set. That is gated on triaging 46 conformance failures, which is engine-conformance work, not harness work. Tracked separately (see *Explicitly out of scope*).

## Goal

Replace three mutually-unaware parallelism mechanisms and a scatter of environment variables with **one policy** that decides — from the environment and from what each engine declares about itself — how much concurrency to use and whether to reuse engines.

## The problem, stated precisely

### Three mechanisms, no shared budget

| Mechanism | Multiplies | Bounded by |
|---|---|---|
| `--workers N` (CLI path) | adapter **host processes** | registry `MaxParallel`, then the `describe` handshake |
| `NR_PARALLEL` (xUnit path) | **browser contexts** in an in-process pool | nothing — a raw environment variable |
| xUnit `parallelizeTestCollections: true` | **test collections** (11 of them) | **nothing** — `maxParallelThreads` is unset, so it defaults to CPU count |

They compose **multiplicatively and unbounded**. Several collection fixtures each own a browser-context pool, and one owns the BattleScribe JVM; they may all be alive at once with nothing capping the product. `MaxParallel = 1` on `battlescribe-ui` protects the CLI path only.

This is why the knobs feel arbitrary. They are not three dials on one system; they are three systems that do not know about each other.

### The knobs encode a policy nobody wrote down

`ServeCommand.BuildOptions` decides warm-reuse by **matching the engine's name as a string**:

```csharp
ReuseRosterEngineAcrossSetups = !reuseDisabled && name is "battlescribe-ui",
```

That is a policy expressed as a special case. It is also how NewRecruit-UI roster warm-reuse was once enabled on a plausible assumption and **silently changed six spec verdicts** while a stopwatch reported success.

### The two engines want opposite strategies

| | BattleScribe UI | NewRecruit |
|---|---|---|
| Cold start | JVM + JavaFX — **expensive** | Chromium — **~1.6 s, cheap** |
| Parallelism | **impossible** (`MaxParallel = 1`) | free — **3.8×** at 4 workers |
| Warm reuse | **2.20× / 1.79×**, verdicts identical | **0.92×** — worthless |

So "one uniform model" cannot mean one uniform behaviour. It must mean **one uniform way of deciding**.

## Architecture

```
        ConcurrencyPolicy.For(environment, engine, mode)
                          │
        ┌─────────────────┼─────────────────┐
        ▼                 ▼                 ▼
   CLI runner      in-process pools     xUnit runner
  (N host procs)   (browser contexts)  (maxParallelThreads)
```

One policy object, computed once per process. Three consumers instead of three independent multipliers.

**Inputs:** CPU count, available memory, whether we are in CI, the engine's declared capabilities, and the mode.
**Outputs:** worker count, pool sizes, `maxParallelThreads`, and reuse on/off per domain.

### Parallelism stays process-level

The runner realises N-way concurrency as **N adapter host processes**, as it does today. Contexts are cheaper than browsers, and the adapter protocol *could* now carry concurrent in-flight requests per host (`corrId` correlation makes it possible) — but process-level parallelism is **already measured to work** (3.8× at 4 workers), and no measurement shows the in-process alternative would beat it. Unifying on in-process concurrency is a plausible future optimisation, not a justified one. This spec unifies the **policy**, not the mechanism.

## Engines declare properties; the policy derives numbers

Extend the existing capability declaration (`EngineRegistry` + the `describe` handshake's `AdapterCapabilities`):

| Property | Meaning | Today |
|---|---|---|
| `MaxParallel` | hard ceiling on concurrent instances; 0 = unlimited | exists |
| `ColdStartCost` | `Cheap` \| `Expensive` — determines whether reuse can pay for itself | **new** (implicit, hardcoded) |
| `ReuseSafe` (per domain) | may this engine be reused across setups without changing verdicts | **new** (hardcoded by engine name) |

**`ReuseSafe` is earned, not asserted.** An engine may only claim it for a domain where `bs-spec compare` has demonstrated verdict-equality against a cold arm. The spec's own history is the argument: the one time this was claimed without evidence, six verdicts changed silently.

**The two reuse properties are not the same question, and the policy needs both:**

```
reuse enabled  ⟺  ReuseSafe(domain)  ∧  ColdStartCost == Expensive
```

`ReuseSafe` asks *"is it correct?"* — reusing NewRecruit-UI's roster engine was **not** (it changed verdicts). `ColdStartCost` asks *"is it worth anything?"* — reusing a NewRecruit browser is perfectly safe and buys **0.92×**, i.e. nothing, because a headless Chromium relaunch is cheap. Enabling reuse on a safe-but-cheap engine adds a warm-state failure mode for no gain, which is a bad trade even when it is a correct one.

## Two modes

### Deterministic (default)

A pure function of `(CPU count, memory, isCI, engine capabilities)`. The same box gets the same plan every time.

Reproducibility is not a nicety here. `compare` establishes verdict-equality by holding everything constant except one variable; a policy that wanders between runs makes that comparison meaningless.

So **`compare` pins both arms to deterministic mode by default** — the config under test is the only thing allowed to differ. The single exception is the adaptive-mode validation below, where *mode itself* is deliberately the variable under test.

### Adaptive ("burn")

A bounded controller that raises concurrency while the box has headroom, using the CPU/GC saturation signal the runtime instrumentation already emits (landed in Spec 1). Intended for CI, where wall-clock is the whole point and reproducibility is not.

Guards, all mandatory:

- never exceeds the engine's declared `MaxParallel`;
- never exceeds a hard resource ceiling derived from memory;
- **hysteresis** — it must not oscillate;
- backs off on thrash (rising GC pressure or rising per-spec latency);
- **must not change verdicts.**

That last guard is the one that makes adaptive mode safe to ship at all, and it is *mechanically checkable*:

```
bs-spec compare --config-a "mode=deterministic" --config-b "mode=adaptive"
```

must be verdict-identical. If burning the CPUs changes a single verdict, adaptive mode is broken and the rail from Spec 1 catches it automatically.

## The numbers are measured, not invented

The deterministic policy's constants come from a measurement campaign on **both** a real 4-vCPU CI runner **and** the development machine — because those two disagree violently, and the disagreement is not intuitive.

Measured on `nr-editor-ui-frozen`:

| Parallelism | 32-core dev box | 4-vCPU CI runner |
|---:|---:|---:|
| 2 | 122 s | 167 s |
| 4 | 65 s | 115 s |
| **6** | — | **96 s** ← best |
| 8 | 35 s | 93 s |
| 16 | **27 s** ← best | 91 s |

And on `nr-frozen`, the CI runner **degrades past 6**: 48 s at P=6, **75 s at P=16** — worse than P=2.

The local box says "keep scaling to 16." The runner says "you already lost." Extrapolating from local would have made a CI lane ~30 % slower while looking like an optimisation. **Any constant in this policy that was not measured on the hardware it runs on is a guess**, and this project has already paid for that mistake.

## Retiring the knobs

**Deleted, not defaulted:**

- `NR_PARALLEL`
- `BSSPEC_DISABLE_WARM_REUSE`

**Demoted to an explicit override** (for diagnosis, not for ordinary use):

- `--workers N`
- `--reuse` / `--no-reuse`

The test is simple: **if you have to set a flag to get good performance, the policy has failed.**

Afterwards, exactly one environment variable remains in the harness: `OTEL_EXPORTER_OTLP_ENDPOINT`. It survives precisely because it is *not ours* — it is an industry standard we honour, not a dial we invented.

## Bounding the xUnit path

`maxParallelThreads` (currently unset) and the fixture pool sizes both come from the policy. The unbounded multiplication of collections × pools × the JVM stops being accidental.

This is the one place the current system is not merely arbitrary but genuinely unbounded, and it is invisible today: the measured peak (`browser: 1`, `browser-context: 5` on `nr-frozen`) is low only because the lanes are narrow. The *ceiling* is CPU-count-wide and nothing enforces anything.

## CI re-scoping

- CI lanes run in **adaptive mode** — wall-clock is what CI is for.
- The present `NR_PARALLEL: 6` (measured optimal on a 4-vCPU runner) becomes a **fallback constant inside the policy**, not a value pasted into YAML in two places that nothing keeps in sync.
- Lane structure is revisited once adaptive mode's real speedup is measured — not before.

## How this is proven

Three gates, all of which exist today:

1. **`compare` verdict-equality on every policy change.** A configuration change that alters conformance results is not an optimisation; it is a regression.
2. **`compare deterministic vs adaptive` is verdict-identical**, or adaptive mode does not ship.
3. **Peak live resources stays within the declared budget** — now observable, with the caveat that any peak read from `harness.resource.count` is a **lower bound** (a spike shorter than the metric export interval falls between exports and is invisible). That caveat travels with the number wherever it is used to set a bound.

## Risks, stated plainly

**The adaptive controller is the only genuinely new machinery here, and controllers fail in ways formulas do not** — oscillation, thrash on a noisy shared runner, and a strong tendency to *look* like they are working while making things slower. It must demonstrate a measured win against the deterministic policy on a real runner. **If it does not, the correct outcome is to ship the deterministic policy alone and say so.** Shipping a controller because it is more interesting than a lookup table would be a failure of exactly the kind this project keeps catching.

**A policy is a single point of failure.** Today a bad `NR_PARALLEL` degrades one lane; a bad policy degrades everything. This is an acceptable trade — one place to be wrong is better than three places that are wrong inconsistently — but it raises the bar on the policy's tests.

## Explicitly out of scope

**Expanding `nr-ui-frozen` to its full spec set.** The lane currently runs **1 of 477** specs it can execute; a measured run passes 431 and fails 46. Those 46 cluster (`force` 11, `selection` 7, `modifier` 7) — the shape of genuine NewRecruit-UI conformance gaps, not flakes.

Resolving them is **engine-conformance triage**, not harness engineering: each is either our driver being wrong (fix it) or NewRecruit genuinely behaving differently (annotate as an expected failure and document it in `docs/nr-behavioral-differences.md`). Exclusion is not an option — the lane must eventually run everything and be green.

That work gates the lane expansion. This spec makes the expansion **affordable**; it does not perform it.
