# Harness Concurrency & Reuse Model — Implementation Plan

> ⚠️ **AS SHIPPED, this is not what happened — and the difference is load-bearing.** This is the plan
> as written, kept as the record of the intent. Three of its statements are false about the code:
>
> 1. **`ConcurrencyPlan.MaxParallelThreads` does not exist.** The policy does *not* output xUnit's
>    thread count and cannot: the runner reads `xunit.runner.json` **before any of our code executes**.
>    The field shipped with **zero consumers** while its doc comment claimed it governed xUnit — the
>    very "decoration dressed as control" this plan exists to delete — and it was removed. The value is
>    declared statically as `"0.5x"` (a machine-relative multiplier) and pinned by
>    `ConcurrencyConfigurationDriftTests`, which carries its own justification. See
>    `ConcurrencyPolicy`'s remarks for the VSTest `RunSettings` alternative, investigated and rejected.
> 2. **`ConcurrencyPolicy.For` takes THREE inputs, not two:** `(MachineProfile, EngineProfile,
>    LoadTarget)`. The third is not a performance parameter — it is *whose machine pays for the
>    traffic*. Its absence is exactly how a courtesy limit on a third party's website got replaced by a
>    constant fitted against a HAR file. See `ConcurrencyPolicy.ThirdPartyLiveLoadLimit`.
> 3. **`Workers` and `PoolSize` are two axes, measured separately, sharing no number.** The plan speaks
>    of "worker count, pool sizes" as one derivation; feeding one integer to both (`PoolSize: workers`)
>    was the defect (#314). `docs/concurrency-policy-measurements.md` §7–§8 is the measurement.
>
> **The authoritative record of what shipped is `docs/concurrency-policy-measurements.md` plus the
> XML docs on `ConcurrencyPolicy` / `EngineProfile` / `ConcurrencyPlan`.** Read a constant off those,
> never off this file.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace three mutually-unaware parallelism mechanisms and two environment variables with one `ConcurrencyPolicy` that derives every concurrency and reuse decision from the machine plus what each engine declares about itself.

**Architecture:** Engines declare properties (`MaxParallel`, `ColdStartCost`, `ReuseSafe` per domain) in **one** place. A pure `ConcurrencyPolicy.For(machine, engine)` derives worker count, pool sizes, `maxParallelThreads` and reuse-on/off. The **parent** computes it and tells the child via explicit `serve` arguments. `compare --policy-a/--policy-b` overrides it per arm, preserving the verdict-equality rail.

**Tech Stack:** .NET 10, xUnit v3, `System.CommandLine`, the telemetry from Spec 1 (`ResourceMetrics`, `TraceSummary`, `bs-spec compare`).

**Spec:** `docs/superpowers/specs/2026-07-13-harness-concurrency-model-design.md`

## The three phases — read this before starting

**You cannot fit `k_engine` until the plumbing exists to sweep it.** So:

| Phase | Tasks | Property |
|---|---|---|
| **1. Plumbing** | 1–7 | Behaviour-**identical** to today. The policy exists and everything consumes it, but its constants reproduce current behaviour exactly. Provable with `compare`. |
| **2. Campaign** | 8 | Sweep parallelism to the knee, on both hardware classes, asserting verdict-equality at every level. Produces numbers. |
| **3. Fit** | 9 | Change literals only, with the measurement attached. |

This isolates every ounce of risk into Task 9. If the numbers are wrong you revert three constants, not an architecture.

**The plan does not contain a value for `k_engine`.** Inventing one would be precisely the sin this spec exists to stop. Task 8 measures it; Task 9 writes it down.

## Global Constraints

Bind **every** task. Violating one fails the build or the review.

- **TFM `net10.0`**; `Nullable=enable`; `TreatWarningsAsErrors=true`; `EnforceCodeStyleInBuild=true`; `AnalysisLevel=latest-recommended`; `GenerateDocumentationFile=true`. **An analyzer warning is a build error.** CA1305 (`IFormatProvider`), CA2007, CA1852 have all bitten this repo.
- **`IsAotCompatible=true` on `Cli`, `TestKit`, and `Telemetry`.** These run trim/AOT analyzers. The OpenTelemetry **SDK** must never be referenced from them; it lives only in `Telemetry.Collector` and `bs-engine-host`. `ConcurrencyPolicy` goes in **TestKit** and must therefore be **BCL-only and AOT-clean**.
- **xUnit1051:** async APIs taking a `CancellationToken` must be passed `TestContext.Current.CancellationToken`.
- **Develop on Windows, ship to Linux.** Three platform-assumption bugs reached CI in the last PR (process-exit reaping; `File.Delete`-throws-when-open; `OrdinalIgnoreCase` on paths). **Anything touching processes, files, timing or paths gets a Linux container run before the push** — `podman` + `mcr.microsoft.com/dotnet/sdk:10.0` works (not the `-preview` tag). Local green means little for these.
- **No new packages. No suppressions** — fix causes.
- **Telemetry must never fail a run**, change a verdict, or add material wall-clock.
- **Every behaviour change must pass `bs-spec compare` verdict-neutral.** A configuration change that alters conformance results is not an optimisation; it is a regression.
- Build gotcha: a spurious DLL file-lock from an orphaned `bs-engine-host`/`java` process fails a build; re-run once before investigating.
- Baseline on `main` (`56b6eca`): `core` = Cli **92** + Tests **1862**. `pre-push` green.

## File Structure

**New:**

| File | Responsibility |
|---|---|
| `src/BattleScribeSpec.TestKit/Concurrency/MachineProfile.cs` | The machine: CPU count, available memory. One place that reads the environment. |
| `src/BattleScribeSpec.TestKit/Concurrency/EngineProfile.cs` | What an engine declares: `MaxParallel`, `ColdStartCost`, `ReuseSafe` per domain. |
| `src/BattleScribeSpec.TestKit/Concurrency/ConcurrencyPolicy.cs` | The pure function. `For(machine, engine)` → `ConcurrencyPlan`. **No I/O. No env vars.** |
| `src/BattleScribeSpec.TestKit/Concurrency/ConcurrencyPlan.cs` | The decision: `Workers`, `PoolSize`, `MaxParallelThreads`, `ReuseRoster`, `ReuseGameData`. |
| `tests/Features/ConcurrencyPolicyTests.cs` | The policy is pure — test it like one. |

**Modified:** `EngineRegistry.cs` (single `MaxParallel` declaration + the new properties), `ServeCommand.cs` (stop string-matching; take the decision as args), `HostEngineFactory.cs` (fix the `KeepAlive` contradiction), `EngineHostLocator.cs` (compose the policy args), `EngineSpec.cs` (`EngineSelection` carries the plan), `RunBatch.cs`, `RunCommand.cs`, `CompareCommand.cs` (`--policy-a/-b`), the three NR fixtures, both `xunit.runner.json`, `.github/workflows/ci.yml`, `README.md`, `docs/warm-reuse.md`.

**Deleted:** every read of `NR_PARALLEL` and `BSSPEC_DISABLE_WARM_REUSE`.

---

### Task 1: `MachineProfile` — one place that reads the machine

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Concurrency/MachineProfile.cs`
- Test: `tests/Features/ConcurrencyPolicyTests.cs`

**Interfaces:**
- Produces: `MachineProfile` (record) with `int CpuCount`, `long AvailableMemoryBytes`; and `MachineProfile.Current()` which reads the real machine.

- [ ] **Step 1: Write the failing test**

```csharp
using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class ConcurrencyPolicyTests
{
    [Fact]
    public void MachineProfile_Current_ReportsARealMachine()
    {
        var machine = MachineProfile.Current();

        Assert.True(machine.CpuCount >= 1, "a machine has at least one CPU");
        Assert.True(machine.AvailableMemoryBytes > 0, "a machine has some memory");
    }

    [Fact]
    public void MachineProfile_IsAValue_SoAPolicyCanBeTestedWithoutTheRealMachine()
    {
        // The whole point: the policy is a pure function of this, so tests can hand it
        // a 4-vCPU CI runner or a 64-core box without owning either.
        var ci = new MachineProfile(CpuCount: 4, AvailableMemoryBytes: 16L * 1024 * 1024 * 1024);
        var big = new MachineProfile(CpuCount: 64, AvailableMemoryBytes: 256L * 1024 * 1024 * 1024);

        Assert.NotEqual(ci, big);
    }
}
```

- [ ] **Step 2: Run it — it must fail to compile**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~ConcurrencyPolicyTests"
```
Expected: `CS0246: The type or namespace name 'Concurrency' could not be found`.

- [ ] **Step 3: Implement**

```csharp
namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The machine a run is happening on. The <b>only</b> place the harness reads the environment
/// to decide concurrency — everything downstream is a pure function of this value.
/// </summary>
/// <remarks>
/// Being a plain value (not a static reader) is the point: a test can hand the policy a 4-vCPU
/// CI runner or a 64-core workstation without owning either. The two disagree violently about
/// optimal parallelism, and the policy must be testable against both.
/// </remarks>
/// <param name="CpuCount">Logical processors available to this process.</param>
/// <param name="AvailableMemoryBytes">
/// Memory the process may reasonably use. Load-bearing: CPU count alone is not a safe input,
/// because a 64-core box will happily launch 64 Chromium contexts and exhaust memory long
/// before it saturates CPU.
/// </param>
public sealed record MachineProfile(int CpuCount, long AvailableMemoryBytes)
{
    /// <summary>Read the real machine.</summary>
    public static MachineProfile Current()
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new MachineProfile(
            CpuCount: Environment.ProcessorCount,
            AvailableMemoryBytes: memory > 0 ? memory : 4L * 1024 * 1024 * 1024);
    }
}
```

> **Implementer note:** `Environment.ProcessorCount` on .NET honours container CPU limits (cgroup quotas) — **verified**: a podman container limited to 2/4/8 vCPU reports 2/4/8 on a 32-core host.
>
> **Correction to an earlier framing of this plan:** that verification is *useful* but was **not** the load-bearing risk it was billed as. This repo's CI runs every job on bare `runs-on: ubuntu-latest` — plain VMs, no `container:`, no `--cpus`, no cgroup quota to leak through. On a bare VM `ProcessorCount` reports the VM's own cores correctly and trivially. The cgroup behaviour matters only for the repo's `bs-spec.Dockerfile`, for developers sandboxing locally, and if CI ever moves to containerised runners. Recorded because a confident-sounding risk that turns out not to exist is worth deleting explicitly, not quietly.

- [ ] **Step 4: Run — both tests pass**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(concurrency): MachineProfile — one place that reads the machine (#271)"
```

---

### Task 2: `EngineProfile` — what an engine declares about itself

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Concurrency/EngineProfile.cs`
- Modify: `src/BattleScribeSpec.TestKit/Engines/EngineRegistry.cs`
- Test: `tests/Features/ConcurrencyPolicyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum ColdStartCost { Cheap, Expensive }`; `EngineProfile` (record) with `int MaxParallel`, `ColdStartCost ColdStartCost`, `bool ReuseSafeRoster`, `bool ReuseSafeGameData`, `long MemPerInstanceBytes`; and `EngineEntry.Profile` exposing it.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void EngineProfiles_EncodeWhatWasMeasured_NotWhatWasAssumed()
    {
        var registry = EngineRegistry.LoadDefault();

        // battlescribe-ui: expensive cold start (JVM + JavaFX), cannot be parallelized,
        // and reuse is verdict-neutral in BOTH domains (measured: 2.20x gamedata / 1.79x roster).
        var bsUi = registry.Resolve(EngineConnectable.Parse("battlescribe-ui")).Profile;
        Assert.Equal(1, bsUi.MaxParallel);
        Assert.Equal(ColdStartCost.Expensive, bsUi.ColdStartCost);
        Assert.True(bsUi.ReuseSafeRoster);
        Assert.True(bsUi.ReuseSafeGameData);

        // newrecruit-ui: cheap cold start (~1.6s Chromium), parallelizes freely — and roster reuse
        // is NOT verdict-safe. It was once enabled on a plausible assumption and silently changed
        // six spec verdicts. That is why ReuseSafe is a declared property and not a guess.
        var nrUi = registry.Resolve(EngineConnectable.Parse("newrecruit-ui")).Profile;
        Assert.Equal(0, nrUi.MaxParallel);
        Assert.Equal(ColdStartCost.Cheap, nrUi.ColdStartCost);
        Assert.False(nrUi.ReuseSafeRoster);
    }
```

- [ ] **Step 2: Run it — fails (no `Profile` member)**

- [ ] **Step 3: Implement `EngineProfile`**

```csharp
namespace BattleScribeSpec.Concurrency;

/// <summary>Is an engine's cold start expensive enough that reusing it could pay for itself?</summary>
public enum ColdStartCost
{
    /// <summary>Cheap to construct — reuse buys nothing. A headless Chromium relaunches in ~1.6s.</summary>
    Cheap,

    /// <summary>Expensive to construct — reuse is where the win is. A JVM + JavaFX launch, per spec.</summary>
    Expensive,
}

/// <summary>
/// What an engine declares about itself. The policy derives every number from this plus a
/// <see cref="MachineProfile"/>; nothing string-matches an engine's name.
/// </summary>
/// <param name="MaxParallel">Hard ceiling on concurrent instances; 0 = unlimited.</param>
/// <param name="ColdStartCost">Whether reuse can pay for itself at all.</param>
/// <param name="ReuseSafeRoster">May the roster engine be reused across setups without changing verdicts?</param>
/// <param name="ReuseSafeGameData">May the gamedata engine be reused across setups without changing verdicts?</param>
/// <param name="MemPerInstanceBytes">Measured memory cost of one concurrent instance; 0 = unknown/negligible.</param>
/// <param name="OversubscriptionFactor">
/// The `k` in `workers ≈ cpuCount × k`. MEASURED per engine, never guessed — the engines
/// demonstrably disagree, and on the same 4-vCPU runner one workload degrades past P=6 while
/// another merely plateaus.
/// </param>
/// <remarks>
/// <b><see cref="ReuseSafeRoster"/> and <see cref="ReuseSafeGameData"/> are EARNED, not asserted.</b>
/// An engine may only claim reuse-safety for a domain where <c>bs-spec compare</c> has demonstrated
/// verdict-equality against a cold arm. The one time this was claimed without evidence
/// (newrecruit-ui roster) it silently changed six spec verdicts while a stopwatch reported success.
/// <para>
/// Note reuse needs BOTH properties: <c>reuse ⟺ ReuseSafe(domain) ∧ ColdStartCost == Expensive</c>.
/// "Is it correct?" and "is it worth anything?" are different questions. Reusing a NewRecruit
/// browser is perfectly safe and buys 0.92× — i.e. nothing — so enabling it would add a warm-state
/// failure mode for no gain. A bad trade even when it is a correct one.
/// </para>
/// </remarks>
public sealed record EngineProfile(
    int MaxParallel,
    ColdStartCost ColdStartCost,
    bool ReuseSafeRoster,
    bool ReuseSafeGameData,
    long MemPerInstanceBytes = 0,
    double OversubscriptionFactor = 1.0);
```

- [ ] **Step 4: Give `EngineEntry` a `Profile`**

In `src/BattleScribeSpec.TestKit/Engines/EngineRegistry.cs`, add `EngineProfile Profile` to the `EngineEntry` record and populate the four builtins. **This becomes the single declaration of `MaxParallel`** — `EngineEntry.MaxParallel` is replaced by `Profile.MaxParallel`, and every consumer follows.

Values, transcribed from what has been **measured** (see `docs/warm-reuse.md`) — do not invent any:

| Engine | MaxParallel | ColdStartCost | ReuseSafeRoster | ReuseSafeGameData |
|---|---|---|---|---|
| `battlescribe` | 0 | `Cheap` | false | false |
| `battlescribe-ui` | **1** | `Expensive` | **true** | **true** |
| `newrecruit` | 0 | `Cheap` | false | false |
| `newrecruit-ui` | 0 | `Cheap` | **false** | false |

Leave `MemPerInstanceBytes = 0` and `OversubscriptionFactor = 1.0` for now — **Task 8 measures them, Task 9 writes them down.**

> **Correction to this plan's "Phase 1 is behaviour-identical" framing.** That claim is true of **reuse** and false of the **worker count**, and conflating them was an error in an earlier draft. Reuse behaviour is genuinely preserved (`worthReusing` requires `ColdStartCost.Expensive`, so cheap engines still get none — exactly as today). But the worker count is **deliberately not** preserved from Task 5 onward: `run --all` stops defaulting to a hardcoded `1` and starts sizing itself, which is the entire point of the task. Two different claims; do not read one as the other. The worker count is safety-capped (`min(cpuCount, 8)` while `MemPerInstanceBytes` is unmeasured) pending Task 8/9.

Extend `EngineConfigEntry` (engines.json) with the same fields so third-party adapters can declare them, defaulting to the conservative values above.

- [ ] **Step 5: Run the tests — pass. Then the full gate.**

```bash
dotnet build && dotnet test -p:TestProfile=core
```
Expected: **zero regressions** (Cli 92 + Tests 1862 + your new tests). `MaxParallel` moved, so this catches every consumer you missed — the compiler finds them for you.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(concurrency): EngineProfile — engines declare properties; one MaxParallel declaration (#271)"
```

---

### Task 3: `ConcurrencyPolicy` — the pure function

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Concurrency/ConcurrencyPlan.cs`, `ConcurrencyPolicy.cs`
- Test: `tests/Features/ConcurrencyPolicyTests.cs`

**Interfaces:**
- Consumes: `MachineProfile` (Task 1), `EngineProfile` (Task 2).
- Produces: `ConcurrencyPlan` (record: `int Workers`, `int PoolSize`, `int MaxParallelThreads`, `bool ReuseRoster`, `bool ReuseGameData`); `ConcurrencyPolicy.For(MachineProfile, EngineProfile) -> ConcurrencyPlan`.

- [ ] **Step 1: Write the failing tests — these ARE the policy's specification**

```csharp
    [Fact]
    public void Policy_ScalesWithTheMachine()
    {
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 512L * 1024 * 1024, OversubscriptionFactor: 1.0);

        var small = ConcurrencyPolicy.For(new MachineProfile(4, 64L << 30), engine);
        var big = ConcurrencyPolicy.For(new MachineProfile(32, 256L << 30), engine);

        Assert.True(big.Workers > small.Workers, "a bigger box must get a bigger plan");
    }

    [Fact]
    public void Policy_NeverExceedsTheEnginesCeiling()
    {
        // battlescribe-ui cannot be parallelized at all — a 64-core box changes nothing.
        var bsUi = new EngineProfile(MaxParallel: 1, ColdStartCost.Expensive,
            ReuseSafeRoster: true, ReuseSafeGameData: true);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), bsUi);

        Assert.Equal(1, plan.Workers);
    }

    [Fact]
    public void Policy_IsBoundedByMemory_NotJustCpu()
    {
        // 64 cores but only 2 GB free, and each instance costs 512 MB: memory binds, not CPU.
        // Without this bound a big box launches 64 browsers and dies before it saturates CPU.
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 512L * 1024 * 1024, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 2L << 30), engine);

        Assert.True(plan.Workers <= 4, $"memory must bind before CPU does; got {plan.Workers}");
        Assert.True(plan.Workers >= 1, "always at least one worker");
    }

    [Theory]
    [InlineData(ColdStartCost.Expensive, true, true, true, true)]    // BS-UI: safe AND worth it
    [InlineData(ColdStartCost.Cheap, true, true, false, false)]      // safe but WORTHLESS (NR: 0.92x)
    [InlineData(ColdStartCost.Expensive, false, false, false, false)] // worth it but NOT SAFE
    public void Policy_EnablesReuse_OnlyWhenSafeAndWorthIt(
        ColdStartCost cost, bool safeRoster, bool safeGameData, bool expectRoster, bool expectGameData)
    {
        var engine = new EngineProfile(MaxParallel: 0, cost, safeRoster, safeGameData);

        var plan = ConcurrencyPolicy.For(new MachineProfile(8, 32L << 30), engine);

        Assert.Equal(expectRoster, plan.ReuseRoster);
        Assert.Equal(expectGameData, plan.ReuseGameData);
    }

    [Fact]
    public void Policy_IsPure_SameInputsSamePlan()
    {
        // Reproducibility is not a nicety: `compare` holds everything constant except one variable,
        // and a policy that wanders between runs makes that comparison meaningless.
        var machine = new MachineProfile(8, 32L << 30);
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap, false, false);

        Assert.Equal(ConcurrencyPolicy.For(machine, engine), ConcurrencyPolicy.For(machine, engine));
    }
