# Hidden Entry Validation Analysis

Analysis of how the BattleScribe engine validates hidden entries, based on the
decompiled Java source (`net.battlescribe.engine.a.f`). This documents a gap in
hidden validation for force entries and how the spec adapters compensate.

## Background

The BattleScribe engine has a `hidden` flag on data model entries (`ForceEntry`,
`CategoryEntry`, `SelectionEntry`). When an entry is hidden (either statically or
via a modifier), it should generally not be selectable. The engine has two
hidden-related validation behaviors:

1. **Constraint skipping** — hidden entries have their constraints suppressed.
2. **Hidden-selection errors** — when a hidden entry still has selections in the
   roster, an error is produced.

## Decompiled Validation Flow

The main validation method is `f.v()` (line 356 in decompiled `f.java`). It
walks the roster tree:

```
Roster
  └→ validate cost limits
  └→ for each Force:
       ├→ check ForceEntry constraints (line 416)
       ├→ check CategoryEntry hidden (line 424)  ← generates "(hidden)" errors
       └→ for each Category:
            ├→ check CategoryEntry constraints (line 437)
            ├→ check SelectionEntry hidden (line 444)  ← generates "(hidden)" errors
            └→ for each Selection:
                 ├→ check SelectionEntry constraints (line 448)
                 └→ check child SelectionEntry hidden (line 455)  ← generates "(hidden)" errors
```

### Constraint Skipping (line 485)

```java
// f.java line 485
if (baseEntry3.isHidden() && (baseEntry2 instanceof SelectionEntry ||
    ((Force)(object = this.b(baseEntry2))).getForces().isEmpty())) continue;
```

When checking constraints for entries in a container:
- **Hidden SelectionEntry** → all constraints skipped unconditionally
- **Hidden ForceEntry** → constraints skipped only if the force has no sub-forces;
  if it has sub-forces, constraints are still evaluated

### Hidden Error Generation (line 617–625)

```java
// f.java line 617 — method signature: a(d, BaseSelectable, List<BaseEntry>)
private <T extends BaseEntry> void a(d d2, BaseSelectable baseSelectable, List<T> list) {
    for (BaseEntry baseEntry : list) {
        BaseEntry resolved = this.a(d2, baseSelectable, baseEntry, true); // resolve with modifiers
        if (!resolved.isHidden()) continue;
        int count = this.a(d2, (BaseRosterElement)baseSelectable, (IFilteredQueryChild)baseEntry,
                          false, false, false);
        if (count == 0) continue;
        String msg = baseSelectable.getFullName()
            + " cannot have any selections of " + baseEntry.getName() + " (hidden)";
        this.a((BaseRosterElement)baseSelectable, /* error info */);
    }
}
```

This method:
1. Resolves each child entry with modifiers applied (the `true` parameter)
2. Checks if the resolved entry `isHidden()`
3. Counts how many matching items exist in the roster
4. If count > 0, creates error: `"{fullName} cannot have any selections of {name} (hidden)"`

It is called for:
| Call site (line) | Container (baseSelectable) | Checked entries (list) | Result |
|---|---|---|---|
| 424 | Force | CategoryEntries | ✅ Hidden category errors |
| 444 | Category | SelectionEntries | ✅ Hidden selection errors |
| 455 | Selection | Child SelectionEntries | ✅ Hidden child selection errors |

## The Gap: No Hidden Force Validation

**ForceEntries are never checked for hidden errors — at any level of nesting.**

The per-force validation method (line 416) does:

```java
// f.java line 416-434
private void a(Force force, List<Callable<Void>> list) {
    d d2 = this.e(force);
    if (force.isChanged()) {
        Iterator<BaseSelectionParent> iterator = d2.ac(); // child ForceEntries
        this.a(d2, force, (BaseEntry)iterator, list);     // → CONSTRAINT checker only
        List<CategoryEntry> object = d2.ad();             // CategoryEntries
        this.a(d2, force, object, list);                  // → HIDDEN error generator ✅
    }
    // ... recurse into categories, selections, child forces
    for (Force force2 : force.getForces()) {
        this.a(force2, list);                             // recurse (same method)
    }
}
```

- **Line 422**: Child ForceEntries (`d2.ac()`) go to the **constraint checker**
  (line 466) — min/max constraints are evaluated, but hidden state is not.
- **Line 424**: CategoryEntries (`d2.ad()`) go to the **hidden-error generator**
  (line 617) — ✅ produces "(hidden)" errors for hidden categories.
- **No call** passes ForceEntries to the hidden-error generator.

At the Roster level (line 137), the code iterates forces and calls
`a(roster, force, list)` which resolves the ForceEntry, updates metadata, and
recurses — but **never checks `forceEntry.isHidden()`**.

The gap exists at every level where forces appear:

| Container | Child type | Constraint check | Hidden error check |
|---|---|---|---|
| Roster → Forces | ForceEntry | ✅ (line 422 via recursion) | ❌ Never |
| Force → Child Forces | ForceEntry | ✅ (line 422) | ❌ Never |
| Force → Categories | CategoryEntry | ✅ (line 437) | ✅ (line 424) |
| Category → Selections | SelectionEntry | ✅ (line 448) | ✅ (line 444) |
| Selection → Children | SelectionEntry | ✅ (line 448) | ✅ (line 455) |

Additionally, the `Force` roster class does not have an `isHidden()` method —
only the data-model class `ForceEntry` has it. The roster `Force` object cannot
directly report its hidden state.

