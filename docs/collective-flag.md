# Collective Flag — Deep Analysis

This document describes how the BattleScribe engine handles entries marked with
`collective="true"`. The analysis is based on the decompiled Java engine source
(`net.battlescribe.engine.a.f`, `d`, `c`, and `net.battlescribe.engine.b.h`).

## Overview

The `collective` flag on a `SelectionEntry` fundamentally changes how the entry
behaves within the roster tree. Instead of each instance being an independent
selection node, collective entries act as a **shared pool across parent
instances** — their count, cost, and constraints are evaluated **per model**
(i.e., divided by the parent selection's number).

**Key principle**: A collective entry's `number` field represents the total count
across all parent instances. Operations (select, deselect, set count) scale by
the parent's number, and validation divides by it.

## Terminology

| Term | Meaning |
|------|---------|
| **Collective entry** | A `SelectionEntry` with `collective="true"` |
| **Parent selection** | The `Selection` node containing the collective child |
| **Root selection** | A selection whose parent is a `Force` (not a `Selection`) |
| **Per-model count** | The effective count per parent instance: `child.number / parent.number` |
| **d2.f(entry)** | "isDuplicate" check — determines if the entry gets new selection nodes vs. number increments |

## When Collective Behavior Activates

Collective replication logic triggers only when **all three conditions** are met:

```java
// f.java:1010
if (selectionEntry.isCollective()
    && baseSelectionParent instanceof Selection
    && !this.i((Selection)baseSelectionParent))
```

1. The entry has `collective="true"`
2. The parent is a `Selection` (not a `Force`)
3. The parent is **not** a root selection (its parent is not a `Force`)

If the parent IS a root selection (i.e., directly under a Force), collective
entries behave identically to non-collective entries. This means the interesting
collective behaviors only manifest in **nested structures** (e.g.,
Force → Unit → Model → Weapon).

### isRootSelection (c.java:1643-1645)

```java
public boolean i(Selection selection) {
    this.a("isRootSelection", selection);
    return selection.getParent() instanceof Force;
}
```

## Selection (Selecting a Collective Entry)

When selecting a collective entry under a non-root parent, the engine iterates
over all **sibling selections** of the parent entry and creates one child per
parent instance:

```java
// f.java:1000-1027 — method c(BaseSelectionParent, SelectionEntry, int n)
for (int i = 0; i < n; ++i) {
    if (selectionEntry.isCollective()
        && baseSelectionParent instanceof Selection
        && !this.i((Selection)baseSelectionParent)) {
        // Collective: replicate across all sibling instances × their number
        for (Selection selection : this.a(d2, (Selection)baseSelectionParent)) {
            for (int j = 0; j < selection.getNumber(); ++j) {
                Selection child = this.b(d2, (BaseSelectionParent)selection, selectionEntry);
                if (child != null) arrayList.add(child);
            }
        }
    } else {
        // Non-collective: create one instance
        Selection child = this.b(d2, baseSelectionParent, selectionEntry);
        if (child != null) arrayList.add(child);
    }
}
```

**Effect**: If the parent has `number=3`, selecting a collective child creates 3
instances (one per parent model), resulting in `child.number = 3`.

### Number Inheritance on Creation (f.java:1308-1314)

When a new selection node is created, if the parent is a Selection and the entry
is collective and not a "duplicate" entry, the child inherits the parent's number:

```java
// f.java:1311-1313
if (baseSelectionParent instanceof Selection
    && selectionEntry.isCollective()
    && !d2.f(selectionEntry)) {
    selection.setNumber(((Selection)baseSelectionParent).getNumber());
}
```

This means a collective child always starts with `number = parent.number`.

### Increment vs. New Node (f.java:1029-1053)

The `b(d2, parent, entry)` method determines whether to increment an existing
selection's number or create a new selection node:

```java
if (baseSelectionParent instanceof Force || d2.f(selectionEntry)) {
    // Root selections OR "duplicate" entries: always create new node
    selection = this.a(d2, baseSelectionParent, selectionEntry);
} else {
    // Non-root: find existing selection and increment its number
    List<Selection> existing = this.c(d2, parent, entry, ...);
    if (existing.isEmpty()) {
        selection = this.a(d2, baseSelectionParent, selectionEntry);
    } else {
        selection = existing.get(0);
        selection.setNumber(selection.getNumber() + 1);
    }
}
```

For collective entries that are not "duplicate" entries (`d2.f()` returns false),
subsequent selections increment the existing node's number rather than creating
new siblings.

## Deselection

Deselecting a collective entry mirrors the selection logic — it removes one
instance per parent model:

```java
// f.java:1218-1246 — method a(Selection, int n)
for (int i = 0; i < n; ++i) {
    if (selectionEntry.isCollective()
        && !d2.f(selectionEntry)
        && baseSelectionParent instanceof Selection) {
        // Collective: remove from all sibling instances × their number
        for (Selection sibling : this.a(d2, (Selection)baseSelectionParent)) {
            for (int j = 0; j < sibling.getNumber(); ++j) {
                List<Selection> children = this.c(d2, sibling, entry, ...);
                if (!children.isEmpty()) {
                    this.a(d2, sibling, children.get(0)); // remove
                }
            }
        }
    } else {
        // Non-collective: remove one instance
        this.a(d2, baseSelectionParent, selection);
    }
}
```

### Decrement vs. Remove (f.java:1248-1263)

Individual removal either decrements the number or removes the node:

```java
// f.java:1251
if (selection.getNumber() == 1 || baseSelectionParent instanceof Force) {
    // Remove the node entirely (recursively removing children first)
    this.b(baseSelectionParent, selection);
    this.p(selection); // subtract costs from roster
    return true;
} else {
    // Decrement: first propagate to collective children, then decrement
    this.g(d2, selection);          // remove proportional children
    selection.setNumber(selection.getNumber() - 1);
    return false;
}
```

## Parent Number Propagation

When a non-collective parent's number changes, its collective children are
scaled proportionally.

### Number Increase (f.java:1118-1143 — method `f()`)

Called after a parent's number is incremented. Adds children to match the new
ratio:

```java
private void f(d d2, Selection selection) {
    SelectionEntry entry = d2.i(selection.getEntryId());
    int newNumber = selection.getNumber() - 1; // previous number (before increment)
    if (newNumber == 0) return;

    for (SelectionEntry childEntry : d2.f(entry)) {
        if (!childEntry.isCollective() || d2.f(childEntry)) continue;
        int currentChildCount = this.a(d2, selection, childEntry, ...);
        int toAdd = (int) Math.ceil(currentChildCount / newNumber);
        for (int i = 0; i < toAdd; ++i) {
            this.b(d2, selection, childEntry);  // creates one instance
        }
    }
    // Also handles collective groups...
}
```

**Example**: Parent goes from number=2 to number=3. Collective child currently
has number=2. `ceil(2/2) = 1`, so 1 child is added → child becomes number=3.

### Number Decrease (f.java:1266-1294 — method `g()`)

Called before a parent's number is decremented. Removes proportional children:

```java
private void g(d d2, Selection selection) {
    SelectionEntry entry = d2.i(selection.getEntryId());
    int parentNumber = selection.getNumber(); // current number (before decrement)
    if (parentNumber == 0) return;

    for (SelectionEntry childEntry : d2.f(entry)) {
        if (!childEntry.isCollective() || d2.f(childEntry)) continue;
        int childCount = this.a(d2, selection, childEntry, ...);
        int toRemove = (int) Math.ceil(childCount / parentNumber);
        for (int i = 0; i < toRemove; ++i) {
            this.a(d2, selection, child);  // removes one instance
        }
    }
}
```

**Example**: Parent goes from number=3 to number=2. Collective child has
number=3. `ceil(3/3) = 1`, so 1 child is removed → child becomes number=2.

## setSelectionCount (Per-Model Semantics)

The `getNumChanges` / `setNumSelections` API uses per-model semantics for
collective entries:

### getNumChanges (f.java:948-970)

```java
public int b(BaseSelectionParent parent, SelectionEntry entry, int desiredCount) {
    // ... constraint clamping ...
    int rawCount = this.a(d2, parent, entry, ...); // actual child count
    int effectiveCount = rawCount;
    if (parent instanceof Selection) {
        // h.a() divides by parent.number for collective entries
        effectiveCount = net.battlescribe.engine.b.h.a(
            (Selection)parent, entry, rawCount);
    }
    return desiredCount - effectiveCount;
}
```

### h.a() — Per-Model Count (h.java:186-191)

```java
public static int a(Selection selection, SelectionEntry selectionEntry, int n) {
    if (!selectionEntry.isCollective()) {
        return n;
    }
    return (int) Math.floor(n / selection.getNumber());
}
```

**Effect**: When you call `setSelectionCount(weapon, 2)` and the parent has
`number=3`, the engine computes `effectiveCount = floor(currentCount / 3)` and
the delta is `2 - effectiveCount`. That delta is then applied via the collective
selection path, which replicates × parent.number, resulting in
`weapon.number = 2 × 3 = 6`.

## Cost Calculation

Selection costs are computed as `entry.costValue × selection.number`:

```java
// f.java:1401-1408
for (Cost cost : entry.getCosts()) {
    Cost c = cost.copy();
    c.setValue(c.getValue() * (double)selection.getNumber());
    // ...
}
selection.setCosts(costList);
```

For a collective entry with `cost = 5pts` and `number = 3`, the selection's
displayed cost is `15pts`. The roster total sums all selection costs.

Since collective children scale with the parent, a unit with 3 models where each
model has one 5pt weapon shows `weapon.cost = 15pts` (3 × 5).

## Constraint Validation (Per-Model)

Constraints on collective entries with `scope="parent"` are evaluated per-model:

### Detection (f.java:494-501)

The engine identifies when per-model validation applies:

```java
// f.java:495-497
if (baseEntry2 instanceof SelectionEntry) {
    baseSelectionEntry = (SelectionEntry)baseEntry2;
    isPerModel = baseSelectionEntry.isCollective()
        && scope == BaseQuery.Scope.PARENT
        && baseSelectable instanceof Selection;
}
```

### Application (f.java:558-561)

When `isPerModel` is true, the actual count is divided by the parent's number
before comparing against the constraint limit:

```java
// f.java:558-560
if (isPerModel) {
    Selection parent = (Selection)baseRosterElement;
    actualCount /= (double)parent.getNumber();
}
```

### Error Message Format (f.java:596-598)

When per-model validation is active, the error message includes " each":

```java
if (isPerModel) {
    stringBuilder.append(" each");
}
```

**Example error**: `"Trooper has 1 selections too many of Weapon (max 2 each)"`

### Important: No Capping

The engine does **NOT** prevent exceeding the constraint maximum. It allows
over-selection and reports a validation error. The constraint is advisory, not
enforcing.

## The "isDuplicate" Check — d2.f(SelectionEntry)

This method (d.java:1126-1141) determines whether an entry should create new
selection nodes (returns `true`) or increment an existing node's number (returns
`false`):

