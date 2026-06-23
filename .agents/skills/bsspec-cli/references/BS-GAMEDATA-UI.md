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
├── gamedataLoadFilesAction → open the primary staged file via the real open path
├── gamedataOpenCatalogueAction → open a specific staged file (the openCatalogue action)
└── Each mutation: locate tree node → manipulate JavaFX scene → verify model
```

**Engine name in specs**: `battlescribe-ui` (in a spec's `engines:` overrides).

## Status: implemented (80/80 gamedata specs pass)

**UI-fidelity invariant: every mutation is driven through a real JavaFX widget; reading state is
the only non-UI code.** There is **no** reflective model-mutation path — `setFieldReflectively` has
been removed. If a field can't be resolved to a control, the agent throws (so a missing UI path
can't masquerade as a pass). The one field the Data Editor has **no widget for**, `defaultCostLimit`
(`UNEDITABLE_FIELDS`; its CostType panel edits only `hidden` — verified in the decompiled
`CostTypeEditPanelController`), is **skipped** with a logged note rather than written to the model;
the spec asserts it on the engines that can set it via a `battlescribe-ui` `expectedState` override.
Do **not** reintroduce a reflective fallback. (`battleScribeVersion` is a serializer save-stamp, not
a spec field — no engine sets or asserts it.)

`DataEditorActions.java` implements: load / openCatalogue / addEntry / removeEntry / setField /
setCost / setCharacteristic / addLink / getDataState / getErrors. The controller is found via the
Stage titled `"Data Editor …"` → `#btnSaveDataFile` handler → `DataEditorWindowController`.

## Edit-panel field mapping & gotchas

`setField` selects the entry (populating `#pnlEditor`), looks up the control by CSS id
(`fieldToCssId`), and drives it via `setNodeValue` (TextField / TextArea / CheckBox / ComboBox /
Spinner). Checkboxes & combos **fire an `ActionEvent`** to commit (the panel writes the model in
the `setOnAction` handler); TextField/TextArea use a text-property `ChangeListener` (setText
commits); a `Spinner<…>` commits via `getValueFactory().setValue(...)`. Confirmed control ids
(from the decompiled `*EditPanelController`):

| Field | CSS id | Control | Notes |
|-------|--------|---------|-------|
| `name` | `#txtName` | TextField | |
| `id` | `#txtUniqueId` | TextField | |
| `targetId` | `#txtTargetId` | TextField | links — see Link type/target below |
| `hidden` | `#chkHidden` | CheckBox | fire ActionEvent to commit |
| `collective`/`imported` | `#chkCollective`/`#chkImport` | CheckBox | id is `chkImport`, not `chkImported` |
| `type` | `#cboType` | ComboBox | match item by `getId()` / `getName()` / toString |
| `typeId`/`typeName` | `#cboType` | ComboBox | profile type — `typeName` matches by `getName()` |
| `publicationId` | `#cboPublication` | ComboBox | match by `getId()` (not `txtPublicationId`) |
| `page`/`comment`/`description` | `#txtPage`/`#txtComment`/`#txtDescription` | Text(Area) | |
| `revision` | `#spnRevision` | Spinner\<Integer\> | |
| `value` (constraint/condition/repeat) | `#spnValue` | Spinner\<Double\> | routed by entry type |
| `repeats` | `#spnRepeats` | Spinner | |
| `field`/`scope`/`childId` | `#cboField`/`#cboScope`/`#txtChildId` | query controls | |
| `percentValue`/`shared`/`includeChild*`/`roundUp`/`importRootEntries`/`library` | `#chk…` | CheckBox | |
| `authorContact`/`authorUrl` | `#txtContactDetails`/`#txtWebsite` | Text | **not** `txtAuthorContact`/`txtAuthorUrl` |
| `defaultSelectionEntryId` | `#cboDefaultSelection` | ComboBox\<INamed\> | match by `getId()` |

Widget-specific paths (not a simple `#cssId` lookup):
- **Link type/target**: setting a link's `targetId` first aligns `#cboType` to the **target's kind**
  (resolved by searching the loaded model — `linkTargetKind`), so a non-default-type target (e.g. an
  entry link to a `selectionEntryGroup`, or an info link to a profile) resolves and is retained.
  Driving `#cboType` makes `getAvailableLinkTypes` a real constraint (a *root* entry-link → group
  correctly fails — the combo only offers `selectionEntry` at root).
- **Cost**: `setCost` drives the cost `Spinner<Double>` in `#pnlCostLimits` (a `TilePane`, one
  `HBox[Label, Spinner]` per cost type in `dataManager.G()` order) — the spinner's listener
  creates/updates/removes the cost.
- **Characteristic**: `setCharacteristic` drives the characteristic `TextArea` at GridPane (col 1,
  row = the characteristic's index) in `#pnlProfile`. Characteristics come from the profile type —
  the row must already exist (we never fabricate one).
- **Modifier value**: drives whichever value control the panel made *managed*
  (`spnNumberValue`/`txtStringValue`/`cboBooleanValue`/`cboCategories`) based on the modifier field's
  data type — so the field→datatype logic isn't reimplemented.
- **Characteristic-type name**: edited via the ProfileType panel (`lstCharacteristicTypes` selection
  → shared `#txtName`), since a characteristic type isn't a selectable main-tree node.

Other gotchas baked into the implementation:
- `getDataState` must serialize a field into the `fields` dict for it to be assertable (booleans as
  `"true"`/`"false"`). When you add a field, serialize it here too.
- `addEntry` of a **group** at a root parent must call `actAddSharedSelectionEntryGroup` —
  `actAddSelectionEntryGroup` is a silent no-op at the root. See `isRootEntry(...)`.
- A shared **profile** with no profile type is dropped by the editor on load — specs that link to a
  shared profile must give it a `typeId`.
- The catalogue-link **target field sanitises** an unresolvable id to empty (still flagging the
  dangling link), so a re-point to a non-existent catalogue can't keep the literal id on
  `battlescribe-ui` — assert the dangling case via a `battlescribe-ui` per-engine override.

## Running

```powershell
pwsh -File setup.ps1                        # once — downloads BattleScribe + Liberica JDK, builds the agent
dotnet test -p:TestProfile=bs-ui-gamedata
```

Java runtime and jars auto-discover (`BsUiPaths.ResolveJavaPath`, `BsGameDataUiEngine.FindOptions`);
resolution order `BS_UI_JAVA_PATH` → `lib/liberica-jdk` → `JAVA_HOME`. CI relies on
`JAVA_HOME` from `actions/setup-java` (`jdk+fx`).

- Each spec relaunches the app → full suite ≈ 8 min (80 specs), matching CI.
- `BS_UI_KEEP_ALIVE=true` is **broken for gamedata** (stale data across specs → "Tree item not
  found"); leave it unset.
- Single spec end-to-end with assertions:
  `dotnet run --project src/BattleScribeSpec.Cli -- run --engine battlescribe --ui specs/gamedata/entry/se-create-in-gamesystem.yaml`
  (add `--all-steps` for per-step state).
- **Agent-side diagnostics**: the agent JVM's `System.err` is pumped for the whole session and
  tee'd to a file when `BSUI_AGENT_STDERR_LOG=<path>` is set — the only window into agent-side
  behavior the request/response protocol can't surface. Add `System.err.println` in the agent and
  read the file. (Without it, runtime agent stderr is not visible.)

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
