# NrRosterUiDriver — NewRecruit Browser UI Automation Adapter

The `BattleScribeSpec.NrRosterUiDriver` project is a UI automation adapter that drives the
**NewRecruit web roster editor** (Vue.js/Nuxt) as a conformance test engine. It implements
`IRosterEngine` — the same interface used by the IKVM-based BS engine and the non-UI NR
adapter — but instead of calling NR's JS API directly, it **interacts through the browser
UI** using Playwright, mimicking real user actions (clicks, form inputs, menu selections).

## Architecture Overview

```
┌────────────────────────────────────────────────────────┐
│  NrRosterUiEngine (C#, IRosterEngine)                  │
│  Thin dispatcher — maps IRosterEngine to UI actions    │
├────────────────────────────────────────────────────────┤
│  NrUiActions (C#, Playwright locators + interactions)  │
│  High-level UI workflows: AddForce, SelectEntry, etc.  │
├────────────────────────────────────────────────────────┤
│  NrUiSetup (C#, data loading + roster creation)        │
│  Loads game data via JS store API, creates roster      │
├────────────────────────────────────────────────────────┤
│  NewRecruitBrowser (shared, Playwright lifecycle)       │
│  Browser/page management, HAR replay, navigation       │
├────────────────────────────────────────────────────────┤
│  NrUiDiagnostics (C#, failure capture)                 │
│  Screenshot, console log, DOM snapshot, Pinia dump     │
├────────────────────────────────────────────────────────┤
│  NrUiProbe (C#, interactive debugging)                 │
│  Ad-hoc DOM exploration with live Playwright session   │
└────────────────────────────────────────────────────────┘
```

State reading: reuses `NewRecruitStateReader` (JS reads from Pinia stores — acceptable per
design decision that only **mutations** must go through UI).

## Source Files

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.NrRosterUiDriver/NrRosterUiEngine.cs` | `IRosterEngine` implementation |
| `src/BattleScribeSpec.NrRosterUiDriver/NrUiActions.cs` | All Playwright UI interactions |
| `src/BattleScribeSpec.NrRosterUiDriver/NrUiSetup.cs` | Game data loading, roster creation |
| `src/BattleScribeSpec.NrRosterUiDriver/NrUiDiagnostics.cs` | Failure diagnostics capture |
| `src/BattleScribeSpec.NrRosterUiDriver/NrUiProbe.cs` | Interactive probe/debug mode |

## Design Decisions

1. **All mutations via UI** — every roster-modifying action (add force, select entry, set
   cost limit, rename, etc.) is performed via Playwright UI interaction. No `EvaluateAsync`
   calls that mutate NR engine state.

2. **State reading via JS** — reading roster state (forces, selections, costs, errors) uses
   Pinia store access. This is not a mutation and avoids fragile DOM scraping.

3. **Supporter bypass** — NR locks some features (notes, custom names) behind a supporter
   paywall. The adapter injects a fake supporter user via JS during setup to unlock these.

4. **Setup via UI with directory picker mock** — Game data loading uses a
   `showDirectoryPicker` mock that returns spec-generated XML files, then clicks
   "Add more games" → "Add From Folder" in the UI. This keeps the data loading path
   exercising NR's real import logic.

5. **Deferred roster creation** — The roster is NOT created during `Setup`. Instead, it is
   created on the first `AddForce` call via the Lists → New → Create List UI flow. NR
   auto-creates the first force during "Create List", so the first `AddForce` adopts that
   existing force (reads its uid via JS) rather than adding a duplicate.

6. **Engine name = "newrecruit"** — shares assertion overrides with the non-UI NR adapter
   (same behavioral quirks, same expected state adjustments).

## Action → UI Mapping

| Action | UI Flow |
|--------|---------|
| AddForce | First call: Lists → "New" → select catalogue → "Create List" (adopts auto-created force). Subsequent: "List Options" → "Add Force" OR forces panel `+` button |
| RemoveForce | Force Options `.dots` → "Delete Force" |
| DuplicateForce | Force Options `.dots` → "Duplicate Force" |
| SelectEntry | Click entry row (`.boutonSubUnit` or `.addButton`) in panel |
| SelectChildEntry | Open parent options → click child toggle/button |
| DeselectSelection | Click `img[title='Delete Unit']` on unit row |
| DuplicateSelection | Click `img[title='Duplicate Unit']` on unit row |
| SetChildEntryCount | Open parent options → fill `input[type='number']` |
| SetCostLimit | "List Options" → "List Configuration" → `.maxCostInput` |
| SetCustomization (sel) | Open unit panel → submenu → "Rename Unit" / "Add Note" |
| SetCustomization (force) | Force Options → "Rename Force" |
| SetSelectionCount | Child: fill `input[type='number']`; Root: throws (no UI control) |

## Key DOM Selectors

| Selector | Element |
|----------|---------|
| `.bookForce` | Force container |
| `.dotsMenuContainer` | Dots menu trigger containers |
| `.dots` | Clickable dots trigger within menu container |
| `.unitRow` | Selection/unit row |
| `.unitRow.editing` | Currently open selection panel |
| `.inputOption` | Option item in selection panel |
| `.boutonSubUnit` | Entry toggle button (checkbox/radio-like) |
| `.addButton` | Add entry `+` button |
| `input[type='number'].input.minmax` | Numeric option input |
| `img[alt='edit cost limits']` | List Configuration menu item trigger |
| `input[id='{costTypeId}']` | Cost limit input in config dialog |
| `.editname` | Roster name/note edit button |
| `img[title='Delete Unit']` | Delete selection icon |

## Running

```bash
# Run specific spec with NR UI engine
dotnet run --project src/BattleScribeSpec.Cli -- run --engine newrecruit --ui protocol-kitchen-sink