```java
// d.java:1126-1141
public boolean f(SelectionEntry selectionEntry) {
    if (this.b(selectionEntry)) {  // some base condition
        return true;
    }
    for (SelectionEntry child : this.f(selectionEntry)) {
        if (!child.isCollective() && !this.h(child)) {  // has visible non-collective child
            return true;
        }
        if (this.f(child)) {  // recursive check
            return true;
        }
    }
    return false;
}
```

**Returns true (create new nodes) when**: The entry has non-collective,
non-hidden children. This is the "duplicate type" — each selection is a distinct
node with its own children tree.

**Returns false (increment number) when**: The entry has ONLY collective children
(or no children). This is the "model type" — instances are tracked by number on
a single node.

This is why a "Trooper" model with only collective weapon children uses
number-increment semantics: `d2.f(trooper)` returns `false` because all its
children are collective.

## Collective Groups (SelectionEntryGroup)

Entry groups can also be collective. The behavior is similar but checked
differently:

### d2.f(SelectionEntryGroup) — d.java:1143-1153

```java
public boolean f(SelectionEntryGroup group) {
    for (SelectionEntry entry : group.getSelectionEntries()) {
        if (entry.isCollective()) continue;
        return false;  // has non-collective entry → group is NOT all-collective
    }
    for (SelectionEntryGroup subGroup : group.getSelectionEntryGroups()) {
        if (this.f(subGroup)) continue;
        return false;
    }
    return true;  // all entries are collective
}
```

