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

> Instance nodes always have these methods — no optional chaining needed.
> See [NR Dual-Tree API](../../../docs/nr-dual-tree-api.md) for full reference.

| NR accessor | SelectionState field | Notes |
|------------|---------------------|-------|
| `sel.getName()` | Name | Strict call — instances always have getName |
| `sel.getId()` | EntryId | For entryLinks: returns **target** ID |
| `sel.getType?.()` | Type | "unit", "model", "upgrade" — may not exist on all nodes |
| `sel.getAmount()` | Number | Integer count; only called on filtered instances (amount > 0) |
| `sel.isHidden?.()` | Hidden | Boolean, fallback false |
| `sel.getCosts?.()` | Costs[] | Per-unit costs, multiply by getAmount() for totals |
| `sel.getSelections()` | Children[] | All children including amount=0 templates; filtered in extractSelections |
| `sel.getModifiedProfiles?.()` | Profiles[] | Includes modifier effects |
| `sel.getModifiedRules?.()` | Rules[] | Includes modifier effects |
| `sel.getSelectionCategories?.()` | Categories[] | Via category links |
| `sel.source?.page` | Page | Number in NR, must stringify; `sel.page` is always undefined |
| `sel.source?.publication?.id` | PublicationId | Via resolved `.publication` object on source; `sel.publication` is undefined |
| `sel.source?.publication?.name` | PublicationName | Same `.publication` object on source |

## Selection sorting

NR sorts selections alphabetically within each category. Root selections are
ordered by primary category (in XML forceEntry categoryLinks order), then
alphabetically within each category. Child selections follow XML definition
order. See [NR Ordering Analysis](../../../docs/nr-ordering-analysis.md).

## Force state mapping

| NR property | ForceState field | Notes |
|------------|-----------------|-------|
| `f.uid` | Id | Strict — forces always have uid |
| `f.getName()` | Name | Strict call — forces always have getName |
| `f.catalogueId` | CatalogueId | Catalogue used for force |
| `f.isHidden?.()` | Hidden | Boolean |
| extractSelections(f) | Selections[] | Filtered by getAmount() > 0 |
| extractProfiles(f) | Profiles[] | Force-level profiles |
| extractRules(f) | Rules[] | Force-level rules |
| `f.source?.publication?.id` | PublicationId | Via resolved `.publication` object on source |
| `f.source?.page` | Page | Number in NR, must stringify |
| extractForceCategories(f) | Categories[] | Instance nodes — see below |

## Category state mapping

A FORCE's categories are instance nodes; a SELECTION's are plain tag objects. They map to the same
`CategoryState`, and only the first has a node identity.

| NR accessor | CategoryState field | Notes |
|------------|--------------------|-------|
| `c.uid` | Id | Force categories only. **Not `c.id` and not `c.getId()`** — those return the catalogue ENTRY id, and `c.source.id` is the categoryLink's. `uid` is the only node identity, and it is what NR keys its own error hashes on (`"<categoryUid>::<constraintId>"`) and writes as `id` in exported roster XML. |
| `c.source?.targetId ?? c.source?.id` | EntryId | The category ENTRY — shared by every force linking it |
| `c.getName?.()` | Name | |
| `c.isPrimary?.()` | Primary | |
| *(none)* | Id, for a selection's categories | `sel.getSelectionCategories()` returns object literals with no prototype and no uid. Null is the honest answer; the `id` key on them is a catalogue id, not a node. |

See [nr-dual-tree-api.md](../../../docs/nr-dual-tree-api.md#force-category-nodes) for the full
measurement, including that `duplicateForce` mints fresh category uids for the copy.

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
- `OwnerEntryId` — catalogue entry id of the owning roster element
- `EntryId` — entry that defines the constraint
- `ConstraintId` — specific constraint ID
- `RaisedOnType`/`RaisedOnId` — the runtime NODE the error was raised on

**The raising node is `uid`, and it comes from the node in hand.** The walk
already holds the node it read the errors off, so `RaisedOnId` is that node's
`uid` — the same value `extractForce`/`extractSelection`/`extractCategories`
report as `ForceState.Id`/`SelectionState.Id`/`CategoryState.Id`, and
`RaisedOnType` is the kind of node the walk was visiting. The roster is a node
like any other here: `army.uid` is real, while `army.getId()` returns the literal
string `"(roster)"`.

Two traps, both of which produce a plausible-looking wrong answer:

- **`getId()` is the catalogue entry, not the node** — on a category it returns
  `cat-troops`, which several nodes share. Only `uid` is node identity.
- **`error.hash` is not a second name for the raising node.** Its shape is
  `<uid>::<constraintId>`, but that uid is the node the constraint COUNTS OVER —
  the one the message names — which is the raising node only when the
  constraint's scope is `self`. Measured across the roster corpus 2026-08-13: the
  prefix named a different node on 72 of 142 errors. NR's own second reference,
  `error.parent`, IS the raising node: a bare handle carrying a `uid` and nothing
  else, agreeing with the walked node on all 142 and disagreeing on none.

The flat `army.getErrors()` merge has no node in hand and must use that handle.
Because the handle carries no methods, the KIND is resolved by looking its `uid`
up in the tree the same walk indexed. Errors that arrive only through the merge
are entry-group constraints, raised on the group node — which is real in NR
(`isGroup() === true`, its own `errors` array) but has no counterpart in the state
model, so those report `RaisedOnType: "group"` and an id no `ForceState`/
`SelectionState`/`CategoryState` carries. The 17 hidden-entry pseudo-errors are
the only ones NR gives no `error.parent` at all; they are always found by the walk
first, which knows the node directly.

(An earlier version of this file listed an `OwnerId` field here; the adapter never
populated it, and the field no longer exists — issue #421 replaced it with the
raisedOn pair, and #422 populated it on both NewRecruit lanes.)

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

## Reactive object handling

NR uses Vue 3 reactivity. Instance nodes are Vue reactive proxies, but this
does **not** affect primitive property reads (`.uid`, `.id`, method calls).
No `__v_raw` unwrapping is needed for state extraction — Vue's proxy is
transparent for the accessor patterns used by the adapter.

Vue reactivity counters (`vueNameKey`, `vueCostsKey`, `vueAmountKey`, etc.)
on instance nodes trigger re-renders when state changes. These are internal
to NR and not read by the adapter.

## Custom Name & Notes mapping

See [docs/nr-custom-name-notes.md](../../../docs/nr-custom-name-notes.md) for
the full investigation (premium paywall, UI behavior, serialization format).

| NR accessor | State field | Notes |
|------------|-------------|-------|
| `f.customName` | `ForceState.CustomName` | Own property on instance, `undefined` when not set |
| `f.note` | `ForceState.CustomNotes` | NR uses `note`, BS XML uses `customNotes` |
| `sel.customName` | `SelectionState.CustomName` | Own property on instance |
| `sel.note` | `SelectionState.CustomNotes` | NR uses `note`, BS XML uses `customNotes` |

**Adapter pattern** (in `JsHelpers.cs`):
```javascript
customName: f.customName || null,
customNotes: f.note || null       // note → customNotes
```

**Key:** `getName()` always returns the definition name, NOT the custom name.
NR UI renders custom names as "CustomName - OriginalName".
