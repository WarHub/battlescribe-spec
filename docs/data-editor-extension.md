# Extending BattleScribe Spec with Data Editor Conformance

## Problem

The spec system currently tests **roster editing** only (add forces, select entries, assert roster state). Issue #29 requests extending it to test **data editing** — the operations that create and modify `.gst`/`.cat` data files themselves.

## Architecture Delta

Current roster conformance flow:
```
SpecFile.Setup (defines data) → IRosterEngine.Setup → Actions (mutate roster) → GetRosterState (assert)
```

Data editor conformance flow (proposed):
```
SpecFile.Setup (defines initial data OR empty) → IDataEditor.Setup → Actions (mutate data) → GetDataState (assert)
```

Key difference: in roster specs, setup data is _fixed_ and actions mutate a roster built from it. In data editor specs, the data itself is the mutable artifact.

## Files to Modify/Create

### Core Interface (new)

**`src/BattleScribeSpec.TestKit/IDataEditor.cs`** — engine abstraction for data editing.

Modeled after `IRosterEngine.cs` (146 lines). Must define:

```csharp
public interface IDataEditor : IDisposable
{
    // Lifecycle
    void SetTestContext(string specId) { }
    IReadOnlyList<string> Setup(/* initial state: empty or loaded */);
    void Cleanup() { }

    // Structural mutations (add/remove nodes in the data tree)
    DataActionOutputs AddEntry(string parentId, string entryType, DataEntryDef? defaults);
    void RemoveEntry(string entryId);
    void MoveEntry(string entryId, string newParentId);

    // Property mutations (change fields on existing nodes)
    void SetField(string entryId, string field, object? value);

    // Link management
    DataActionOutputs AddLink(string parentId, string linkType, string targetId);

    // State query
    DataFileState GetDataState();
    IReadOnlyList<ValidationErrorState> GetValidationErrors();
}
```

### Protocol Messages (extend existing)

**`src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`** — add new command/response types.

Follow existing pattern (line 15-39): register new `[JsonDerivedType]` entries on `ProtocolCommand`/`ProtocolResponse`.

New commands needed:
- `DataSetupCommand` — initialize with empty system/catalogue or load existing
- `DataActionCommand` — execute a data editing action
- `GetDataStateCommand` — query current data file state

New responses:
- `DataSetupResult` — success/errors
- `DataActionResult` — ok + outputs (created entry IDs)
- `DataStateResponse` — full data tree state

### State Types (new)

**`src/BattleScribeSpec.TestKit/DataEditorTypes.cs`** — state records for assertion.

Parallel to `Roster/RosterTypes.cs` which defines `RosterState`, `ForceState`, `SelectionState`, etc.

```csharp
public record DataFileState(
    string Id, string Name, string Type, /* "gameSystem" | "catalogue" */
    IReadOnlyList<DataEntryState> RootEntries,
    IReadOnlyList<DataEntryState> SharedEntries,
    IReadOnlyList<ValidationErrorState> ValidationErrors);

public record DataEntryState(
    string Id, string Name, string EntryType,
    bool Hidden,
    IReadOnlyList<DataEntryState> Children,
    IReadOnlyDictionary<string, object?>? Fields);
```

### Spec Models (extend existing)

**`src/BattleScribeSpec.TestKit/Roster/RosterSpecModels.cs`** — add data editor step/assertion types.

The existing `StepDef` (line 111) handles roster actions. Options:
1. **Extend `StepDef`** with data-editor fields (simpler, less clean)
2. **New `DataStepDef`** in a separate spec file model (cleaner separation)

Likely approach: new `DataSpecFile` model or a `mode: dataEditor` field on `SpecFile`.

### Runner (new or extend)

**`src/BattleScribeSpec.TestKit/DataEditorRunner.cs`** — parallel to `Roster/RosterRunner.cs`.

The existing `SpecRunner` is 260 lines, dispatches to `IRosterEngine`. A `DataEditorRunner` would dispatch to `IDataEditor` with its own action vocabulary.

### YAML Spec Files (new category)

**`specs/data-editor/*.yaml`** — new spec category.