When a group is entirely collective (all children are collective), it receives
per-model constraint validation similar to individual collective entries
(f.java:498-500).

### Group Default Selection with Collective Scaling (f.java:1082-1099)

When auto-selecting default entries within a collective group, the count is
multiplied by the parent's number:

```java
// f.java:1093-1094
if (selectionEntryGroup.isCollective()) {
    n *= selection.getNumber();
}
```

### Double-Multiplication Effect (Collective Group + Collective Entry)

When a **collective group** contains **collective entries** (both have
`collective="true"`), propagation fires through TWO paths simultaneously:

1. **Path 1** (f.java:1126): Iterates all child entries flattened from groups via
   `d2.f((BaseSelectionEntry)entry)`. Finds collective entries and adds instances.
2. **Path 2** (f.java:1134): Iterates `entry.getSelectionEntryGroups()`, finds
   collective groups with default entries, and adds MORE instances.

Both paths fire for the same entry, producing a multiplicative (n²) effect:

```
Trooper 1→2: Rifle goes 1→2 (path1: +1), then 2→4 (path2: +ceil(2/1)=2)
Trooper 2→3: Rifle goes 4→6 (path1: +ceil(4/2)=2), then 6→9 (path2: +ceil(6/2)=3)
Final: Trooper=3, Rifle=9 (instead of expected 3)
```

