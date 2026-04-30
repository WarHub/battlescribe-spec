# Data Editor Investigation

Research findings from reverse-engineering BattleScribe DataEditor v2.03.21 and NR Editor v1.4.6.
Backs the design in [data-editor-extension.md](data-editor-extension.md).

## Sources Investigated

| Source | Location | Method |
|--------|----------|--------|
| BattleScribe DataEditor | `DataEditor.jar` (282 KB, 139 classes) | CFR decompilation |
| DesktopCommon | `lib/DesktopCommon.jar` (1.1 MB, 99 classes) | CFR decompilation |
| BattleScribeEngine | `lib/BattleScribeEngine.jar` | CFR decompilation (existing) |
| DataUtils | `lib/DataUtils.jar` | CFR decompilation (existing) |
| NR Editor | https://github.com/giloushaker/nr-editor | Source reading + live Playwright |
| wham EditorServices | `D:\repos\wham\src\WarHub.ArmouryModel.EditorServices\` | Source reading |

Decompiled output: `D:\repos\battlescribe-decompiled\{DataEditor,DesktopCommon,BattleScribeEngine,DataUtils}\`
BattleScribe extracted install: `C:\Users\amadeusz\Downloads\BattleScribe_2.03.21_Extracted\`

## DataEditor Architecture (Java/Kotlin, JavaFX)

### JAR Dependencies (beyond engine/DataUtils)

| JAR | Purpose |
|-----|---------|
| `controlsfx-11.0.1` | JavaFX advanced controls |
| `antlr-runtime-3.5.2` | Expression/condition parsing |
| `ST4-4.0.8` | StringTemplate rendering |
| `jetty-server-9.4.26` | Embedded HTTP server (data indexing?) |
| `jna-5.5.0` | Native platform integration |
| `kotlinx-coroutines-core-1.3.0` | Async operations |

### Controller Hierarchy

```
BaseEditPanelController (abstract)
├── BaseDataEditPanelController
│   └── BaseBookDataEditPanelController
│       └── CatalogueEditPanelController
├── BaseModifyableDataEditPanelController
│   └── RootDataEditPanelController
├── BaseEntryEditPanelController
│   ├── BaseRootEntryEditPanelController
│   │   └── BaseSelectionEntryEditPanelController
│   │       ├── SelectionEntryEditPanelController
│   │       └── SelectionEntryGroupEditPanelController
│   ├── CategorisedEditPanelController
│   ├── CommentableEditPanelController
│   └── LinkEditPanelController
├── ConditionEditPanelController
├── ConditionGroupEditPanelController
├── ConstraintEditPanelController
├── CostTypeEditPanelController
├── FilteredQueryEditPanelController
├── ModifierEditPanelController
├── ProfileEditPanelController
├── ProfileTypeEditPanelController
├── PublicationEditPanelController
├── QueryEditPanelController
├── QuickConstraintsPanelController
├── RepeatEditPanelController
└── RuleEditPanelController
```

Main window: `DataEditorWindowController` (16 inner classes, references `net.battlescribe.engine.a.a` controller + `net.battlescribe.engine.b.h` data manager).

### Engine Integration

DataEditor does **not** purely manipulate model POJOs. It uses:
- `net.battlescribe.engine.a.a` — engine controller (same as roster engine entry point)
- `net.battlescribe.engine.b.h` — `BaseDataManager` (obfuscated), manages data file lifecycle
- `net.battlescribe.engine.a.d`/`e` — additional engine services

This means data editing goes through the engine layer for validation/consistency, not just direct model mutation.

## Operation Inventory

### Context Menu Operations (from `DataEditorWindowController`)

```
mitAddCatalogueLink      mitAddInfoGroup          mitAddSharedInfoGroup
mitAddCategoryEntry      mitAddInfoLink           mitAddSharedProfile
mitAddCategoryLink       mitAddModifier           mitAddSharedRule
mitAddCondition          mitAddModifierGroup      mitAddSharedSelectionEntry
mitAddConditionGroup     mitAddProfile            mitAddSharedSelectionEntryGroup
mitAddConstraint         mitAddProfileType        mitCollapse
mitAddCostType           mitAddPublication        mitCopy
mitAddEntryLink          mitAddRepeat             mitCut
mitAddForceEntry         mitAddRule               mitExpand
                         mitAddSelectionEntry     mitFollowLink
                         mitAddSelectionEntryGroup mitPaste
                                                  mitRemove
