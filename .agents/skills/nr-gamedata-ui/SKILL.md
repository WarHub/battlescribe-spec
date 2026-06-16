---
name: nr-gamedata-ui
description: >
  Debug NR Editor GameData UI driver actions and test failures. Use when investigating
  NrGameDataUiDriver test failures, reading Pinia editorStore state, discovering
  NR Editor DOM/CSS selectors for new actions, or understanding the frozen static-file
  test setup. Covers the probe workflow (--engine gamedata/newrecruit-ui --probe), NrGameDataUiDiagnostics
  artifacts, frozen vs. live mode, and how to extend NrGameDataUiActions.
---

# NR GameData UI Driver

Drives the NR Editor web application via Playwright for GameData conformance testing.
Follows the **hybrid UI driver pattern**: mutations through real UI interactions (clicks,
context menus, property panels), state reads via Playwright JS injection into Pinia stores.

## Architecture

```
NrGameDataUiEngine (IGameDataEngine)
├── Setup: NrGameDataUiSetup.LoadAndOpenCatalogueAsync()
│   ├── Generate XML via CatXmlGenerator
│   ├── Inject showDirectoryPicker mock OR call loadSystemFromFs() directly via Pinia
│   └── Navigate to catalogue editor via UI tree click
├── Mutations: NrGameDataUiActions.*
│   ├── AddEntry    → right-click tree node → context menu → "Add" → type
│   ├── RemoveEntry → right-click tree node → "Delete" → confirm
│   ├── SetField    → click entry → edit property panel input
│   └── AddLink     → right-click → "Add Link" → select target
├── State: NrGameDataUiActions.ReadStateAsync()
│   └── page.EvaluateAsync() reading Pinia editorStore
│       (same JS as NewRecruitGameDataEngine.GetState())
└── Diagnostics: NrGameDataUiDiagnostics
    └── screenshot + console log + DOM snapshot + editorStore JSON
```

**Engine name**: `newrecruit-ui` (use in spec `engines` field overrides).

## Frozen vs. live mode

| Mode | Description | Env var to skip |
|------|-------------|-----------------|
| Frozen | Serves `.testdata/nr-editor/` static files locally via Playwright route interception | `NR_EDITOR_UI_FROZEN_SKIP=true` |
| Live | Connects to live NR Editor at `NR_EDITOR_URL` | N/A |

Both modes use the same `NrGameDataUiEngine` — the difference is in `CreateFrozenAsync` vs `CreateAsync`.

**Setup**: run `setup.ps1` — it downloads `.testdata/nr-editor/` (the NR Editor gh-pages snapshot,
pinned to the commit in `testdata.json`, shared with `NewRecruitGameDataEngine`) and installs the
Playwright browsers.

## Test profiles

| Profile | Command |
|---------|---------|
| `nr-editor-ui-frozen` | `dotnet test -p:TestProfile=nr-editor-ui-frozen` |
| `nr-editor-ui-live` | `dotnet test -p:TestProfile=nr-editor-ui-live` |

Frozen tests are included in `pre-push` and run via a static Playwright route interception
(no live network needed). They use the pinned NR Editor commit from `testdata.json`.

## Probe mode — discover selectors

```powershell
# Launch NR Editor with spec data, open a JS REPL.
# The probe always runs headed (browser visible) — NR_HEADLESS does not apply here.
# It prefers the frozen static files (.testdata/nr-editor/) and falls back to
# NR_EDITOR_URL (default https://giloushaker.github.io/nr-editor) when they're absent.
dotnet run --project src/BattleScribeSpec.Debugger -- --engine gamedata/newrecruit-ui --probe my-gamedata-spec
```

In the probe REPL, use JS to inspect the Pinia stores:

```javascript
// Get all Pinia store IDs
const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia
[...pinia._s.keys()]

// Inspect editorStore
const ed = pinia._s.get('editor') || pinia._s.get('editorStore')
JSON.stringify(Object.keys(ed))

// Check current catalogue
ed.catalogue?.name
ed.catalogue?.selectionEntries?.map(e => ({id: e.id, name: e.name}))
```

## State extraction

State is read from the Pinia `editorStore` via `page.EvaluateAsync`:

```javascript
// Key path (tries multiple store names for version compatibility):
const editorStore = pinia._s.get('editor') || pinia._s.get('editorStore')
    || pinia._s.get('catalogue') || pinia._s.get('catalogues')

// Catalogue root (tries multiple property names):
const cat = editorStore?.catalogue || editorStore?.currentCatalogue
    || editorStore?.rootCatalogue || editorStore?.rootEntry
```

The `window.__bsspec_editor_ui` context object stores references to pinia/stores set during Setup,
available in all subsequent `EvaluateAsync` calls.

## Action implementation patterns

These are the **actual** selectors the driver uses (verified via `--probe`). The NR Editor is a
Vue/Nuxt SPA with no test hooks, so several intuitive selectors do **not** work — the notes below
call out each trap. The authoritative source is `NrGameDataUiActions.cs`; read it before changing
an action.

### Tree navigation — no `data-id`, resolve via the Pinia store

The NR Editor does **not** render `data-id` (or `role=treeitem`) attributes on tree nodes, so you
cannot locate an entry by ID in the DOM. `FindTreeNodeByIdAsync` instead:

1. Resolves the entry **recursively** in the Pinia store (children of children) to get its name
   and `collectionClass`.