**This is a BattleScribe bug**: the correct result is linear scaling (parent=3 →
child=3, one per model). NewRecruit correctly produces ×3. The spec
`collective-group-default-scaling` uses NR's correct ×3 as the base assertion
with a `battlescribe` engine override documenting the ×9 bug.

### Group Constraint Per-Model Validation (d.java:1143-1153)

A constraint on a `SelectionEntryGroup` uses per-model validation when **all**
entries in the group are collective:

```java
// d.java:1143-1153
public boolean f(SelectionEntryGroup group) {
    for (SelectionEntry entry : group.getSelectionEntries()) {
        if (!entry.isCollective()) return false;
    }
    return true;
}
```

This check (`d2.f(group)`) is used at f.java:498-500 to decide whether to divide
the actual count by parent.number before comparing against the constraint limit.
The **group itself** does not need to be collective — only its entries matter for
per-model constraint evaluation.

## Sibling Replication

The `this.a(d2, (Selection)baseSelectionParent)` call at f.java:1011 returns all
**sibling selections** of the same entry type as the parent. This handles the
case where the same collective entry is selected under multiple sibling instances
of the parent entry.

In practice, for the common case where `d2.f(parentEntry)` returns false (the
parent uses number-increment), there is only one parent Selection node with
`number > 1`, and the "siblings" list contains just that one node.

## Summary of Per-Model Arithmetic

