# `shared` Flag Semantics

The `shared` boolean flag exists on three protocol types: **constraints**, **conditions**,
and **repeats**. It controls how the engine identifies selections when counting them for
validation or evaluation — specifically, whether to count by the **shared entry's original
ID** or by the **entry-link-specific composite ID**.

## Context: Entry Links and Shared Entries

In BattleScribe, a `sharedSelectionEntry` (or `sharedSelectionEntryGroup`) is a reusable
definition that lives in the catalogue's shared section. It becomes available in a roster
through **entry links** — references that expand the shared entry into a selectable option.

During catalogue expansion, each entry link creates a composite ID:
`{linkId}::{sharedEntryId}` (e.g., `link-alpha::shared-unit`). This means selections made
through different entry links have **different composite IDs** even though they reference the
same underlying shared entry.

## `shared=true` on Constraints

**Behavior:** When a constraint has `shared=true`, the engine counts selections by the
shared entry's **original ID** rather than the composite entry link ID. This means all
selections from any entry link referencing the same shared entry are counted together toward
a single limit.

**Without `shared=true`:** Each entry link's constraint counts only its own selections
independently. The max/min limit applies per-link, not globally. On conditions and repeats,
`shared=false` means `childId` is matched by composite entry-link ID, so a `childId`
pointing to a shared entry won't match any selections accessed via entry links — effectively
disabling the condition/repeat for cross-link scenarios.

### Example

```yaml
sharedSelectionEntries:
  - id: shared-unit
    constraints:
      - type: max
        value: 3
        scope: roster
        shared: true    # counts ALL selections of shared-unit across all links
entryLinks:
  - id: link-alpha
    targetId: shared-unit
  - id: link-beta
    targetId: shared-unit
```

With `shared=true`: selecting 2 via link-alpha and 1 via link-beta = **3 total** (at limit).
Without `shared=true`: each link would independently track its own count.

### Error Attribution

When a shared constraint fires, the error references the **original shared entry ID** (not
the composite link ID):
- Error `on: selection shared-unit` with `from: shared-unit/constraint-id`

Each constraint error correctly reports its own constraint ID, even when both `shared=true`
and `shared=false` constraints exist on the same shared entry. The constraint value in the
error message (e.g., "maximum 2" vs "maximum 3") distinguishes them.

### Scope Interaction

The `scope` field (parent, force, roster) determines **where** to look for selections.
The `shared` flag determines **how** to identify/group them when counting.

## `shared=true` on Conditions

**Behavior:** When a condition has `shared=true` and references a `childId`, the engine
counts selections by the shared entry's original ID rather than the composite link ID.

This matters when a condition's `childId` points to a shared entry referenced by multiple
entry links. With `shared=true`, the condition counts selections from ALL entry links that
reference that shared entry, not just one specific link.

**Spec evidence:** `condition-shared-flag`

### Example

```yaml
sharedSelectionEntries:
  - id: shared-trigger
    name: Trigger Unit
entryLinks:
  - id: link-1
    targetId: shared-trigger
  - id: link-2
    targetId: shared-trigger

selectionEntries:
  - id: se-target
    modifiers:
      - type: set
        field: name
        value: Activated
        conditions:
          - type: atLeast
            value: 2
            field: selections
            scope: force
            childId: shared-trigger
            shared: true    # counts selections from BOTH link-1 and link-2
```

With `shared=true`: 1 selection via link-1 + 1 via link-2 = 2 total → condition met.
Without `shared=true`: each link counted separately → condition would need 2 from a single link.

### `shared=false` on Conditions

**BattleScribe behavior:** When a condition has `shared=false` (the default) and `childId`
references a shared entry, the engine matches by **composite entry-link ID** rather than the
base shared ID. Since selections accessed via entry links have composite IDs (e.g.,
`link-alpha::shared-trigger`), and the `childId` is the raw shared ID (`shared-trigger`),
the condition can never match any selections. This effectively disables the condition for
shared entries accessed through entry links.