```

- [ ] **Step 2: Run — fails (no such type)**

- [ ] **Step 3: Implement**

`ConcurrencyPlan.cs`:

```csharp
namespace BattleScribeSpec.Concurrency;

/// <summary>
/// One decision, for one engine on one machine. Every concurrency and reuse knob in the harness
/// reads from this — the CLI's worker count, the in-process pools' size, xUnit's collection
/// parallelism, and whether engines are reused across setups.
/// </summary>
/// <remarks>
/// One policy governing everything is a single point of failure, deliberately. Today a bad
/// NR_PARALLEL degrades one lane and a bad --workers default degrades another, independently and
/// inconsistently. One place to be wrong is one place to measure, fix and tune.
/// </remarks>
public sealed record ConcurrencyPlan(
    int Workers,
    int PoolSize,
    int MaxParallelThreads,
    bool ReuseRoster,
    bool ReuseGameData);
```

`ConcurrencyPolicy.cs`:

```csharp
namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The single source of every concurrency and reuse decision in the harness. A <b>pure function</b>
/// of the machine and what the engine declares about itself — no I/O, no environment variables,
/// no string-matching on engine names.
/// </summary>
public static class ConcurrencyPolicy
{
    /// <summary>Derive the plan. Deterministic: the same machine and engine always give the same plan.</summary>
    public static ConcurrencyPlan For(MachineProfile machine, EngineProfile engine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(engine);

        // Scale with the machine...
        var byCpu = (int)Math.Ceiling(machine.CpuCount * engine.OversubscriptionFactor);

        // ...but memory binds before CPU on a big box with hungry instances. Without this a
        // 64-core machine launches 64 browsers and exhausts memory long before it saturates CPU.
        var byMemory = engine.MemPerInstanceBytes > 0
            ? (int)(machine.AvailableMemoryBytes / engine.MemPerInstanceBytes)
            : int.MaxValue;

        var workers = Math.Max(1, Math.Min(byCpu, byMemory));

        // The engine's hard ceiling wins over everything. 0 = unlimited.
        if (engine.MaxParallel > 0)
        {
            workers = Math.Min(workers, engine.MaxParallel);
        }

        // Reuse needs BOTH: correct AND worth it. Reusing a cheap-to-start engine is safe and
        // buys nothing (measured: 0.92x for NewRecruit) — it would add a warm-state failure mode
        // for no gain, which is a bad trade even when it is a correct one.
        var worthReusing = engine.ColdStartCost == ColdStartCost.Expensive;

        return new ConcurrencyPlan(
            Workers: workers,
            PoolSize: workers,
            MaxParallelThreads: workers,
            ReuseRoster: worthReusing && engine.ReuseSafeRoster,
            ReuseGameData: worthReusing && engine.ReuseSafeGameData);
    }
}
```

- [ ] **Step 4: Run the tests — all pass**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(concurrency): ConcurrencyPolicy — the pure function (#271)"
```