| Operation | Formula | Example (parent.number=3, weapon.cost=5pts) |
|-----------|---------|---------------------------------------------|
| Select collective child | child.number += parent.number | weapon: 0 → 3 |
| Deselect collective child | child.number -= parent.number | weapon: 3 → 0 |
| Parent increases (2→3) | add ceil(child.number / oldNumber) | weapon: 2 → 3 |
| Parent decreases (3→2) | remove ceil(child.number / currentNumber) | weapon: 3 → 2 |
| setSelectionCount(n) | child.number = n × parent.number | setCount(2) → weapon=6 |
| getNumChanges(n) | n - floor(child.number / parent.number) | getChanges(2) with weapon=6 → 0 |
| Cost display | entry.cost × child.number | weapon.cost = 5 × 3 = 15pts |
| Constraint check | actualCount / parent.number vs. limit | 3/3=1 vs. max=2 → OK |

## NR Engine Internals — Source Code Analysis

NewRecruit handles collective entries **differently from BattleScribe** at the
selection-tree level, but achieves the **same correct total cost**. This section
is based on direct analysis of NR's JavaScript source code (from the nr-editor
static deployment and the live NR HAR capture).

### Where `collective` appears in NR source code

The `collective` flag is used at **three architectural layers** in NR:

#### Layer 1: Data model (`Base` class in entry.js / BA2pibXD.js)

The `Base` class (NR's equivalent of BattleScribe's `SelectionEntry`) defines
the raw catalog data properties and computed flags:

```javascript
class Base {
    // ... raw data fields
    collective;                 // the boolean flag from XML catalog data
    collective_recursive;       // computed: are ALL descendants collective?
    limited_to_one;             // computed: can amount be > 1?
    loaded;                     // computed flags already calculated?

    process() {
        // Called once when entry is first used
        this.loaded || (
            this.collective_recursive = this.isCollectiveRecursive(),
            this.limited_to_one = !this.canAmountBeAbove1(),
            this.loaded = true
        );
    }

    isCollective() {
        return this.collective;
    }

    // Recursively checks ALL descendants (not just direct children)
    isCollectiveRecursive() {
        const stack = [...this.selectionsIterator()];
        for (; stack.length;) {
            const child = stack.pop();
            if (!child.isCollective() && !child.isGroup()) return false;
            stack.push(...child.selectionsIterator());
        }
        return true;
    }
}
```

For **entry links** (`Ec` class), `isCollective()` is overridden to inherit
from the target:

```javascript
class Ec extends Base {    // entry link
    isCollective() {
        return super.isCollective() || this.target?.isCollective();
    }
}
```

**Key difference from BattleScribe**: BS's `isDuplicate` (`d2.f`) checks only
**direct children** and **skips hidden** entries. NR's `isCollectiveRecursive()`
checks **all descendants recursively** and does **not consider hidden status**.
In practice this rarely matters, but hidden non-collective children could cause
different instancing behavior between the two engines.

#### Layer 2: Roster runtime (`Cs` / `tU` classes in BA2pibXD.js)

The `collective_recursive` flag controls two critical runtime decisions:

**`checkIsInstanced()`** — determines single-node-with-count vs. separate instances:

```javascript
// On selection selector (manages instances of an entry)
checkIsInstanced() {
    return this.isUnit || this.source.isForce()
        ? true
        : !this.source.isQuantifiable() || this.isLimitedTo1
            ? false
            : !this.source.collective_recursive;
    //      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    //      collective_recursive=true → NOT instanced → single node with count
    //      collective_recursive=false → instanced → separate nodes per instance
}
```

This is the NR equivalent of BattleScribe's `isDuplicate`:
- BS: "has non-collective, non-hidden direct children → isDuplicate=true → separate nodes"
- NR: "NOT all descendants are collective → collective_recursive=false → instanced → separate nodes"

**`checkIsSubUnit()`** — determines if a selection is a sub-unit:

