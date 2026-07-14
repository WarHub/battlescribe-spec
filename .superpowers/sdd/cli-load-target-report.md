# The CLI never asked whose website it was pointing at (#317)

**Status: fixed, verified, merged into `perf/concurrency-model`.** Closes §9.4's known gap
(`docs/concurrency-policy-measurements.md`), now written up as **§9.5** in the same document.

---

## 1. The defect

On `main`, `RunCommand` had:

```csharp
var workers = new Option<int>("--workers") { DefaultValueFactory = _ => 1 };
```

**`bs-spec run --all` against live NewRecruit was serial.**

On this branch the worker count comes from `ConcurrencyPolicy.For(...)` = `ceil(cpuCount × k)`. For
`newrecruit` that is `ceil(32 × 0.375)` = **12 adapter processes, each with its own browser**; for
`newrecruit-ui` (`k = 1.0`) it is **32**. If the engine is configured against the live site, that is 12–32
concurrent browsers on **newrecruit.eu**, a volunteer-run production website — up from 1, by a default
nobody chose.

Nothing else bounds it. `grep -rE 'retry|backoff|throttl|rate.?limit|429' src/BattleScribeSpec.NewRecruit/`
→ **zero hits**. Concurrency *is* the brake.

`4db354c` fixed the xUnit half of this (`LiveNrRosterFixture` declares `ThirdPartyLive` → 2) and correctly
scoped the CLI half out rather than guessing at it (§9.4). This is that half.

## 2. The signal — and why it is reliable

**`NR_ENGINE_URL`, read per-domain.** Established by reading the code, not by assuming:

| Route | What decides live vs local |
|---|---|
| `HostEngineFactory.CreateRosterEngineAsync`, `"newrecruit"` / `"newrecruit-ui"` | `NR_ENGINE_URL` is `{ Length: > 0 }` ⇒ `CreateAsync(url)` (**live**); otherwise `FindFrozenHarFile()` ⇒ `CreateFrozenAsync(har)` (**local**) |
| `HostEngineFactory.CreateGameDataEngineAsync`, all four engines | **never reads it** — always a frozen static dir (`.testdata/nr-editor`) |
| `battlescribe`, `battlescribe-ui` | no network code at all (in-process IKVM / a local JVM) |
| `compare --config-a/--config-b` | a `KEY=VALUE` overlay on the **child's environment** — `--config-a NR_ENGINE_URL=…` takes an arm live from a parent shell that has no such variable |