---

### Task 4: The parent decides; the child is told

Today the **child** decides reuse by string-matching its own engine name (`ServeCommand.cs:83-84`), and `MaxParallel` is declared a second time in its capabilities (`:64`). Meanwhile `HostEngineFactory:49` sets `KeepAlive = keepAlive || !rosterReuseDisabled` — so `battlescribe-ui` roster keeps the app alive **regardless of what `ServeCommand` decided**. Two mechanisms, one intent, currently contradicting each other.

**Files:**
- Modify: `src/BattleScribeSpec.EngineHost/ServeCommand.cs`, `HostEngineFactory.cs`
- Modify: `src/BattleScribeSpec.TestKit/Engines/EngineHostLocator.cs`
- Modify: `src/BattleScribeSpec.Cli/EngineSpec.cs`
- Test: `tests/Features/ServeCommandPolicyTests.cs`

**Interfaces:**
- Consumes: `ConcurrencyPlan` (Task 3).
- Produces: `serve --policy k=v,...` (the SAME vocabulary and the SAME parser as `run`/`compare`); `EngineSelection` gains `ConcurrencyPlan? PlanOverride`; `EngineHostLocator.Resolve` composes the policy args.
- Produces: `PolicyOverride.Parse(string) -> ConcurrencyPlan modifications` — **one parser, shared by all three commands.** Keys: `workers=N`, `reuse=on|off`, `reuse-roster=on|off`, `reuse-gamedata=on|off`.

