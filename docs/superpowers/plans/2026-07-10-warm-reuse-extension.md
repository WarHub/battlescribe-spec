# Warm-reuse extension: per-domain control, BS-UI gamedata, measurements — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Extend host-side warm engine reuse beyond NewRecruit to the BattleScribe Data Editor (gamedata `battlescribe-ui`), make reuse controllable per domain (so the blocked BS-UI *roster* path stays cold and unchanged), probe whether BS-UI *roster* reuse is possible at all, and add reproducible performance measurements + per-engine applicability docs to PR #302.

**Architecture:** Builds on the warm-reuse mechanism already on this branch (`AdapterOptions.ReuseEngineAcrossSetups` + `AdapterHandler`). Investigation (docs below) established: the BattleScribe **Data Editor** already loads catalogue/gst files by path at runtime (`gamedataLoadFilesAction` → `openCataloguePath`), so gamedata `battlescribe-ui` can warm-reuse with a small C# fix; the **Roster Editor** picks a game system from a startup-scanned combo with no runtime reload, so roster `battlescribe-ui` is blocked pending a live probe. Reuse must therefore be **per-domain**, because one `battlescribe-ui` host process serves both domains with independent engines (`RosterEditor.jar` vs `DataEditor.jar`).

**Tech Stack:** .NET 10, xUnit, the NDJSON adapter protocol (TestKit), Playwright (NR), the BattleScribe desktop app + `bs-ui-java-agent` (BS-UI), PowerShell/bash for the benchmark harness.

## Global Constraints

- **Per-domain, opt-in, default off.** No engine changes behavior unless its domain flag is explicitly set. The `battlescribe-ui` **roster** path must remain byte-for-byte its current cold-start behavior (zero risk).
- **Enable warm-reuse for:** `newrecruit`/`newrecruit-ui` (both domains, as today) and `battlescribe-ui` **gamedata only**. `battlescribe` (in-process) and `battlescribe-ui` **roster** stay cold.
- **Correctness first.** Warm-reuse must produce identical conformance verdicts to cold runs. Any stale-data leakage across specs is a blocking defect. The gate is: a warm two-game-system sequence yields the same state/export as two cold runs.
- **No new protocol message**; no `docs/protocol-schema.json` / `ProtocolSchemaDriftTests` changes.
- **Measurements must be reproducible**: a committed benchmark script + an env toggle to disable reuse (ablation), and a results doc stating the environment. Real numbers only — never fabricate; if an engine's live run can't execute in an environment, say so.
- Repo conventions: `dotnet build` before `--no-build`; `TreatWarningsAsErrors=true`; xUnit1051 → `TestContext.Current.CancellationToken`; analyzers-as-errors (`new()` target-typed, formatting).
- Investigation references (read for context): `.superpowers/sdd/` is scratch; the feasibility findings are summarized in this plan's task Context blocks with `file:line` anchors.

---

