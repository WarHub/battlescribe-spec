# BattleScribe Desktop UI → Engine Flow

This document maps the BattleScribe 2.03.21 desktop UI actions to their exact engine
method calls. Traced from decompiled source (JADX 1.5.5) of `RosterEditor.jar` and
`BattleScribeEngine.jar`.

## Architecture

The UI controller (`RosterEditorWindowController`) holds a direct reference to the
engine (`net.battlescribe.engine.a.f`) as `this.b` (also exposed via
`getRosterManager()`). There is no intermediate "roster manager" layer — the engine IS
the roster manager.

## Engine Refresh Cycle — `t()`

Every mutation calls `t()` internally (f.java:150):

```
synchronized t() {
    u();             // mark dependent entries as changed via dependency graph
    a(false, true);  // cost refresh — single-pass over CHANGED selections
    v();             // validate constraints
    d();             // clear query cache (this.k)
    w();             // clear all 'changed' flags
}
```

This is the **only** refresh mechanism. The UI does NOT call any additional
validation/update methods after engine mutations — it only updates its tree view
and dirty flag.

## UI Post-Mutation Calls

After every engine mutation, the UI calls:
- `w()` → `roster.setSaved(false)` — marks roster as unsaved (dirty flag)
- `c()` → updates window title + refreshes tree display

These are purely UI-level. **No additional engine calls.**

## Selection Operations

### Add Root Entry (catalogue tree + button / double-click)

```
UI: RosterEditorWindowController.a(SelectionEntry)
  → this.b.a(selectionEntry)                    // selectRootEntry (f.java:919)
    → b(b(selectionEntry), selectionEntry)      // finds force, delegates to selectEntry
      → c(parent, entry, 1)                     // creates selection
      → t()                                     // full refresh
```

Our adapter: `Engine.SelectEntry(force, entry)` → `_engine.b(force, entry)` ✅ Same

### Add Child Entry (selection panel button/checkbox)

```
UI: RosterEditorWindowController.addSelection(parent, childEntry)
  → this.b.b(parent, childEntry)                // selectEntry (f.java:936)
    → c(parent, entry, 1)                       // creates/increments selection
    → t()                                       // full refresh
```

Our adapter: `Engine.SelectEntry(parent, entry)` → `_engine.b(parent, entry)` ✅ Same

### Change Count (spinner)

```
UI: RosterEditorWindowController.setNumSelections(parent, entry, newCount)
  delta = this.b.b(parent, entry, newCount)     // getNumChanges (f.java:895)
  if isDuplicate(entry) → delta = 0 → NO-OP
  if delta > 0:
    for i in 0..delta:
      this.a(parent, entry, false)              // calls selectEntry
        → this.b.b(parent, entry)              // selectEntry → t() each time
  if delta < 0:
    selection = this.b.c(parent, entry)         // getCurrentSelection
    for i in 0..|delta|:
      this.a(selection, false)                  // calls deselectEntry
        → this.b.m(selection)                  // deselectEntry → t() each time
```

Our adapter: Matches this exactly — loops individual `SelectEntry`/`DeselectEntry`
calls with `GetNumChanges` to compute delta.

**Critical behavioral note:** Each individual call triggers a full `t()` refresh.
For self-referencing cost-field repeat modifiers, intermediate cost states are
visible to subsequent repeat queries. This produces different results than the
engine's atomic `setNumSelections` API (which does all changes + 1 refresh).

### Remove Selection (× button)

```
UI: RosterEditorWindowController.removeSelection(selection)
  → this.b.m(selection)                         // deselectEntry (f.java:1160)
    → a(selection, 1)                           // actual deselect logic
    → t()                                       // full refresh
```

Our adapter: `Engine.DeselectEntry(selection)` → `_engine.m(selection)` ✅ Same

### Duplicate Selection

```
UI: RosterEditorWindowController.d(selection)
  → this.b.l(selection)                         // selectFavourite (duplicate)
```

Our adapter: `Engine.DuplicateSelection(selection)` → `_engine.k(selection)` ✅ Same

## Force Operations

### Add Root Force (live editing path)

```
UI: EditRosterWindowController.addForce() → e() loading dialog task:
  → getRosterManager().b(gameSystem, catalogue, linkedCatMap, forceEntry, favourites, errors)
    // selectRootForce (f.java:856)
    → a(gameSystem, catalogue, map, forceEntry, list, list2)  // creates force
    → a(a(), forceA, list2)                                    // adds to roster
    → t()                                                      // full refresh
```