- [ ] **Step 1: Write the failing test**

Assert that `ServeCommand.BuildOptions` takes the reuse decision **as parameters** and no longer reads `BSSPEC_DISABLE_WARM_REUSE` or matches on the engine name; and that `HostEngineFactory`'s `KeepAlive` agrees with the passed decision rather than contradicting it.

- [ ] **Step 2: Run — fails**

- [ ] **Step 3: Change `ServeCommand`**

`BuildOptions(string name, bool headless, bool keepAlive)` becomes `BuildOptions(string name, bool headless, ConcurrencyPlan plan)`. Delete the `BSSPEC_DISABLE_WARM_REUSE` read. Set:

```csharp
            ReuseRosterEngineAcrossSetups = plan.ReuseRoster,
            ReuseGameDataEngineAcrossSetups = plan.ReuseGameData,
```

`AdapterCapabilities.MaxParallel` comes from the engine's `EngineProfile.MaxParallel` (Task 2), **not** a string-match.

**Add a single `--policy k=v,...` option to `serve`** and build the plan from it — the same vocabulary and the same parser `run` and `compare` use. Do not invent `serve`-specific reuse flags; that would be three vocabularies for one idea, which is the disease being cured.

**Delete `--keep-alive` from `serve`.** "Keep the app alive between specs" *is* reuse — two names for one concept. It becomes `--policy reuse=on`.

