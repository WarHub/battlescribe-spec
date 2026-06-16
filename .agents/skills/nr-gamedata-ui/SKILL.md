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

**Setup requirements**:
- Run `setup.ps1` to download `.testdata/nr-editor/` (the NR Editor gh-pages snapshot).
- Run `pwsh -File src/BattleScribeSpec.NrGameDataUiDriver/install-playwright.ps1` to install Playwright browsers.
- Same NR Editor snapshot as `NewRecruitGameDataEngine` (no separate download).

## Test profiles

| Profile | Command |
|---------|---------|
| `nr-editor-ui-frozen` | `dotnet test -p:TestProfile=nr-editor-ui-frozen` |
| `nr-editor-ui-live` | `dotnet test -p:TestProfile=nr-editor-ui-live` |

Frozen tests are included in `pre-push` and run via a static Playwright route interception
(no live network needed). They use the pinned NR Editor commit from `testdata.json`.

## Probe mode — discover selectors

```powershell
# Launch NR Editor with spec data, open a JS REPL
dotnet run --project src/BattleScribeSpec.Debugger -- --engine gamedata/newrecruit-ui --probe my-gamedata-spec

# With no-headless for visual inspection
$env:NR_HEADLESS = "false"
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

### Tree navigation

```csharp
// Find tree node by entry ID (data-id attribute or text search)
private static async Task<ILocator?> FindTreeNodeByIdAsync(IPage page, string entryId)
{
    var byDataId = page.Locator($"[data-id='{entryId}']");
    if (await byDataId.IsVisibleAsync())
        return byDataId;
    // Fall back to :id: substring token in tree text
    var byTreeItem = page.Locator($"[role='treeitem']").Filter(new() { HasText = $":{entryId}:" });
    return await byTreeItem.IsVisibleAsync() ? byTreeItem : null;
}
```

### Context menu operations

```csharp
// Right-click tree node → context menu → pick item
await treeNode.ClickAsync(new() { Button = MouseButton.Right });
await page.GetByRole(AriaRole.Menuitem, new() { Name = "Add" }).ClickAsync();
await page.GetByRole(AriaRole.Menuitem, new() { Name = "Selection Entry" }).ClickAsync();
```

### Nested operations & UI realities (verified via `--probe` REPL)

The NR Editor's actual entry-node context menu does **not** use `role=menuitem`; items are
`.context-menu > div` elements. Hard-won specifics:

- **Add child entry** — right-click the parent's tree node and click the text-labelled item:
  `Entry` for a `selectionEntry`, `Group` for a `selectionEntryGroup` (see `GetAddChildMenuLabel`).
  The icons are inline **base64 data URIs**, so match by **exact text** (anchored regex
  `^\s*Group\s*$` — an unanchored "Group" also matches "Modifier Group" / "Info Group").
  Root-section adds are different (icon-`src` filename match in `AddEntryToRootSectionAsync`).
- **Finding nested nodes** — `FindTreeNodeByIdAsync` resolves the entry **recursively** in the
  Pinia store (children of children), then expands **all** `h3.arrowTitle.collapsed` nodes in a
  loop (depth-0 section expansion alone leaves deeper parents collapsed and their children
  unrendered), then matches `.{collectionClass} h3:is(.normalTitle,.arrowTitle)` by name.
- **collective on model-type entries** — the `Collective` checkbox is **disabled** in the NR
  Editor for `type: model` entries (verified: `disabled=true`), so `se-set-field-collective` is
  intentionally `skip`ped for `newrecruit-ui` — a genuine product limitation, not a driver gap.

The probe REPL is drivable non-interactively for DOM discovery — pipe JS expressions to stdin:
`echo '<js>' | dotnet run --project src/BattleScribeSpec.Debugger -- --engine gamedata/newrecruit-ui --probe <spec>`.

### Property panel field editing

```csharp
// Click entry to select → edit name in right panel
await treeNode.ClickAsync();
var nameInput = page.Locator("input[placeholder*='Name'], input[name='name']").First;
await nameInput.FillAsync(newName);
await nameInput.DispatchEventAsync("change");
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

## Key selector notes

> **These selectors are best-effort estimates and must be validated by probing.**
> Run `--probe` on actual NR Editor pages to verify.

| Element | Selector pattern |
|---------|-----------------|
| Tree nodes | `[data-id='{id}']` or `[role='treeitem']` filtered by text |
| Context menu items | `[role='menuitem']` with name matching |
| Property name input | `input[name='name']` or `input[placeholder*='Name']` |
| Delete confirm button | `button:has-text('Delete')`, `button:has-text('Confirm')` |

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
