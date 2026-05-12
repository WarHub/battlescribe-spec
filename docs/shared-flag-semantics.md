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

**Spec evidence:** `condition-shared-counting`

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

**Behavior:** When a condition has `shared=false` (the default) and `childId` references a
shared entry, the engine matches by **composite entry-link ID** rather than the base shared
ID. Since selections accessed via entry links have composite IDs (e.g.,
`link-alpha::shared-trigger`), and the `childId` is the raw shared ID (`shared-trigger`),
the condition can never match any selections. This effectively disables the condition for
shared entries accessed through entry links.

**Spec evidence:** `condition-not-shared-per-link` — adding 3+ selections of the shared
trigger via two entry links never activates the condition (stays "Target", not "Activated"),
contrasting with `condition-shared-counting` where `shared=true` activates at 2 total.

## `shared=true` on Repeats

**Behavior:** Analogous to conditions. When a repeat has `shared=true`, it counts selections
by the shared entry's original ID when calculating the repeat multiplier. Selections from
ALL entry links referencing the same shared entry contribute to the repeat count.

**Spec evidence:** `modifier-repeat-shared-counting` — a repeat with `childId` pointing to
a shared entry and `shared=true` counts selections from both entry links. Adding one trooper
via link-alpha (+10pts) then one via link-beta (total 2 troopers → +20pts) confirms
cross-link counting.

### `shared=false` on Repeats

**Behavior:** Analogous to conditions. When a repeat has `shared=false` (the default) and
`childId` references a shared entry, the engine matches by composite entry-link ID, so no
selections match the raw `childId`. The repeat multiplier stays at zero regardless of how
many selections exist via entry links.

**Spec evidence:** `modifier-repeat-not-shared-per-link` — adding troopers via two entry
links does not change the Squad's cost (stays at 50pts base), contrasting with
`modifier-repeat-shared-counting` where `shared=true` increments cost per trooper.

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
| `constraint-not-shared-per-link` | **Contrast spec:** shared=true and shared=false on same entry; per-link fires at per-link limit, shared fires at aggregate limit |
| `constraint-shared-linked` | Same-constraint-ID side effect: duplicate IDs on separate entries produce independent errors |
| `constraint-entry-link-merged` | Shared entry constraint + link constraint merge |
| `constraint-entry-link-own` | Link-only constraint (no shared entry constraint) |
| `condition-shared-counting` | Condition with shared=true counts across entry links |
| `condition-not-shared-per-link` | Condition with shared=false: childId can't match composite IDs, condition never fires |
| `modifier-repeat-shared-counting` | Repeat with shared=true counts across entry links |
| `modifier-repeat-not-shared-per-link` | Repeat with shared=false: childId can't match composite IDs, multiplier stays zero |

## Changes from Review

1. **Removed `constraint-shared`** — tested shared=true on separate non-shared entries with
   different constraint IDs, which is a no-op (no cross-entry counting). Redundant.
2. **Removed `constraint-shared-deduplication`** — single entry with min=2 auto-satisfied,
   no error-proving, no deduplication scenario. Redundant.
3. **Redesigned `constraint-not-shared-per-link`** — now contrasts shared=true vs shared=false
   on the same shared entry with error-proving steps for both constraints.
4. **Added `modifier-repeat-shared-counting`** — proves shared=true on repeats counts across
   entry links (previously unverified claim).
5. **Documented same-constraint-ID side effect** — explicit in `constraint-shared-linked`
   description and this document.
6. **Fixed adapter constraintId attribution bug** — errorIdMap in BattleScribeEngine was
   overwriting entries for the same shared entry, causing all errors to be attributed to the
   shared=true constraint's ID. Fixed with multimap and value-matching resolution.
7. **Added `condition-not-shared-per-link`** — proves shared=false on conditions effectively
   disables cross-link childId matching.
8. **Added `modifier-repeat-not-shared-per-link`** — proves shared=false on repeats
   effectively disables cross-link childId matching.