- [ ] **Step 4: Fix the `KeepAlive` contradiction in `HostEngineFactory`**

`CreateRosterEngineAsync`/`CreateGameDataEngineAsync` take the reuse decision explicitly and set `KeepAlive` **from it** — deleting both `BSSPEC_DISABLE_WARM_REUSE` reads and the `keepAlive || !reuseDisabled` double-negative. `KeepAlive` must now mean exactly "the plan says reuse this engine", with `--keep-alive` remaining an explicit override for interactive debugging.

- [ ] **Step 5: Compose the args in `EngineHostLocator.Resolve`**

Its `serve` branch currently builds `serve --engine X [--headed] [--keep-alive]`. Add the reuse flags from the plan. **Note the known gap (#305): headed/keep-alive are silently dropped for non-builtin launchables.** Do not fix #305 here; do not make the policy args silently dropped the same way — if a launchable adapter cannot receive them, say so explicitly in your report.

- [ ] **Step 6: Verify — the gate is `compare`, not a unit test**

```bash
dotnet build && dotnet test -p:TestProfile=core
```

Then prove the refactor is **behaviour-identical** — this is the whole point of Phase 1:

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine battlescribe-ui --gamedata --filter "entry/,export/" \
  --config-a "" --config-b ""
```
Expected: verdicts identical, speedup ≈ **1.0×**. Both arms now run the *new* code; if reuse silently turned off, the timing would collapse toward the 353 s cold number instead of ~160 s. **Paste the output in your report.**

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor(host): parent decides the policy; child is told via serve args (#271)"
```

---

### Task 5: The CLI consumes the policy; `--workers` is demoted

**Files:**
- Modify: `src/BattleScribeSpec.Cli/Commands/RunCommand.cs`, `RunBatch.cs`, `EngineSpec.cs`
- Test: `tests/BattleScribeSpec.Cli.Tests/` (follow the existing surface tests)

- [ ] **Step 1: Write the failing test** — with no `--workers`, a `run --all` on a multi-core box must plan **more than one** worker for an engine whose `MaxParallel` is 0 (today it defaults to 1). With `--workers N`, N wins.

- [ ] **Step 2: Run — fails (today the default is hardcoded 1)**

- [ ] **Step 3: Implement**

**DELETE `--workers` and `--keep-alive` from `run`.** They are not demoted — they are *removed*, because each is one policy key wearing its own flag:

- `--workers N` → `--policy workers=N`
- `--keep-alive` → `--policy reuse=on` (keeping the app alive between specs IS reuse; two names, one concept)

**Add `--policy k=v,...`** — the one perf/reuse vocabulary, using the shared parser from Task 4. `RunBatch.ExecuteAsync` computes `ConcurrencyPolicy.For(MachineProfile.Current(), selection.Entry.Profile)` and applies any `--policy` overrides on top.

Keep `ResolveWorkersAsync`'s live describe-probe clamp — an adapter may advertise a lower ceiling than the registry knows. **The policy proposes; the describe handshake still disposes.**

> The acceptance test for the whole spec: **if you have to set a flag to get good performance, the policy has failed.** These overrides exist for diagnosing, not for operating — so their verbosity is a feature, not a cost.

**`--headed` stays**, because it is *presentation*, not performance. But see Step 3b.

- [ ] **Step 3b: A flag is accepted or rejected — never silently dropped**

`EngineHostLocator.Resolve` currently **drops `--headed` and `--keep-alive` on the floor for launchable (`exec:`/`dotnet:`) adapters** (#305). A flag that quietly does nothing is worse than one that errors: the user believes they configured something, and they did not.

Two rules, and the distinction is the point:

- **Capability mismatch → ERROR.** `--headed` against an engine with no UI, or against an adapter that cannot receive it, is a *mistake*. Fail loudly, naming what the engine actually supports. This closes #305 properly — by rejecting, not by silently conveying.
- **Policy override → ALLOWED, but warned.** Forcing `reuse=on` on an engine whose profile says `ReuseSafe = false` is precisely the ablation `compare` needs in order to *prove* reuse-safety. That is what an override is for. Warn: *"forcing reuse on an engine not declared reuse-safe; verdicts may change — use `bs-spec compare` to check."*

Add a test for each rule.

- [ ] **Step 3c: Update `.github/workflows/ci.yml` IN THIS TASK — or you break CI**

`ci.yml:89` passes `--workers 2` to the reference-adapter step. **Deleting `--workers` without updating it breaks CI immediately**, three tasks before Task 9 gets to the YAML. Change it to `--policy workers=2` in this task.

(The three `NR_PARALLEL:` env settings can stay for now — after Task 7 they are inert no-ops, not breakages. Task 9 removes them.)

- [ ] **Step 4: Verify**

```bash
dotnet test -p:TestProfile=core
dotnet test tests/BattleScribeSpec.Cli.Tests
python -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"   # the YAML must still parse
```

And prove verdict-neutrality at the new default:

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine newrecruit-ui --roster --filter "catalogue/,category/" \
  --config-a "" --config-b ""
```
Expected: verdicts identical, exit 0. **Report the worker count the policy chose and the peak `adapter-process` from the trace summary — they must be arithmetically consistent.** (Precedent: a `--workers 2` run once reported "1 cold" and that turned out to be a real cross-worker under-count bug.)

- [ ] **Step 5: Commit**

---

### Task 6: `compare --policy-a/--policy-b` — keep the rail working

Deleting `BSSPEC_DISABLE_WARM_REUSE` removes the channel `compare` uses to ablate reuse. Replace it with a **better** one: `compare` can now vary *any* policy decision, not just reuse.

**Files:**
- Modify: `src/BattleScribeSpec.Cli/Commands/CompareCommand.cs`, `EngineSpec.cs`
- Test: `tests/BattleScribeSpec.Cli.Tests/CompareCommandTests.cs`

- [ ] **Step 1: Write the failing test** — `compare --policy-a "reuse=on" --policy-b "reuse=off"` runs the two arms with different reuse decisions and still asserts verdict-equality (exit 0 when they match; non-zero when they diverge).

- [ ] **Step 2: Run — fails (no such option)**

- [ ] **Step 3: Implement**

**DELETE `--workers` from `compare`** — it becomes `--policy-a "workers=N" --policy-b "workers=N"`, the same as everywhere else.

Add `--policy-a` / `--policy-b`, using the **shared parser** from Task 4 (`workers=N`, `reuse=on|off`, `reuse-roster=`, `reuse-gamedata=`). They flow into `EngineSelection.PlanOverride`, hence into the child's `serve --policy` args.

`--config-a` / `--config-b` **stays** for genuine **environment** experiments — it is a different axis, not the policy channel. Keeping both is not redundancy: one varies the harness's decisions, the other varies the child's environment.

- [ ] **Step 4: Reproduce the recorded warm-reuse result through the new channel** — this proves the rail still works:

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine battlescribe-ui --gamedata --filter "entry/,export/" \
  --policy-a "reuse=on" --policy-b "reuse=off"
```
Expected: verdicts identical, speedup ≈ **2.20×** (the figure recorded in `docs/warm-reuse.md`). **If the verdicts diverge, STOP and report it** — that would mean warm-reuse is not verdict-neutral, which matters far more than this task. Do not loosen the comparison.

- [ ] **Step 5: Commit**

---

### Task 7: The xUnit path — bound the unbounded

`parallelizeTestCollections: true` with `maxParallelThreads` **unset** means collections run CPU-count-wide, and each fixture can own a browser-context pool or a JVM. Nothing bounds the product. This is the one place the current system is not merely arbitrary but genuinely unbounded — and it is invisible.

**Files:**
- Modify: `tests/Infrastructure/FrozenNrRosterFixture.cs`, `LiveNrRosterFixture.cs`, `FrozenNrGameDataUiFixture.cs`
- Modify: `tests/xunit.runner.json`, `tests/BattleScribeSpec.Cli.Tests/xunit.runner.json`
- Modify: `README.md` (the `NR_PARALLEL` row), and the stale prose in `FrozenNrUiRosterFixture.cs:9`, `FrozenNrGameDataUiConformanceTests.cs:14`

- [ ] **Step 1: Delete every `NR_PARALLEL` read.** All three fixtures compute their pool size from `ConcurrencyPolicy.For(MachineProfile.Current(), <the engine's profile>)`. Note the three current defaults differ (5 / 10 / 5) and the factory signatures carry a *fourth* copy — the policy replaces all of them.

- [ ] **Step 2: Set `maxParallelThreads` in both `xunit.runner.json`** from the policy's value. If xUnit's config cannot be computed at runtime, set a conservative literal **and say so in your report** — a hardcoded bound is still infinitely better than none, but I want to know it is hardcoded.

- [ ] **Step 3: Verify — and report the number**

```bash
dotnet test -p:TestProfile=core
dotnet test -p:TestProfile=nr-frozen
```

Read the metrics artifact back and report the **peak `harness.resource.count`** (total and by kind), before and after. Baseline from Spec 1: `nr-frozen` peak was `browser: 1, browser-context: 5`. **Remember this is a LOWER BOUND** — the 2 s export interval means a shorter spike is invisible. Say so when you quote it.

- [ ] **Step 4: Commit**

**Residual, documented rather than fixed here (code review follow-up):** this task bounds xUnit's
own thread count (`maxParallelThreads`) and bounds each individual pool's size
(`ConcurrencyPolicy.For(...).PoolSize`). It does **not** bound the product across
simultaneously-live xUnit collection fixtures — a collection fixture lives for the whole
collection, not for one thread-slot, so two independent collections (e.g.
`FrozenNrRosterFixture` and `FrozenNrGameDataUiFixture`) can be fully alive at once, each with a
pool sized up to the cap; total live browser-contexts can reach the *sum* across them, not the
cap. `BrowserResourceRaceGate` only serializes a fixture against its own resource-metrics test, not
against sibling fixtures. The original framing — "collections × pools × the JVM compose
multiplicatively with nothing capping the product" — is therefore bounded **per-factor** here, not
as a product. Tracked as [#314](https://github.com/WarHub/battlescribe-spec/issues/314) (a shared
budget the pools draw from is the likely real fix); not attempted in this task, since it is
architecture, not cleanup.

---

### Task 8: The measurement campaign — find the knee

**This task produces numbers, not code.** It is the reason Tasks 1–7 exist.

**Files:**
- Create: `docs/concurrency-policy-measurements.md`

- [ ] **Step 1: Sweep, per `(engine, domain)`, on BOTH hardware classes**

For each of `newrecruit`, `newrecruit-ui` (and `battlescribe-ui` for the reuse figures — it cannot be parallelised, so `k` is moot there):

```bash
# For P in 1,2,4,6,8,12,16,24,32,48 ... KEEP GOING until wall-clock stops improving
# or starts degrading. Then go two levels further to be sure it is a knee.
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine <e> --roster --filter "<a representative set>" \
  --policy-a "workers=<P>" --policy-b "workers=<P>"
```

Record: wall, verdict-equality (must hold at **every** level), peak `harness.resource.count` by kind, and peak RSS per instance.

**Hardware:** the 4-vCPU GitHub runner (what CI actually runs) **and** the dev box.

- [ ] **Step 2: Do not stop where the previous sweep stopped**

The existing 32-core data **stopped at P=16 while still improving** (35 s at P=8 → 27 s at P=16). That is not an optimum, it is where the sweep ran out. A `k` fitted to "16" would be an artifact of the sampling. **Find the actual knee.**

- [ ] **Step 3: Expect the engines to disagree, and record it**

On the *same* 4-vCPU runner, `nr-frozen` **degrades** past P=6 (48 s → 75 s at P=16, worse than P=2) while `nr-editor-ui-frozen` merely **plateaus** (96 s → 91 s). Short CPU-bound specs die on contention; long I/O-heavy ones tolerate oversubscription. If your data reproduces this, `k` is per-engine and the spec is right. **If it does not, that is a finding — report it rather than smoothing it over.**

- [ ] **Step 4: Write `docs/concurrency-policy-measurements.md`** — every sweep, the hardware it ran on, the verdict-equality result at each level, the fitted `k_engine` and `memPerInstance_engine`, and **the knee you found with the evidence that it is one**.

- [ ] **Step 5: Commit**

---

### Task 9: Fit the constants

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Engines/EngineRegistry.cs` (the builtins' `EngineProfile` values)
- Modify: `.github/workflows/ci.yml` (delete the three `NR_PARALLEL` env settings and the `--workers 2`)
- Modify: `docs/warm-reuse.md`, `README.md`

- [ ] **Step 1: Write the measured `OversubscriptionFactor` and `MemPerInstanceBytes` into each builtin's `EngineProfile`**, citing `docs/concurrency-policy-measurements.md`. **These are the only literals in this task.**

> ⚠️ **Step 1b was NOT followed — its premise was false. Read this before re-reading it.** The step
> below reasons that once *every* builtin declares a measured `MemPerInstanceBytes`, the cap's
> `== 0` gate can never fire again and it becomes dead weight. Two things falsify that: (a) only two
> of four builtins were measured, and (b) `EngineRegistry.DefaultProfile` **and** the `engines.json`
> path both let an engine register *without declaring `MemPerInstanceBytes` at all* — this harness is
> explicitly open to other engines, so "every engine is measured" is a state it can never reach.
> The `== 0` gate means the cap **self-retires per engine, automatically** — the measured engines
> bypass it with no code change. So it was **kept and renamed `UndeclaredMemoryWorkerCap`**, and
> redocumented as the permanent conservative default for any engine that has not declared its
> footprint (declaring one is how an engine opts into full machine-width parallelism).
>
> The `newrecruit` sweep (measured *after* this plan was written; `docs/concurrency-policy-measurements.md`
> §5) then vindicated the deviation empirically: that engine still declares `0`, and deleting the cap
> would have given it `cpuCount` = 32 workers on the dev box — **58.9 s vs 23.1 s at the capped 8**,
> straight over a **1.97× cliff at P=16** the plan did not know existed. See Task 9's report.

- [ ] **Step 1b: Remove the provisional safety cap in `ConcurrencyPolicy.For`.** A stopgap
  (`ProvisionalUnmeasuredMemoryCap`, `min(cpuCount, 8)`, added on `perf/concurrency-model` ahead of
  this task) currently caps worker count whenever an engine declares `MemPerInstanceBytes == 0` —
  precisely because, before this step, that was true for every builtin. Once Step 1 gives every
  builtin engine a real, measured `MemPerInstanceBytes`, that condition is never true for a
  builtin again and the cap becomes dead weight: an unreachable guard nobody remembers the reason
  for. Delete the cap and its two `Policy_CapsWorkers_WhenMemPerInstanceIsUnmeasured` /
  `Policy_DoesNotApplyTheProvisionalCap_OnceMemPerInstanceIsMeasured` tests (or, if a third-party
  adapter can still declare `MemPerInstanceBytes = 0` via `engines.json`, keep the cap but say
  explicitly in the commit message why it still earns its keep). **A "temporary" guard that
  outlives its reason is how magic numbers are born — do not let this one become one.**

- [ ] **Step 2: Delete the CI knobs.** The three `NR_PARALLEL: 6` / `NR_PARALLEL: 2` settings and the `--workers 2` in ci.yml go; the policy decides. The 4-vCPU runner is simply one machine the policy is fitted to.

- [ ] **Step 3: Prove it, on CI, not locally**

Push and let CI run. Compare the lane wall-times against the recorded baselines (`nr-frozen` 48 s at the old `NR_PARALLEL: 6`; `nr-editor-ui-frozen` 96 s). **The policy must be at least as fast.** If it is slower, the fit is wrong — revert the constants (not the architecture) and say so.

- [ ] **Step 4: Verdict-equality one last time**

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine battlescribe-ui --gamedata --filter "entry/,export/" \
  --policy-a "reuse=on" --policy-b "reuse=off"
```
Expected: verdicts identical, ≈ 2.20×.

- [ ] **Step 5: Update the docs.** `README.md` loses the `NR_PARALLEL` row. `docs/warm-reuse.md`'s reproduction section uses `--policy-a/--policy-b`. State plainly that **the only environment variable left in the harness is `OTEL_EXPORTER_OTLP_ENDPOINT`** — and that it survives precisely because it is not ours.

- [ ] **Step 6: Commit**

---

## Verification

Done when:

1. `dotnet test -p:TestProfile=pre-push` green.
2. **`grep -r "NR_PARALLEL\|BSSPEC_DISABLE_WARM_REUSE" src/ tests/ .github/ README.md` returns nothing.**
3. `bs-spec run --all` with no `--workers` picks a sensible worker count on a big box and on a 4-vCPU runner, and they differ.
4. `compare --policy-a "reuse=on" --policy-b "reuse=off"` on `battlescribe-ui --gamedata` reports **verdicts identical, ≈2.20×**.
5. CI lane wall-times are **no worse** than the recorded baselines, with the `NR_PARALLEL` settings deleted.
6. `docs/concurrency-policy-measurements.md` records the knee for each engine, on each hardware class, with evidence that it is a knee.