### Task 1: Split reuse into per-domain flags

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs`
- Modify: `src/BattleScribeSpec.EngineHost/ServeCommand.cs`
- Test: `tests/Features/AdapterHandlerTests.cs`

**Interfaces:**
- Replaces `AdapterOptions.ReuseEngineAcrossSetups` (single) with `ReuseRosterEngineAcrossSetups` and `ReuseGameDataEngineAcrossSetups` (both `bool`, default false).
- Consumes: `IRosterEngine.Cleanup()`, `IGameDataEngine.Cleanup()`.

**Context:** Today `ReuseEngineAcrossSetups` is one flag threaded into `HandleSetup`/`HandleSetupFromFiles` (roster) and `HandleGameDataSetup` (gamedata) and `HandleTeardown` (both). A single `battlescribe-ui` process serves both roster (`engine`) and gamedata (`gdEngine`) with separate instances, and only gamedata may reuse — so the flag must be per-domain.

- [ ] **Step 1: Update the reuse tests to the per-domain API**

In `tests/Features/AdapterHandlerTests.cs`, the three reuse tests currently set `ReuseEngineAcrossSetups = true`. Change the roster-driven tests (`Reuse_KeepsOneEngine_AcrossSetupTeardownCycles`, `Reuse_SelfHeals_WhenCleanupThrows`) to set `ReuseRosterEngineAcrossSetups = true`; leave `NoReuse_DisposesAndRecreates_PerSetup` setting neither (both default false). Add one new test that a gamedata engine reuses under `ReuseGameDataEngineAcrossSetups = true` but a roster engine in the same options does NOT reuse when only the gamedata flag is set — proving independence:

```csharp
[Fact]
public async Task PerDomainFlags_AreIndependent()
{
    var rosterEngines = new List<CountingRosterEngine>();
    var connection = new InMemoryAdapterConnection(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => { var e = new CountingRosterEngine(); rosterEngines.Add(e); return e; },
                Name = "battlescribe-ui",
                ReuseRosterEngineAcrossSetups = false,      // roster stays cold
                ReuseGameDataEngineAcrossSetups = true,     // gamedata warm (no gd factory here → roster-only proof)
            },
            input, output, ct));

    var ct = TestContext.Current.CancellationToken;
    var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };
    await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
    await connection.SendCommandAsync(new TeardownCommand(), ct);
    await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
    await connection.SendCommandAsync(new TeardownCommand(), ct);
    await connection.DisposeAsync();

    Assert.Equal(2, rosterEngines.Count);                      // roster recreated (reuse flag false for its domain)
    Assert.All(rosterEngines, e => Assert.Equal(1, e.DisposeCalls));
    Assert.All(rosterEngines, e => Assert.Equal(0, e.CleanupCalls));
}
```

- [ ] **Step 2: Run tests to confirm they fail to compile**

Run: `dotnet build`
Expected: FAILS — `ReuseRosterEngineAcrossSetups`/`ReuseGameDataEngineAcrossSetups` don't exist yet.

- [ ] **Step 3: Replace the single option with two in `AdapterOptions`**

In `AdapterHandler.cs`, delete `ReuseEngineAcrossSetups` and add:

```csharp
/// <summary>
/// When true, keep the roster engine alive across setup/teardown cycles, resetting it via
/// <see cref="IRosterEngine.Cleanup"/> between specs (self-heal to dispose+recreate on failure)
/// instead of disposing and recreating. See <see cref="ReuseGameDataEngineAcrossSetups"/>.
/// </summary>
public bool ReuseRosterEngineAcrossSetups { get; init; }

