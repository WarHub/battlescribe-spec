# Scenario Specs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A third spec domain, `scenario`, that drives one engine family's data editor and roster editor in one spec, joined by explicit hand-offs over a data directory.

**Architecture:** `ScenarioRunner` (TestKit) composes the existing `GameDataRunner` and `RosterRunner` through a new step-at-a-time entry point. It holds a data directory, seeded from the data editor's own export and written only by `saveData`, plus the saved rosters. Four hand-offs (`saveData`, `newRoster`, `saveRoster`, `openRoster`) map onto existing interface members and existing protocol v1.1 commands. No wire or interface change.

**Tech Stack:** .NET 10 (SDK pinned by `global.json`), xUnit v3 on Microsoft.Testing.Platform, YamlDotNet static context, IKVM (BattleScribe engine), Playwright (NR engines), Java agent (BattleScribe desktop apps).

**Spec:** `docs/adr/002-scenario-specs.md`

## Global Constraints

- Spec path `specs/scenario/{category}/{id}.yaml`. Domain string `"scenario"`. Model `ScenarioSpecFile`, namespace `BattleScribeSpec.Scenario`.
- Top-level step items are exactly one of `data:` (list of `GameDataStepDef`), `roster:` (list of `StepDef`) or `action:`. The `action:` value is one of `saveData`, `newRoster`, `saveRoster`, `openRoster`.
- Hand-off fields: `file` (required on `saveRoster` and `openRoster`, forbidden elsewhere) and `expectFailure` (optional on every hand-off).
- `setup` is `GameDataSetupDef` verbatim (`gameSystem`, `catalogues`, `edit`). `edit` is required.
- No change to `docs/protocol-schema.json`, `IRosterEngine`/`IGameDataEngine` members, or the YAML of roster/gamedata specs.
- Analyzers are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`). Run a plain `dotnet build` before pushing; `-p:FunctionalBuild=true` is for inner loops only.
- Tests pass `TestContext.Current.CancellationToken` (xUnit1051). Test code never reads the working directory (`tests/BannedSymbols.txt`).
- The app is normative (AGENTS.md): expectations come from `battlescribe-ui`. Where a store-direct engine differs, that is a finding.

## Review Focus

- A hand-off run before any roster exists (`saveRoster` before `newRoster`) must fail as a spec error naming the step, not crash in the engine. Pin it in Task 2's lint and runner tests.
- `openRoster` naming a `file` no earlier `saveRoster` wrote is the spec's mistake. It must never satisfy `expectFailure` (#25's rule). Pin it in a Task 2 runner test.
- A data editor whose export root is neither `gameSystem` nor `catalogue` must fail `saveData` with the root named. Pin it in Task 2.
- A second `newRoster`/`openRoster` on the same in-process engine must not see the previous data: no stale entry lookup, no lingering catalogue. Pin it in Task 3 (re-setup test) and Task 4.
- `run --all` with no domain flag must include scenarios. An adapter that advertises only one domain must report each scenario as skipped with the reason, never pass it. Pin it in Task 6.

---

### Task 1: Step-at-a-time runner entry points

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Roster/RosterRunner.cs`
- Modify: `src/BattleScribeSpec.TestKit/GameData/GameDataRunner.cs`
- Test: `tests/Features/GameDataRunnerBaseIdentityTests.cs` (new)

**Interfaces:**
- Produces, on `RosterRunner`:
  - `internal void BeginSteps(string specId, string? specDir)` resets per-run state the way `Run` does.
  - `internal void SetCatalogueIds(IReadOnlyList<string> ids)`.
  - `internal bool ExecuteStep(StepDef step, int stepIndex)`: the body of `Run`'s loop, including its try/catch and `NotifyStepCompleted`. Returns `false` when the run must stop.
  - `internal IReadOnlyList<string> Errors`, `internal IReadOnlyList<string> SkippedSteps`, `internal string? HarnessError`.
