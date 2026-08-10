# BsRosterUiDriver — BattleScribe Desktop UI Automation Adapter

The `BattleScribeSpec.BsRosterUiDriver` project is a UI automation adapter that drives the
**real BattleScribe Roster Editor desktop application** (Java/JavaFX) as a conformance test
engine. It implements `IRosterEngine` — the same interface used by the IKVM-based BS engine
and the NewRecruit browser adapter — but instead of embedding the engine, it **launches the
actual BS desktop app** and controls it via a custom Java agent injected into the JVM.

## Architecture Overview

The system has four layers:

```
┌──────────────────────────────────────────────────────────┐
│  BsUiRosterEngine (C#, IRosterEngine implementation)     │
│  Thin dispatcher — maps IRosterEngine calls to *Action   │
│  JSON-RPC methods with minimal local logic               │
├──────────────────────────────────────────────────────────┤
│  AgentClient (C#, JSON-RPC 2.0 over TCP)                 │
│  Typed async methods for each agent command              │
├──────────────────────────────────────────────────────────┤
│  RosterActions (Java, high-level orchestration)          │
│  Full UI workflow automation: opens dialogs, fills forms,│
│  polls for state changes, handles errors                 │
├──────────────────────────────────────────────────────────┤
│  SceneGraphCommands + EngineAccessor (Java, low-level)   │
│  Scene graph queries, engine reflection, direct FX ops   │
└──────────────────────────────────────────────────────────┘
```

## Source Files

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.BsRosterUiDriver/BsRosterApp.cs` | Process lifecycle management |
| `src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs` | C# JSON-RPC client |
| `src/BattleScribeSpec.BsRosterUiDriver/BsUiRosterEngine.cs` | `IRosterEngine` implementation |
| `src/BattleScribeSpec.BsRosterUiDriver/BsUiDataStaging.cs` | XML file staging for BS data dir |
| `src/BattleScribeSpec.BsRosterUiDriver/BsUiDiagnostics.cs` | Failure diagnostics capture |
| `src/BattleScribeSpec.BsRosterUiDriver/BsUiProbe.cs` | Interactive probe/debug mode |
| `src/bs-ui-java-agent/src/bsspec/uiagent/BsUiAgent.java` | Java agent entry point (`premain`) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/JsonRpcServer.java` | TCP JSON-RPC server + FX thread dispatch |
| `src/bs-ui-java-agent/src/bsspec/uiagent/RosterActions.java` | High-level action orchestration (UI workflow automation) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/SceneGraphCommands.java` | Command implementations (scene graph) |
| `src/bs-ui-java-agent/src/bsspec/uiagent/EngineAccessor.java` | Engine discovery + reflection-based state reading |
| `src/bs-ui-java-agent/src/bsspec/uiagent/ActionRecorder.java` | UI interaction recording |

## Startup Sequence

1. **`BsRosterApp.StartAsync()`** launches the Java process:
   ```
   java -javaagent:"bs-ui-java-agent.jar" -Xms1024m "-Duser.home=<isolated>" -jar "RosterEditor.jar"
   ```
2. The Java agent's `premain` binds a TCP socket on loopback and prints
   `BSUI_AGENT_PORT=<port>` to stdout.
3. The C# side reads stdout line-by-line until it sees the port announcement (30s timeout).
4. **`AgentClient`** connects via TCP and sends a `ping` to confirm connectivity.
5. The adapter waits for the "Roster Editor" window to appear (30s).
6. Startup dialogs (e.g., "download data?" confirmation) are dismissed automatically.

### Isolated Home Directory

BattleScribe stores data relative to `user.home`. The adapter creates an isolated temp
directory (or uses a provided path) with the structure:

```
<home>/
  BattleScribe/
    data/         ← game system XML files staged here
    rosters/
    settings/
      settings.xml
      repositories.xml
