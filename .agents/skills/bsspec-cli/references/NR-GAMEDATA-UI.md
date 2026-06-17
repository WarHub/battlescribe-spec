# NewRecruit Editor GameData UI Driver

Drives the NR Editor web app via Playwright for GameData conformance testing
(`--engine newrecruit --ui`, gamedata domain). Hybrid pattern: mutations through real UI
interactions (clicks, context menus, property panels); state reads via JS injection into the
Pinia `editorStore`. See the parent [SKILL.md](../SKILL.md) for CLI verbs.

## Architecture

```
NrGameDataUiEngine (IGameDataEngine)
├── Setup: NrGameDataUiSetup.LoadAndOpenCatalogueAsync()
│   ├── Generate XML via CatXmlGenerator
│   ├── Inject showDirectoryPicker mock OR call loadSystemFromFs() via Pinia
│   └── Navigate to the catalogue editor via UI tree click
├── Mutations: NrGameDataUiActions.*
│   ├── AddEntry    → right-click tree node → context menu → "Add" → type
│   ├── RemoveEntry → right-click tree node → "Delete" → confirm
│   ├── SetField    → click entry → edit property-panel input
│   └── AddLink     → right-click → "Add Link" → select target
├── State: NrGameDataUiActions.ReadStateAsync()  (page.EvaluateAsync over editorStore;
│           same JS as NewRecruitGameDataEngine.GetState())
└── Diagnostics: NrGameDataUiDiagnostics
```

**Engine name in specs**: `newrecruit-ui`. The authoritative source for selectors is
`NrGameDataUiActions.cs` — read it before changing an action.

## Frozen vs. live mode

| Mode | Description | Skip env var |
|------|-------------|--------------|
| Frozen | Serves `.testdata/nr-editor/` static files locally via Playwright route interception | `NR_EDITOR_UI_FROZEN_SKIP=true` |
| Live | Connects to the live NR Editor at `NR_EDITOR_URL` | — |

Both use `NrGameDataUiEngine`; the difference is `CreateFrozenAsync` vs `CreateAsync`.
`setup.ps1` downloads `.testdata/nr-editor/` (the NR Editor gh-pages snapshot pinned in
`testdata.json`, shared with `NewRecruitGameDataEngine`) and the Playwright browsers.

Test profiles: `dotnet test -p:TestProfile=nr-editor-ui-frozen` (in `pre-push`, static route
interception, no network) and `…=nr-editor-ui-live`.

## Probe — discover selectors

```powershell
dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui my-gamedata-spec
```

Always runs headed (`NR_HEADLESS` does not apply). Prefers the frozen static files, falling
back to `NR_EDITOR_URL` (default `https://giloushaker.github.io/nr-editor`). In the REPL:

```javascript
const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia
[...pinia._s.keys()]                                   // all store ids
const ed = pinia._s.get('editor') || pinia._s.get('editorStore')
ed.catalogue?.name
ed.catalogue?.selectionEntries?.map(e => ({ id: e.id, name: e.name }))
```

The REPL is drivable non-interactively for DOM discovery:
`echo '<js>' | dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui <spec>`.

State is read from the Pinia `editorStore` (the path tries multiple store/property names for
version compatibility); `window.__bsspec_editor_ui` holds references set during Setup, available
in all subsequent `EvaluateAsync` calls.

## Selectors & traps (verified against the pinned snapshot)

The NR Editor is a Vue/Nuxt SPA with no test hooks, so several intuitive selectors fail.

| Element | Selector | Trap to avoid |
|---------|----------|---------------|
| Tree node by id | resolve in the Pinia store → match `.{collectionClass} h3:is(.normalTitle,.arrowTitle)` by name | no `data-id` / `role=treeitem` in the DOM |
| Context-menu items | `.context-menu > div` filtered by **anchored** text (`^\s*Group\s*$`) | not `role=menuitem`; unanchored "Group" also matches "Modifier Group" / "Info Group" |
| Entry id / name fields | `td:last-child input[type='text']` in the editor-table row | no `name`/`id` attribute on the input |
| Delete / Save confirm | `GetByRole(Button, Name="Confirm"\|"Delete"\|"Yes")` (`"OK"\|"Save"\|"Close"` for saves) | button label varies by dialog |

Notes baked into `FindTreeNodeByIdAsync` / `AddEntry`:
- Resolve the entry **recursively** in the store (children of children) to get its name and
  `collectionClass`, then expand **all** `h3.arrowTitle.collapsed` nodes in a loop — expanding
  only depth-0 leaves deeper parents collapsed and their children unrendered.
- **Add child entry**: right-click the parent, click the text-labelled item — `Entry` for a
  `selectionEntry`, `Group` for a `selectionEntryGroup` (`GetAddChildMenuLabel`), matched by
  **anchored** text.
- **Add at a root section**: different code path (`AddEntryToRootSectionAsync`); the menu item
  is matched by its icon `src` filename, not text.
- **`collective` is a root-entry limitation, not a skip**: the `Collective` checkbox is
  *disabled* for root entries, so `se-set-field-collective` nests the entry under a `Squad`
  (type `unit`) to enable it. The spec runs on `newrecruit-ui` and is **not** skipped
  (un-skipped in `da9da15`) — do not "fix" it by adding a skip.
- A new entry's id/name live in the editor table on the right; fill the name input, then
  `PressAsync("Tab")` — the panel commits on blur/Enter, not on input.

## Diagnostics

On failure, `NrGameDataUiDiagnostics.CaptureAsync()` writes
`artifacts/nr-gamedata-ui-diagnostics/{timestamp}-{specId}/`: `screenshot.png`, `console.txt`,
`editor-state.json` (full Pinia `editorStore`), `dom.html`. On a CI failure: open the
screenshot, check `editor-state.json` for the state the driver thought was loaded, then
`console.txt` for JS errors.

## How to extend

**New mutation action**: add the method to `IGameDataEngine` (see the `changing-protocol-types`
skill); in `NrGameDataUiActions.cs` probe the DOM for selectors (use `probe`); implement
following existing patterns (`FindTreeNodeByIdAsync`, context-menu clicks); add a YAML spec
under `specs/gamedata/`; run `tools/format-specs.ps1` then `dotnet test -p:TestProfile=nr-editor-ui-frozen`.

**New entry type**: in `AddEntry`, map the `entryType` string to the NR Editor context-menu
label (may differ from the BattleScribe type name) and add a case in the menu navigation.

If you bump the pinned NR Editor commit in `testdata.json`, re-verify selectors with `probe` —
they can drift between snapshots.

## Source map

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