- Produces, on `GameDataRunner`: the same `BeginSteps`/`ExecuteStep`/`Errors`. There is no catalogue setter.
- Produces a new `GameDataRunner` constructor `GameDataRunner(IGameDataEngine engine, string? engineName = null, string? baseEngineName = null)`.
  - Per-engine override keys resolve to `engineName` when the spec has an entry for it, otherwise to `baseEngineName ?? engineName`. That is the rule `RosterRunner.OverrideKeyFor` already uses.
  - Snapshot resolution keeps using `engineName`.
- `Run` in both runners is re-expressed over these members. Its observable behaviour is unchanged.

- [ ] Write `GameDataRunnerBaseIdentityTests`, using a fake engine:
  - `Override_ForConcreteName_Wins`: the spec has an `engines:` entry for both `battlescribe-ui` and `battlescribe`, and the runner is built with (`battlescribe-ui`, `battlescribe`). The concrete entry applies.
  - `Override_FallsBackToBase`: only a `battlescribe` entry exists. It applies to the `battlescribe-ui` run.
  - `NoBase_KeepsTodaysRule`: no base name is given. A `battlescribe` entry does not apply to a `battlescribe-ui` run.
- [ ] Run them and see them fail. Implement. Then run `--filter "FullyQualifiedName~GameDataRunnerBaseIdentityTests"` and see it pass.
- [ ] Regression check: run the `smoke-bs` profile, then `artifacts/bin/BattleScribeSpec.Tests/debug/BattleScribeSpec.Tests --filter "Engine=BsRoster|Engine=BsGameData"`. Expected: the same pass and skip counts as before the change.
- [ ] Commit: `refactor(testkit): step-at-a-time entry points on both runners`.

