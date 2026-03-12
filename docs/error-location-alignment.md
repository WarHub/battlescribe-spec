# Error Location Alignment: BS Oracle vs NR

BattleScribe's Java engine distributes validation errors across the roster tree
based on the constraint's scope (roster/force/category/selection). NewRecruit
consistently attributes errors to the selection that violates the constraint.
This document catalogues the discrepancies and the adapter strategy used to
align the BS oracle's output with NR.

## NR Error Override Inventory

Out of all specs with `engines.newrecruit.errors` overrides, the discrepancies
fall into three categories.

### Category 1: Location Remap (addressable by adapter)

BS has the error but reports it at a higher tree level (roster or force)
than NR (selection). An adapter in the oracle can remap these.

| Spec | BS (top-level) | NR (override) | Notes |
|------|---------------|---------------|-------|
| `constraint-entry-link-shared-counting` | `on: roster` | `on: selection shared-unit` | Shared constraint, roster→selection |
| `constraint-entry-link-shared-target` | `on: roster` | `on: selection shared-unit` | Shared constraint, roster→selection |
| `constraint-entry-link-own` | `on: force fe-1` | `on: selection shared-unit` | Entry link, force→selection (resolve link target) |
| `constraint-entry-link-merged` | `on: force fe-1` | `on: selection shared-unit` | Entry link, force→selection; also different `from` |

### Category 2: Missing Errors (not addressable)

BS doesn't report these errors at all — fundamental engine behavioral
differences. No adapter can create errors from nothing.

| Spec | NR Error | Why BS doesn't report |
|------|----------|----------------------|
| `constraint-max-violation` | `on: selection se-unit-a` | BS doesn't error in this scenario |
| `constraint-percent-value` | `on: selection se-1` | BS handles percent constraints differently |
| `scope-parent` | `on: selection se-unit-a` | BS scope resolution differs |
| `modifier-field-hidden` | `on: selection se-1` (hidden) | BS doesn't report hidden as constraint violation |
| `modifier-set-boolean` | `on: selection se-1` (hidden) | BS doesn't report hidden as constraint violation |
| `selection-hidden-entry` | `on: selection se-1` (hidden) | BS doesn't report hidden as constraint violation |
| `hidden-cascade-to-children` | `on: selection se-squad` (hidden) | BS doesn't report hidden as constraint violation |

### Category 3: Count/Location Differences (partially addressable)

| Spec | BS | NR | Notes |
|------|----|----|-------|
| `constraint-min-on-force-linked` | 1 error on force | 2 errors: force + selection | NR duplicates on both levels |
| `constraint-shared-linked` | 2 errors on category | 1 error on category | NR deduplicates shared min |

## Adapter Strategy

The BS oracle already has `RemapCategoryErrorsToSelection()` which moves
category-level max/hidden constraint errors to selection-level, matching NR.
The same pattern extends to roster and force levels.

### Roster → Selection Remap

For roster-level errors with a known `entryId` (not cost limits), remap to
`on: selection {entryId}`. The entryId from the error ID map
(`ownerId::entryId::constraintId`) directly identifies the selection.

Handles: `constraint-entry-link-shared-counting`, `constraint-entry-link-shared-target`

### Force → Selection Remap

For force-level errors where the `entryId` is an entry link, resolve the
link's target entry and remap to `on: selection {targetId}`.

Handles: `constraint-entry-link-own`, `constraint-entry-link-merged` (location only)

### What This Doesn't Fix

- Category 2 specs (7 overrides): fundamental engine differences, not location issues
- Category 3 specs (2 overrides): error count differences, not just location
- `constraint-entry-link-merged` `from` difference: NR uses link constraint,
  BS uses shared constraint (merged constraint attribution differs)
