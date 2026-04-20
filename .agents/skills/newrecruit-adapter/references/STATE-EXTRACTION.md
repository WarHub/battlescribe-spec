# NR State Extraction Reference

## Overview

`NewRecruitStateReader.cs` extracts roster state from the NR Vue app by evaluating
JavaScript expressions against Pinia stores and Vue reactive objects.

## State extraction flow

```
Page.EvaluateAsync<T>(javascript)
    ↓
Read Pinia stores (systemsStore, lists)
    ↓
Walk roster → forces → selections → profiles/rules/categories/costs
    ↓
Map to RosterState / ForceState / SelectionState / etc.
```

## Pinia store access pattern

All reads go through `window.__bsspec` (saved during setup) and Pinia stores:

```javascript
const bsspec = window.__bsspec;
const army = bsspec.army;
const book = bsspec.book;
const row = bsspec.row;
```

## Selection state mapping

| NR accessor | SelectionState field | Notes |
|------------|---------------------|-------|
| `sel.getName?.()` | Name | Optional chaining throughout |
| `sel.getId?.()` | EntryId | May include link prefix |
| `sel.getType?.()` | Type | "unit", "model", "upgrade" |
| `sel.getAmount?.()` | Number | Integer count, fallback 1 |
| `sel.isHidden?.()` | Hidden | Boolean, fallback false |
| `sel.getCosts?.()` | Costs[] | Mapped via costIndex |
| `sel.getSelections?.()` | Children[] | Recursive |
| `sel.getModifiedProfiles?.()` | Profiles[] | Includes modifier effects |
| `sel.getModifiedRules?.()` | Rules[] | Includes modifier effects |
| `sel.getSelectionCategories?.()` | Categories[] | Via category links |
| `sel.source?.page` | Page | Number in NR, must stringify; `sel.page` is always undefined |
| `sel.source?.publication?.id` | PublicationId | Via resolved `.publication` object on source; `sel.publication` is undefined |
| `sel.source?.publication?.name` | PublicationName | Same `.publication` object on source |

## Selection sorting

Selections are sorted by:

1. **`__bsspec_seq`** — insertion sequence number (set by SelectEntryByIdAsync)
2. **Catalogue entry order** — `window.__bsspec.entryOrder` index as tiebreaker

This replicates BattleScribe's insertion-order display, since NR internally sorts
alphabetically.

## Force state mapping

| NR property | ForceState field | Notes |
|------------|-----------------|-------|
| `force.name` | Name | From force entry |
| `force.catalogueId` | CatalogueId | Catalogue used for force |
| `force.selections` | Selections[] | Sorted as above |
| `force.availableEntries` | AvailableEntryCount | Count of selectable entries |
| `force.profiles` | Profiles[] | Force-level profiles |
| `force.rules` | Rules[] | Force-level rules |
| `f.source?.publication?.id` | PublicationId | Via resolved `.publication` object on source |
| `f.source?.publication?.name` | PublicationName | Same `.publication` object on source |
| `f.source?.page` | Page | Number in NR, must stringify |

## Cost state mapping

```javascript
const costs = [];
for (const [typeId, ct] of Object.entries(catalogue.costIndex || {})) {
    costs.push({
        name: ct.name,
        typeId: typeId,
        value: calculateTotalCost(roster, typeId)
    });
}
```

Total costs are calculated by summing selection costs recursively.

**Important**: NR's `army.calcTotalCosts()` omits hidden cost types. The adapter
always uses manual summation from individual selections' `getCosts()` to include
all cost types (visible and hidden) in roster-level totals.

## Validation errors

NR validation errors are derived by running constraint checks and then walking
the roster hierarchy:

- Invoke `checkConstraints()` on the current army/roster to populate
  constraint state.
- Cost limit errors are handled natively by NR after applying `defaultCostLimit`
  via `setMaxCosts` at roster creation (see NR-INTERNALS.md for details).
- Traverse `roster → forces → categories → selections`, calling
  `checkConstraints()` on each element recursively (including child selections).
- Collect constraint violations attached to each element after checking.
- Roster-level cost limit errors (with `constraint.type === 'max'` and
  `constraint.field`) are mapped to `costLimits/{field}` entries.

Each detected violation is mapped to `ValidationErrorState` with:
- `Message` — error text
- `OwnerType` — roster/force/category/selection
- `OwnerId` — ID of the owning roster element
- `EntryId` — entry that defines the constraint
- `ConstraintId` — specific constraint ID

## Publication ID resolution

NR resolves `publicationId` XML attributes into actual publication objects at
parse time. Every entry type (selections, rules, profiles, categories, forces)
stores a `.publication` object reference instead of a raw `publicationId` string.

**Pattern:** `obj.publication?.id` — not `obj.publicationId`

```javascript
// ✅ Correct — NR resolves the reference into an object
rule.publication?.id    // → "pub-core"
rule.publication?.name  // → "Core Rulebook"

// ❌ Wrong — NR does NOT keep the raw string
rule.publicationId      // → undefined
```

This applies uniformly to all entry types:
- `selection.publication?.id` (via source: `src.publication?.id`)
- `rule.publication?.id`
- `profile.publication?.id`
- `category.publication?.id`
- `force.publication?.id`

The `.publication` object has these properties:
- `id` — publication ID string (matches XML `publicationId`)
- `name` — full name (e.g. "Core Rulebook")
- `shortName` — abbreviated name (e.g. "CR")
- `catalogue` — back-reference to owning catalogue (circular)

For `publicationName`, read it directly from the same `.publication` object:

```javascript
const pub = sel.publication || src?.publication;
const pubId = pub?.id || null;
const pubName = pub?.name || null;
```

## Reactive object unwrapping

NR uses Vue 3 reactivity. Raw objects are accessed via `__v_raw` to avoid
reactive proxy overhead and ensure consistent property access:

```javascript
const raw = selection?.__v_raw || selection;
```

This is especially important for:
- Setting `__bsspec_seq` tags (must be on raw object)
- Reading `uid` for identity tracking
- Avoiding Vue reactivity tracking during bulk reads