### Task 2: Scenario model, loader, data directory, runner

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Scenario/ScenarioSpecModels.cs`, `ScenarioDataDirectory.cs`, `ScenarioRunner.cs`
- Modify: `src/BattleScribeSpec.TestKit/SpecLoader.cs`, `SpecYamlStaticContext.cs`, `Roster/IRosterEngine.cs` (doc only: `Cleanup` followed by `Setup`/`SetupFromFiles` yields a fresh engine)
- Test: `tests/Features/ScenarioRunnerTests.cs`, `tests/Features/ScenarioDataDirectoryTests.cs` (new)

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `ScenarioSpecFile : SpecFileBase { required GameDataSetupDef Setup; required List<ScenarioStepDef> Steps; }`
  - `ScenarioStepDef { List<GameDataStepDef>? Data; List<StepDef>? Roster; string? Action; string? File; ExpectFailureDef? ExpectFailure; }`
  - `ScenarioHandoffs` constants: `SaveData = "saveData"`, `NewRoster = "newRoster"`, `SaveRoster = "saveRoster"`, `OpenRoster = "openRoster"`, plus `All`.
  - `SpecLoader.FindScenarioSpecsDirectory()`, `DiscoverScenarioSpecs(string dir)`, `LoadScenario(string path)`, `LoadScenarioFromYaml(string yaml, string? defaultId = null)`, `ResolveScenarioSpec(string specName)`.
  - `internal sealed class ScenarioDataDirectory`:
    - `void Write(string xml)` reads the root element and `id`. It replaces an existing id in place and appends a new one.
    - `IReadOnlyList<(string FileName, string Content)> Files` in insertion order.
    - `IReadOnlyList<string> CatalogueIds`.
  - `public sealed class ScenarioRunner(IGameDataEngine data, IRosterEngine roster, string engineName, string? baseEngineName = null)` with:
    - `SpecResult Run(ScenarioSpecFile spec)`;
    - `bool UpdateSnapshots`, `OnDiverge`;
    - `Action<int, GameDataStepDef, GameDataState>? OnDataStepCompleted`;
    - `Action<int, StepDef, RosterState, IReadOnlyList<ValidationErrorState>>? OnRosterStepCompleted`.
- Also fix `SpecLoader.DiscoverEmbeddedSpecs` so it yields only `specs/roster/**` resources. Today it sweeps every tree in as roster. Category and id are parsed after the `roster` segment.

**Behaviour:**
- Setup: `data.Setup` + `data.OpenFile(edit)`.
- Seed: `OpenFile(id)` + `ExportActiveFile()` for the game system and then each catalogue in setup order, then `OpenFile(edit)`.
- Steps: walk the items with one global step index. A block's steps go to its runner's `ExecuteStep`. After each step, the errors the runner added are copied into the scenario's list.
- Hand-offs follow the ADR table. Spec-side checks run outside the `expectFailure` window: an unknown `file`, or a hand-off that needs a roster before any `newRoster`/`openRoster`. Engine calls run inside it.
- A non-empty error list from `SetupFromFiles` becomes an `InvalidOperationException` naming the hand-off.
- `finally`: `data.Cleanup()` and `roster.Cleanup()`, each guarded.

- [ ] Write `ScenarioDataDirectoryTests`:
  - a `.gst` and a `.cat` named by id;
  - rewriting an id keeps its position;
  - an unknown root throws, naming the root;
  - `CatalogueIds` excludes the game system.
- [ ] Write `ScenarioRunnerTests` against recording fakes (an `IGameDataEngine` whose export reflects in-memory edits, and an `IRosterEngine` that records `SetupFromFiles` payloads and `LoadRoster` input):
  - `Seed_ExportsEveryFile_ThenReopensEdit`.
  - `UnsavedEdit_DoesNotReachNewRoster`.
  - `SavedEdit_ReachesNewRoster`.
  - `OpenRoster_LoadsSavedXml_AfterSetupFromCurrentData`.
  - `OpenRoster_UnknownFile_IsSpecError_EvenUnderExpectFailure`.
  - `SaveRoster_BeforeAnyRoster_IsSpecError`.
  - `ExpectFailure_OnOpenRoster_Satisfied_RunContinues`.
  - `StepIndex_IsGlobalAcrossBlocks`: an error in the second block's first step reads `Step N` with N counted over all items.
  - `AddForce_DefaultsCatalogue_FromDataDirectory`.
  - `Loads_FromYaml`: a minimal scenario YAML round-trips through `LoadScenarioFromYaml`.
- [ ] Run the tests and see them fail. Implement. Run them again and see them pass.
- [ ] Commit: `feat(testkit): scenario spec model, data directory and runner (#273)`.

### Task 3: `battlescribe` roster engine — file setup is a full equal of `Setup`

**Files:**
- Modify: `src/BattleScribeSpec.BattleScribe/BattleScribeEngine.cs`, `BattleScribeRosterEngine.cs`
- Test: `tests/Features/BsFileSetupParityTests.cs` (new; `Category=Conformance` is NOT set, and it carries no `Engine` trait)

**Interfaces:** none new. `SetupFromFiles` builds, from the loaded Java model, everything `Setup` builds from the Protocol model:
- the entry lookup behind `GetEntryById`;
- recorded cost types (for `SetCostLimit` and cost ordering);
- the constraint-declarer index;
- force entries from the game system **and** catalogues.

Both setup paths start from a clean slate: loaded catalogues, lookups, cost types, declarer index and the auto-select flag are all reset.

- [ ] Write `BsFileSetupParityTests.EveryInlineRosterSpec_PassesThroughFileSetup`:
  - A wrapper `IRosterEngine` routes `Setup(gs, cats)` to `SetupFromFiles` over `CatXmlGenerator` output.
  - Run every `specs/roster` spec without `dataSource` through `RosterRunner`.
  - Assert the per-spec verdict equals the direct engine's verdict.
- [ ] Write `SecondFileSetup_SeesOnlyNewData`: set up from files A and select an entry; then `Cleanup` + `SetupFromFiles(B)`, where B lacks that entry and adds another. The old entry is unknown and the new one is selectable.
- [ ] Run them and see them fail. Implement. Run them again and see them pass. The `BsRoster` lane must be unchanged.
- [ ] Commit: `fix(battlescribe): SetupFromFiles builds what Setup builds; re-setup starts clean`.

### Task 4: NR roster engines — file setup and re-setup

**Files:**
- Modify: `src/BattleScribeSpec.NrRosterUiDriver/NrRosterUiEngine.cs`, possibly `src/BattleScribeSpec.NewRecruit/NewRecruitRosterEngine.cs`
- Test: extend the frozen fixtures' feature tests, or add `tests/Features/NrFileSetupTests.cs` behind the frozen HAR.

**Interfaces:** none new.
- `NrRosterUiEngine.SetupFromFiles` records the game system, catalogues and entry and force-entry names from the XML. After it, `AddForce`/`SelectEntry` work exactly as after `Setup`.
- `Cleanup` followed by `SetupFromFiles` with the same game-system id loads the new data. Today the load-once guard skips it.
- `NewRecruitRosterEngine`: verify `Cleanup` → `SetupFromFiles` → `LoadRoster` round-trips on the frozen HAR. Fix anything that does not.

- [ ] Write tests: a frozen `newrecruit` and a frozen `newrecruit-ui` roster engine. `SetupFromFiles` (XML from `CatXmlGenerator`), then `AddForce` + `SelectEntry`, assert state. Then `Cleanup` + `SetupFromFiles` with an edited cost, then select again, and assert the new cost.
- [ ] Run them and see them fail. Implement. Run them again and see them pass. `smoke-nr-ui` is unchanged.
- [ ] Commit: `fix(newrecruit): file setup records what Setup records; re-setup reloads`.

### Task 5: Test lanes, registry, lint, inventories

**Files:**
- Create: `tests/Infrastructure/ScenarioConformanceTestBase.cs`, `tests/Conformance/BsScenarioConformanceTests.cs`, `BsScenarioUiConformanceTests.cs`, `FrozenNrScenarioConformanceTests.cs`, `FrozenNrUiScenarioConformanceTests.cs`, fixtures as needed, `tests/Infrastructure/ScenarioSpecLintTests.cs`
- Modify: `tests/TestProfiles/EngineLanes.cs`, `TestProfiles.cs`, `tests/Infrastructure/LaneCompositionTests.cs`, `TheoryRowIdentityTests.cs`, `tests/Features/SpecNameResolutionTests.cs`, `tests/Infrastructure/DocumentedSpecCountLintTests.cs`, `README.md`, `AGENTS.md`, `.github/workflows/ci.yml`, `.vscode/settings.json` (narrow the spec-schema mapping to `specs/roster/**`)

**Interfaces:**
- Consumes: Task 2's `ScenarioRunner`, `SpecLoader.*Scenario*`.
- Lane traits: `BsScenario`, `FrozenNrScenario`, `FrozenNrUiScenario` (pre-push `Whole`) and `BsScenarioUi` (`DesktopApp`, pre-push `None`).
- Profiles: `bs-ui-scenario` (claims `BsScenarioUi`) and `smoke-scenario`.
- CI: `thorough-ui-bs` runs `bs-ui-scenario`.
- Rows carry the spec name `category/id`. Roster blocks use the base identity `battlescribe` for `battlescribe-ui`.

- [ ] Lint rules in `ScenarioSpecLintTests`, each with a broken sample in `EveryBrokenSample_IsRejected`:
  - formatted;
  - required fields;
  - id equals the filename and category equals the directory;
  - each item is exactly one kind;
  - the hand-off action is known;
  - `file` is present exactly where required;
  - `setup.edit` names a loaded file;
  - no roster block and no `saveRoster` before a `newRoster`/`openRoster`;
  - `openRoster.file` was saved earlier;
  - step ids are unique across the spec;
  - nested steps pass the roster and gamedata known-action and required-parameter checks (reuse those helpers);
  - the last item is a block whose last step is an assertion.
- [ ] Wire the lanes and registry until `--test-profile lint` passes and every `TestProfileRegistryTests` / `CiWorkflowTests` / `LaneCompositionTests` case passes.
- [ ] Commit: `test: scenario lanes, lint and inventories (#273)`.

### Task 6: CLI, batch and engine host

**Files:**
- Modify: `src/BattleScribeSpec.Cli/SpecLoading.cs`, `EngineSpec.cs`, `Commands/RunCommand.cs`, `RunBatch.cs`, `CompareCommand.cs`, `src/BattleScribeSpec.EngineHost/HostSpecLoading.cs`, `src/BattleScribeSpec.TestKit/Batch/SpecSuiteRunner.cs`, `SpecSuiteOptions.cs`, `SpecSuiteOutput.cs`
- Test: `tests/BattleScribeSpec.Cli.Tests/*` (resolution, run of one scenario on `battlescribe`, `run --all --scenario`)

**Interfaces:**
- Consumes: Task 2.
- `EngineDomain.Scenario`. A `--scenario` flag, mutually exclusive with `--roster`/`--gamedata`.
- Inference from `specs/scenario/`.
- The load target of a scenario selection is the stricter of the two endpoints.
- `run --all` / `compare` include the domain by default.
- The batch describe gate requires both `roster` and `gamedata`; otherwise every scenario is reported `skipped` with "engine does not support both roster and gamedata".
- One `AdapterProcess` backs a `JsonProtocolGameDataEngine` and a `JsonProtocolEngine` for each scenario spec.
- `bs-spec run` artifact flags (`--screenshots`, `--record`, `--timeline`, `--save-roster`) are refused for scenarios with a message. `--all-steps`/`--json` dumps cover both block kinds. `--update-snapshots` works.

- [ ] CLI tests: `InferEngineType` on a scenario path; `bs-spec run <scenario> --engine battlescribe` exits 0 on a passing spec; `run --all --scenario --engine battlescribe` runs every scenario; an adapter advertising roster only skips scenarios with that reason.
- [ ] Commit: `feat(cli): run, run --all and compare cover scenario specs (#273)`.

### Task 7: First scenarios, checked against the apps

**Files:**
- Create: `specs/scenario/handoff/*.yaml`

The specs:
- `handoff-saved-edit-reaches-new-roster`: an entry's cost is edited and saved; a new roster selects it at the new cost.
- `handoff-unsaved-edit-stays-in-editor`: an edit that is not saved does not reach a new roster; after `saveData` it does.
- `handoff-reopen-roster-against-edited-data`: a roster is built and saved, the cost is edited and saved, and the roster is reopened. The expectation is whatever the BattleScribe desktop apps do.
- `handoff-entry-added-in-editor`: an entry added in the data editor (declared id) is selectable in a new roster.
- `handoff-save-each-file`: two files edited, each saved after `openFile`. A new roster sees both.

- [ ] Write each spec. Run it on `battlescribe-ui` first (`bs-spec run --engine battlescribe --ui <spec>`, or the `bs-ui-scenario` profile) and record what the app does. Then run `battlescribe`, `newrecruit` and `newrecruit-ui`. Resolve each divergence the AGENTS.md way: fix the adapter, or override with a reason.
- [ ] `pwsh -File tools/format-specs.ps1`, lint, then the `pre-push` profile.
- [ ] Commit: `spec(scenario): first hand-off scenarios (#273)`.

### Task 8: Docs, follow-ups, PR

- [ ] `docs/running-tests.md`, `docs/adapter-protocol.md` (a short "scenario specs need both domains" note), `.agents/skills/writing-specs`, AGENTS.md key files and lanes.
- [ ] File follow-ups as sub-issues of #73, each with Type, Priority and Size set and the right blocked-by links (AGENTS.md "Issues and the backlog"):
  - BS-UI shared data folder and the reload prompt (#280-adjacent);
  - a scenario JSON Schema.
- [ ] Push, open the PR against `main`, bind it, watch CI to green.