/// <summary>
/// Gamedata counterpart of <see cref="ReuseRosterEngineAcrossSetups"/>. Independent because a
/// single host process serves both domains with separate engines and their warm-reuse feasibility
/// differs (e.g. battlescribe-ui: gamedata reusable, roster not). Default false.
/// </summary>
public bool ReuseGameDataEngineAcrossSetups { get; init; }
```

- [ ] **Step 4: Thread the per-domain flags through dispatch and handlers**

In `RunAsync`'s switch, pass the roster flag to the roster setups, the gamedata flag to gamedata setup, and BOTH to teardown:

```csharp
SetupCommand setup => HandleSetup(setup, engineFactory, options.ReuseRosterEngineAcrossSetups, ref engine, out catalogueIds),
SetupFromFilesCommand setupFiles => HandleSetupFromFiles(setupFiles, engineFactory, options.ReuseRosterEngineAcrossSetups, ref engine, out catalogueIds),
TeardownCommand => HandleTeardown(options.ReuseRosterEngineAcrossSetups, options.ReuseGameDataEngineAcrossSetups, ref engine, ref gdEngine),
GameDataSetupCommand gdSetup => HandleGameDataSetup(gdSetup, options, ref gdEngine),
```

Update `HandleTeardown`'s signature and body to take both flags:

```csharp
private static ProtocolResponse HandleTeardown(
    bool reuseRoster, bool reuseGameData, ref IRosterEngine? engine, ref GameData.IGameDataEngine? gdEngine)
{
    engine = ResetOrDispose(engine, reuseRoster, e => e.Cleanup());
    gdEngine = ResetOrDispose(gdEngine, reuseGameData, e => e.Cleanup());
    return new TeardownResult();
}
```

`HandleSetup`/`HandleSetupFromFiles` keep their `bool reuse` parameter (now fed the roster flag). `HandleGameDataSetup` reads `options.ReuseGameDataEngineAcrossSetups` internally (replace the `options.ReuseEngineAcrossSetups` reference). `ResetOrDispose` is unchanged.

- [ ] **Step 5: Update `ServeCommand.BuildOptions`**

Replace the single `ReuseEngineAcrossSetups = name is "newrecruit" or "newrecruit-ui",` line with:

```csharp
// Warm-reuse is per domain: NR reloads data per spec in both domains; battlescribe-ui can
// warm-reuse only its Data Editor (gamedata) — the Roster Editor loads game data at JVM
// startup with no runtime reload, so its roster domain stays cold. battlescribe (in-process)
// gains nothing. See docs/warm-reuse.md.
ReuseRosterEngineAcrossSetups = name is "newrecruit" or "newrecruit-ui",
ReuseGameDataEngineAcrossSetups = name is "newrecruit" or "newrecruit-ui" or "battlescribe-ui",
```

- [ ] **Step 6: Build and run the AdapterHandler tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AdapterHandlerTests"`
Expected: all pass (the renamed reuse tests + the new independence test + the 6 originals).