**Why it is reliable:** it is *the same test, on the same variable, that the child itself performs* — the
parent's verdict and the child's behaviour read one value, so they cannot disagree. There is no `--url`
flag on any `bs-spec` command (checked), and no other route to a live endpoint on the CLI path. The
hardcoded `https://www.newrecruit.eu` defaults in `src/BattleScribeSpec.NewRecruit/` are C# parameter
defaults on `CreateAsync`, which `HostEngineFactory` only reaches *with* a URL in hand;
`ProbeCommand`'s `?? "https://www.newrecruit.eu"` fallback is a single-process diagnostic (`bs-engine-host
probe`), not a worker-count path.

And it is checked mechanically, not by this paragraph: **`HostEngineFactory_LiveEndpointRoutes_AreDeclaredByTheRegistry`**
extracts every `GetEnvironmentVariable("*_URL")` read from each of the factory's two engine-construction
methods and asserts the set **equals** what the registry declares for that domain. A new live route that
the policy cannot see now fails the build.

## 3. The fix

**The engine declares which service it drives; the CLI derives the load target; the policy acts on it.**
`ConcurrencyPolicy` stays a pure function of `(MachineProfile, EngineProfile, LoadTarget)` and still
string-matches no engine name — the derivation lives in the caller, which is where the fact is known.

`EngineEndpoint` (new, `BattleScribeSpec.Engines`) is a **per-domain** declaration on `EngineEntry`:

| Engine | Roster | GameData |
|---|---|---|
| `battlescribe`, `battlescribe-ui` | `OnThisMachine` | `OnThisMachine` |
| `newrecruit`, `newrecruit-ui` | `FromUrlVariable("NR_ENGINE_URL")` | `OnThisMachine` |
| any `exec:`/`dotnet:` adapter | **`Undeclared`** | **`Undeclared`** |

`EngineSelection.LoadTarget` resolves it against the environment *the child will see* (process env, with
`compare`'s per-arm `--config-*` layered on top — which is why the arm's config now lives **on the
selection** rather than beside it: the object that computes the plan and the environment that decides the
endpoint have to be the same object).

**Fail-safe by construction — only positive evidence buys `Local`:**

- `Undeclared` is the enum's **zero value** ⇒ a default-constructed declaration means "I don't know",
  never "it's fine".
- An **unparseable** URL (`www.newrecruit.eu` with no scheme, a scheme typo, whitespace) ⇒ `ThirdPartyLive`.
- **Unset or empty** ⇒ `Local` — the same `{ Length: > 0 }` test the child uses to load the frozen HAR.
- **Loopback** (`localhost`, `127.0.0.1`, `[::1]`) or `file:` ⇒ `Local`. A private LAN address is somebody's
  box, just not this one, and gets no credit.

**Not a blanket throttle.** Frozen runs keep the measured worker count (the 14.3×), and so do *gamedata*
runs even with `NR_ENGINE_URL` exported — which is why the declaration is per-domain.

**The limit holds against `--policy`.** `ApplyPolicyOverride` computes its base plan **for the load target**
(so `--policy reuse-roster=on` — a flag that says nothing about workers — cannot resurrect 12 of them
through an untouched `Workers` field), and an override that would *raise* the limit on a live engine is
**refused**, not silently clamped (#305: a flag is honoured or rejected, never dropped).
`ConcurrencyPolicy.ClampToLoadTarget` is the backstop for any other path that builds a plan.

`"endpoint": "local" | "third-party-live" | "url-var:NAME"` in `engines.json` is a third-party adapter's
one-line opt-in to the machine's full width — the same bargain as `memPerInstanceBytes`, on the axis that
costs a stranger rather than this box. An unrecognized value is rejected at load rather than silently read
as "undeclared".

## 4. What a CLI run gets — 32-core dev box

| Run | Workers | Pool |
|---|---:|---:|
| `run --all --engine newrecruit` (frozen) | **12** | 4 |
| `run --all --engine newrecruit-ui` (frozen) | **32** | 16 |
| either, `NR_ENGINE_URL=https://www.newrecruit.eu` | **2** | **2** |
| `--gamedata`, with `NR_ENGINE_URL` set | **12 / 32** | 4 / 16 |
| unknown `exec:` adapter | **2** | 2 |

## 5. Tests — each falsifiable, each **verified red** against the named mutant

No live run was used to verify any of this: every gate is a pure function or a source-structure assertion.
(The one thing this fix exists to prevent is hammering newrecruit.eu.)

| Test | Production change that makes it fail | Verified |
|---|---|---|
| `EffectivePlan_LiveNewRecruit_IsHeldToTheThirdPartyLoadLimit_NotTheMachinesWidth` (Cli) | **M1**: drop the load-target derivation from `EffectivePlan` (back to `ConcurrencyPolicy.For(machine, profile)`, defaulting to `Local`) — *the regression itself* | **RED** ✓ |
| `EffectivePlan_FrozenNewRecruit_KeepsTheFullMeasuredWorkerCount` (Cli) + `UrlVariable_UnsetOrEmpty_IsLocal` (2 rows) | **M2**: blanket `ThirdPartyLive` — ignore the URL and throttle every NR run ("the lazy safe fix", which would satisfy the live test above) | **RED** ✓ |
| `EffectivePlan_UnknownAdapter_FailsSafeToThirdPartyLive` (Cli) + `Registry_AdHocLaunchableAdapter_IsUndeclared_AndThereforeThirdPartyLive` | **M3**: default an undeclared endpoint to `OnThisMachine` (flip the fail-safe) | **RED** ✓ |
| `HostEngineFactory_LiveEndpointRoutes_AreDeclaredByTheRegistry` (Lint) | **M4**: add an undeclared `NR_ENGINE_URL` read to the **gamedata** switch | **RED** ✓ |
| `PolicyOverride_CannotRaiseTheLoadLimit_OnALiveEngine` (Cli) | **M5**: delete the rejection in `ApplyPolicyOverride` | **RED** ✓ |
| `EffectivePlan_GameDataDomain_IsLocal_EvenWithTheLiveUrlSet` (Cli) + the lint gate | **M6**: declare the endpoint per-engine instead of per-domain (the "obvious simplification") | **RED** ✓ |

