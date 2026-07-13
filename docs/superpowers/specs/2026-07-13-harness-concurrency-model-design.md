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
        ConcurrencyPolicy.For(machine, engine)
                          │
        ┌─────────────────┼─────────────────┐
        ▼                 ▼                 ▼
   CLI runner      in-process pools     xUnit runner
  (N host procs)   (browser contexts)  (maxParallelThreads)
```

One policy object, computed once per process. Three consumers instead of three independent multipliers.

**Inputs:** CPU count, available memory, and the engine's declared capabilities.
**Outputs:** worker count, pool sizes, `maxParallelThreads`, and reuse on/off per domain.

Note there is no `isCI` input. CI is not a mode — it is a **small machine**, and the policy already takes the machine as an input. Branching on "am I in CI" would be re-introducing exactly the kind of special case this spec exists to delete.

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

## One mode: deterministic, and as fast as the hardware allows

A pure function of `(CPU count, available memory, engine capabilities)`. The same box gets the same plan every time — and a bigger box gets a bigger plan.

**There is no adaptive/feedback mode.** An earlier draft proposed one (a controller that watches CPU saturation and raises concurrency mid-run). It is cut, deliberately. Controllers oscillate, thrash on noisy shared runners, and have a strong tendency to *look* like they are working while being slower; and there is no evidence a good controller would beat a well-fitted formula here. Building one would be choosing the more interesting mechanism over the justified one — the precise failure this project keeps catching. If a formula ever demonstrably leaves speed on the table, that is the moment to revisit, with the measurement in hand.

Reproducibility is also not a nicety: `compare` establishes verdict-equality by holding everything constant except one variable, and a policy that wanders between runs makes that comparison meaningless.

### The shape

```
workers = clamp(
    min( ceil(cpuCount × k_engine),                      // scales with the machine
         floor(availableMemory / memPerInstance_engine)  // a browser or JVM costs real RAM
    ),
    1,
    MaxParallel                                          // the engine's hard ceiling
)
```

- **`k_engine`** — the per-engine oversubscription factor. **Measured, per engine and domain.** It is *not* a global constant, because the engines demonstrably disagree (below).
- **`memPerInstance_engine`** — measured RSS per concurrent instance. Without this bound a 64-core box will happily launch 64 Chromium contexts and OOM long before it saturates CPU. CPU count alone is not a safe input.
- **`MaxParallel`** — declared by the engine; `1` for `battlescribe-ui`, which cannot be parallelised at all.

This satisfies the requirement directly: **more cores means more workers**, up to the point where memory or the engine's own ceiling binds.

## The numbers are measured, not invented — and the existing data is not yet sufficient

Two hard facts from what has already been measured, both of which constrain the campaign.

### Fact 1: local hardware and CI disagree violently

Measured on `nr-editor-ui-frozen`:

| Parallelism | 32-core dev box | 4-vCPU CI runner |
|---:|---:|---:|
| 2 | 122 s | 167 s |
| 4 | 65 s | 115 s |
| 6 | — | 96 s |
| 8 | 35 s | 93 s |
| 16 | 27 s | 91 s |

The local box says "keep scaling." Extrapolating from it to CI would have made a lane ~30 % **slower** while looking like an optimisation. Any constant not measured on the hardware it runs on is a guess.

### Fact 2: the optimum depends on the workload, not just the CPU count

On the **same** 4-vCPU runner:

| Parallelism | `nr-editor-ui-frozen` | `nr-frozen` |
|---:|---:|---:|
| 4 | 115 s | 58 s |
| **6** | 96 s | **48 s** ← best |
| 8 | 93 s | 59 s |
| 16 | **91 s** | **75 s** ← *worse than P=2* |

`nr-frozen` (short, fast store specs) **degrades hard** past 6 — contention dominates. `nr-editor-ui-frozen` (long, I/O-heavy UI specs) merely **plateaus**. A single `workers = f(cpu)` cannot express both behaviours. Hence `k_engine`, measured per engine and domain.

### The campaign must find the knee — the existing data does not

**The 32-core sweep stopped at 16 while still improving** (35 s at P=8 → 27 s at P=16). That is not an optimum; it is where the sweep ran out. The true optimum on that box may be 24, 32, or higher, and fitting `k` to "16" would bake in an artifact of the sampling rather than a property of the hardware.

So the campaign must, for each `(engine, domain)` and each hardware class:

1. **sweep parallelism until wall-clock stops improving or begins to degrade** — i.e. actually locate the knee, and keep going far enough to be sure it is one;
2. record peak RSS per concurrent instance (for `memPerInstance_engine`);
3. **assert verdict-equality across every level with `bs-spec compare`** — a parallelism level that changes conformance results is not a faster configuration, it is a broken one;
4. fit `k_engine` to the knee, and state the hardware it was measured on.

Hardware classes to measure: the 4-vCPU GitHub runner (what CI actually runs) and the development machine. If those two fit the same `k`, the formula is portable; if they do not, that itself is a finding and the policy needs a CPU-count-dependent `k`, which must be stated rather than smoothed over.

## The parent decides; the child is told

The reuse decision is currently made **in the child** (`ServeCommand.BuildOptions`, by string-matching the engine's name), and `compare` ablates it by setting `BSSPEC_DISABLE_WARM_REUSE=1` in the child's environment. That is how warm-reuse was proven verdict-neutral at 2.20×.

Deleting that variable without replacing the channel would therefore **break the rail that gates every change this spec makes** — a circular dependency, and a real one.

So:

- **The parent computes the policy.** It already knows the machine and the engine; it is the only place that can see the whole run.
- **The child is told the decision** via explicit `serve` arguments. `EngineHostLocator` already composes the child's command line (`serve --engine X [--headed] [--keep-alive]`); the policy's decisions join it there. The child stops guessing.
- **`compare` gets `--policy-a` / `--policy-b`** to override the policy per arm. These flow through `EngineSelection` into the serve arguments. `--config-a` / `--config-b` remain for genuine *environment* experiments — they are not the policy channel.

No environment variable is in the loop, and there is exactly one authoritative decision-maker.

### Two defects this exposes, fixed as part of it

**`MaxParallel` is declared twice and can silently disagree** — once in `EngineRegistry.Builtins` and again in `ServeCommand`'s `AdapterCapabilities` (by string-match). One declaration, consumed by both.

**`KeepAlive` and `ReuseRosterEngineAcrossSetups` already contradict each other.** `HostEngineFactory` sets `KeepAlive = keepAlive || !rosterReuseDisabled`, so for `battlescribe-ui` roster the app is kept alive **regardless of what `ServeCommand`'s reuse flag decided**. Two mechanisms, one intent, currently disagreeing — which is this spec's thesis in miniature.

## Retiring the knobs

**Deleted, not defaulted:**

- `NR_PARALLEL` — the pools' size comes from the policy.
- `BSSPEC_DISABLE_WARM_REUSE` — replaced by `compare --policy-a/--policy-b`, which is a *better* ablation channel: it can vary any policy decision, not just reuse.

**Deleted, because they are the same concept under two names:**

- `--workers N` — it is one policy key wearing its own flag.
- `--keep-alive` — "keep the app alive between specs" *is* reuse. Two names, one idea, and in the new model `KeepAlive` literally means "the plan says reuse this engine."

### One flag, one vocabulary, everywhere

| Command | Perf/reuse | Other |
|---|---|---|
| `run` | `--policy k=v,...` | `--headed` |
| `compare` | `--policy-a` / `--policy-b` | `--config-a` / `--config-b` |
| `serve` | `--policy k=v,...` | `--headed` |

Keys: `workers=N`, `reuse=on|off`, `reuse-roster=on|off`, `reuse-gamedata=on|off`. **One parser, shared by all three commands.**

`--headed` stays a separate flag because it is *presentation*, not performance — a different axis entirely. `--config-*` stays because it is *environment*, not policy.

The test is simple: **if you have to set a flag to get good performance, the policy has failed.** These overrides exist for diagnosis, not for operating.

### Flags must be accepted or rejected — never silently dropped

Today `EngineHostLocator` **silently drops** `--headed` and `--keep-alive` for launchable (`exec:`/`dotnet:`) adapters (#305). A flag that quietly does nothing is worse than one that errors: the user believes they configured something, and they did not.

Two rules, and the distinction between them matters:

- **A capability mismatch is an error.** `--headed` against an engine with no UI, or against an adapter that cannot receive it, is a *mistake*. Fail loudly, naming what the engine does support.
- **A policy override is allowed, and warned.** Forcing `reuse=on` on an engine not declared reuse-safe is precisely the ablation `compare` needs in order to *prove* reuse-safety. That is what an override is for. But it warns: *"forcing reuse on an engine not declared reuse-safe; verdicts may change — use `bs-spec compare` to check."*

Afterwards, exactly one environment variable remains in the harness: `OTEL_EXPORTER_OTLP_ENDPOINT`. It survives precisely because it is *not ours* — it is an industry standard we honour, not a dial we invented.

## Bounding the xUnit path

`maxParallelThreads` (currently unset) and the fixture pool sizes both come from the policy. The unbounded multiplication of collections × pools × the JVM stops being accidental.

This is the one place the current system is not merely arbitrary but genuinely unbounded, and it is invisible today: the measured peak (`browser: 1`, `browser-context: 5` on `nr-frozen`) is low only because the lanes are narrow. The *ceiling* is CPU-count-wide and nothing enforces anything.

## CI re-scoping

- CI lanes get their parallelism from the policy, like everything else. The 4-vCPU runner is simply one hardware class the policy is fitted to — CI is not a special mode, it is a small machine.
- The present `NR_PARALLEL: 6` (measured optimal on a 4-vCPU runner) becomes a **fallback constant inside the policy**, not a value pasted into YAML in two places that nothing keeps in sync.
- Lane structure is revisited once the fitted policy's real speedup is measured on the runner — not before.

## How this is proven

Three gates, all of which exist today:

1. **`compare` verdict-equality on every policy change.** A configuration change that alters conformance results is not an optimisation; it is a regression.
2. **Verdict-equality holds across every parallelism level swept in the campaign.** A level that changes conformance results is not a faster configuration; it is a broken one, and it is disqualified regardless of its wall-clock.
3. **Peak live resources stays within the declared budget** — now observable, with the caveat that any peak read from `harness.resource.count` is a **lower bound** (a spike shorter than the metric export interval falls between exports and is invisible). That caveat travels with the number wherever it is used to set a bound.

## Risks, stated plainly

**The policy is a single point of failure — deliberately.** Today a bad `NR_PARALLEL` degrades one lane and a bad `--workers` default degrades another, independently and inconsistently. After this, one policy governs everything: get it wrong and everything is wrong. That is the *point*. A single place to be wrong is a single place to measure, fix and tune — which is exactly what the current three-mechanism scatter denies us. The trade is accepted knowingly; it raises the bar on the policy's own tests, and it is the reason verdict-equality gates every change to it.

**The real risk is fitting `k` to bad data.** The formula is only as good as the campaign behind it, and the existing sweep already contains the failure mode: it stopped at P=16 while still improving, so "16" is an artifact of where I stopped measuring, not a knee. A `k` fitted to that number would be a guess wearing a measurement's clothes. The campaign's job is to *find* the knee, and to keep going far enough past it to be sure it is one.

## Explicitly out of scope

**Expanding `nr-ui-frozen` to its full spec set.** The lane currently runs **1 of 477** specs it can execute; a measured run passes 431 and fails 46. Those 46 cluster (`force` 11, `selection` 7, `modifier` 7) — the shape of genuine NewRecruit-UI conformance gaps, not flakes.

Resolving them is **engine-conformance triage**, not harness engineering: each is either our driver being wrong (fix it) or NewRecruit genuinely behaving differently (annotate as an expected failure and document it in `docs/nr-behavioral-differences.md`). Exclusion is not an option — the lane must eventually run everything and be green.

That work gates the lane expansion. This spec makes the expansion **affordable**; it does not perform it.
