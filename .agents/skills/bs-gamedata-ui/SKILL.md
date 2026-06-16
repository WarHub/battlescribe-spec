---
name: bs-gamedata-ui
description: >
  Work with the BS GameData UI driver (BattleScribe desktop Data Editor via Java agent RPC).
  Use when debugging BsGameDataUiDriver test failures, extending DataEditorActions.java with new
  data editor methods, probing the BattleScribe data editor scene graph, understanding the
  JSON-RPC dispatch for data editor methods, or supporting new entry types and fields.
---

# BS GameData UI Driver

Drives the BattleScribe desktop Data Editor UI via a Java agent for GameData conformance testing.
Follows the **hybrid UI driver pattern**: mutations through real UI interactions (Java agent RPC
→ JavaFX scene graph manipulation), state reads via Java model traversal.

## Architecture

```
BsGameDataUiEngine (IGameDataEngine) — C# side
├── Setup: BsUiDataStaging.StageDataFilesAsync()
│   ├── Generate XML via CatXmlGenerator
│   ├── Write .gst/.cat files to isolated BS home directory
│   └── Launch BattleScribe + connect Java agent
├── Mutations: AgentClient.CallAsync("editorXxxAction", params)
│   ├── editorAddEntryAction    → DataEditorActions.addEntry()
│   ├── editorRemoveEntryAction → DataEditorActions.removeEntry()
│   ├── editorSetFieldAction    → DataEditorActions.setField()
│   └── editorAddLinkAction     → DataEditorActions.addLink()
├── State: AgentClient.CallAsync("editorGetDataState")
│   └── DataEditorActions.getDataState() → Java model traversal → JSON
└── Diagnostics: BsGameDataUiDiagnostics.CaptureAsync()
    └── scene dump + thread dump + data state + screenshot

DataEditorActions.java — Java agent side
├── Dispatched from JsonRpcServer when method.startsWith("editor")
├── editorLoadFilesAction → load the staged .gst/.cat into the editor controller
├── Each mutation: locate tree node → interact with JavaFX scene → verify state
└── Status: fully implemented (load / addEntry / removeEntry / setField / addLink / getDataState)
```

**Engine name in specs**: `battlescribe-ui` (use in a spec's `engines:` field overrides).
**Debugger `--engine` flag**: `gamedata/battlescribe-ui` (the `gamedata/` type prefix selects the
GameData engine; it is inferred from a `specs/gamedata/...` path when omitted).

## Current status: Implemented (49/49 gamedata specs pass)

`DataEditorActions.java` is fully implemented: load / addEntry / removeEntry /
setField / addLink / getDataState. The controller is found via the Stage titled
`"Data Editor …"` → `#btnSaveDataFile` handler → `DataEditorWindowController`.

### Edit-panel field mapping & gotchas

`setField` selects the entry (which populates `#pnlEditor`), looks up the control by CSS id,
sets its value, then **fires an `ActionEvent`**. The panel writes the model in each control's
`setOnAction` handler, so `setSelected()` / `setValue()` alone do **not** persist — you must
fire the action. Confirmed control ids (from decompiled `*EditPanelController`):

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
- **getDataState must serialize** `collective`, `imported`, `defaultSelectionEntryId` into the
  `fields` dict (they were missing originally → asserted as `''`). Booleans emit `"true"`/`"false"`.
- **addEntry of a group at a root parent** (game system / catalogue) must call
  `actAddSharedSelectionEntryGroup` — `actAddSelectionEntryGroup` only handles a
  `BaseSelectionEntry` parent and is a **silent no-op at the root**. A plain selection entry is
  already handled at the root by `actAddSelectionEntry`. See `isRootEntry(...)`.

## Running

```powershell
pwsh -File setup.ps1                        # once — downloads BattleScribe + Liberica JDK, builds the agent
dotnet test -p:TestProfile=bs-ui-gamedata
```

That's the whole setup — the Java runtime and jars are auto-discovered (`BsUiPaths.ResolveJavaPath`,
`BsGameDataUiEngine.FindOptions`). Resolution order is `BS_UI_JAVA_PATH` → `lib/liberica-jdk` →
`JAVA_HOME`; CI relies on the `JAVA_HOME` from `actions/setup-java` (`jdk+fx`), so it sets nothing.