- [ ] **Step 7: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs src/BattleScribeSpec.EngineHost/ServeCommand.cs tests/Features/AdapterHandlerTests.cs
git commit -m "feat(host): per-domain warm-reuse flags; enable battlescribe-ui gamedata (#271)"
```

---

### Task 2: Warm-reuse the BattleScribe Data Editor (gamedata `battlescribe-ui`)

**Files:**
- Modify: `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiEngine.cs`
- Modify: `src/BattleScribeSpec.EngineHost/HostEngineFactory.cs`
- Possibly modify: `src/bs-ui-java-agent/src/bsspec/uiagent/DataEditorActions.java` (only if the load-order hardening is needed)

**Interfaces:**
- Consumes: `ReuseGameDataEngineAcrossSetups` (Task 1), `BsGameDataUiEngine.KeepAlive`.

**Context (feasibility):** `BsGameDataUiEngine.SetupAsync` cold path stages files then loads them via `gamedataLoadFilesAction` (`BsGameDataUiEngine.cs:320-330`), which the Java `openCataloguePath` (`DataEditorActions.java:124-178`) reads by explicit path at runtime — a genuine runtime loader used every spec. The **warm** branch (`:273-283`) only re-stages and `return []`, never re-loading. With Task 1's gamedata reuse ON, `AdapterHandler` keeps the `BsGameDataUiEngine` instance across setups and calls `Cleanup()` between specs; but `Cleanup()`→`CleanupAsync()` disposes the app unless `KeepAlive` is true. So warm-reuse needs BOTH: the host sets `KeepAlive=true` on the gamedata engine, and the warm branch actually re-loads the new files.

- [ ] **Step 1: Extract the staged-file load into a helper**

In `BsGameDataUiEngine.cs`, extract the cold path's load block (`:320-330`) into a private method so both paths share it:

```csharp
private async Task LoadStagedFilesAsync(
    ProtocolGameSystem gameSystem, IReadOnlyList<(string FileName, string Content)> files)
{
    var gsDir = Path.Combine(_app!.DataDirectoryPath, gameSystem.Id);
    var gstPath = Path.Combine(gsDir, "system.gst");
    var catPaths = files.Where(f => f.FileName.EndsWith(".cat", StringComparison.Ordinal))
        .Select(f => Path.Combine(gsDir, f.FileName)).ToArray();
    var loadParams = new JsonObject
    {
        ["gstPath"] = gstPath,
        ["catPaths"] = new JsonArray([.. catPaths.Select(p => JsonValue.Create(p))]),
    };
    await CallActionAsync("gamedataLoadFilesAction", loadParams);
}
```

Replace the inline cold-path block with `await LoadStagedFilesAsync(gameSystem, files);`.

- [ ] **Step 2: Re-load in the warm branch**

In the `KeepAlive && _app is not null && _client is not null` warm branch (`:273-283`), after `StageDataFilesAsync(...)`, replace `return [];` with a load of the newly-staged files, so the running editor opens the new game system:

```csharp
var warmFiles = BuildXmlFiles(gameSystem, catalogues);
await BsUiDataStaging.StageDataFilesAsync(_app.DataDirectoryPath, gameSystem, catalogues, warmFiles);
await LoadStagedFilesAsync(gameSystem, warmFiles);
Console.Error.WriteLine("[bs-gamedata-ui] Warm start: loaded new game data into existing instance.");
return [];
```

- [ ] **Step 3: Verify/harden the Java load order (gst before catalogues)**

Read `DataEditorActions.java`'s `gamedataLoadFilesAction`/`loadFiles` (~`:87-108`). Confirm it opens the `.gst` before the primary catalogue so a **brand-new** game system is resolved before its catalogues (the investigation flagged that `loadFiles` may open only the primary=first catalogue). If it does NOT open the gst first, adjust `loadFiles` to open `gstPath` (via `openCataloguePath`) before the catalogues. If the Java agent's compiled classes are used at runtime, rebuild the agent jar per `src/bs-ui-java-agent/build.ps1` (or the repo's documented build) and note it in the report. If it already opens the gst first, make no Java change and state that.

- [ ] **Step 4: Set `KeepAlive=true` for the BS-UI gamedata engine in the host**

In `HostEngineFactory.CreateGameDataEngineAsync`, the `battlescribe-ui` case (`HostEngineFactory.cs:79-86`) returns `new BsGameDataUiEngine(options)`. Change it to `new BsGameDataUiEngine(options) { KeepAlive = true }` so `Cleanup()` between specs preserves the app and the warm `Setup` path runs. (The roster case is untouched — `BsUiRosterEngine` keeps `KeepAlive=false`, so its app stays cold.)

- [ ] **Step 5: Local warm-vs-cold correctness check (gate)**

Frozen BS-UI data + JDK are present locally under `lib/battlescribe` / `lib/liberica-jdk`. Run two `battlescribe-ui` gamedata specs that use **different game systems** through the host warm path and confirm identical results to a cold run. Use the CLI:

```bash
dotnet build
# Warm (default now): two different-system gamedata specs, one process
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all \
  --engine battlescribe-ui --gamedata \
  --filter "<two gamedata spec ids using different game systems>" \
  --expected-failures battlescribe-ui --workers 1 2> /tmp/bsgd-warm.stderr; echo "exit=$?"
