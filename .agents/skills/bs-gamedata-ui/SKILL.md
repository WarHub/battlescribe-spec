---
name: bs-gamedata-ui
description: >
  Work with the BS GameData UI driver (BattleScribe desktop Data Editor via Java agent RPC).
  Use when debugging BsGameDataUiDriver test failures, implementing DataEditorActions.java stubs,
  probing the BattleScribe data editor scene graph, understanding the JSON-RPC dispatch for
  data editor methods, or extending the driver to support new entry types.
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
│   ├── editorMoveEntryAction   → DataEditorActions.moveEntry()
│   ├── editorSetFieldAction    → DataEditorActions.setField()
│   └── editorAddLinkAction     → DataEditorActions.addLink()
├── State: AgentClient.CallAsync("editorGetDataState")
│   └── DataEditorActions.getDataState() → Java model traversal → JSON
└── Diagnostics: BsGameDataUiDiagnostics.CaptureAsync()
    └── scene dump + thread dump + data state + screenshot

DataEditorActions.java — Java agent side
├── Dispatched from JsonRpcServer when method.startsWith("editor")
├── Each method: locate tree node → interact with JavaFX scene → verify state
└── Status: ALL STUBS (not yet implemented, need UI probing)
```

**Engine name**: `battlescribe-ui` (use in spec `engines` field overrides).

## Current status: Stubs

All `DataEditorActions.java` methods throw `UnsupportedOperationException`.
They must be implemented after probing the BattleScribe data editor UI.

**Why stubs?** The BattleScribe data editor is a separate UI context from the roster editor.
Its window title, tree structure, context menu layout, and property panel are not yet mapped.

## Environment setup

Same as `BsUiRosterEngine` — the data editor reuses the same binary artifacts:

```powershell
# Binary artifacts (downloaded by setup.ps1):
lib/battlescribe/jre/bin/java        # JavaFX-capable JRE
lib/battlescribe/RosterEditor.jar    # BattleScribe main JAR
lib/battlescribe/lib/*.jar           # dependency JARs

# Agent JAR (must be built):
pwsh -File src/bs-ui-java-agent/build.ps1
# Output: src/bs-ui-java-agent/bs-ui-java-agent.jar
```

`BsGameDataUiEngine.FindOptions()` auto-discovers these paths.

## Test profiles

| Profile | Command |
|---------|---------|
| `bs-ui-gamedata` | `dotnet test -p:TestProfile=bs-ui-gamedata` |

Set `BS_UI_SKIP=true` to skip all BS UI tests.

## Probe workflow — implement DataEditorActions.java

Before implementing stubs, you must probe the BattleScribe data editor to discover:
- The window title of the data editor window
- The tree item structure (how entries appear as `:id:` tokens)
- Context menu items for add/remove/move/link operations
- Property panel structure for field editing

### Step 1: Launch probe

```powershell
# Launch BS with a simple game system, enter the agent REPL
dotnet run --project src/BattleScribeSpec.Debugger -- --engine battlescribe-ui --probe gamedata/basic/entry-add
```

### Step 2: Explore the data editor

In the agent REPL (JSON-RPC calls):

```json
// List all open windows
{"method": "getWindows", "params": {}}

// Navigate to data editor window (title TBD — look for "Data Editor" or similar)
// Then dump its scene graph
{"method": "dumpTree", "params": {"maxDepth": 4, "windowTitle": "Data Editor"}}

// Try clicking the "Data Editor" menu item to open it
{"method": "findNodeByText", "params": {"text": "Data Editor"}}
{"method": "clickNode", "params": {"selector": "#menuDataEditor"}}

// After opening, dump tree again
{"method": "dumpTree", "params": {"maxDepth": 5}}

// Find the entry tree view
{"method": "findNode", "params": {"selector": "TreeView, .tree-view, #entryTree"}}
```

### Step 3: Map the UI structure

Document in `DataEditorActions.java`:
1. Window title for the data editor (e.g., `"BattleScribe Data Editor"`)
2. How to navigate from Roster Editor → Data Editor (menu item? toolbar button?)
3. Tree item format (`:id:` substring? full name? separate label field?)
4. Context menu items (e.g., "Add Selection Entry", "Add Force Entry")
5. Property panel fields (CSS selectors for name, type, hidden inputs)

### Step 4: Implement stubs

Each stub follows the `RosterActions.java` pattern:
1. Locate tree item by `entryId` (substring search `:id:`)
2. Right-click to open context menu
3. Click the appropriate menu item
4. Fill dialog if shown (name, type)
5. Poll until the entry appears in the tree
6. Read the new entry's ID via engineAccessor or scene text
7. Return JSON `{"entryId": "..."}` for addEntry/addLink

```java
// Pattern from RosterActions.java (adapt for data editor):
private String addEntry(JsonObject params) {
    String parentId = params.get("parentId").getAsString();
    String entryType = params.get("entryType").getAsString();

    // Run on background thread (complex UI sequence)
    String contextMenuItemText = entryTypeToMenuLabel(entryType);
    // 1. Select parent tree item
    selectTreeItemById(parentId);
    // 2. Right-click → context menu
    JsonObject cmResult = rightClickCurrentTreeItem();
    // 3. Click "Add" → entryType
    clickContextMenuCascade("Add", contextMenuItemText);
    // 4. Wait for new entry
    String newId = waitForNewChildEntry(parentId);
    return "{\"entryId\":\"" + newId + "\"}";
}
```

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

`NotSupportedException` failures (stubs not implemented) are expected until implementation.

## How to extend

### Opening the data editor window

After probing, the engine's `SetupAsync()` must call a method to open the data editor window.
In `BsGameDataUiEngine.cs`, replace the TODO comment:

```csharp
// TODO: Open the Data Editor window once selector is known.
await OpenDataEditorWindowAsync();
```

Implement `OpenDataEditorWindowAsync()` to navigate to the data editor using the discovered
menu item or toolbar button.

### Adding a new action to DataEditorActions.java

1. Add a new case to `dispatch()`: `case "editorNewAction":`
2. Implement the method following the probe-discovered UI structure
3. No C# changes needed — `BsGameDataUiEngine.cs` calls via generic `CallActionAsync()`

### State extraction in Java

`getDataState()` must traverse the Java data model. The model is accessed via:
- `engineAccessor` (which can access the data editor engine via reflection)
- Or direct model access if the data editor has a different engine class than the roster editor

Check `EngineAccessor.java` to see how the roster engine is located, then find the parallel
method for the data editor model.

## Reference files

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiEngine.cs` | Main engine class |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiDiagnostics.cs` | Failure diagnostics |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiProbe.cs` | Probe session launcher |
| `src/bs-ui-java-agent/src/bsspec/uiagent/DataEditorActions.java` | Java agent stubs |
| `src/bs-ui-java-agent/src/bsspec/uiagent/JsonRpcServer.java` | RPC routing (editor→DataEditorActions) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/RosterActions.java` | Reference for action patterns |
| `src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs` | Shared C# RPC client |
| `src/BattleScribeSpec.BsRosterUiDriver/BsRosterApp.cs` | Shared app lifecycle (reused) |
| `src/BattleScribeSpec.BsRosterUiDriver/BsUiDataStaging.cs` | Shared file staging (reused) |
| `tests/Infrastructure/BsGameDataUiFixture.cs` | Test fixture |
| `tests/Conformance/BsGameDataUiConformanceTests.cs` | Conformance tests |
| `tests/test-profiles/bs-ui-gamedata.runsettings` | Test profile |
