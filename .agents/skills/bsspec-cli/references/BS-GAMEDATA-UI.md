# BattleScribe GameData UI Driver

Drives the BattleScribe desktop **Data Editor** via the Java agent for GameData conformance
testing (`--engine battlescribe --ui`, gamedata domain). Hybrid pattern: mutations via real
UI interactions (agent RPC → JavaFX scene manipulation), state reads via Java model traversal.
See the parent [SKILL.md](../SKILL.md) for CLI verbs; this reference covers the driver internals.

## Architecture

```
BsGameDataUiEngine (IGameDataEngine) — C# side
├── Setup: BsUiDataStaging.StageDataFilesAsync()
│   ├── Generate XML via CatXmlGenerator → write .gst/.cat to an isolated BS home
│   └── Launch BattleScribe + connect the Java agent
├── Mutations: AgentClient.CallAsync("gamedataXxxAction", params)
│   ├── gamedataAddEntryAction    → DataEditorActions.addEntry()
│   ├── gamedataRemoveEntryAction → DataEditorActions.removeEntry()
│   ├── gamedataSetFieldAction    → DataEditorActions.setField()
│   └── gamedataAddLinkAction     → DataEditorActions.addLink()
├── State: AgentClient.CallAsync("gamedataGetDataState") → Java model traversal → JSON
└── Diagnostics: BsGameDataUiDiagnostics.CaptureAsync()

DataEditorActions.java — Java agent side
├── Dispatched from JsonRpcServer when method.startsWith("gamedata")
├── gamedataLoadFilesAction → load staged .gst/.cat into the editor controller
└── Each mutation: locate tree node → manipulate JavaFX scene → verify model
```