grep -c "Data Editor GameData UI\|Warm start: loaded new game data" /tmp/bsgd-warm.stderr
```

Expected: exit 0 (under `--expected-failures`), the run executes both specs, and the stderr shows **one** app launch (`NR/BattleScribe Data Editor` creation line once) plus a `Warm start: loaded new game data` line for the second spec — proving the app was reused and reloaded. Then run the same filter with warm-reuse disabled (Task 4's env toggle, or temporarily forcing `KeepAlive=false`) and confirm the **same pass/fail verdicts**. A verdict difference between warm and cold is a blocking defect — investigate stale-data leakage (gst-first order, id reuse) before proceeding.

If the BattleScribe app cannot launch in this environment (no display/JDK error rather than a spec failure), record that the warm correctness check could not run here and that CI (`thorough-ui-bs`) is the gate; do NOT claim it passed.

- [ ] **Step 6: Commit**

```bash
git add src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiEngine.cs src/BattleScribeSpec.EngineHost/HostEngineFactory.cs
# add the Java file + rebuilt agent jar only if Step 3 changed them
git commit -m "feat(host): warm-reuse the BattleScribe Data Editor across gamedata specs (#271)"
```

---

### Task 3: Probe BS-UI roster reuse feasibility (spike; conditional implementation)

**Files:**
- Investigation only, then EITHER modify `src/BattleScribeSpec.BsRosterUiDriver/BsUiRosterEngine.cs` + `HostEngineFactory.cs` + `ServeCommand.cs` (if feasible) OR add a short section to `docs/warm-reuse.md` (Task 4) documenting the blocker.

**Interfaces:** none produced unless implementation proceeds.

**Context (the open question):** The Roster Editor selects a game system by display name from `#cboGameSystem` (`RosterActions.java:205-269`), populated from a startup scan of the data directory. There is no runtime path-load and no agent-reachable rescan. The ONE unknown: whether BattleScribe's **New Roster dialog re-enumerates game systems from disk each time it opens**. If it does, roster warm-reuse works with no Java change; if not, it needs a BS-app restart (blocked).

- [ ] **Step 1: Run the probe (requires the local BS app stack)**

Drive the Roster Editor via the agent: `Setup` with game system A (id `gs-a`, name `System A`), then WITHOUT disposing, stage a second game system B (id `gs-b`, name `System B`) into the data directory and open the **New Roster** dialog, then read `#cboGameSystem`'s items. Do this with a small throwaway harness (an xUnit `[Fact]` tagged `Category=Manual`, or a scratch console using `BsUiRosterEngine`/`AgentClient` + `BsUiDataStaging`). Report whether `System B` appears in the combo.

If the BS app can't launch in this environment, record that the probe could not run and STOP here — mark roster reuse "unresolved; document as blocked pending a live probe" and skip to Task 4. Do not guess.

- [ ] **Step 2a: If `System B` appears (feasible) — implement roster warm-reuse**

Then the New Roster dialog rescans. Update `BsUiRosterEngine.SetupAsync`'s warm branch (`:209-227`) so that after `CloseCurrentRosterIfOpenAsync()` + `StageDataFilesAsync`, the next `createRosterAction` opens a fresh New Roster dialog (which now lists the new system). Set `KeepAlive=true` for the BS-UI roster engine in `HostEngineFactory` and flip `ReuseRosterEngineAcrossSetups` to include `battlescribe-ui` in `ServeCommand`. Then run the Task 2 Step 5 correctness check for **roster** (two different-system roster specs, warm vs cold, identical verdicts). This must pass or the change is reverted.

- [ ] **Step 2b: If `System B` does NOT appear (blocked) — document it**

Leave `battlescribe-ui` roster cold (no code change). Record the probe result; Task 4's `docs/warm-reuse.md` states roster `battlescribe-ui` cannot warm-reuse across game systems without a BS-app restart, with the probe evidence, and lists the same-system grouping fallback as possible future work.

- [ ] **Step 3: Commit (whichever branch was taken)**

```bash
# 2a: git add the roster driver + host wiring; commit "feat(host): warm-reuse BattleScribe Roster Editor across specs (#271)"
# 2b: no code commit here — the doc lands in Task 4
```

---

### Task 4: Performance measurements + per-engine applicability docs

**Files:**
- Modify: `src/BattleScribeSpec.EngineHost/ServeCommand.cs` (ablation env toggle)
- Create: `scripts/bench-warm-reuse.ps1` (and/or `.sh`)
- Create: `docs/warm-reuse.md`
- Modify: `README.md` (short pointer), `docs/superpowers/plans/2026-07-09-nr-ui-warm-reuse.md` (link the results)

**Interfaces:** none.