Plus, not mutation-tested individually: `Policy_ClampToLoadTarget_BindsAPlanTheOverrideBuilt_AndLeavesLocalPlansAlone`,
`UrlVariable_LoopbackOrFile_IsLocal`, `UrlVariable_Unparseable_IsThirdPartyLive_NotLocal`,
`Undeclared_IsThirdPartyLive_AndIsTheZeroValue`, `OnThisMachine_IgnoresTheEnvironment_*`,
`Registry_*_DeclareTheirRosterEndpointLive_AndTheirGameDataEndpointLocal`, `EnginesJson_*` (4).

**The first two are the pair that matters.** M1 makes the live test red while the frozen test stays green;
M2 does the reverse. Neither mutant can be made to pass both — which is the property "throttle everything
and call it safe" was designed to lack.

## 6. Results

- `dotnet test -p:TestProfile=core`: **Cli 123 passed** (was 117, +6), **Tests 1994 passed** (was 1968,
  +26). 0 failed, 0 skipped.
- Retired-knob lint gate (`RetiredEnvironmentKnobs_AreReadByNoProductionCodeOrFixture_AndSetByNoWorkflow`):
  **green**. `NR_ENGINE_URL` is endpoint configuration, not a performance knob — it does not set the worker
  count, the worker count is *derived* from it by the one policy. Its list (`NR_PARALLEL`,
  `BS_UI_KEEP_ALIVE`, `BSSPEC_DISABLE_WARM_REUSE`) is untouched.
- No conformance verdict changed: no engine, adapter, protocol or spec-evaluation code was touched.
  `docs/protocol-schema.json` and `ProtocolSchemaDriftTests` untouched.
- Analyzers-as-errors (CA1305/CA2007/CA1852, xUnit1051), AOT (no reflection in `Cli`/`TestKit`/`Telemetry`):
  build clean.

## 7. Remaining paths where a live third-party engine could still get a machine-width worker count

1. **A launchable adapter that lies.** `engines.json` `"endpoint": "local"` is taken at its word — an
   adapter author who declares `local` and then drives a third party's site gets `ceil(cpuCount × k)`
   workers. This is by construction (an adapter's declaration is the only thing we can know about it) and
   is the same trust model as `memPerInstanceBytes`. The default for *saying nothing* is the safe one.
2. **A loopback proxy to a remote host.** `NR_ENGINE_URL=http://localhost:8080` resolves `Local`; if that
   port is a reverse proxy to newrecruit.eu, the traffic is remote and the harness cannot see it. Deliberate
   — the alternative is throttling every developer running a genuine local mirror.
3. **`--policy workers=N` on a launchable adapter** is *already* impossible (`EngineHostLocator.Resolve`
   throws — there is no `--policy` channel to an `exec:`/`dotnet:` adapter), so an undeclared engine cannot
   be pushed above the limit by an override. Noted because it makes the rejection in §3 exhaustive in
   practice: `--policy` only ever reaches built-ins, all of which declare their endpoints.
4. **`bs-engine-host probe`** defaults to `https://www.newrecruit.eu` when `NR_ENGINE_URL` is unset
   (`ProbeCommand.cs:141`). It is a single-process, single-page diagnostic with no concurrency and no
   spec suite — one visitor, not a worker pool — so it is out of this fix's scope. It is the only place in
   the repo that reaches the live site *without* being asked to.

None of these can be reached by an ordinary `bs-spec run --all` against a built-in engine, which is the path
the regression lived on.
