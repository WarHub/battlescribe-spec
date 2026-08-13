# NR Dual-Tree Architecture & API Reference

> Behaviors verified April 2026 via live Playwright probing of [newrecruit.eu](https://newrecruit.eu).
> See also: [NR Behavioral Differences](nr-behavioral-differences.md),
> [NR Ordering Analysis](nr-ordering-analysis.md),
> [NR Store Mapping](nr-store-mapping.md).

## Overview

NewRecruit's roster tree is a **dual-tree** structure. Every catalogue entry
exists as both a **selector** (static template from catalogue data) and zero or
more **instances** (dynamic materialized selections). The two node types have
completely **disjoint** method sets — code must always know which type it holds.

The tree **strictly alternates** between instances and selectors:
- **Instance** nodes own `selectors[]` (child selector nodes)
- **Selector** nodes own `instances[]` (child instance nodes)
- Selector nodes **never** have a `selectors` property
- Instance nodes **never** have an `instances` property

```
Force
 └─ selectors[]                          ← category selectors
     ├─ (No Category)  [selector node]
     │   └─ instances[]                  ← category instance (1 per category)
     │       └─ selectors[]              ← entry selectors
     │           ├─ se-unit-a  [selector]
     │           │   └─ instances[]      ← entry instances (0 = unselected)
     │           │       └─ [instance]   ← materialized selection
     │           │           ├─ selectors[]    ← child entry selectors
     │           │           │   └─ se-child  [selector]
     │           │           │       └─ instances[]
     │           │           └─ getSelections() → all child instances
     │           └─ se-unit-b  [selector]
     │               └─ instances[]
     └─ cat-troops  [selector node]
         └─ instances[]
             └─ selectors[]
                 └─ se-categorized  [selector]
                     └─ instances[]
```

To search for a nested selector by ID, traverse the alternating tree:

```javascript
function findSelectorDeep(selectors, id) {
    for (const s of selectors) {
        if (s.id === id || s.ids?.includes(id)) return s;
        // Selectors have instances, instances have selectors
        for (const inst of (s.instances || [])) {
            const found = findSelectorDeep(inst.selectors || [], id);
            if (found) return found;
        }
    }
    return null;
}
// Always start from the instance's selectors — never from selector.selectors
findSelectorDeep(instance.selectors, targetId);
```

## Node Types

### Selector Nodes

Selectors are the static template layer. Each represents a catalogue entry
definition (selectionEntry, selectionEntryGroup, categoryLink, etc.).

**Own properties:**
`root`, `parent`, `instances`, `source`, `extra_instances`, `isInstanced`,
`isUnit`, `isSubUnit`, `isQuantifiable`, `isLimitedTo1`, `id`, `ids`, `uid`,
`hidden`, `book`, `booksDate`, `initializedHeaders`, `notReactive`

**Methods (23 on prototype):**

| Method | Description |
|--------|-------------|
| `addInstance()` | Create a new instance under this selector |
| `delete()` | Remove the selector |
| `getSelections()` | Iterate child instances |
| `getName()` | Entry name |
| `getId()` | Entry ID |

**Does NOT have:** `incrementAmount`, `setAmount`, `getAmount`, `autocheck`,
`dupe`, `getSelectedEntries`

**Key properties:**
- `id` — single entry ID (for entryLinks: the **link** ID)
- `ids` — array of IDs (for entryLinks: contains **both** link and target IDs)
- `instances` — array of materialized instances under this selector

### Instance Nodes

Instances are the dynamic materialized layer. Each represents an actual
selection in the roster with a count, costs, modifiers applied, etc.

**Own properties:**
`selector`, `selectors`, `source`, `propagateChanges`, `uid`, `isDeleted`,
`isDanglingUnit`, `isDetached`, `catalogueId`, `catalogueName`,
`catalogueRevision`, `booksDate`, `associations`, `incomingAssociations`,
`errors`, `allErrors`, `state`, `vueNameKey`, `vueReadMeKey`, `vueHiddenKey`,
`vueInfoKey`, `vueConstraintsKey`, `vueCategoriesKey`, `vueCostsKey`,
`vueAmountKey`, `vueErrorsKey`, `errorsValid`, `recursiveErrorsValid`,
`childsInitialized`, `switchingParent`, `availableForces`, `customName`, `note`

**Methods (~200+ on prototype, key ones):**

| Method | Description |
|--------|-------------|
| `incrementAmount()` | Bump count by 1 (activates amount=0 templates) |
| `setAmount(ctx, n)` | Set exact count. **Two args required** — `setAmount({}, 5)` |
| `getAmount()` | Current count (0 = unselected template) |
| `delete()` | Remove this instance |
| `getSelections()` | All child instances (including amount=0 templates) |
| `getSelectedEntries()` | Only children with amount > 0 |
| `getName()` | Selection name (with modifiers applied) |
| `getId()` | Entry ID (for entryLinks: returns **target** ID) |
| `autocheck()` | Cascade auto-selection for min constraints |
| `dupe()` | Async — duplicate this selection |
| `isHidden()` | Whether hidden by modifiers |
| `getType()` | "unit", "model", "upgrade" |
| `getCosts()` | Per-unit cost array |
| `getModifiedProfiles()` | Profiles with modifiers applied |
| `getModifiedRules()` | Rules with modifiers applied |
| `getSelectionCategories()` | Category links |
| `checkConstraints()` | Run constraint validation |
| `getCustomName()` | Returns customName when set (premium feature) |

**Does NOT have:** `addInstance`

**Custom Name & Notes** (own properties, premium feature):
- `customName` — custom display name, default `undefined`. UI renders as "CustomName - OriginalName"
- `note` — custom notes text, default `undefined`. Visible in expanded selection detail panel
- `getName()` always returns the **definition** name, NOT the custom name
- See [nr-custom-name-notes.md](nr-custom-name-notes.md) for full investigation

### Force Category Nodes

`force.getCategories()` returns **instance nodes** — the same prototype as forces and selections,
with `isCategory() === true`. So a force category has a durable `uid`, exactly like a selection
does, and that uid is its node identity.

**Do not read `id` or `getId()` on one.** Both return the **catalogue entry** id (`cat-troops`),
and `source.id` is the categoryLink's id while `source.targetId` is the entry's. Each of those is a
real value that identifies a definition, not a node, so using one where a node id belongs produces
something that looks right and is wrong. Two links to the same category entry give one force two
category nodes that agree on all three.

Corroboration, measured 2026-08-13:

- NR keys its own validation-error identity on the uid — every error's `hash` is
  `"<categoryUid>::<constraintId>"`.
- NR's exported roster XML writes it as the `id` attribute:
  `<category name="Troops" id="ig1q6t7" primary="false" entryId="cat-troops"/>` — the same
  id/entryId split BattleScribe uses.
- Stable across repeated reads (same object reference), across `deselectSelection` and
  `selectEntry`. `duplicateForce` mints **fresh** category uids for the copy, which is correct: the
  copy is a different force and owns different nodes.

`selection.getSelectionCategories()` is a different thing despite the similar name. It returns
plain object literals — keys `["id", "entryId", "name", "primary"]`, no prototype, no methods, no
uid — because a selection's categories are the tags it counts against, not nodes in the tree. Its
`id` key is a catalogue id and is **not** a node identity.

## addInstance() Category Relocation

When `addInstance()` is called on a selector, NR creates the instance under the
**correct category selector** based on the entry's `categoryLinks`. This may be a
**different** selector than the one `findSelectorById` found.

### Without categoryLinks

The entry lives under `(No Category)`. After `addInstance()`, the instance
appears in the **same** selector's `instances` array.

```
Before:  (No Category) → se-plain [instances: 0]
After:   (No Category) → se-plain [instances: 1]  ← same node
```

### With categoryLinks

The entry selector initially lives under `(Illegal Units)` (NR's staging
category for uncategorized-yet entries). After `addInstance()`, the instance
appears under the **target category** selector — `selector.instances` on the
original node stays empty.

```
Before:  (Illegal Units) → se-marine [instances: 0]
         cat-troops       (does not exist yet)

After:   (Illegal Units) → se-marine [instances: 0]  ← unchanged!
         cat-troops → se-marine [instances: 1]        ← new location
```

### Adapter implication

**Never check `selector.instances` after `addInstance()`** to find the new
instance. Use a before/after uid diff on `getSelections(force)`:

```javascript
const before = new Set(getSelections(force).map(s => s.uid));
selector.addInstance();
const after = getSelections(force);
const newSel = after.find(s => s.uid && !before.has(s.uid));
if (!newSel) throw new Error('addInstance did not produce a new selection');
newSel.autocheck();
return newSel.uid;
```

## Method Usage by Action

| Action | Node type | Correct method | Why |
|--------|-----------|----------------|-----|
| Select root entry | Selector | `addInstance()` | Creates new instance |
| Activate child entry (amount 0→1) | Instance | `incrementAmount()` | Bumps existing template |
| Change selection count | Instance | `setAmount({}, n)` | Exact count, triggers cost propagation |
| Deselect / remove | Instance | `delete()` | Removes instance entirely |
| Duplicate selection | Instance | `dupe()` | Async clone |

### Strict guards

Every action should verify the method exists before calling — this catches
accidental node type confusion:

```javascript
// On selectors
if (typeof selector.addInstance !== 'function')
    return 'ERROR: not a selector node';

// On instances
if (typeof instance.incrementAmount !== 'function')
    return 'ERROR: not an instance node';
```

## `setAmount()` Signature

**Two arguments required:** `setAmount(ctx, count)` where `ctx` is a tracker
context object (pass `{}`).

```javascript
sel.setAmount({}, 5);    // ✅ Correct
sel.setAmount(5);        // ❌ Corrupts: sets ctx=5, n=undefined
```

One-arg `setAmount(0)` was previously used as a deselection fallback — this is
**wrong** and causes silent state corruption. Always use `delete()` for
deselection.

## getSelections() vs getSelectedEntries()

Both are instance methods that return child instances:

| Method | Returns | Use case |
|--------|---------|----------|
| `getSelections()` | **All** children including amount=0 templates | Walking the full tree, finding entries to activate |
| `getSelectedEntries()` | Only children with `amount > 0` | State extraction (active selections only) |

The state reader filters `getSelections()` with `getAmount() > 0` rather than
using `getSelectedEntries()` for consistency and to maintain control over the
filter logic.

## ID Resolution for EntryLinks

EntryLinks have dual IDs. The three access paths return different values:

| Accessor | Returns | Example |
|----------|---------|---------|
| `selector.id` | **Link** ID | `el-weapon` |
| `selector.ids` | **Both** IDs | `["el-weapon", "se-shared-weapon"]` |
| `instance.getId()` | **Target** ID | `se-shared-weapon` |
| `instance.source.id` | **Link** ID | `el-weapon` |

When searching for an entry by ID, check all three:
```javascript
children.find(c =>
    c.getId() === entryId
    || c.source?.id === entryId
    || c.selector?.ids?.includes?.(entryId));
```