```

This prevents interference with the user's real BattleScribe installation.

## Data Staging

`BsUiDataStaging` handles writing game system and catalogue XML files into the isolated
BS data directory before launch:

1. Creates a subdirectory named by the game system ID under `data/`.
2. Writes all `.gst` and `.cat` files.
3. Generates an `index.bsi` file — a BattleScribe data index XML that lists all data files
   with their IDs, names, and types.

The XML files are generated from Protocol types via `CatXmlGenerator` (from the
`BattleScribeSpec.NewRecruit` project which shares the XML generation logic).

## Warm Start (KeepAlive)

When `KeepAlive = true`, the adapter preserves the running BattleScribe process between
spec runs. On subsequent `Setup()` calls:

1. Pings the existing agent to verify it's responsive.
2. Closes any open roster (dismisses unsaved changes).
3. Re-stages data files for the new spec.
4. Skips JVM startup entirely.

If the ping fails, falls back to a cold start (kills the old process, launches fresh).
This is useful for iterative debugging where JVM startup time (~5-10s) is significant.

---

## JSON-RPC Communication Protocol

### Transport

- **Protocol**: JSON-RPC 2.0
- **Transport**: TCP over loopback (`127.0.0.1`)
- **Framing**: Line-delimited — each message is a single JSON object terminated by `\n`
- **Connections**: Single client at a time (sequential request-response)
- **Port**: Dynamically assigned (0 = OS picks), announced via stdout

### Request Format

```json
{"jsonrpc": "2.0", "method": "methodName", "params": {"key": "value"}, "id": 1}
```

### Response Format (success)

```json
{"jsonrpc": "2.0", "id": 1, "result": <JSON value>}
```

### Response Format (error)

```json
{"jsonrpc": "2.0", "id": 1, "error": {"code": -32603, "message": "ExceptionType: details"}}
```

Standard error codes:
- `-32700` — Parse error (malformed JSON)
- `-32600` — Invalid request (missing `method`)
- `-32603` — Internal error (exception during execution)

### Threading Model

Commands are dispatched through three routing paths:

- **FX thread methods** — Scene graph inspection/interaction commands that must run on the
  JavaFX Application Thread. Dispatched via `Platform.runLater()` with a **60-second timeout**.
  If the FX thread is blocked (deadlock), the agent returns an error.

- **Action methods** — Methods whose names end in `Action` run on a background thread in
  `RosterActions.dispatch()`. These orchestrate complete workflows and internally call
  `SceneGraphCommands`-style operations on the FX thread as needed.

- **Other methods** — Run on the server IO thread directly. This is used for engine
  reflection/diagnostic operations that cannot safely run on the FX thread.

---

## Command Reference

### Connectivity

#### `ping`

Liveness check.

- **Thread**: FX
- **Params**: none
- **Returns**: `"pong"` (string literal)

---

### Scene Graph Inspection

#### `dumpTree`

Dumps the JavaFX scene graph tree of a single window.

- **Thread**: FX
- **Params**:
  - `maxDepth` (int, default: 10) — Maximum tree traversal depth
  - `windowTitle` (string, optional) — Substring match on `Stage.getTitle()`. If omitted, uses first available scene.
- **Returns**:
  ```json
  {
    "windowTitle": "Roster Editor - MyRoster (2000pts)",
    "tree": { "type": "VBox", "id": null, "children": [...] }
  }
  ```

#### `dumpAllWindows`

Dumps scene graphs of ALL open windows. Useful for diagnosing which dialogs are open.

- **Thread**: Background (self-marshals to FX)
- **Params**:
  - `maxDepth` (int, default: 4)
- **Returns**:
  ```json
  {
    "windowCount": 2,
    "windows": [
      {
        "type": "Stage", "title": "Roster Editor", "showing": true,
        "focused": true, "modality": "NONE", "width": 1200, "height": 800,
        "tree": { ... }
      },
      {
        "type": "Stage", "title": "Confirm", "showing": true,
        "focused": false, "modality": "APPLICATION_MODAL", ...
      }
    ]
  }
  ```

#### `getWindows`

Lists all open JavaFX windows (without scene graph dump).

- **Thread**: FX
- **Params**: none
- **Returns**:
  ```json
  [
    {"type": "Stage", "title": "Roster Editor", "showing": true, "width": 1200, "height": 800},
    {"type": "Stage", "title": "Confirm", "showing": true, "width": 400, "height": 200}
  ]
  ```

#### `findNode`

Finds a single node by CSS selector.

- **Thread**: FX
- **Params**:
  - `selector` (string, required) — JavaFX CSS selector (e.g., `"#btnNewRoster"`, `".label"`)
  - `windowTitle` (string, optional)
- **Returns**: Node JSON object or `null`
  ```json
  {"type": "Button", "id": "btnNewRoster", "text": "New", "visible": true, "disabled": false}
  ```

#### `findNodeByText`

Finds a node by its text content (case-insensitive substring match).

- **Thread**: FX
- **Params**:
  - `text` (string, required) — Text to search for
  - `nodeType` (string, optional) — Filter by class simple name (e.g., `"Button"`, `"Label"`)
  - `windowTitle` (string, optional) — If omitted, searches ALL windows
- **Returns**: Node JSON object or `null`

#### `getUiState`

Reads the visible roster state directly from the UI (not the engine). Extracts roster name
from the window title, forces from the roster tree, and costs from visible labels.

- **Thread**: FX
- **Params**:
  - `windowTitle` (string, optional) — Defaults to "Roster Editor"
- **Returns**:
  ```json
  {
    "rosterName": "My Army",
    "forces": [{"name": "Battalion", "children": [...]}],
    "costs": [{"name": "pts", "value": "1500"}]
  }
  ```

#### `captureScreenshot`

Captures a PNG screenshot of the JavaFX scene.

- **Thread**: FX
- **Params**:
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"png": "<base64-encoded PNG>", "width": 1200, "height": 800}
  ```
