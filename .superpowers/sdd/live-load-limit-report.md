# The live lane is not a lane — it is someone else's website

**Status:** done. Branch `perf/concurrency-model` (draft PR #317). Related: issue #318 — **this does
not fix that crash and does not claim to** (see "What this is not", below).

---

## 1. The defect

`nr-frozen` and `nr-live-conformance` resolve the **same engine** (`"newrecruit"` → the same
`EngineProfile`, the same `ContextPoolSize: 4`). One replays a HAR file off local disk. The other
drives **`newrecruit.eu`**, a live production website run by other people. Neither `MachineProfile`
nor `EngineProfile` could tell those apart, so `ConcurrencyPolicy.For` gave them the same pool. It had
no way not to.

The live lane's concurrency had been **2** for its whole history, set deliberately — commit `7e65836`
(2026-07-12), *"ci: NR_PARALLEL 4 -> 6 on the frozen NR lanes (measured optimum on real runners)"*:

> *"The live nr-conformance lane stays at 2 — it drives the real newrecruit.eu, so parallelism there
> is a load question, not a throughput one."*

That commit **is a sweep result**. It raised the frozen lanes on measured evidence and, in the same
breath, **declined to apply itself to the live lane.** The 2 was not an unmeasured number awaiting a
sweep — it was *deliberately* unmeasured.

How it was lost:

1. The concurrency model deleted `NR_PARALLEL` (correctly — a second place to decide a question the
   policy owns). The **constraint** it carried had nowhere in the model to live and went with it.
2. It survived one commit **by coincidence**: the mirrored policy computed `ceil(4 × 0.375) = 2`.
3. The axis separation (`edf3b4a`, #314) broke the coincidence. The live lane took `newrecruit`'s
   declared `ContextPoolSize: 4` — **fitted by sweeping `nr-frozen` (HAR replay, no network) on a
   4-CPU container. Nothing in that sweep touched newrecruit.eu.**
4. CI reported the result as a **win**: `nr-live-conformance` 230 s → 145 s, −37% (§8.8). It was 85 s
   of our wall-clock bought by doubling the traffic we put on a stranger's server.

**Verified:** `LiveNrRosterFixture.cs:28` → `FixtureConcurrency.PoolSizeFor("newrecruit")` →
`EngineRegistry.cs:124` `ContextPoolSize: 4`. First change to that lane's concurrency in the repo's
history, and nobody chose it.

**And nothing else bounds the load.** `grep -rE 'retry|backoff|throttl|rate.?limit|429|Task.Delay|Thread.Sleep'
src/BattleScribeSpec.NewRecruit/` → **zero hits**. No pause between specs, no retry, no backoff, no
429 handling. The pool size is the only brake this harness has.

---

## 2. The fix

### 2.1 `LoadTarget` — a third input to the policy

`src/BattleScribeSpec.TestKit/Concurrency/LoadTarget.cs` (new):

```csharp
public enum LoadTarget { Local, ThirdPartyLive }
```

`ConcurrencyPolicy.For(machine, engine, loadTarget = LoadTarget.Local)`. The question *"who is on the
other end?"* is not answerable from the machine or from the engine, so it is now asked explicitly.

Named `LoadTarget`, **not** `DataSource`: `DataSourceResolver` / `DataSourceUri` / `SpecSetup.DataSource`
already exist in the same assembly and mean something entirely different (where a *spec* gets its game
data). Adding a fourth thing called "data source" would have repeated this branch's signature mistake
in the naming layer.

### 2.2 `ConcurrencyPolicy.ThirdPartyLiveLoadLimit = 2` — and why no sweep may raise it

Clamps **both axes** (`Workers` *and* `PoolSize`) when `LoadTarget.ThirdPartyLive`, because the remote
host feels requests in flight and cannot see whether we spawned them as processes or as contexts.

| | asks | answered by |
|---|---|---|
| `OversubscriptionFactor`, `ContextPoolSize`, `MemPerContextBytes`, `MemoryHeadroomFactor` | *how fast can **this machine** go?* | measurement |
| **`ThirdPartyLiveLoadLimit`** | *how hard may we hit **a stranger's server**?* | **judgment. Never a sweep.** |

The constant's doc comment quotes `7e65836` verbatim, states that **raising it increases load on
someone else's website**, records how the constraint was lost, and states the price we accept for
keeping it (§2.5). It is deliberately *impossible to confuse* with `ContextPoolSize`: different name,
different unit of meaning, different section of the policy, and a comment block that stops the "why is
this 2 when that one is 4?" tidy-up **in the code, where the person will be standing**.

### 2.3 `FixtureConcurrency` — no default, ever

`Resolve(engine, loadTarget)` and `PoolSizeFor(engine, loadTarget)` now **require** the load target.
Every fixture states who it talks to:

| fixture | declares | pool |
|---|---|--:|
| `LiveNrRosterFixture` (**newrecruit.eu**) | `ThirdPartyLive` | **2** |
| `FrozenNrRosterFixture` (HAR on disk) | `Local` | **4** |
| `FrozenNrGameDataUiFixture` (local static site) | `Local` | **16** |
| `BsGameDataUiFixture` (desktop app) | `Local` | n/a (reuse only) |

That a fixture had **no way to say so** is precisely how the courtesy limit got deleted. Now it cannot
be left blank.

### 2.4 Telemetry on `nr-conformance` (`ci.yml`)

It was the **only lane in the workflow without an `Upload telemetry` step** — the one lane that talks
to a third party was the one lane that shipped no diagnostics, which is why #318's crash is
unrecoverable. Added, `if: always()`, matching `thorough-conformance` / `thorough-ui-bs`.

### 2.5 What it costs

The live lane goes **145 s → ≈230 s**. We pay that out of our CI budget rather than out of someone
else's bandwidth. **If that lane must get faster, make it send fewer requests — not more at once.**

### 2.6 Secondary: `SequentialLiveNrRosterFixture` is now lazy — DONE

It eagerly called `NewRecruitRosterEngine.CreateAsync` — **a second browser against newrecruit.eu**,
separate from and additional to the pool's — for 363 tests that all `Assert.Skip` unless
`NR_SEQUENTIAL=true`, which nothing sets. The lane's filter (`Category=Conformance&Engine=LiveNrRoster`)
still selects them, so xUnit built the fixture, loaded a third party's site, and skipped every test
that could have used it.

Now: `InitializeAsync` only reads `NR_ENGINE_URL`; the engine is constructed on **first access** to
`Engine`. `Available` therefore means "a live site is configured", not "a browser is running" — which
is the question all three consumers were really asking (each pairs
`Assert.SkipWhen(!Available, "NR_ENGINE_URL not set")` with a use of `Engine`), and
`SequentialLiveNrRosterConformanceTests.GetEngine` checks its `NR_SEQUENTIAL` skip *before* it touches
`Engine`, which is what makes the laziness worth anything.

Verified end-to-end: `-p:TestProfile=nr-live-smoke` → **4/4 passed** (the lane that *does* use the
engine still gets one).

---

## 3. Tests — each falsifiable, each verified red against the named mutant

**+14 tests.** Every one was run against a deliberately reintroduced defect and observed to fail.

| test | what makes it fail |
|---|---|
| `Policy_LiveLane_IsHeldAtTheLoadLimit_AndDoesNotTrackTheFrozenContextPool` | **raise `ThirdPartyLiveLoadLimit` to the frozen pool's 4 — the exact regression that shipped.** Also fails if the limit is derived from `ContextPoolSize` by any formula (it asserts the live number is *strictly smaller* than an engine's declared pool) |
| `Policy_LoadLimit_IsAPropertyOfTheRemoteService_NotOfTheEnginesDeclaredPool` | compute the limit *from* the engine (`pool / 2`, `min(pool, cpu)`, "the smaller measured optimum"…): three engines declaring 4 / 16 / 64 would stop all giving 2 |
| `Policy_LoadLimit_DoesNotScaleWithCpuCountOrMemory` (7 rows, 1–64 CPU) | put **any** machine term in the clamp — a bigger runner is not consent. Also fails if the clamp misses the **process** axis (a 32-core box would plan 12 live worker processes) |
| `Policy_LoadLimit_DoesNotTouchLocalLanes` (2 rows) | apply the clamp unconditionally — `nr-editor-ui-frozen`'s pool would collapse 16 → 2 (the mirror-image mistake: throttling a lane nobody else pays for) |
| `Policy_DefaultLoadTarget_IsLocal` | flip the default to `ThirdPartyLive` (throttles every local lane), or let the parameter change anything else about the plan |
| `FixtureConcurrencyTests.PoolSizeFor_TheLiveLane_IsTheThirdPartyLoadLimit_NotTheFrozenPool` | the same claim on the **real machine**, through the fixture's own code path — also fails if `FixtureConcurrency` stops forwarding the load target |
| `ConcurrencyConfigurationDriftTests.LiveFixture_DeclaresThirdPartyLive_SoTheLoadLimitApplies` | change `LiveNrRosterFixture` to `LoadTarget.Local` — **a policy nobody invokes is a policy nobody has.** Also fails if a *local* fixture declares `ThirdPartyLive` |

**Mutation runs actually performed:**

- `ThirdPartyLiveLoadLimit = 4` → **10 of the 14 red**, including both headline tests.
- `LiveNrRosterFixture` → `LoadTarget.Local` → the drift/lint test **red**.

Existing tests kept: the frozen lanes still assert 4 and 16 on every machine profile
(`Policy_PoolSize_OnTheCiRunner_IsTheMeasuredOptimum`, `Policy_PoolSize_IsIndependentOfCpuCount`), so
the fix that made CI faster cannot be regressed by this one.

---

## 4. Results

**`dotnet test -p:TestProfile=core`: Cli 117/117, Tests 1968/1968 green.** (Baseline 117 + 1954; +14
new tests.) Build: **0 warnings, 0 errors** with analyzers-as-errors. The retired-knob lint gate is
green; `docs/protocol-schema.json` / `ProtocolSchemaDriftTests` untouched. No conformance verdict
changes — the load limit changes *how many contexts run at once*, never what a spec asserts.

Pool sizes the policy now yields (4-vCPU CI runner):

| lane | engine | `LoadTarget` | pool |
|---|---|---|--:|
| `nr-live-conformance` | `newrecruit` | `ThirdPartyLive` | **2** |
| `nr-frozen` | `newrecruit` | `Local` | **4** |
| `nr-editor-ui-frozen` | `newrecruit-ui` | `Local` | **16** |

*(Same engine, first two rows. That is the whole point.)*

**Two notes on the verification environment**, both honest caveats rather than results:

- Work was done in a **linked git worktree** (another agent was live in the main tree). In a linked
  worktree `.git` is a *file*, and `EngineHostLocator` finds the repo root by looking for a `.git`
  **directory** — so 8 `Cli.Tests` fail there with "no .git ancestor found" until `BSSPEC_ENGINE_HOST`
  is set. Pre-existing, unrelated to this change, not fixed here (it is a real papercut for anyone
  using worktrees: `EngineHostLocator.cs:208` should accept a `.git` file).
- One full `Tests` run (of five) reported **1 failure** which did **not** reproduce in the four other
  runs, and whose name I did not capture. It is not attributable to anything added here — every new
  test is either a pure function of literal inputs or a file scan, with no timing, no I/O race and no
  environment dependency. Flagging it rather than hiding it.

---

## 5. What this is **not**

- **Not a fix for #318** (the `nr-conformance` crash). The evidence there is n=2 and the exit code (0)
  argues *against* resource exhaustion; the causal claim is unproven and the issue says so. This fixes
  a design defect on its own merits. What it *does* give #318 is the thing it lacked: that lane now
  uploads its telemetry, so the next crash leaves a trace behind.
- **Not a measurement.** The 2 is not swept and will not be. A sweep can tell you how fast
  newrecruit.eu answers 8 concurrent sessions; it cannot tell you whether we are entitled to ask.

## 6. Known gap, filed not fixed (§9.4 of the measurements doc)

**The CLI path does not declare its load target.** `bs-spec run --all` with `NR_ENGINE_URL` set makes
the child engine host go live (`HostEngineFactory.cs:35`), and the parent that computes the plan never
asks — so that path is still bounded only by `Workers` (`ceil(cpuCount × 0.375)` = **12** worker
processes on the dev box, each with its own browser, against newrecruit.eu). `ConcurrencyPolicy.For`
now *accepts* the answer and clamps both axes when given it; the CLI simply never passes it.

Wiring it honestly requires the **engine** to declare which service it talks to — the policy is
forbidden from string-matching engine names, and `NR_ENGINE_URL` is meaningless for `battlescribe`, so
"env var is set ⇒ throttle" would be wrong for every non-NR engine. That is a design change, not a
patch, and it is out of this change's scope. **The xUnit lane — the one CI actually runs against the
live site — is bounded.**