**NewRecruit behavior (engine limitation):** NR ignores `shared=false` on conditions — they
fire as if `shared=true`. See [NewRecruit Engine Limitation](#newrecruit-engine-limitation)
below for the detailed root cause.

**Spec evidence:** `condition-shared-flag` — adding 1 Alpha Trigger + 1 Beta Trigger
(total=2 across links) activates `Target Shared` (shared=true) in both engines, but only
activates `Target Not Shared` (shared=false) in NewRecruit. BattleScribe correctly leaves it
inactive.

## `shared=true` on Repeats

**Behavior:** Analogous to conditions. When a repeat has `shared=true`, it counts selections
by the shared entry's original ID when calculating the repeat multiplier. Selections from
ALL entry links referencing the same shared entry contribute to the repeat count.

**Spec evidence:** `modifier-repeat-shared-flag`

### `shared=false` on Repeats

**BattleScribe behavior:** Analogous to conditions. When a repeat has `shared=false` (the
default) and `childId` references a shared entry, the engine matches by composite entry-link
ID, so no selections match the raw `childId`. The repeat multiplier stays at zero regardless
of how many selections exist via entry links.

**NewRecruit behavior (engine limitation):** NR ignores `shared=false` on repeats — they
fire as if `shared=true`. Same root cause as conditions (see
[NewRecruit Engine Limitation](#newrecruit-engine-limitation)).

**Spec evidence:** `modifier-repeat-shared-flag` — adding troopers via two entry links does
not change the Squad's cost in BattleScribe (stays at 50pts base), but NR increments cost
per trooper regardless of the shared flag.

## Same-Constraint-ID Side Effect

**Important:** Using the same constraint ID on multiple separate (non-shared) entries with
`shared=true` does NOT cause cross-entry counting. Each entry's constraint still counts its
own selections independently. The same constraint ID merely affects error reporting structure.

This is a **side effect of ID reuse**, not a feature of the `shared` flag. When two entries
have matching constraint IDs and `shared=true`:
- Each entry independently evaluates its own constraint
- When violated, each produces its own error
- Errors are NOT deduplicated or merged

**Spec evidence:** `constraint-shared-linked` — demonstrates that deselecting both entries
produces two separate errors, one per entry, despite matching constraint IDs.

**Recommendation:** Avoid duplicate constraint IDs in specs unless explicitly testing this
side effect. Using unique IDs makes spec behavior unambiguous.

## Existing Spec Coverage

| Spec ID | What it Tests |
|---------|---------------|
| `constraint-entry-link-shared-counting` | **Key spec:** Two links → same shared entry, shared max counted across both, error fires at aggregate limit |
| `constraint-entry-link-shared-target` | Single link → shared entry with shared constraint, error fires at limit |
| `constraint-shared-flag` | **Contrast spec:** shared=true and shared=false on same entry; per-link fires at per-link limit, shared fires at aggregate limit |
| `constraint-shared-linked` | Same-constraint-ID side effect: duplicate IDs on separate entries produce independent errors |
| `constraint-entry-link-merged` | Shared entry constraint + link constraint merge |
| `constraint-entry-link-own` | Link-only constraint (no shared entry constraint) |
| `condition-shared-flag` | Unified: shared=true counts across links; shared=false never fires in BS (NR diverges) |
| `modifier-repeat-shared-flag` | Unified: shared=true counts across links; shared=false multiplier stays zero in BS (NR diverges) |

## NewRecruit Engine Limitation

NewRecruit correctly implements `shared=false` for **constraints** but ignores it for
**conditions** and **repeats**. Both behaviors have been confirmed by:
1. Live browser testing (headed Playwright session against `newrecruit.eu`)
2. Frozen HAR testing (NR v34.55, bundle `BA2pibXD.js`)
3. Direct JS source analysis

### Root Cause: `hash()` function and missing `childId` adaptation

NR's reactive counting system keys listeners via a `hash()` function:

```js
hash(e) {
  return `${this.prefix(e.includeChildSelections, e.includeChildForces)}::${e.field}::${e.childId || ...}`
}
```

The hash does **NOT** include `shared`. This means conditions/repeats with `shared=true` and
`shared=false` but the same `field`/`childId` share the identical listener bucket — they
receive the same cross-link count.

For **constraints**, the setup code explicitly adapts `childId` when `shared===false`:

```js
// In the constraint setup loop:
this.listen(t, {
  ...t.source,
  childId: t.source.shared===!1 && !this.source.isCategory()
    ? this.source.id        // per-link composite ID → unique hash key
    : this.source.getId()   // shared base ID → cross-link hash key
})
```

When `shared=false`, the constraint's `childId` is replaced with `this.source.id` (the
per-link composite ID like `link-alpha::shared-unit`). This creates a **distinct hash key**
for each link, so each constraint counts only its own link's selections. This is correct ✓

For **conditions and repeats**, no equivalent `childId` adaptation exists. The `childId`
stays as the raw shared entry ID (`shared-trigger`). Both shared=true and shared=false
conditions hash to the **same key**, receive the same cross-link count, and always fire as if
`shared=true`.

### The `find()` asymmetry

The `find(scope, shared)` method also has a `shared=false` branch:

```js
find(e, t, n) {
  if (t === !1) switch(e) {
    case "force": return this.source.isGroup() ? this : this.parent;  // restricted
    // ...
  }
  switch(e) {
    case "force": return this.findParentOrSelf(i => i.source.isForce());  // full traversal
    // ...
  }
}
```

When `shared=false`, `find("force")` returns `this.parent` (immediate parent) instead of
traversing up to the force node. This restriction is only meaningful for selections **nested
inside a group** — for direct force children (the common case for shared entry links), the
immediate parent IS the force scope, so the result is identical.

### Net effect

- `shared=false` on **constraints**: ✅ works correctly via explicit `childId` adaptation
- `shared=false` on **conditions**: ❌ ignored — same hash as `shared=true`, same count
- `shared=false` on **repeats**: ❌ ignored — same hash as `shared=true`, same count

This is an NR engine design gap, not an adapter bug. The spec test suite documents this with
per-engine `expectedState` overrides in `condition-shared-flag.yaml` and
`modifier-repeat-shared-flag.yaml`.

## Changes from Review

1. **Removed `constraint-shared`** — tested shared=true on separate non-shared entries with
   different constraint IDs, which is a no-op (no cross-entry counting). Redundant.
2. **Removed `constraint-shared-deduplication`** — single entry with min=2 auto-satisfied,
   no error-proving, no deduplication scenario. Redundant.
3. **Redesigned `constraint-not-shared-per-link` → `constraint-shared-flag`** — now
   contrasts shared=true vs shared=false on the same shared entry with error-proving steps
   for both constraints.
4. **Merged condition specs → `condition-shared-flag`** — unified shared=true and shared=false
   conditions in one spec with per-engine expectedState overrides for NR divergence.
5. **Merged modifier-repeat specs → `modifier-repeat-shared-flag`** — same approach.
6. **Documented same-constraint-ID side effect** — explicit in `constraint-shared-linked`
   description and this document.
7. **Fixed adapter constraintId attribution bug** — errorIdMap in BattleScribeEngine was
   overwriting entries for the same shared entry, causing all errors to be attributed to the
   shared=true constraint's ID. Fixed with multimap and value-matching resolution.
8. **Documented NR engine limitation** — `shared=false` on conditions/repeats is silently
   ignored by NR due to missing `childId` adaptation in the condition/repeat setup path
   (unlike constraints which have explicit per-link `childId` substitution). Documented with
   per-engine expectedState overrides rather than skip flags.