- **Requires**: `javafx.swing` module available in the JVM

---

### UI Interaction — General

#### `clickNode`

Fires synthetic mouse events (PRESSED, RELEASED, CLICKED) on a node.

- **Thread**: FX
- **Params**:
  - `selector` (string, optional) — CSS selector
  - `text` (string, optional) — Alternative: find node by text content
  - `windowTitle` (string, optional) — If omitted with `text`, searches ALL windows
  - `doubleClick` (boolean, default: false) — Sets click count to 2
  - `async` (string, optional) — If `"true"`, fires via `Platform.runLater()`
- **Returns**:
  ```json
  {"clicked": true, "doubleClick": false, "x": 600.5, "y": 400.0, "async": false}
  ```
- **Note**: Either `selector` or `text` must be provided.

#### `fireButton`

Directly calls `ButtonBase.fire()` — more reliable than synthetic clicks for buttons.
Walks up the parent chain (up to 5 levels) to find a ButtonBase if the selector hits a
child node.

- **Thread**: FX
- **Params**:
  - `selector` (string, required) — CSS selector
  - `windowTitle` (string, optional)
  - `async` (string, optional) — If `"true"`, fires via `Platform.runLater()`.
    **Critical for buttons that trigger modal dialogs** — synchronous fire would deadlock.
- **Returns**:
  ```json
  {"fired": true, "async": true}
  ```

#### `setNodeText`

Sets text content of a `TextInputControl` (TextField, TextArea). Also fires a synthetic
`KEY_RELEASED` event to trigger BattleScribe's `onKeyReleased` handlers that persist changes.

- **Thread**: FX
- **Params**:
  - `selector` (string, required)
  - `text` (string, required) — New text value
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"set": true}
  ```

#### `pressKey`

Fires synthetic key events (KEY_PRESSED + KEY_RELEASED) on a node.

- **Thread**: FX
- **Params**:
  - `key` (string, required) — JavaFX `KeyCode` name (e.g., `"DELETE"`, `"ENTER"`, `"D"`)
  - `selector` (string, optional) — Target node. If omitted, uses scene's focus owner.
  - `windowTitle` (string, optional)
  - `ctrl` (boolean, default: false)
  - `alt` (boolean, default: false)
  - `shift` (boolean, default: false)
  - `meta` (boolean, default: false)
- **Returns**:
  ```json
  {"pressed": true, "key": "Delete"}
  ```

---

### UI Interaction — ComboBox

#### `getComboBoxItems`

Reads all items and current selection from a ComboBox.

- **Thread**: FX
- **Params**:
  - `selector` (string, required)
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {
    "selectedIndex": 0,
    "selectedText": "Battalion Detachment",
    "items": [
      {"index": 0, "text": "Battalion Detachment"},
      {"index": 1, "text": "Patrol Detachment"}
    ]
  }
  ```

#### `selectComboBoxItem`

Selects an item in a ComboBox by text (substring match) or index.

- **Thread**: FX
- **Params**:
  - `selector` (string, required)
  - `text` (string, optional) — Substring match on item's `toString()`
  - `index` (int, optional) — Direct index selection
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"selectedIndex": 1, "selectedText": "Patrol Detachment"}
  ```

---

### UI Interaction — TreeView

#### `selectTreeItem`

Programmatically selects a tree item by text match or index. Recursively searches the tree.

- **Thread**: FX
- **Params**:
  - `selector` (string, required) — CSS selector for the TreeView (e.g., `"#treeRoster"`)
  - `text` (string, optional) — Substring match on `TreeItem.getValue().toString()`
  - `index` (int, optional) — Row index
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"selected": true, "selectedText": "HQ [1-2]"}
  ```
  or on failure:
  ```json
  {"selected": false, "error": "Item not found: xyz"}
  ```

#### `clickTreeItem`

Selects a tree item, scrolls it into view, finds the rendered TreeCell, and fires synthetic
mouse events on it. Used for double-clicking catalogue entries (which adds them to the roster).

- **Thread**: FX
- **Params**:
  - `selector` (string, required)
  - `text` (string, required) — Text to find in tree
  - `doubleClick` (boolean, default: false)
  - `rightClick` (boolean, default: false) — Uses SECONDARY mouse button
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"clicked": true, "doubleClick": true, "text": "Tactical Squad", "cellFound": true}
  ```
  - `cellFound`: Whether the actual rendered TreeCell was located (false = clicked the TreeView itself as fallback)

#### Scoping a catalogue-tree lookup to one force

`#treeCatalogue` is not per-force: it holds the whole roster, one subtree per force, each offering
that force's own copy of the same catalogue entries. So `rosterSelectEntryAction` confines its search
to the target force's subtree (`resolveTreeScope`) — an unscoped search returns whichever copy comes
first in tree order, and clicking it adds the selection to a different force entirely.