Notes:
- Each spec relaunches the app (~5 s) → full suite ≈ 4–5 min, matching CI.
- `BS_UI_KEEP_ALIVE=true` is **broken for gamedata** (stale data across specs → "Tree item not
  found"); leave it unset.
- Single spec end-to-end with assertions:
  `dotnet run --project src/BattleScribeSpec.Debugger -- --engine gamedata/battlescribe-ui specs/gamedata/entry/se-create-in-gamesystem.yaml`
  (add `--dump` for per-step state).

## Probe workflow — visual inspection

```powershell
dotnet run --project src/BattleScribeSpec.Debugger -- --engine gamedata/battlescribe-ui --probe specs/gamedata/entry/se-create-in-gamesystem.yaml
```

This stages the spec's game system / catalogues, launches BattleScribe with the Java agent, loads
the data editor, and **leaves the app open for manual inspection** — press Enter in the terminal to
shut it down. Unlike the NR gamedata probe, the BS probe has **no JSON-RPC REPL**; use it to watch
the editor by hand, or attach to the agent port from your own tooling.

How the driver locates things (already discovered — you don't need to re-probe to use it):
- **Window**: the Data Editor opens directly as a Stage titled `"Data Editor …"`; setup waits for it.
- **Controller**: found via that Stage → `#btnSaveDataFile`'s `setOnAction` handler →
  `DataEditorWindowController`.
- **Entries**: located by tree item via the controller's tree, not by a `:id:` token.

To **add** a new action you generally edit `DataEditorActions.java` directly (see *How to extend*).
Re-probe only when a new field/control id or menu label is unknown.

## JSON-RPC routing

In `JsonRpcServer.java`, methods starting with `"editor"` route to `DataEditorActions.dispatch()`:

```java
} else if (method.startsWith("editor")) {
    result = dataEditorActions.dispatch(method, params);
} else if (method.endsWith("Action")) {
    result = rosterActions.dispatch(method, params);
```

Adding new data editor methods: add a `case "editorXxxAction":` in `DataEditorActions.dispatch()`.

## Diagnostics on failure

When a test fails, `BsGameDataUiDiagnostics.CaptureAsync()` writes to
`artifacts/bs-gamedata-ui-diagnostics/{timestamp}-{specId}.txt`:
- Open windows list
- Full scene graph dump (depth 4 for all windows)
- Data state JSON (`editorGetDataState`)
- Thread dump (detects deadlocks)
- Stack trace

A `NotSupportedException` from `CallActionAsync` means the Java agent reported a method as
unimplemented (`UnsupportedOperationException` / "not yet implemented"). All current actions are
implemented, so this should only appear if you add a new `editorXxxAction` case in C# before wiring
its Java counterpart.

## How to extend

### Adding a new action to DataEditorActions.java

1. Add a new `editorXxxAction` line to `DataEditorActions.dispatch()`.
2. Implement the method, following the existing actions (`addEntry` / `setField`):
   locate the tree item via the cached `DataEditorWindowController`, mutate on the FX thread
   (`Platform.runLater` / `runOnFxGet`), then poll until the model reflects the change.
3. Call it from `BsGameDataUiEngine.cs` via `CallActionAsync(...)`. The controller is cached and
   reused across calls (cleared on each `editorLoadFilesAction`).

### Edit-panel fields

Field writes go through `setField`, which sets the control then **fires an `ActionEvent`** so the
panel's `setOnAction` handler commits the value to the model — see the control-id table above for
the confirmed CSS ids and per-field quirks.

### State extraction in Java

`getDataState()` traverses the loaded data model via the cached controller and serializes each
entry's fields. When adding a field, remember to serialize it in `getDataState()` too, or the
assertion will see `''` (this already bit `collective` / `imported` / `defaultSelectionEntryId`).

## Reference files

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiEngine.cs` | Main engine class |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiDiagnostics.cs` | Failure diagnostics |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiProbe.cs` | Probe session launcher |
| `src/bs-ui-java-agent/src/bsspec/uiagent/DataEditorActions.java` | Java agent actions (load/add/remove/setField/addLink/getState) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/JsonRpcServer.java` | RPC routing (editor→DataEditorActions) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/RosterActions.java` | Reference for action patterns |
| `src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs` | Shared C# RPC client |
| `src/BattleScribeSpec.BsRosterUiDriver/BsRosterApp.cs` | Shared app lifecycle (reused) |
| `src/BattleScribeSpec.BsRosterUiDriver/BsUiDataStaging.cs` | Shared file staging (reused) |
| `tests/Infrastructure/BsGameDataUiFixture.cs` | Test fixture |
| `tests/Conformance/BsGameDataUiConformanceTests.cs` | Conformance tests |
| `tests/test-profiles/bs-ui-gamedata.runsettings` | Test profile |