Our adapter: `Engine.AddForce(catalogue, forceEntry)` → `_engine.b(gameSystem, ...)` ✅ Same

### Add Child Force (live editing path)

```
UI: EditRosterWindowController.addForce() → e() with parent as Force:
  → getRosterManager().b(parentForce, gameSystem, catalogue, linkedCatMap, forceEntry, ...)
    // selectForce (f.java:868)
    → a(force, gameSystem, catalogue, map, forceEntry, list, list2)  // creates child force
    → a(force, forceA, list2)                                        // adds to parent
    → t()                                                            // full refresh
```

Our adapter: `Engine.CreateChildForce(parentForce, ...)` → `_engine.b(parentForce, ...)` ✅ Same

### Remove Force

```
UI: EditRosterWindowController.removeForce(force) → b(force) loading dialog:
  → getRosterManager().g(force)                 // deselectForce (f.java:1140)
    → c(force, false, true, false)              // remove all selections
    → a(force) or a(parentForce, force)         // remove from parent
    → c(listC)                                  // cleanup
    → u(); t()                                  // mark changed + full refresh
```

Our adapter: `Engine.RemoveForce(force)` → `_engine.g(force)` ✅ Same

## Initial Roster Creation — `setRoster`

When the desktop UI creates a new roster or opens an existing one:

```
setRoster(roster, gameSystem, maps..., z=true):   // f.java:28
  b(true)                   // set loading = true
  j()                       // clear state
  a(roster, gs, maps...)    // set up forces and validate selections
  a(gs, errors)             // set up cost types
  if (z) x()               // auto-select default root entries (entries with min>=1)
  a(true, true)             // FULL cost refresh (ALL selections, not just changed)
  v()                       // validate
  d()                       // clear cache
  w()                       // clear changed flags
  b(false)                  // set loading = false
```

Our adapter flow differs because we don't use `setRoster`:
- We call `selectRootForce` (which creates force + calls `t()`)
- Then `x()` (auto-select defaults, creates entries without refresh)
- Then `t()` (full refresh to process the auto-selected entries)

The difference: `setRoster` uses `a(true, true)` (refresh ALL) while our `t()` uses
`a(false, true)` (refresh only CHANGED). Since auto-selected entries are freshly
created, they should be in a changed state and picked up by `t()`.

## The `isDuplicate` Distinction (f.java:978-1001)

When `selectEntry` creates a selection (private `b(dVar, parent, entry)` at line 978):

- If `parent instanceof Force` OR `isDuplicate(entry)`:
  - ALWAYS creates a NEW selection node (separate tree item)
- Else (non-isDuplicate, parent is Selection):
  - Looks for existing selection with same entry
  - If found: increments `selection.setNumber(getNumber() + 1)`
  - If not found: creates new selection node

For count changes via the spinner:
- `isDuplicate` entries → `getNumChanges` returns 0 → NO-OP (spinner disabled in UI)
- Non-isDuplicate entries → delta computed, individual calls loop

## Edit Roster Dialog — Force Management (Batch Mode)

The "Edit Roster" dialog (`rosterForceManager.o() = true`) uses a different path
that manipulates the roster model directly without engine calls:

- `rosterForceManager.n()` — creates `new Force(...)`, adds to roster list
- `rosterForceManager.a(force)` — removes from roster list recursively

This is a batch-editing mode where the engine is not involved until the dialog
closes and the roster is reloaded. Our adapter does NOT use this path.

## Summary: Adapter ↔ UI Method Mapping

| UI Action | Engine Method (f.java) | Our Adapter Call | Match |
|-----------|----------------------|------------------|-------|
| Add root entry | `a(entry)` → `b(force, entry)` → `t()` | `SelectEntry(force, entry)` | ✅ |
| Add child entry | `b(parent, entry)` → `t()` | `SelectEntry(parent, entry)` | ✅ |
| Count change | N × `b(parent, entry)` or N × `m(sel)` | N × `SelectEntry` / `DeselectEntry` | ✅ |
| Remove selection | `m(selection)` → `t()` | `DeselectEntry(selection)` | ✅ |
| Duplicate | `k(selection)` | `DuplicateSelection(selection)` | ✅ |
| Add root force | `b(gs, cat, map, fe, ...)` → `t()` | `AddForce(cat, fe)` | ✅ |
| Add child force | `b(pf, gs, cat, map, fe, ...)` → `t()` | `CreateChildForce(pf, fe, cat)` | ✅ |
| Remove force | `g(force)` → `u()` + `t()` | `RemoveForce(force)` | ✅ |