**A force's subtree contains its child forces' subtrees**, which offer those entries a third time, so
confining to the parent is not yet confining to the parent. The search therefore also refuses to
descend into any nested force's subtree. Those ids come from roster state, not from the tree: every
tree node renders the same `Name:id:…` shape, so the tree cannot say which of its nodes is a force.

#### `clickTreeCellButton`

Fires a Button embedded inside a TreeCell's graphic node. Used for the "remove force" (X)
button in the Edit Roster dialog's force tree.

- **Thread**: Background (resolves on FX, fires async via `Platform.runLater`)
- **Params**:
  - `selector` (string, required) — TreeView selector
  - `text` (string, required) — Tree item text to locate
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"fired": true, "itemText": "[Force: Battalion]"}
  ```
- **Important**: The button is fired asynchronously because it typically triggers a modal
  confirmation dialog (`showAndWait`). The caller must handle the resulting dialog separately.

---

### UI Interaction — Spinner

#### `setSpinnerValue`

Sets a Spinner's value either by stepping (increment/decrement) or direct value assignment.

- **Thread**: FX
- **Params**:
  - `selector` (string, required)
  - `steps` (int, optional) — Positive = increment, negative = decrement
  - `value` (int, optional) — Direct value (uses `SpinnerValueFactory.setValue()`)
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"value": 3}
  ```
- **Note**: Either `steps` or `value` should be provided. If `value >= 0`, it takes precedence.

---

### UI Interaction — Label-Based Control Discovery

These commands locate controls by their adjacent label text in the UI layout. This is how
BattleScribe's edit panel works: each entry has a Label followed by a Spinner, CheckBox,
or Button in the same parent container.

#### `clickControlByLabel`

Finds a control adjacent to a label matching the given text, then interacts with it:
- **Spinner**: Increments by 1 (or decrements if `action: "decrement"`)
- **CheckBox**: Toggles (fires)
- **Button**: Fires

Falls back to searching CheckBoxes by their own text if no label+sibling match is found.

- **Thread**: FX
- **Params**:
  - `text` (string, required) — Label text to match (substring)
  - `windowTitle` (string, optional)
  - `action` (string, optional) — `"decrement"` for spinners; default is increment
- **Returns**:
  ```json
  {"clicked": true, "controlType": "spinner", "action": "increment", "labelText": "Tactical Squad"}
  ```
  or:
  ```json
  {"clicked": true, "controlType": "checkbox", "action": "toggle", "labelText": "Veteran Sergeant"}
  ```
  or on failure:
  ```json
  {"clicked": false, "error": "Control not found for text: xyz"}
  ```
- **Note**: Interactions are scheduled via `Platform.runLater()` to avoid deadlocks when
  the value change triggers BS engine recalculation on the FX thread.

#### Which label answers to a name

`contains` cannot tell an entry from its neighbours, and the spec corpus is full of neighbours:
`Armor` sits inside `Light Armor`, `Heavy Armor` and `Armor Type` in one panel; `Trigger` inside
`Alpha Trigger` and `Beta Trigger` in another; `Unit 1` inside `Unit 10`. Under `contains` the answer
was whichever node `lookupAll` yielded first — a different entry's control, driven silently.

Equality alone is not the rule either: BattleScribe decorates a row with its cost, so `Sergeant`
renders as `Sergeant • 12pts`. So candidates are **ranked** (`RosterActions.LabelMatch`):

| rank | rule | example, for the name `Armor` |
|---|---|---|
| `EXACT` | the label is the name | `Armor` |
| `DECORATED` | the name, then a non-alphanumeric character | `Armor • 3pts`, `Armor Type` |
| `CONTAINED` | the name appears anywhere | `Light Armor` |

The **best rank present in the window** is chosen first, over every label, checkbox and radio — and
before anything is driven, and without consulting the action. Picking the rank after the action would
let a control that declines to act (an unticked checkbox asked to decrement) hand the request down to
a worse rank, which is the neighbour-driving bug wearing a fallback's clothes.

`occurrence` (for two links onto one shared entry, which render identically) counts within the chosen
rank — identical spellings share a rank by construction.

#### Three outcomes, not two

`RosterActions.tryClickControlByLabel` returns a `ControlOutcome`, because a caller has three
different jobs after driving a control:

| outcome | meaning | what the caller must do |
|---|---|---|
| `NOT_FOUND` | no control carries this label | report it, or fall back to another route |
| `DRIVEN` | the control was operated | wait for the roster to change |
| `ALREADY_SET` | the control was **already** in the asked-for state | do **not** wait — nothing will change |