### Consequence

If a force entry becomes hidden (via modifier) after a force of that type has
been added to the roster, the BattleScribe engine will:
- ✅ Skip constraint validation for the hidden force (if it has no sub-forces)
- ❌ NOT produce a "(hidden)" error like it does for selections

This appears to be an oversight in the engine — the hidden-error-generation
pattern is consistently applied to categories, selections, and child selections,
but not to forces.

## NewRecruit Behavior

### Live UI Investigation (Playwright, 2025-04)

Verified with synthetic data loaded into NR at newrecruit.eu. Scenario: two forces
(Visible Force, Hideable Force), with a "Hide Trigger" upgrade that hides the
force and a selection via modifier. After adding Hide Trigger in Visible Force:

**NR internal tree state** (via Pinia store and instance method calls):

```
FORCE: Hideable Force
  isHidden(): true           ← modifier correctly applied
  getErrors(): 1             ← propagated from child, NOT own error
    -> "Hideable Unit cannot be selected while hidden"
  selector.hidden: 0         ← static value, NOT modifier-applied
  vueHiddenKey: 1            ← Vue reactivity counter, signals state change

  General (category) isHidden=false errors=1 (propagated)
    Hideable Unit isHidden=true errors=1
      -> "Hideable Unit cannot be selected while hidden"

FORCE: Visible Force
  isHidden(): false
  getErrors(): 0
  vueHiddenKey: 0

  General isHidden=false errors=0
    Hide Trigger isHidden=false errors=0
    Visible Unit isHidden=false errors=0
```

**Key findings:**

1. **`isHidden()` on instances** works correctly — reflects modifier-applied state.
   Available on force/category/selection instances via prototype chain.
2. **`selector.hidden`** is the STATIC definition value (always 0/false for these
   entries) — NOT the dynamic modifier-applied state.
3. **`vueHiddenKey`** is a Vue reactivity counter incremented when hidden state
   changes. Used by Vue components to trigger re-render of hidden indicators.
4. **Error format**: `"<span class='optName'>Hideable Unit</span> cannot be selected while hidden"`
   (HTML spans in the message, stripped by our adapter).
5. **`getErrors()`** on parent nodes aggregates ALL descendant errors — the red
   error icon on the force header in the UI is child error propagation, NOT a
   force-level hidden error.
6. **NR does NOT produce errors for hidden forces** — only for hidden selections.
   The force's own `errors` array is empty; `getErrors()` includes only propagated
   child selection errors.

### NR Architecture Notes

NR uses a dual-tree structure:
- **Selector tree** (`selectors[]`): catalogue-level definitions with `hidden`
  (static), `source` (with modifiers), and `instances[]`
- **Instance tree** (`instances[]`): roster-level objects with `isHidden()` (dynamic),
  `getErrors()` (aggregated), and full prototype methods

The instance prototype has ~200 methods including `isHidden`, `getErrors`,
`isForce`, `isCategory`, `getName`, `applyModifications`, `checkConstraints`,
`refreshErrors`, etc.

## Adapter Compensation

Since neither the BattleScribe engine nor NR natively validates hidden forces,
the spec adapters do not currently synthesize hidden-force errors.

### BattleScribe adapter

the BattleScribe engine adapter reads modifier-applied hidden state by calling the engine's
internal modifier-application method:

```csharp
// Creates a COPY of the entry with modifiers applied
(SelectionEntry)_engine.a(forceContext, selection, originalEntry, true)
(ForceEntry)_engine.a(forceContext, force, originalForceEntry, true)
```

This correctly returns the dynamic hidden state for both force entries and
selection entries. The `_engine.a(d, BaseSelectable, T, boolean)` method at
`c.java:1109-1134` creates a temporary copy with modifiers applied.

**Note**: The engine's entry map (`a.i(entryId)`) returns ORIGINAL entries —
modifiers are never applied to them. Always use the copy-creating method above.

### NR Adapter

The NR adapter reads modifier-applied hidden state via `f.isHidden()` on
force/selection instance objects. This correctly reflects modifiers for both
forces and selections.

## Summary Table

| Validation | BattleScribe Engine | NR Native | BattleScribe adapter | NR Adapter |
|---|---|---|---|---|
| Hidden selection error | ✅ "(hidden)" | ✅ "while hidden" | ✅ via error text | ✅ via error text |
| Hidden category error | ✅ "(hidden)" | ❓ Unknown | ✅ via error text | ✅ via error text |
| Hidden force error | ❌ Not implemented | ❌ Not implemented | ❌ Not generated | ❌ Not generated |
| Constraint skip for hidden | ✅ | ❓ Assumed from matching outcomes; not directly confirmed | ✅ | ✅ |
| Force hidden state read | N/A | `inst.isHidden()` (resolved) | `_engine.a()` copy (resolved) | `inst.isHidden()` (resolved) |
| Selection hidden state read | N/A | `inst.isHidden()` (resolved) | `_engine.a()` copy (resolved) | `inst.isHidden()` (resolved) |

## References

- Decompiled source: `net.battlescribe.engine.a.f` (validation engine)
  - `v()` at line 356: main validation entry point
  - Hidden error generation at line 617–625
  - Constraint skipping at line 485
- NR error synthesis: `JsHelpers.cs` lines 453–459
- BattleScribe error detection: `BattleScribeEngine.cs` lines 560–573
