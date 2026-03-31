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
| *(not available)* | Page | Always null |
| source entry definition | PublicationId | From entry definition |

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

## Validation errors

NR validation errors are derived by running constraint checks and then walking
the roster hierarchy:

- Invoke `checkConstraints()` on the current army/roster to populate
  constraint state.
- Traverse `roster → forces → categories → selections`, calling
  `checkConstraints()` on each element recursively (including child selections).
- Collect constraint violations attached to each element after checking.
- Inject additional cost-limit validation during this traversal (e.g. total
  roster or force cost exceeding configured limits).

Each detected violation is mapped to `ValidationErrorState` with:
- `Message` — error text
- `OwnerType` — roster/force/category/selection
- `OwnerId` — ID of the owning roster element
- `EntryId` — entry that defines the constraint
- `ConstraintId` — specific constraint ID

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