`ALREADY_SET` exists because of the single-choice group: its members are radio buttons, and selecting
an already-selected radio is a no-op in JavaFX rather than a re-fire. Reporting that as `DRIVEN` left
`rosterSelectChildEntryAction` polling for a delta that could never arrive — a full 10s
`STATE_POLL_TIMEOUT_MS` ending in "the click did nothing", about a postcondition that already held.

A **decrement** never yields `ALREADY_SET`: neither the `"+"` button nor a radio can take anything
away, so both decline a decrement request and the caller falls through to its DELETE path.

A **checkbox** is the one control that answers both directions, and it answers them by toggling — so
it is driven only when it is on the wrong side of what was asked. Firing it blind ticked an unticked
box for a decrement (adding the selection the caller asked to remove) and unticked a ticked one for a
select (removing the selection the caller asked for); each then waited out its poll for the opposite
of what it had just caused.

| control | select, already there | decrement, nothing to remove |
|---|---|---|
| Spinner | steps up (a count has no "already") | steps down |
| Button (`"+"`) | fires (adds another) | declines |
| CheckBox | `ALREADY_SET` | declines (box already unticked) |
| RadioButton | `ALREADY_SET` | declines |

#### `setSpinnerValueByLabel`

Finds a Spinner adjacent to a label matching the given text and sets it to the target value
by stepping (increment/decrement one at a time).

- **Thread**: FX
- **Params**:
  - `text` (string, required) — Label text to match
  - `value` (int, required) — Target integer value
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"set": true, "controlType": "spinner", "labelText": "Tactical Squad",
   "previousValue": 1, "value": 5, "elapsedMs": 120}
  ```
  or if already at target:
  ```json
  {"set": true, "controlType": "spinner", "labelText": "Tactical Squad",
   "previousValue": 5, "value": 5, "noChange": true}
  ```
  or on failure:
  ```json
  {"set": false, "error": "Spinner not found for label text: xyz"}
  ```
- **Note**: Steps one-at-a-time to ensure each increment triggers the engine's value
  change handlers correctly. May be slow for large deltas.

---

### Action Recording

The `ActionRecorder` subsystem attaches event listeners to the scene graph to record
user interactions as structured actions.

#### `startRecording`

Begins recording interactions in the specified (or first available) scene. Attaches:
- A mouse click event filter on the scene (for button clicks and tree item clicks)
- Value-change listeners on all Spinners, ComboBoxes, and CheckBoxes
- A recursive `ListChangeListener` that auto-attaches to new nodes added to the scene

- **Thread**: FX
- **Params**:
  - `windowTitle` (string, optional)
- **Returns**:
  ```json
  {"status": "recording"}
  ```

#### `stopRecording`

Stops recording and detaches all listeners. Returns all recorded actions.

- **Thread**: FX
- **Params**: none
- **Returns**:
  ```json
  {
    "actions": [
      {"type": "buttonClick", "timestamp": 1716150000000, "text": "New", "id": "btnNewRoster"},
      {"type": "comboBoxSelect", "timestamp": 1716150001000, "value": "Battalion", "id": "cboForceEntry"},
      {"type": "spinnerChange", "timestamp": 1716150002000, "value": "3", "oldValue": "1"},
      {"type": "treeItemClick", "timestamp": 1716150003000, "text": "HQ", "item": "HQ [1-2]"},
      {"type": "checkBoxToggle", "timestamp": 1716150004000, "text": "Veteran", "selected": "true"}
    ]
  }
  ```

#### `getRecordedActions`

Returns currently recorded actions without stopping. Useful for checking what's been
captured so far.

- **Thread**: FX
- **Params**: none
- **Returns**:
  ```json
  {"recording": true, "actions": [...]}
  ```

**Recorded action types:**
| Type | Trigger | Properties |
|------|---------|------------|
| `buttonClick` | Mouse click on Button | `text`, `id` |
| `treeItemClick` | Mouse click on TreeCell | `text`, `item` |
| `comboBoxSelect` | ComboBox value change | `value`, `id` |
| `spinnerChange` | Spinner value change | `value`, `oldValue` |
| `checkBoxToggle` | CheckBox selection change | `text`, `selected` |

---

### Engine Access

These commands use Java reflection to access the obfuscated BattleScribe engine internals
running in the same JVM.

#### `findEngine`

Locates the BS engine instance using multiple strategies:

1. **FXML handler traversal** (primary): Finds `#btnNewRoster` → reads its `onAction` handler
   → extracts `ControllerMethodEventHandler.handler` → `MethodHandler.controller` →
   `RosterEditorWindowController.b` (the engine field).
2. **Node properties/userData** (fallback): Walks the scene root's properties looking for
   the controller class.
3. **Static field scan** (last resort): Scans all loaded `net.battlescribe.*` classes for
   static fields of the engine type.

After finding the engine, also:
- Caches the `getRoster()` method reference for subsequent calls
- Patches the engine's thread count from 8 → 1 (via `sun.misc.Unsafe`) to prevent
  multi-threaded validation issues during external API calls