Example spec:
```yaml
id: data-editor-add-selection-entry
category: data-editor
description: Add a selection entry to a catalogue

setup:
  dataFile:
    type: catalogue
    id: cat-1
    name: Test Catalogue
    gameSystemId: gs-1
    # Initial content (may reference a minimal game system)

steps:
  - action: addEntry
    id: add-unit
    parentId: cat-1
    entryType: selectionEntry
    defaults:
      name: "New Unit"
      type: upgrade

  - expectedDataState:
      rootEntries:
        - name: "New Unit"
          entryType: selectionEntry
```

### Adapter Integration

**`src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs`** — extend dispatch (line 37-44).

Either:
- Add `DataSetupCommand`/`DataActionCommand` cases to existing handler
- Create `DataAdapterHandler.cs` for separate data editor protocol (likely better)

### Engine Implementations (new)

**`src/BattleScribeSpec.BattleScribe/BattleScribeDataEditor.cs`** — IKVM-based adapter.

Uses classes from:
- `DataEditor.jar` → `net.battlescribe.desktop.dataeditor.*` controllers
- `DataUtils.jar` → `net.battlescribe.a.*` utility classes  
- `BattleScribeEngine.jar` → `net.battlescribe.engine.a.a` (engine controller)

The DataEditor uses `net.battlescribe.engine.b.h` ("BaseDataManager") — this is the engine-level API for managing data. The UI controllers delegate to it.

**Challenge:** DataEditor.jar heavily depends on JavaFX (`javafx.*`). IKVM cannot run JavaFX UI. The adapter must identify and use the **headless engine/model layer** only (the `engine.b.h` data manager), NOT the UI controllers.

**`src/BattleScribeSpec.NewRecruit/NewRecruitDataEditor.cs`** — Playwright-based adapter.

Drives https://giloushaker.github.io/nr-editor/ via browser automation:
- Pinia store: `$store` global (as documented in `types/global.d.ts`)
- Key operations: `$store.add()`, `$store.remove()`, `$store.duplicate()`
- State extraction: iterate catalogue tree via store API

## Key Design Decisions

### 1. What constitutes "data state" for assertions?

The data tree is much deeper/wider than a roster. Options:
- **Full tree** (expensive, verbose assertions)
- **Targeted subtree** (assert only specific paths, like roster assertions do with partial matching)
- **Query-based** (XPath-like addressing into the data tree)

Recommendation: partial matching (as roster assertions do) — assert only fields explicitly specified.

### 2. Entry type vocabulary

From decompiled BattleScribe DataEditor (32 menu items) and NR Editor (`get_initial_object`):

| Entry Type Key | Description |
|---------------|-------------|
| `selectionEntry` | Unit/upgrade/model |
| `selectionEntryGroup` | Group with min/max constraints |
| `entryLink` | Link to shared entry |
| `categoryEntry` | Category definition |
| `categoryLink` | Category assignment |
| `forceEntry` | Force type definition |
| `costType` | Point/cost type |
| `profileType` | Profile column definitions |
| `profile` | Stat profile (characteristics) |
| `rule` | Text rule |
| `infoGroup` | Group of profiles/rules |
| `infoLink` | Link to shared profile/rule |
| `publication` | Source book reference |
| `catalogueLink` | Catalogue dependency |
| `constraint` | Min/max limits |
| `modifier` | Conditional field changes |
| `modifierGroup` | Group of modifiers |
| `condition` | Logic condition |
| `conditionGroup` | AND/OR condition groups |
| `repeat` | Repeat modifier N times |
| `characteristic` | Individual stat value |

### 3. Setup model

Two modes:
1. **Empty setup** — create a new game system or catalogue from scratch
2. **Pre-populated setup** — load existing data (reuse existing `ProtocolGameSystem`/`ProtocolCatalogue` types as initial state)

### 4. Allowed children rules

Critical for validation. The `allowed_children(parent, parentKey)` mapping determines what entry types can be nested where. This must be captured as a rule table, derived from:
- BattleScribe XSD schema (`wham/src/dataformat/xml/schema/v2_03/`)
- NR Editor's `allowed_children` function (in private `nr-shared` submodule)
- Decompiled DataEditor context menu visibility logic

### 5. IKVM feasibility for DataEditor