2. Expands **all** `h3.arrowTitle.collapsed` nodes in a loop — expanding only the depth-0 section
   leaves deeper parents collapsed and their children unrendered.
3. Matches `.{collectionClass} h3:is(.normalTitle, .arrowTitle)` by visible name.

### Context menu operations — `.context-menu > div`, not `role=menuitem`

The entry-node context menu items are plain `.context-menu > div` elements (no ARIA roles):

```csharp
await treeNode.ClickAsync(new() { Button = MouseButton.Right });
// Match the menu item by its exact visible text (icons are inline base64 data URIs).
await page.Locator(".context-menu > div").Filter(new() { HasTextRegex = new Regex(@"^\s*Entry\s*$") })
    .ClickAsync();
```

- **Add child entry** — right-click the parent's tree node and click the text-labelled item:
  `Entry` for a `selectionEntry`, `Group` for a `selectionEntryGroup` (see `GetAddChildMenuLabel`).
  Match by **anchored** text (`^\s*Group\s*$`) — an unanchored "Group" also matches
  "Modifier Group" / "Info Group".
- **Add at a root section** — different code path (`AddEntryToRootSectionAsync`): the menu item is
  matched by its icon `src` filename, not text.
- **collective is a root-entry limitation, not a skip** — the `Collective` checkbox is **disabled**
  for **root** entries in the NR Editor, so `se-set-field-collective` nests the entry under a parent
  (`Squad` of type `unit`) so the checkbox is enabled. The spec runs on `newrecruit-ui` and is
  **not** skipped (it was un-skipped in commit `da9da15`). Do not "fix" it by adding a skip.

The probe REPL is drivable non-interactively for DOM discovery — pipe JS expressions to stdin:
`echo '<js>' | dotnet run --project src/BattleScribeSpec.Debugger -- --engine gamedata/newrecruit-ui --probe <spec>`.

### Reading an entry's ID and name

A newly-added entry's ID and name live in the editor table on the right, **not** in a
`name`/`id`-attributed input. Read them from the last cell of the matching row:

```csharp
var entryId = await idRow.Locator("td:last-child input[type='text']").InputValueAsync();
var nameInput = nameRow.Locator("td:last-child input[type='text']").First;
await nameInput.FillAsync(name);
await nameInput.PressAsync("Tab");   // commit — the panel writes the model on blur/Enter, not on input
```

## Diagnostics on failure

When a test fails, `NrGameDataUiDiagnostics.CaptureAsync()` writes to
`artifacts/nr-gamedata-ui-diagnostics/{timestamp}-{specId}/`:
- `screenshot.png` — browser screenshot at failure
- `console.txt` — browser console messages  
- `editor-state.json` — full Pinia editorStore serialization
- `dom.html` — full DOM snapshot

**Inspecting CI failures**:
1. Download the `nr-gamedata-ui-diagnostics` artifact from the CI run
2. Open `screenshot.png` to see what the browser showed
3. Open `editor-state.json` to check what state the driver thought was loaded
4. Check `console.txt` for JS errors

## How to extend

### Adding a new mutation action

1. Add the method to `IGameDataEngine` (see `changing-protocol-types` skill)
2. In `NrGameDataUiActions.cs`, probe the NR Editor DOM for the correct selectors:
   - Use `--probe` mode to find the context menu item text, property panel field name, etc.
3. Implement the action method following the existing patterns (FindTreeNodeByIdAsync, context menu clicks)
4. Write a YAML spec in `specs/gamedata/` to test the new action
5. Run `tools/format-specs.ps1` then `dotnet test -p:TestProfile=nr-editor-ui-frozen`

### Adding support for a new entry type

In `NrGameDataUiActions.cs`, update `AddEntry` to handle the new `entryType` string:
- Map the string to the NR Editor context menu label (may differ from the BattleScribe type name)
- Add a case in the context menu navigation logic

## Key selectors (verified against the pinned snapshot)

These reflect the NR Editor commit pinned in `testdata.json`. If you bump that commit, re-verify
with `--probe` — the SPA has no stable test hooks, so selectors can drift between snapshots.

| Element | Selector | Trap to avoid |
|---------|----------|---------------|
| Tree node by ID | resolve in Pinia store → match `.{collectionClass} h3:is(.normalTitle,.arrowTitle)` by name | no `data-id` / `role=treeitem` in the DOM |
| Context menu items | `.context-menu > div` filtered by **anchored** text | not `role=menuitem`; unanchored text over-matches |
| Entry id / name fields | `td:last-child input[type='text']` in the editor table row | no `name`/`id` attribute on the input |
| Delete / Save confirm | `GetByRole(Button, Name="Confirm"\|"Delete"\|"Yes")` (`"OK"\|"Save"\|"Close"` for saves) | button label varies by dialog |

## Reference files

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiEngine.cs` | Main engine class |
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiActions.cs` | All mutations + state |
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiSetup.cs` | File loading + routing |
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiDiagnostics.cs` | Failure diagnostics |
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiProbe.cs` | Interactive probe REPL |
| `src/BattleScribeSpec.NewRecruit/NewRecruitGameDataEngine.cs` | Non-UI reference implementation |
| `tests/Infrastructure/FrozenNrGameDataUiFixture.cs` | Frozen mode test fixture |
| `tests/Conformance/FrozenNrGameDataUiConformanceTests.cs` | Frozen mode conformance tests |
| `tests/test-profiles/nr-editor-ui-frozen.runsettings` | Frozen test profile |