- **Thread**: Background
- **Params**: none
- **Returns**:
  ```json
  {"found": true, "engineClass": "net.battlescribe.engine.a.f", "via": "handler.controller.b"}
  ```
  or:
  ```json
  {"found": true, "engineClass": "net.battlescribe.engine.a.f", "cached": true}
  ```
  or:
  ```json
  {"found": false, "tried": ["scene:found", "handler_type:...", "engine_field_null"]}
  ```

#### `getRosterState`

Reads the complete roster state via reflection. Calls `engine.a()` (getRoster), then
recursively serializes the entire roster tree.

- **Thread**: Background
- **Params**: none
- **Requires**: `findEngine` must have been called successfully
- **Returns**:
  ```json
  {
    "name": "My Army",
    "gameSystemId": "abc-123",
    "gameSystemName": "Warhammer 40,000",
    "costs": [{"name": "pts", "typeId": "points", "value": 1500.0}],
    "costLimits": [{"name": "pts", "typeId": "points", "value": 2000.0}],
    "forces": [
      {
        "id": "force-1",
        "name": "Battalion Detachment",
        "catalogueId": "cat-1",
        "entryId": "entry-1",
        "catalogueName": "Space Marines",
        "customName": null,
        "customNotes": null,
        "hidden": false,
        "publicationId": null,
        "page": null,
        "rules": [{"name": "Rule1", "description": "...", "hidden": false, "page": null, "publicationId": null, "publicationName": null}],
        "categories": [{"name": "HQ", "entryId": "cat-entry-1", "primary": false, "customName": null, "customNotes": null, "publicationId": null, "page": null}],
        "publications": [{"id": "pub-1", "name": "Codex"}],
        "selections": [
          {
            "id": "sel-1",
            "name": "Captain",
            "entryId": "entry-captain",
            "entryGroupId": "group-hq",
            "type": "model",
            "number": 1,
            "hidden": false,
            "page": "45",
            "publicationId": "pub-1",
            "publicationName": "Codex",
            "customName": null,
            "customNotes": null,
            "categories": [...],
            "profiles": [
              {
                "name": "Captain",
                "typeId": "unit-type",
                "typeName": "Unit",
                "hidden": false,
                "page": "45",
                "publicationId": "pub-1",
                "publicationName": "Codex",
                "characteristics": [
                  {"name": "M", "typeId": "char-m", "value": "6\""},
                  {"name": "WS", "typeId": "char-ws", "value": "2+"}
                ]
              }
            ],
            "rules": [...],
            "costs": [{"name": "pts", "typeId": "points", "value": 100.0}],
            "children": [...]
          }
        ],
        "childForces": []
      }
    ]
  }
  ```
  - Selections are sorted alphabetically by name within each level
  - `children` contains nested selections (recursively same structure)

#### `exportRosterXml`

Serializes the current roster to BattleScribe `.ros` XML format using the engine's built-in
serializer (`net.battlescribe.a.c.e.a(Roster, OutputStream)`).

- **Thread**: Background
- **Params**: none
- **Returns**:
  ```json
  {"xml": "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<roster ...>...</roster>"}
  ```

#### `getValidationErrors`

Reads validation errors from the engine's roster state. Walks forces and selections
collecting constraint violations.

- **Thread**: Background
- **Params**: none
- **Returns**: Array of validation error objects
  ```json
  [
    {"message": "Must have 1-2 HQ selections", "entryId": "cat-hq", "scope": "force"},
    {"message": "Maximum 1 per army", "entryId": "entry-captain", "scope": "selection"}
  ]
  ```

##### What one call remembers

Attributing a single error can cost a reflective walk of the object graph (`collectInstances`) and a
roster search per candidate constraint (`constraintValuesOf`), and a roster with N errors asks the
same handful of questions N times over a model that cannot change while the call runs. Both are
memoized for the duration of ONE `getValidationErrors` call, in `EngineAccessor.ValidationPass`, and
the memory is dropped when it returns.

Per call rather than per session, deliberately: the roster changes between calls, and an entry that
is absent now exists after the next selection — a session-scoped "not found" would outlive the fact
that produced it. `findClass` is the one exception and caches for the session, because a loaded class
stays loaded; it remembers hits only, since a class not loaded yet may be loaded later.

---

### High-Level Action RPCs

These methods orchestrate complete UI workflows on the Java side. They run on a background
thread and internally coordinate FX thread operations, window waits, and state polling.

All action methods:
- Are dispatched by `RosterActions.dispatch()` when the method name ends in `Action`
- Run on the server's IO thread (NOT the FX thread)
- Return JSON with at minimum a `success` field
- May include `forceId` and/or `selectionId` for entity identification
- Poll `getRosterState()` after mutations to confirm the state change took effect

#### Available Actions