```

Toolbar: `btnSaveDataFile`, `btnSaveDataFileAs`, `btnAddData`, `btnErrorMessages`

### NR Editor Operations (from `editorStore.ts`, 2063 lines)

| Method | Signature | Notes |
|--------|-----------|-------|
| `create_system` | `(name, path?, extension?)` | New game system/catalogue |
| `add` | `(data, childKey?, parents?)` | Add entry to parent(s) |
| `remove` | `(entry_or_entries?)` | Remove from tree |
| `duplicate` | `()` | Deep-copy selection |
| `cut/copy/paste` | `(event)` | Clipboard via JSON serialization |
| `pasteLink` | `()` | Paste as entryLink |
| `move` | `(obj, from, to, type)` | Between catalogues (root↔shared) |
| `undo/redo` | `()` | Stack of `{ type, undo, redo }` closures |
| `do_action` | `(type, undo, redo)` | Register undoable operation |
| `save_catalogue` | `(catalogue, path?, ...)` | Serialize to XML |

### Feature Parity Matrix

| Operation | BS DataEditor | NR Editor |
|-----------|:---:|:---:|
| Add entry | ✓ | ✓ |
| Remove entry | ✓ | ✓ |
| Copy/Cut/Paste | ✓ | ✓ |
| Paste as Link | — | ✓ |
| Duplicate | — | ✓ |
| Move root↔shared | — | ✓ |
| Sort entries | — | ✓ |
| Follow link | ✓ | ✓ |
| View usages | ✓ | ✓ |
| Validate data | ✓ | partial |
| Undo/Redo | unclear | ✓ |
| Create new file | ✓ | ✓ |
| Save file | ✓ | ✓ |
| GitHub import | — | ✓ |

### Entry Types (unified vocabulary)

| Key | BS Menu | NR `get_initial_object` |
|-----|---------|------------------------|
| `selectionEntry` | ✓ `mitAddSelectionEntry` | ✓ `{type:"upgrade", import:true, name, hidden:false, id}` |
| `selectionEntryGroup` | ✓ `mitAddSelectionEntryGroup` | ✓ (similar) |
| `entryLink` | ✓ `mitAddEntryLink` | ✓ `{import:true, name, hidden:false, id}` |
| `categoryEntry` | ✓ `mitAddCategoryEntry` | ✓ (default) |
| `categoryLink` | ✓ `mitAddCategoryLink` | ✓ `{type:"category", name, hidden:false, id}` |
| `forceEntry` | ✓ `mitAddForceEntry` | ✓ (default) |
| `costType` | ✓ `mitAddCostType` | ✓ `{name, id, defaultCostLimit:-1}` |
| `profileType` | ✓ `mitAddProfileType` | ✓ (default) |
| `profile` | ✓ `mitAddProfile` | ✓ `{name, typeId, typeName, hidden:false, id, characteristics:[]}` |
| `rule` | ✓ `mitAddRule` | ✓ (default) |
| `infoGroup` | ✓ `mitAddInfoGroup` | ✓ (default) |
| `infoLink` | ✓ `mitAddInfoLink` | ✓ (default) |
| `publication` | ✓ `mitAddPublication` | ✓ (default) |
| `catalogueLink` | ✓ `mitAddCatalogueLink` | ✓ `{type:"catalogue", name, id}` |
| `modifier` | ✓ `mitAddModifier` | ✓ `{type:"set", value:true, field:"hidden"}` |
| `modifierGroup` | ✓ `mitAddModifierGroup` | ✓ `{type:"and"}` |
| `condition` | ✓ `mitAddCondition` | ✓ `{type:"atLeast", value:1, field:"selections", scope:"parent", childId:"any", shared:true}` |
| `conditionGroup` | ✓ `mitAddConditionGroup` | ✓ `{type:"and"}` |
| `constraint` | ✓ `mitAddConstraint` | ✓ `{type:"min", value:1, field:"selections", scope:"parent", shared:true, id}` |
| `repeat` | ✓ `mitAddRepeat` | ✓ `{value:1, repeats:1, field:"selections", scope:"parent", childId:"any", shared:true, roundUp:false}` |
| `association` | — | ✓ `{min:1, max:1, scope, childId, ids:[], name, id}` (NR extension) |
| `associationLink` | — | ✓ (NR extension) |

### Property Panels → Field Editing

| Panel | Editable Fields |
|-------|----------------|
| BaseEditPanel | name, id, hidden |
| CommentableEditPanel | comment |
| CategorisedEditPanel | primary/secondary category links |
| ConstraintEditPanel | type (min/max), value, field, scope, shared, childId, includeChildSelections/Forces |
| ConditionEditPanel | type, value, field, scope, childId, shared, includeChildSelections/Forces |
| ModifierEditPanel | type (set/increment/decrement/append), field, value |
| RepeatEditPanel | value, repeats, field, scope, childId, shared, roundUp |
| ProfileEditPanel | typeId, typeName, characteristics[] (name, typeId, $text) |
| ProfileTypeEditPanel | characteristicTypes[] (name, id) |
| CostTypeEditPanel | name, defaultCostLimit, hidden |
| PublicationEditPanel | name, shortName, publisher, publicationDate, publisherUrl |
| LinkEditPanel | targetId, type |
| SelectionEntryEditPanel | type (unit/model/upgrade), collective |
| SelectionEntryGroupEditPanel | defaultSelectionEntryId |
| CatalogueEditPanel | revision, authorName, authorContact, authorUrl, readme |

## Key Insights

1. **Engine layer required** — DataEditor routes through `net.battlescribe.engine.b.h` (data manager), not just model POJOs. For IKVM adapter, must identify and use this data manager API.

2. **ANTLR presence** — `antlr-runtime-3.5.2.jar` in lib suggests expression parsing. Likely for condition/constraint text-based expression syntax that the editor validates.

3. **Shared module is private** — NR Editor's core logic (`assets/shared/battlescribe/`) lives in private `giloushaker/nr-shared`. The public `editorStore.ts` delegates to it for `allowed_children`, `onAddEntry`, `onRemoveEntry`, `scrambleIds`, etc.

4. **`allowed_children` is central** — determines valid parent→child relationships. Must be reverse-engineered from BattleScribe XSD schema or observed behavior.

5. **NR extends BS** — associations, paste-as-link, move, duplicate, sort are NR-only. Conformance spec should define a "core" tier (BS parity) and "extended" tier (NR additions).

6. **ID generation** — NR uses `generateBattlescribeId()` (UUID format). BS DataEditor uses `java.util.UUID.randomUUID()`. Both produce standard UUIDs.