**Engine name in specs**: `battlescribe-ui` (in a spec's `engines:` overrides).

## Status: implemented (49/49 gamedata specs pass)

`DataEditorActions.java` is fully implemented: load / addEntry / removeEntry / setField /
addLink / getDataState. The controller is found via the Stage titled `"Data Editor …"` →
`#btnSaveDataFile` handler → `DataEditorWindowController`.

## Edit-panel field mapping & gotchas

`setField` selects the entry (populating `#pnlEditor`), looks up the control by CSS id, sets
its value, then **fires an `ActionEvent`** — the panel commits the model in each control's
`setOnAction` handler, so `setSelected()` / `setValue()` alone do **not** persist. Confirmed
control ids (from the decompiled `*EditPanelController`):

| Field | CSS id | Control | Notes |
|-------|--------|---------|-------|
| `name` | `#txtName` | TextField | |
| `id` | `#txtUniqueId` | TextField | |
| `targetId` | `#txtTargetId` | TextField | |
| `hidden` | `#chkHidden` | CheckBox | fire ActionEvent to commit |
| `collective` | `#chkCollective` | CheckBox | |
| `imported` | `#chkImport` | CheckBox | **id is `chkImport`, not `chkImported`** |
| `type` | `#cboType` | ComboBox | match item by text |
| `defaultSelectionEntryId` | `#cboDefaultSelection` | ComboBox\<INamed\> | **match item by `getId()`**, not text |

Other gotchas baked into the implementation:
- `getDataState` must serialize `collective`, `imported`, `defaultSelectionEntryId` into the
  `fields` dict (booleans as `"true"`/`"false"`) — they were missing originally and asserted
  as `''`. When you add a field, serialize it here too.
- `addEntry` of a **group** at a root parent (game system / catalogue) must call
  `actAddSharedSelectionEntryGroup` — `actAddSelectionEntryGroup` only handles a
  `BaseSelectionEntry` parent and is a **silent no-op at the root**. A plain selection entry is
  handled at the root by `actAddSelectionEntry`. See `isRootEntry(...)`.

## Running

```powershell
pwsh -File setup.ps1                        # once — downloads BattleScribe + Liberica JDK, builds the agent
dotnet test -p:TestProfile=bs-ui-gamedata
```

Java runtime and jars auto-discover (`BsUiPaths.ResolveJavaPath`, `BsGameDataUiEngine.FindOptions`);
resolution order `BS_UI_JAVA_PATH` → `lib/liberica-jdk` → `JAVA_HOME`. CI relies on
`JAVA_HOME` from `actions/setup-java` (`jdk+fx`).

- Each spec relaunches the app (~5 s) → full suite ≈ 4–5 min, matching CI.
- `BS_UI_KEEP_ALIVE=true` is **broken for gamedata** (stale data across specs → "Tree item not
  found"); leave it unset.
- Single spec end-to-end with assertions:
  `dotnet run --project src/BattleScribeSpec.Cli -- run --engine battlescribe --ui specs/gamedata/entry/se-create-in-gamesystem.yaml`
  (add `--all-steps` for per-step state).

## Probe — visual inspection (no REPL)

```powershell
dotnet run --project src/BattleScribeSpec.Cli -- probe --engine battlescribe --ui specs/gamedata/entry/se-create-in-gamesystem.yaml
```

Stages the data, launches BattleScribe + agent, loads the data editor, and **leaves the app
open** — press Enter to shut down. There is **no JSON-RPC REPL**; watch the editor by hand or
attach to the agent port from your own tooling.

How the driver locates things (already discovered — no re-probe needed to use it):
- **Window**: the Data Editor opens directly as a Stage titled `"Data Editor …"`; setup waits for it.
- **Controller**: that Stage → `#btnSaveDataFile`'s `setOnAction` handler → `DataEditorWindowController`.
- **Entries**: located by tree item via the controller's tree, not by a `:id:` token.

Re-probe only when a new field/control id or menu label is unknown.

## JSON-RPC routing

`JsonRpcServer.java` routes by method prefix, mirroring the engine identifiers:

```java
} else if (method.startsWith("gamedata")) {
    result = dataEditorActions.dispatch(method, params);
} else if (method.startsWith("roster")) {
    result = rosterActions.dispatch(method, params);
```

Adding a gamedata method: add a `gamedataXxxAction` entry in `DataEditorActions.dispatch()`.

## Diagnostics

On failure, `BsGameDataUiDiagnostics.CaptureAsync()` writes
`artifacts/bs-gamedata-ui-diagnostics/{timestamp}-{specId}.txt`: open windows, full scene dump
(depth 4, all windows), data-state JSON (`gamedataGetDataState`), thread dump, stack trace.

A `NotSupportedException` from `CallActionAsync` means the agent reported a method as
unimplemented — all current actions are implemented, so this only appears if you add a C#
`gamedataXxxAction` case before wiring its Java counterpart.

## How to extend

1. Add a `gamedataXxxAction` line to `DataEditorActions.dispatch()`.
2. Implement it like `addEntry` / `setField`: locate the tree item via the cached
   `DataEditorWindowController`, mutate on the FX thread (`Platform.runLater` / `runOnFxGet`),
   then poll until the model reflects the change. (The controller is cached and reused across
   calls, cleared on each `gamedataLoadFilesAction`.)
3. Call it from `BsGameDataUiEngine.cs` via `CallActionAsync(...)`.
4. Field writes go through `setField` (see the control-id table). Serialize any new field in
   `getDataState()` too, or the assertion will see `''`.

## Source map

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiEngine.cs` | Main engine class |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiDiagnostics.cs` | Failure diagnostics |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiProbe.cs` | Probe session launcher |
| `src/bs-ui-java-agent/src/bsspec/uiagent/DataEditorActions.java` | Java agent actions (load/add/remove/setField/addLink/getState) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/JsonRpcServer.java` | RPC routing (gamedata→DataEditorActions) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/RosterActions.java` | Reference for action patterns |
| `src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs` | Shared C# RPC client |
| `src/BattleScribeSpec.BsRosterUiDriver/BsRosterApp.cs` | Shared app lifecycle |
| `src/BattleScribeSpec.BsRosterUiDriver/BsUiDataStaging.cs` | Shared file staging |
| `tests/Infrastructure/BsGameDataUiFixture.cs` | Test fixture |
| `tests/Conformance/BsGameDataUiConformanceTests.cs` | Conformance tests |
| `tests/test-profiles/bs-ui-gamedata.runsettings` | Test profile |