| Action | Params | Description |
|--------|--------|-------------|
| `createRosterAction` | `forceEntryId`, `catalogueId`, `gameSystemName`, `rosterName`, `costLimit?` | Creates a new roster via New Roster dialog |
| `addForceAction` | `forceEntryId`, `catalogueId` | Adds a force via Edit Roster dialog |
| `addChildForceAction` | `parentForceId`, `forceEntryId`, `catalogueId` | Adds a sub-force under an existing force |
| `removeForceAction` | `forceId` | Removes a force via Edit Roster → X button → confirm |
| `selectEntryAction` | `forceId`, `entryId` | Double-clicks a catalogue entry to add a selection |
| `selectChildEntryAction` | `forceId`, `parentSelectionId`, `entryId`, `entryName` | Increments a child entry via the edit panel |
| `deselectSelectionAction` | `forceId`, `selectionId` | Decrements/removes a selection |
| `setSelectionCountAction` | `forceId`, `selectionId`, `count` | Sets selection count via spinner |
| `duplicateSelectionAction` | `forceId`, `selectionId` | Duplicates via Ctrl+D |
| `duplicateForceAction` | `forceId` | Duplicates via Ctrl+D |
| `setCostLimitAction` | `costTypeId`, `costName`, `value` | Sets cost limit via Edit Roster spinner |
| `setCustomizationAction` | `forceId`, `selectionId?`, `categoryEntryId?`, `customName?`, `customNotes?` | Sets custom name/notes via context menu. Supports forces, categories, and selections. |

#### Action Patterns

Actions follow common patterns:

1. **Dialog actions** (createRoster, addForce, setCostLimit): Open Edit Roster → manipulate → Done → wait for dialog close
2. **Tree actions** (selectEntry, duplicateSelection): Select in roster tree → perform action → poll for state change
3. **Edit panel actions** (selectChildEntry, deselectSelection, setSelectionCount): Select parent → interact with edit panel → poll for state change
4. **Context menu actions** (setCustomization): Select entity → right-click → select menu item → fill dialog → confirm

---

### Diagnostics

#### `threadDump`

Dumps all JVM threads with their current stack traces (up to 15 frames each).

- **Thread**: Background
- **Params**: none
- **Returns**:
  ```json
  {
    "threads": [
      {
        "name": "JavaFX Application Thread",
        "state": "RUNNABLE",
        "stack": [
          "com.sun.glass.ui.win.WinApplication._enterNestedEventLoopImpl(Native Method)",
          "com.sun.glass.ui.win.WinApplication._enterNestedEventLoop(WinApplication.java:140)",
          "..."
        ]
      },
      {
        "name": "bs-ui-agent-server",
        "state": "WAITING",
        "stack": [...]
      }
    ]
  }
  ```

---

## Timeout Architecture

Operations pass through multiple timeout layers:

| Layer | Timeout | Where |
|-------|---------|-------|
| Startup timeout | 30s | BsRosterApp.StartAsync |
| AgentClient.CallTimeout | 90s | Per *Action JSON-RPC round-trip |
| AgentClient.CallTimeout | 30s | Per low-level JSON-RPC round-trip |
| FX thread dispatch | 60s | JsonRpcServer.executeOnFxThread |
| Window wait | 15s | RosterActions.waitForWindow |
| State poll | 10s | RosterActions.waitForStateChange |
| Diagnostic timeout | 5s | BsUiDiagnostics (reduced) |

For a typical high-level action, the effective max wait is roughly:
window wait (15s) + state poll (10s) = 25s on the Java side, well within the 90s RPC timeout.

The FX dispatch timeout (60s) is intentionally longer than the low-level CallTimeout (30s) so the .NET
side times out first with a clearer error message.

---

## Failure Diagnostics

When an action fails, `BsUiDiagnostics.CaptureAsync()` writes a diagnostic dump file to
`artifacts/bs-ui-diagnostics/` (configurable via `BS_UI_DIAGNOSTICS_DIR`). The dump includes:

1. **Metadata**: Timestamp, spec ID, action description, error type/message
2. **Open windows**: `getWindows()` result
3. **All windows scene dump**: `dumpAllWindows(maxDepth: 4)` — shows all dialogs
4. **Thread dump**: All JVM threads + stacks (identifies deadlocks)
5. **Scene graph**: `dumpTree(maxDepth: 5)` of the main window
6. **Stack trace**: Full .NET exception

The diagnostic capture uses a reduced 5s timeout to avoid hanging when the agent is
partially stuck.

### What a failed wait says

`RosterActions.waitForStateChange` takes an optional describer that renders the LAST state read
into the timeout message. Pass it wherever the predicate asks a question the state can answer:
"Timed out waiting for state change" on its own says only that the loop ran out, and cannot
distinguish an action that did nothing from one whose result the predicate did not recognise from
one that acted somewhere else. Those are different bugs — and on this lane they were two of them
hiding behind one message. `selectEntryAction` and `selectChildEntryAction` pass one.