# Interactive probe mode
dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui protocol-kitchen-sink

# Visible browser (non-headless)
dotnet run --project src/BattleScribeSpec.Cli -- run --engine newrecruit --ui --headed protocol-kitchen-sink
```

## Diagnostics

On failure, `NrUiDiagnostics` captures:
- **Screenshot** of current page state
- **Console logs** collected during the test
- **DOM snapshot** (page HTML)
- **Pinia state dump** (serialized stores)

Artifacts are saved to `artifacts/nr-ui-diagnostics/` — relative to the working directory, which is
the repo root under `bs-spec`. Under `dotnet test` that would be the test assembly's output folder,
so the fixtures anchor it at the repo root (`TestPaths.AnchorDiagnosticsAtRepoRoot`); an explicit
`NR_UI_DIAGNOSTICS_DIR` wins over both. CI's `thorough-conformance` job uploads the directory as
`thorough-conformance-nr-ui-diagnostics` when it fails.

**A timeout inside an action names itself.** Playwright's own message for a `WaitForFunctionAsync` is
`Timeout 20000ms exceeded.` and nothing more, so `NrRosterUiEngine.WithDiagnosticsAsync` rewrites it
with the action, the page, an observation read back from the editor, and where the report went:

    NR UI addForce-fe-patrol: Timeout 20000ms exceeded. (page: https://www.newrecruit.eu/app/MyLists).
    Observed: forces=2 forcesPanel=0 forceRows=0 unitRows=0 popups=1. Report: …/nr-ui-diagnostics/….

Read it the way `docs/nr-ui-roster-coverage.md` §5b reads setup's: the route says whether the driver
was even on the editor, and the counts say whether the panel it was reaching for had rendered. A
timeout that already carries such a description (setup's) is passed through unchanged.

## Probe Mode

The interactive probe (`NrUiProbe`) provides a REPL for live DOM exploration:
- Loads game data and creates roster
- Opens a browser window (non-headless)
- Accepts JavaScript expressions to evaluate against the page
- Useful for discovering selectors, testing interaction flows

## Known Limitations

- **SetSelectionCount (root)**: Root-level selections have no count spinner in NR UI.
  Throws `NotSupportedException`. Only child entry counts are adjustable via UI.
- **Force notes**: No dedicated UI in NR. Silently ignored by the driver. Specs must
  use `engines: newrecruit:` overrides to expect empty `customNotes` on forces.
- **Hidden entries**: Cannot be selected/deselected via UI. Throws `NotSupportedException`.
- **Cost display rendering**: In probe/test setup, cost display may not render if
  `getCostTypes()` returns undefined (probe-specific issue, not an NR limitation).
- **Vue event handlers**: `element.click()` via JS does NOT trigger Vue event handlers.
  Must use Playwright's real event dispatch (`ClickAsync`) for dropdown menus.