**Context:** The user asked for actual numbers in #302: how much warm-reuse saves and the test-suite timings. Reuse is on by default for the enabled engines; add an env toggle so cold (ablation) runs are reproducible, then a script that times warm vs cold on a fixed spec set.

- [ ] **Step 1: Add an ablation env toggle**

In `ServeCommand.BuildOptions`, gate the reuse flags so setting `BSSPEC_DISABLE_WARM_REUSE=1` forces cold (for measurement/diagnosis):

```csharp
var reuseDisabled = Environment.GetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE") == "1";
...
ReuseRosterEngineAcrossSetups = !reuseDisabled && name is "newrecruit" or "newrecruit-ui",
ReuseGameDataEngineAcrossSetups = !reuseDisabled && name is "newrecruit" or "newrecruit-ui" or "battlescribe-ui",
```

(If Task 3 enabled roster `battlescribe-ui`, include it in the roster line.)

- [ ] **Step 2: Write the benchmark script**

Create `scripts/bench-warm-reuse.ps1` that, for a given `--engine` + `--filter` + `--gamedata|--roster`, runs the same `bs-spec run --all … --workers 1` batch twice — once warm (default) and once cold (`$env:BSSPEC_DISABLE_WARM_REUSE='1'`) — timing each with `Measure-Command`, and prints a small table: spec count, warm wall-time, cold wall-time, absolute saving, per-spec saving, and speedup. It must build first (`dotnet build`) and fail loudly if the engine can't run (non-zero from a driver/launch error vs a spec failure). Keep it dependency-free (no external modules).

- [ ] **Step 3: Capture real numbers**

Run the script for at least:
- `newrecruit-ui --roster` over a fixed set of ~8 NR-applicable roster specs.
- `battlescribe-ui --gamedata` over ~6 gamedata specs spanning ≥2 game systems.

Record the actual output. If an engine cannot run locally (Playwright/JDK/display absent), note it and mark those numbers "to be captured on CI (`nr-conformance` / `thorough-ui-bs`)" rather than inventing them.

- [ ] **Step 4: Write `docs/warm-reuse.md`**

Document: (1) what host warm-reuse is and how it maps to the in-process pool; (2) the **per-engine applicability table** — `newrecruit`/`newrecruit-ui` (both domains, web app reloads data), `battlescribe-ui` gamedata (Data Editor runtime file load), `battlescribe-ui` roster (blocked/enabled per Task 3 outcome, with the reason), `battlescribe` in-process (no benefit); (3) the **measured numbers** from Step 3 (or the CI-capture note); (4) the `BSSPEC_DISABLE_WARM_REUSE` toggle and the `scripts/bench-warm-reuse.ps1` harness. Add a one-line pointer from `README.md` and link it from the NR-UI plan doc.

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.EngineHost/ServeCommand.cs scripts/bench-warm-reuse.ps1 docs/warm-reuse.md README.md docs/superpowers/plans/2026-07-09-nr-ui-warm-reuse.md
git commit -m "docs,perf: warm-reuse benchmark harness, measurements, and per-engine applicability (#271)"
```

---

## Final verification (before updating the PR)

- [ ] `dotnet build` clean; `dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"` green.
- [ ] `git diff --name-only main...HEAD` includes no `docs/protocol-schema.json` change.
- [ ] Warm-vs-cold correctness confirmed for every enabled engine/domain (identical verdicts), or the gap explicitly deferred to CI with a stated reason.
- [ ] Retitle PR #302 to reflect the widened scope (UI-engine warm-reuse + measurements) and update its body with the measured numbers and the per-engine table. Push (`--force-with-lease` if rebased).

## Out of scope (follow-ups)

- BS-UI **roster** warm-reuse if the Task 3 probe shows the dialog does not rescan (would need a BS-app data-directory rescan capability or same-system spec grouping).
- Cross-process browser/app sharing (one browser/app, N contexts across worker processes).
- `AdapterProcess` stderr forwarding (issue #303) — improves benchmark observability but not required.