### `BS_UI_VALIDATION_TRACE=1`

Prints every validation error with each id source that could name it — the owning element's
`getValidationErrorIds()`, the object BattleScribe attached to the error, and what
`resolveValidationRef` made of them. Off by default; on, it is a line per error per state read.

Which element carries usable ids varies by owner type, and the only way to find out is to look at
all of them at once. Note that the attached object is the roster element the error hangs on, NOT
the source constraint — see `resolveValidationRef`'s javadoc before reading ids off it.

---

## BsUiRosterEngine — IRosterEngine Mapping

The `BsUiRosterEngine` is now a thin dispatcher. Most UI workflow logic lives in Java-side
`*Action` RPCs.

| IRosterEngine Method | Action RPC | What Java does |
|---------------------|------------|----------------|
| `Setup(gs, cats)` | (no RPC — C# handles launch) | Stage files → launch app → wait for window → dismiss dialogs → patch supporter |
| `AddForce(forceEntryId, catId)` | `createRosterAction` (first) / `addForceAction` (subsequent) | Opens New/Edit Roster dialog → selects game system → adds force → selects catalogue + force entry → Done |
| `AddChildForce(parentId, entryId, catId)` | `addChildForceAction` | Edit Roster → select parent in tree → Add Force → select → Done |
| `RemoveForce(forceId)` | `removeForceAction` | Edit Roster → click cell button (X) → confirm YES |
| `SelectEntry(forceId, entryId)` | `selectEntryAction` | Select force in roster tree → double-click entry in catalogue tree → poll for new selection |
| `SelectChildEntry(forceId, parentId, entryId)` | `selectChildEntryAction` | Select parent in roster tree → `clickControlByLabel` in edit panel → poll for new selection |
| `DeselectSelection(forceId, selId)` | `deselectSelectionAction` | Select parent in tree → decrement via `clickControlByLabel`. Fallback: select in tree → DELETE key |
| `SetSelectionCount(forceId, selId, count)` | `setSelectionCountAction` | Select parent in tree → `setSpinnerValueByLabel` → poll for count match |
| `DuplicateSelection(forceId, selId)` | `duplicateSelectionAction` | Select in roster tree → Ctrl+D → poll for new selection |
| `DuplicateForce(forceId)` | `duplicateForceAction` | Select force in roster tree → Ctrl+D → poll for new force |
| `SetCostLimit(costTypeId, value)` | `setCostLimitAction` | Edit Roster → `setSpinnerValueByLabel` for cost name → Done |
| `SetCustomization(forceId, selId, catEntryId, name, notes)` | `setCustomizationAction` | Select entity in tree → right-click → "Customise Name..." → fill fields → Done. Supports forces, categories (via `categoryEntryId` tree navigation using `getEntryId()` reflection), and selections. |
| `GetRosterState()` | `findEngine` + `getRosterState` | (low-level RPCs, not actions) |
| `GetValidationErrors()` | `getValidationErrors` | (low-level RPC) |

State polling for mutation confirmation now happens inside the Java action implementations.

---

## BsUiProbe — Interactive Debug Mode

`BsUiProbe` provides a standalone workflow for ad-hoc exploration:

```csharp
await using var probe = new BsUiProbe(options);
await probe.LaunchAsync(gameSystem, catalogues, xmlFiles, Console.Error);

// Interactive use:
await probe.DumpTreeAsync(Console.Out);
await probe.DumpWindowsAsync(Console.Out);
var client = probe.Client;
// ... call any AgentClient method
```

Used by the debugger tool for manual investigation of BattleScribe behavior.

---

## Building the Java Agent

```powershell
pwsh -File src/bs-ui-java-agent/build.ps1   # all platforms (PowerShell 7+)
```

Normally you don't run this by hand — `setup.ps1` invokes it. It compiles the Java sources and
packages them into `bs-ui-java-agent.jar` with the `MANIFEST.MF` specifying
`Premain-Class: bsspec.uiagent.BsUiAgent`.

Dependencies: Gson (bundled or on classpath), JavaFX (from the BS JRE).

---

## Configuration

The adapter is configured via `BsUiOptions`:

```csharp
public record BsUiOptions
{
    /// Path to the Java executable (must include JavaFX modules)
    public required string JavaPath { get; init; }

    /// Path to RosterEditor.jar
    public required string RosterEditorJarPath { get; init; }

    /// Path to bs-ui-java-agent.jar
    public required string AgentJarPath { get; init; }

    /// Optional isolated home directory. If null, a temp directory is created and cleaned up.
    public string? IsolatedHomePath { get; init; }
}
```

The BattleScribe JRE (which bundles JavaFX) is required — a standard JDK without JavaFX
modules will not work.