```javascript
checkIsSubUnit() {
    return !this.source.isQuantifiable()
        || this.isLimitedTo1
        || this.isUnit
        || this.source.collective_recursive   // ← collective entries can't be sub-units
            ? false
            : !!(this.parent && (this.parent.isUnit() || this.parent.getParentUnit()));
}
```

**`propagateChanges`** — controls cost/amount propagation up the tree:

```javascript
class Cs {                          // regular selection instance
    propagateChanges = true;        // propagates by default
    // ...
    enable(amount, flag) {
        this.state.setPropagate(this.propagateChanges);
        // ...
    }
}

class tU extends Cs {               // instanced/header selection
    propagateChanges = false;        // does NOT propagate
    // ...
}
```

The `propagateChanges` flag feeds into the `ez()` function which builds the
multiplication array for `getSelectionCount()`:

```javascript
function ez(selection, selfAmount) {
    const result = [];
    let current = selfAmount;
    if (selection.propagateChanges === false) return [];  // ← stops here!
    let parent = selection.getParent();
    for (; parent && !Object.is(parent, parent.getParent());) {
        if (parent.propagateChanges === false) {
            result.push(current);
            result.push(0);
            break;
        }
        const multiplied = (xY(parent) ? parent.getAmount() : 1) * current;
        result.push(multiplied);
        current = multiplied;
        parent = parent.getParent();
    }
    return result;
}
```

#### Layer 3: NR Editor UI (catalogue.js)

The catalogue editor shows a "Collective" checkbox with tooltip:

```javascript
{
    name: "Collective",
    status: this.collective,
    field: "collective",
    title: "indicates that multiple instances of this entry may be combined " +
           "into one entry with an amount",
    default: false
}
```

The checkbox visibility is controlled by `collective()` computed property which
returns `0` (hidden) for root catalog entries:

```javascript
collective() {
    if (this.item.parent?.isCatalogue()) {
        const key = this.item.parentKey;
        if (key === "selectionEntries" || key === "entryLinks") return 0;
    }
    switch (this.item.editorTypeName) {
        case "selectionEntryLink": return /* ... */;
        // ...
    }
}
```

Data importers also set `collective: true` for unit-scoped equipment:
```javascript
// In data import (e.g., from army builder text)
if (scope === "unit") {
    equipment.collective = true;
    weapon.collective = true;
    group.collective = true;
    entry.collective = true;
}
```

### Complete list of `collective` usages in NR source