**Viable path:** Don't use `DataEditor.jar` controllers (they need JavaFX). Instead:
- Use `BattleScribeEngine.jar` `net.battlescribe.engine.b.h` (data manager) directly
- Use `DataUtils.jar` for load/save/serialization
- Construct data model objects via existing `net.battlescribe.model.data.*` classes (already working via IKVM in `JavaModelFactory.cs`)

The existing `JavaModelFactory.cs` already creates `GameSystem`, `Catalogue`, `SelectionEntry`, etc. — extend it to support mutation (add/remove children, set fields).

### 6. NR Editor feasibility

**Viable path:** Playwright drives the web editor at `https://giloushaker.github.io/nr-editor/`.
- Load a test file (import catalogue)
- Execute operations via `$store.add()`, `$store.remove()`, etc. (evaluate JS)
- Read state back from the Pinia store

Similar pattern to existing `BattleScribeSpec.NewRecruit` which drives roster builder via Playwright.

## Reference: Existing Patterns to Follow

| Concern | Roster equivalent | Data editor parallel |
|---------|------------------|---------------------|
| Engine interface | `IRosterEngine.cs` | `IDataEditor.cs` |
| State types | `Roster/RosterTypes.cs` (RosterState) | `DataEditorTypes.cs` (DataFileState) |
| Protocol types | `ProtocolMessages.cs` | Same file or new section |
| Setup data types | `ProtocolGameSystem`/`ProtocolCatalogue` | Reuse (they ARE the data) |
| Runner | `Roster/RosterRunner.cs` | `DataEditorRunner.cs` |
| BS adapter | `BattleScribeEngine.cs` | `BattleScribeDataEditor.cs` |
| NR adapter | `NewRecruitRosterEngine.cs`* | `NewRecruitDataEditor.cs` |
| Spec category | `specs/selection/`, `specs/force/` | `specs/data-editor/` |
| Adapter handler | `AdapterHandler.cs` | Extend or new handler |

## Implementation Order

1. Define `IDataEditor` interface + `DataFileState` types
2. Add protocol messages (`DataSetupCommand`, `DataActionCommand`, `GetDataStateCommand`)
3. Implement `DataEditorRunner` (action dispatch + assertion)
4. Add `DataStepDef`/`DataExpectedStateDef` to spec models
5. Write first spec YAML (`data-editor/add-entry-basic.yaml`)
6. Implement BS adapter (IKVM, model-level mutation via JavaModelFactory pattern)
7. Implement NR adapter (Playwright + `$store` Pinia API)
8. Extend lint rules for new spec category
9. Add test profile for data editor specs

## Available Decompiled Sources

| Package | Purpose | Key Classes |
|---------|---------|-------------|
| `net.battlescribe.model.data.*` | Data model (mutable POJOs) | `GameSystem`, `Catalogue`, `SelectionEntry`, `Constraint`, `Modifier`, `Profile`, etc. |
| `net.battlescribe.engine.b.h` | Data manager (engine API) | Obfuscated; manages data file lifecycle |
| `net.battlescribe.engine.a.a` | Engine controller | Referenced by DataEditor controllers |
| `net.battlescribe.desktop.dataeditor.*` | UI controllers (JavaFX) | NOT usable via IKVM; reference only |
| `net.battlescribe.desktop.common.*` | Shared desktop UI | `ManageDataWindowController`, `BattleScribeApplication` |

Decompiled at: `D:\repos\battlescribe-decompiled\{DataEditor,DesktopCommon,BattleScribeEngine,DataUtils}\`

## NR Editor API Surface

Global store (`$store`): `ReturnType<typeof useEditorStore>` — accessed via Playwright `page.evaluate()`.

Key methods:
- `$store.create_system(name)` — create new game system
- `$store.add(data, childKey, parents)` — add entry to parent
- `$store.remove(entries)` — remove entries
- `$store.duplicate()` — duplicate selection
- `$store.cut/copy/paste()` — clipboard operations
- `$store.undo()/redo()` — undo/redo stack
- `$store.move(obj, from, to, type)` — move between catalogues
- `$store.get_initial_object(key, parent)` — factory defaults for new entry types
- `$store.saveCatalogue(data)` — persist changes

Live at: https://giloushaker.github.io/nr-editor/  
Source: https://github.com/giloushaker/nr-editor (shared logic submodule is private)