| Location | File | Usage |
|----------|------|-------|
| `Base.collective` | entry.js | Raw data property on catalog entry |
| `Base.collective_recursive` | entry.js | Computed flag: all descendants collective? |
| `Base.isCollective()` | entry.js | Getter returning `this.collective` |
| `Base.isCollectiveRecursive()` | entry.js | Recursive check for `collective_recursive` |
| `Base.process()` | entry.js | Computes `collective_recursive` on first use |
| `Ec.isCollective()` | entry.js | Entry link override: `self \|\| target` |
| `checkIsInstanced()` | BA2pibXD.js | `!collective_recursive` → instanced (separate nodes) |
| `checkIsSubUnit()` | BA2pibXD.js | `collective_recursive → false` (can't be sub-unit) |
| `Cs.propagateChanges` | BA2pibXD.js | `true` on regular selections (propagates) |
| `tU.propagateChanges` | BA2pibXD.js | `false` on instanced selections (stops propagation) |
| `ez()` | BA2pibXD.js | Multiplication array; stops at `propagateChanges=false` |
| Editor checkbox | catalogue.js | UI toggle with tooltip text |
| Editor visibility | catalogue.js | Hidden for root entries at catalogue level |
| Data import | entry.js | Sets `collective: true` for unit-scoped equipment |
| Entry link creation | entry.js | Copies `collective` from source entry |

### Representation difference

| Aspect | BattleScribe | NewRecruit |
|--------|-------------|------------|
| **Selection count** | Collective child's `number` = parent.number × per-model count (e.g., Rifle ×3 when Trooper ×3) | Collective child's `amount` stays at per-model count (e.g., Rifle ×1 even when Trooper ×3) |
| **Cost per selection** | `entry.cost × selection.number` (5 × 3 = 15 for the selection node) | `entry.cost × getAmount()` (5 × 1 = 5 for the selection node) |
| **Total roster cost** | Sum of all selection-level costs = 15 | `getTotalCosts()` returns 15 — NR multiplies by `getModelAmount()` internally |
| **Key API** | `selection.getNumber()` returns scaled value | `sel.getAmount()` returns per-model; `sel.getModelAmount()` returns parent model count |

### NR internal properties (discovered via live probing)

For a collective entry (Rifle, `collective=true`, parent Trooper ×3):

```text
Rifle.getAmount()       = 1       # per-model selection count (not scaled)
Rifle.getModelAmount()  = 3       # parent's model count (used as cost multiplier)
Rifle.getPointsCost()   = 5       # per-instance cost
Rifle.source.collective = true    # the collective flag from catalog data
Rifle.source.collective_recursive = true  # NR's recursive collective tracking
```

Non-collective parent (Trooper):
```text
Trooper.source.collective = false
Trooper.source.collective_recursive = true  # has collective descendants
```

### `getModelAmount()` — NR's cost multiplier

`getModelAmount()` is the key method NR uses to correctly account for parent
model counts in cost calculations. Its source:

```javascript
getModelAmount() {
    if (this.getBook().getSystem().settings.extractModelCountFromName) {
        const match = this.getName().match(/([0-9]+) .*/);
        return match && match[1] ? parseInt(match[1]) : 1;
    } else {
        return this.getSelectionCount("root");
    }
}
```

By default it delegates to `getSelectionCount("root")` which multiplies the
selection's own amount through the entire parent chain.

### `getSelectionCount()` — the export multiplier

NR has a critical method `getSelectionCount(stopAtId)` that multiplies the
selection's own amount through the parent chain up to the specified ancestor.
This is used by the `.ros`/`.json` export serializer (`JU()`) with the argument
`"root"` to compute the export `number` attribute.

```javascript
getSelectionCount(stopAtId) {
    const selfAmount = this.getSelfAmountElseChilds();
    if (!stopAtId) return selfAmount;        // no arg → return own amount
    const multiplied = ez(this, selfAmount); // builds multiplication array
    let idx = 0, parent = this.getParent();
    for (; parent && parent.getId() !== stopAtId;)
        parent = parent.getParent(), parent && idx++;
    return multiplied[idx] ?? 0;
}
```

```text
# With Trooper ×3, Rifle (collective, amount=1), Badge (non-collective, amount=1):

Trooper.getSelectionCount()       = 3   # same as getAmount()
Trooper.getSelectionCount("root") = 3

Rifle.getSelectionCount()         = 1   # own amount only
Rifle.getSelectionCount("root")   = 3   # 1 × 3 (multiplied through Trooper)
Rifle.getSelectionCountIn()       = 1   # per-instance count

Badge.getSelectionCount()         = 1   # own amount only
Badge.getSelectionCount("root")   = 3   # 1 × 3 (multiplied through Trooper)
Badge.getSelectionCountIn()       = 1   # per-instance count
```

**Key finding**: `getSelectionCount("root")` returns the **same value for both
collective and non-collective children** — both get multiplied by the parent
model count. The collective flag does NOT affect the export number.

### NR export formats compared

**BS-format export** (`.ros` XML / `.json`):
Uses `getSelectionCount("root")` for `number`, multiplies costs by that number.
Both Rifle and Badge appear as single nodes with `number=3`.

```json
{
  "name": "Trooper", "number": 3, "type": "model",
  "selections": [
    { "name": "Badge", "number": 3, "type": "upgrade",
      "source.collective": false,
      "costs": [{"name": "pts", "value": 6}] },
    { "name": "Rifle", "number": 3, "type": "upgrade",
      "source.collective": true,
      "costs": [{"name": "pts", "value": 15}] }
  ]
}
```

**Internal save format** (`toJsonObject`):
Uses raw `amount` values. No cost data — costs are recomputed on load.

```json
{
  "name": "Trooper", "option_id": "se-trooper", "amount": 3,
  "options": [
    { "name": "Badge", "option_id": "se-badge", "amount": 1 },
    { "name": "Rifle", "option_id": "se-rifle", "amount": 1 }
  ]
}
```

### What this means for specs

The adapter uses `getSelectionCount("root")` for all selection `number` fields,
which correctly handles both collective and non-collective children. For cost
calculation, the adapter multiplies unit cost by `getSelectionCount("root")`,
matching BattleScribe's `cost × number` semantics.

All collective specs now pass on both engines (BattleScribe and NR frozen).
Where tree structure differs (e.g., isDuplicate creates separate nodes in BS
but NR uses a single node with incremented number), the specs use per-engine
`expectedState` overrides to document both behaviors while asserting semantic
equivalence (total costs match).

The `deselectSelection` adapter action uses NR's `decrementAmount()` method,
which reduces the per-model count by 1 — matching BattleScribe's deselect
semantics (decrement number, or remove if number reaches 0).

## Spec Coverage

The following specs validate collective behavior:

| Spec ID | Behavior Tested |
|---------|----------------|
| `collective-number-propagation` | Parent number increase+decrease propagates to child |
| `collective-child-inherits-number` | New child created with parent's number |
| `collective-per-model-operations` | setSelectionCount + deselect per-model semantics (both engines agree) |
| `collective-constraint-per-model` | Constraint validation per-model division |
| `collective-group-default-scaling` | Group double-processing bug in BS produces n² scaling; NR correctly gives linear ×3 (BS override documents ×9 bug) |
| `collective-group-constraint-per-model` | Group constraint uses per-model validation |
| `collective-group-no-default` | Group without defaultSelectionEntryId — no auto-propagation |
| `collective-sibling-replication` | Duplicate-type parent replicates collective child to all sibling nodes in BS; NR uses single ×N node (engine overrides document structural difference) |
| `collective-root-ignored` | Collective flag on root entry (parent=Force) is ignored |
| `collective-is-duplicate` | isDuplicate (d2.f) determines increment-vs-new-node behavior in BS; NR always uses increment mode (engine overrides document structural difference) |

## Source References

| File | Lines | Content |
|------|-------|---------|
| `f.java` | 1000-1027 | Selection with collective replication |
| `f.java` | 1010 | Collective activation condition (3 checks) |
| `f.java` | 1029-1053 | Increment vs. new node logic |
| `f.java` | 1118-1143 | Parent number increase propagation (`f()`) |
| `f.java` | 1207-1246 | Deselection with collective replication |
| `f.java` | 1248-1263 | Decrement vs. remove (calls `g()` before) |
| `f.java` | 1266-1294 | Parent number decrease propagation (`g()`) |
| `f.java` | 1308-1314 | Number inheritance on creation |
| `f.java` | 1382-1384 | Group collective scaling |
| `f.java` | 1398-1413 | Cost = entry.cost × selection.number |
| `f.java` | 480-502 | Constraint per-model detection |
| `f.java` | 558-561 | Constraint per-model application |
| `f.java` | 596-598 | " each" suffix in error messages |
| `f.java` | 948-970 | getNumChanges per-model semantics |
| `h.java` | 186-191 | `h.a()` — per-model count helper |
| `d.java` | 1126-1141 | `d2.f(SelectionEntry)` — isDuplicate |
| `d.java` | 1143-1153 | `d2.f(SelectionEntryGroup)` — all-collective check |
| `c.java` | 1643-1645 | `isRootSelection` — parent instanceof Force |
