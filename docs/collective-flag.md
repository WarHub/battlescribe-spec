# Collective Flag — Reference Behavior and Engine Differences

This document is the reference for how `collective="true"` behaves in BattleScribe roster engines.

**NewRecruit (NR) is the canonical engine.** Spec defaults should follow NR behavior. Where BattleScribe (BS) differs, this document treats the difference as either a BS bug or a BS-specific design choice.

The analysis below combines:

- NR JavaScript source analysis
- live/runtime probing results from NR
- decompiled BS Java source (`net.battlescribe.engine.a.f`, `d`, `c`, and `net.battlescribe.engine.b.h`)

> **Note:** Java source citations (e.g. `f.java:1000`, `d.java:1126`) refer to decompiled
> BattleScribe engine code (`lib/BattleScribeEngine.jar`) not present as files in this repository.
> In-repo implementation references use full paths under `src/`.

## Overview

Conceptually, a collective child is chosen **per parent model**, not as an independent child tree under each parent instance.

Example:

- `Trooper` has `number=3`
- `Rifle` is a child of `Trooper` and has `collective=true`
- selecting `Rifle` once means **1 Rifle per Trooper**, not “one Rifle on one specific Trooper instance”

For specs and BS-format exports, that usually appears as a single `Rifle` node with `number=3`. In NR's internal runtime, however, the logical amount is still **1 per model**; the exported `number` is derived later by multiplying through the parent chain.

Two immediate consequences follow from that model:

1. `setSelectionCount` on a collective child uses **per-model semantics**.
2. Constraints on collective entries with `scope="parent"` validate **per model**, not against the raw exported count.

The flag is only interesting when the parent is itself a `Selection`. At the root (`Force -> Selection`), collective behavior is effectively ignored and entries behave like ordinary root selections.

### Terminology

| Term | Meaning |
|------|---------|
| **Collective entry** | A `SelectionEntry` with `collective="true"` |
| **Parent selection** | The `Selection` node containing the collective child |
| **Per-model amount** | The logical amount per parent model |
| **Exported number** | The BS-format `number` after multiplying through the parent chain |
| **`collective_recursive`** | NR flag meaning “all descendants are collective-compatible, so this entry can stay merged into one counted node” |
| **`d2.f(entry)` / isDuplicate** | BS heuristic deciding whether an entry should create separate nodes instead of incrementing one node's count |

## Core Behaviors

### Per-model state vs. exported numbers (NR reference)

NR stores the logical amount on the selection itself, then derives BS-style exported numbers by multiplying through the parent chain.

For a collective entry (`Rifle`) under `Trooper ×3`:

```text
Rifle.getAmount()       = 1
Rifle.getModelAmount()  = 3
Rifle.getSelectionCount()       = 1
Rifle.getSelectionCount("root") = 3
```

For a non-collective sibling (`Badge`) under the same `Trooper ×3`:

```text
Badge.getAmount()       = 1
Badge.getSelectionCount()       = 1
Badge.getSelectionCount("root") = 3
```

**Important:** in NR, `getSelectionCount("root")` multiplies through the parent chain for **all** children. The `collective` flag does **not** change the export multiplier. It affects:

- whether the entry is treated as merged vs. instanced (`collective_recursive` / `isInstanced`)
- related UI/runtime behavior built on that distinction

The adapter therefore uses `getSelectionCount("root")` for the exported `number` field regardless of whether the child is collective.

### Selection, deselection, and parent-count changes

NR's logical behavior is linear and per-model:

| Operation | Logical result | Exported result (parent `number=3`) |
|-----------|----------------|-------------------------------------|
| Select a collective child once | amount becomes `1` per model | exported `number=3` |
| `setSelectionCount(2)` on the child | amount becomes `2` per model | exported `number=6` |
| Deselect the child once | amount decreases by `1` per model | exported `6 -> 3` |
| Parent `2 -> 3` with child at `1` per model | child stays at `1` per model | exported `2 -> 3` |
| Parent `3 -> 2` with child at `1` per model | child stays at `1` per model | exported `3 -> 2` |

That is the behavior specs should treat as correct.

### Cost calculation

NR and BS use different internal representations but agree on the correct total cost.

| Aspect | BattleScribe | NewRecruit |
|--------|-------------|------------|
| **Selection count** | Collective child's `number` is already scaled (e.g. `Rifle ×3`) | Collective child's `amount` stays per-model (e.g. `Rifle ×1`) |
| **Selection-level cost** | `entry.cost × selection.number` | `entry.cost × getAmount()` |
| **Total roster cost** | Sum of scaled selection costs | `getTotalCosts()` multiplies through `getModelAmount()` / `getSelectionCount("root")` |
| **What specs assert** | Exported/scaled value | Exported/scaled value from adapter |

So with `Trooper ×3` and `Rifle = 5pts`:

- BS stores `Rifle.number = 3`, `Rifle.cost = 15`
- NR stores `Rifle.amount = 1`, `Rifle.getPointsCost() = 5`, but total/exported cost is still `15`

### Constraint validation

Collective constraints are validated per model.

- For a collective `SelectionEntry` with `scope="parent"`, the effective count is divided by the parent number before comparison.
- For a `SelectionEntryGroup`, per-model validation applies when **all entries in the group are collective-compatible**.
- Over-selection is still allowed; engines report validation errors rather than clamping the count.

That means a `max 2` collective weapon constraint under `Trooper ×3` allows:

- exported `number=6` (`2 per model × 3 models`) with no error
- exported `number=9` (`3 per model × 3 models`) with a validation error

### Instancing vs. merged nodes

The `collective` flag also determines whether repeated selection increments one counted node or creates separate instance nodes.

NR's rule is the reference rule:

- if an entry is **collective-compatible all the way down** (`collective_recursive=true`), it stays merged and repeated selection increments one node's amount
- if any descendant requires instance separation, the entry is **instanced** and repeated selection creates distinct nodes

This is why:

- a model with only collective descendants becomes one node with `number=3`
- a model with a non-collective child becomes three separate `number=1` nodes

BS has a similar but not identical heuristic (`d2.f(entry)` / `isDuplicate`), described in the implementation section.

## Engine Differences

### 1. BS sibling replication (design difference)

When a collective child is selected on an **instanced** parent, NR applies the change only to the selected instance.

BS instead walks all sibling parent instances of the same entry and replicates the collective child to each of them.

Example:

- three separate `Sergeant` nodes exist
- selecting collective `Special Weapon` on the first `Sergeant`
- **NR:** only the first `Sergeant` gets the weapon
- **BS:** all three `Sergeant` siblings get the weapon

This is a design difference, not NR behavior to emulate. Specs should default to the NR result and use a BS override where needed.

### 2. BS `setSelectionCount` on instanced entries is a no-op (bug)

For entries that BS classifies as `isDuplicate` / separate-instance entries, calling `setSelectionCount` is broken.

Canonical NR behavior:

- increasing an instanced parent's count scales children through parent-chain multiplication
- exported numbers and costs scale accordingly

BS bug:

- `setSelectionCount` on the duplicate entry does nothing
- the entry stays at `number=1`
- child counts and costs remain unchanged

The spec `collective-instance-amount` documents this with a `battlescribe` engine override.

### 3. BS collective group double-multiplication is an `n²` bug

When a **collective group** contains a **collective default entry**, NR scales linearly:

- parent `1 -> 3`
- default child `1 -> 3`

BS incorrectly processes the same entry twice during parent-number propagation:

1. once via flattened group contents
2. again via the collective-group default-entry path

That yields the bug:

```text
parent=3 -> child=9
```

This is a BS-only bug. NR's linear result is the correct reference behavior.

## Implementation Details

### NewRecruit internals

#### Where `collective` appears in NR source code

NR uses `collective` at three layers:

1. **data model** (`Base` / `Ec`)
2. **roster runtime** (`Cs`, `tU`, `checkIsInstanced`, `getSelectionCount`)
3. **editor UI** (catalogue checkbox / visibility)

#### Layer 1: data model (`Base` / `Ec`)

The raw flag lives on the entry model, and NR derives `collective_recursive` from descendants.

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

For entry links, NR treats collectiveness as `self || target`:

```javascript
class Ec extends Base {    // entry link
    isCollective() {
        return super.isCollective() || this.target?.isCollective();
    }
}
```

This is the key NR rule to keep in mind:

- `collective` is the raw catalog flag on one entry
- `collective_recursive` is the runtime property that decides whether the whole subtree can remain merged

That one recursive notion replaces a lot of BS-specific reasoning about direct children, duplicates, and hidden entries. BS's `d2.f(entry)` effectively cares about visible non-collective descendants; NR's `collective_recursive` does not have a hidden-entry exception and simply asks whether the subtree is collectively compatible.

#### Layer 2: roster runtime (`Cs`, `tU`, `checkIsInstanced`)

**`checkIsInstanced()`** is the runtime switch between merged-vs-instanced behavior:

```javascript
checkIsInstanced() {
    return this.isUnit || this.source.isForce()
        ? true
        : !this.source.isQuantifiable() || this.isLimitedTo1
            ? false
            : !this.source.collective_recursive;
    //      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    //      collective_recursive=true  -> NOT instanced -> single node with count
    //      collective_recursive=false -> instanced     -> separate nodes
}
```

This is the NR equivalent of BS `isDuplicate`:

- **BS:** “has non-collective, non-hidden direct children” -> duplicate / separate nodes
- **NR:** “not recursively collective-compatible” -> instanced / separate nodes

**`checkIsSubUnit()`** also keys off `collective_recursive`:

```javascript
checkIsSubUnit() {
    return !this.source.isQuantifiable()
        || this.isLimitedTo1
        || this.isUnit
        || this.source.collective_recursive   // collective-compatible entries can't be sub-units
            ? false
            : !!(this.parent && (this.parent.isUnit() || this.parent.getParentUnit()));
}
```

**`propagateChanges`** is important and easy to misread:

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
    propagateChanges = false;       // does NOT propagate
    // ...
}
```

The important detail is:

- **individual instance nodes (`Cs`) have `propagateChanges=true`**
- **only the instanced header/selector node (`tU`) has `propagateChanges=false`**

So propagation is not disabled on ordinary child selections. It stops only when the multiplication walk reaches the header node that represents an instanced boundary.

That feeds into `ez()`, which builds the multiplication array used by `getSelectionCount()`:

```javascript
function ez(selection, selfAmount) {
    const result = [];
    let current = selfAmount;
    if (selection.propagateChanges === false) return [];  // stops immediately on header nodes
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

#### Layer 3: NR Editor UI (`catalogue.js`)

The editor exposes a `Collective` checkbox with tooltip text:

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

The UI also hides the checkbox for root catalogue entries:

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

That matches the runtime reality: the interesting collective behavior is below a parent `Selection`, not at the force/root boundary.

#### NR internal properties (live probing)

For a collective entry (`Rifle`, parent `Trooper ×3`):

```text
Rifle.getAmount()       = 1       # per-model amount
Rifle.getModelAmount()  = 3       # parent's model count used for total cost/export
Rifle.getPointsCost()   = 5       # per-instance cost
Rifle.source.collective = true
Rifle.source.collective_recursive = true
```

For the non-collective parent (`Trooper`):

```text
Trooper.source.collective = false
Trooper.source.collective_recursive = true  # has only collective-compatible descendants
```

#### `getModelAmount()` — NR's total-cost multiplier

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

By default, total-cost scaling comes from `getSelectionCount("root")`, i.e. from parent-chain multiplication.

#### `getSelectionCount()` — export multiplier for every child

```javascript
getSelectionCount(stopAtId) {
    const selfAmount = this.getSelfAmountElseChilds();
    if (!stopAtId) return selfAmount;        // no arg -> return own amount
    const multiplied = ez(this, selfAmount); // builds multiplication array
    let idx = 0, parent = this.getParent();
    for (; parent && parent.getId() !== stopAtId;)
        parent = parent.getParent(), parent && idx++;
    return multiplied[idx] ?? 0;
}
```

Observed behavior with `Trooper ×3`, `Rifle` collective, `Badge` non-collective:

```text
Trooper.getSelectionCount()        = 3
Trooper.getSelectionCount("root") = 3

Rifle.getSelectionCount()          = 1
Rifle.getSelectionCount("root")   = 3
Rifle.getSelectionCountIn()        = 1

Badge.getSelectionCount()          = 1
Badge.getSelectionCount("root")   = 3
Badge.getSelectionCountIn()        = 1
```

**Key finding:** `getSelectionCount("root")` returns the same parent-multiplied export number for both collective and non-collective children. The collective flag does **not** control exported `number`; it controls whether the subtree is merged/instanced and how the UI/runtime treat it.

#### NR export formats compared

**BS-format export** (`.ros` XML / `.json`):
Uses `getSelectionCount("root")` for `number`, so both collective and non-collective children appear with parent-multiplied counts.

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
Uses raw amounts only.

```json
{
  "name": "Trooper", "option_id": "se-trooper", "amount": 3,
  "options": [
    { "name": "Badge", "option_id": "se-badge", "amount": 1 },
    { "name": "Rifle", "option_id": "se-rifle", "amount": 1 }
  ]
}
```

#### Complete list of `collective` usages in NR source

| Location | File | Usage |
|----------|------|-------|
| `Base.collective` | entry.js | Raw data property on catalog entry |
| `Base.collective_recursive` | entry.js | Computed flag: all descendants collective? |
| `Base.isCollective()` | entry.js | Getter returning `this.collective` |
| `Base.isCollectiveRecursive()` | entry.js | Recursive check for `collective_recursive` |
| `Base.process()` | entry.js | Computes `collective_recursive` on first use |
| `Ec.isCollective()` | entry.js | Entry link override: `self || target` |
| `checkIsInstanced()` | BA2pibXD.js | `!collective_recursive` -> instanced |
| `checkIsSubUnit()` | BA2pibXD.js | `collective_recursive -> false` |
| `Cs.propagateChanges` | BA2pibXD.js | `true` on regular selections |
| `tU.propagateChanges` | BA2pibXD.js | `false` only on instanced header/selector |
| `ez()` | BA2pibXD.js | Multiplication array builder; stops at `propagateChanges=false` |
| Editor checkbox | catalogue.js | UI toggle with tooltip text |
| Editor visibility | catalogue.js | Hidden for root entries at catalogue level |
| Data import | entry.js | Sets `collective: true` for unit-scoped equipment |
| Entry link creation | entry.js | Copies `collective` from source entry |

### BattleScribe internals

BS exposes the same logical ideas more directly because it stores the scaled/exported count on `selection.number` itself.

#### Activation condition: when BS collective behavior actually turns on

BS only enters its special collective selection path when all three conditions hold:

```java
// f.java:1010
if (selectionEntry.isCollective()
    && baseSelectionParent instanceof Selection
    && !this.i((Selection)baseSelectionParent))
```

So BS requires:

1. `collective=true`
2. parent is a `Selection`
3. parent is **not** a root selection

The root check is:

```java
public boolean i(Selection selection) {
    this.a("isRootSelection", selection);
    return selection.getParent() instanceof Force;
}
```

That is why `collective-root-ignored` behaves the same way on both engines.

#### Selection

When selecting a collective child under a non-root parent, BS iterates sibling parent selections and creates one child per parent instance:

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

This is the implementation source of BS sibling replication.

#### Number inheritance on creation

When BS creates a new collective selection node and the entry is not duplicate-style, it seeds the child with the parent's number:

```java
// f.java:1311-1313
if (baseSelectionParent instanceof Selection
    && selectionEntry.isCollective()
    && !d2.f(selectionEntry)) {
    selection.setNumber(((Selection)baseSelectionParent).getNumber());
}
```

So if `Trooper.number = 3`, newly selecting `Weapon` creates `Weapon.number = 3` immediately.

#### Increment vs. new node (`d2.f(entry)`)

BS chooses between “increment one node's number” and “create a separate node” here:

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

If `d2.f(entry)` is false, repeated selection increments one node's `number`. If it is true, BS creates separate sibling nodes.

#### Deselection

BS mirrors selection on deselect:

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

And individual removal either decrements the node or removes it entirely:

```java
// f.java:1251
if (selection.getNumber() == 1 || baseSelectionParent instanceof Force) {
    // Remove the node entirely
    this.b(baseSelectionParent, selection);
    this.p(selection); // subtract costs from roster
    return true;
} else {
    // Decrement: first propagate to collective children, then decrement
    this.g(d2, selection);
    selection.setNumber(selection.getNumber() - 1);
    return false;
}
```

#### Parent-number propagation

When a non-collective parent changes number, BS scales collective children proportionally.

Increase path:

```java
private void f(d d2, Selection selection) {
    SelectionEntry entry = d2.i(selection.getEntryId());
    int newNumber = selection.getNumber() - 1; // previous number
    if (newNumber == 0) return;

    for (SelectionEntry childEntry : d2.f(entry)) {
        if (!childEntry.isCollective() || d2.f(childEntry)) continue;
        int currentChildCount = this.a(d2, selection, childEntry, ...);
        int toAdd = (int) Math.ceil(currentChildCount / newNumber);
        for (int i = 0; i < toAdd; ++i) {
            this.b(d2, selection, childEntry);
        }
    }
    // Also handles collective groups...
}
```

Decrease path:

```java
private void g(d d2, Selection selection) {
    SelectionEntry entry = d2.i(selection.getEntryId());
    int parentNumber = selection.getNumber(); // current number before decrement
    if (parentNumber == 0) return;

    for (SelectionEntry childEntry : d2.f(entry)) {
        if (!childEntry.isCollective() || d2.f(childEntry)) continue;
        int childCount = this.a(d2, selection, childEntry, ...);
        int toRemove = (int) Math.ceil(childCount / parentNumber);
        for (int i = 0; i < toRemove; ++i) {
            this.a(d2, selection, child);
        }
    }
}
```

For ordinary collective children, this produces the same linear behavior NR gives logically:

- parent `1 -> 3` -> child `1 -> 3`
- parent `3 -> 1` -> child `3 -> 1`

#### `setSelectionCount` / `getNumChanges` use per-model semantics

BS computes deltas for collective children in per-model units.

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

The helper is:

```java
public static int a(Selection selection, SelectionEntry selectionEntry, int n) {
    if (!selectionEntry.isCollective()) {
        return n;
    }
    return (int) Math.floor(n / selection.getNumber());
}
```

So with `Trooper.number = 3` and `Weapon.number = 6`, BS interprets the effective child count as `floor(6 / 3) = 2`, i.e. “2 per model”.

That is why `setSelectionCount(2)` on a collective child under `Trooper ×3` yields exported `Weapon.number = 6`.

#### Cost calculation

BS multiplies entry cost by the stored `selection.number`:

```java
for (Cost cost : entry.getCosts()) {
    Cost c = cost.copy();
    c.setValue(c.getValue() * (double)selection.getNumber());
    // ...
}
selection.setCosts(costList);
```

For collective entries, that stored number is already parent-scaled, so `5pts × Weapon.number(3) = 15pts`.

#### Constraint validation

BS identifies per-model validation like this:

```java
if (baseEntry2 instanceof SelectionEntry) {
    baseSelectionEntry = (SelectionEntry)baseEntry2;
    isPerModel = baseSelectionEntry.isCollective()
        && scope == BaseQuery.Scope.PARENT
        && baseSelectable instanceof Selection;
}
```

Then divides by the parent number before comparison:

```java
if (isPerModel) {
    Selection parent = (Selection)baseRosterElement;
    actualCount /= (double)parent.getNumber();
}
```

And uses ` each` in the validation message:

```java
if (isPerModel) {
    stringBuilder.append(" each");
}
```

Like NR, BS does **not** cap the count. It allows over-selection and reports an error instead.

#### `d2.f(SelectionEntry)` — BS duplicate/instancing heuristic

```java
public boolean f(SelectionEntry selectionEntry) {
    if (this.b(selectionEntry)) {
        return true;
    }
    for (SelectionEntry child : this.f(selectionEntry)) {
        if (!child.isCollective() && !this.h(child)) {
            return true;
        }
        if (this.f(child)) {
            return true;
        }
    }
    return false;
}
```

Interpretation:

- **returns `true`** -> create separate nodes
- **returns `false`** -> increment one node's `number`

In common cases this lines up with NR `collective_recursive`, but the implementations are not identical:

- BS reasons mostly in terms of direct visible/non-hidden children plus recursion
- NR reasons directly in terms of recursive collectiveness of the whole subtree

#### Collective groups (`SelectionEntryGroup`)

BS has a separate all-collective check for groups:

```java
public boolean f(SelectionEntryGroup group) {
    for (SelectionEntry entry : group.getSelectionEntries()) {
        if (entry.isCollective()) continue;
        return false;
    }
    for (SelectionEntryGroup subGroup : group.getSelectionEntryGroups()) {
        if (this.f(subGroup)) continue;
        return false;
    }
    return true;
}
```

That is used for group-level per-model constraint validation. The group itself does not need `collective=true` for this validation rule to apply; what matters is that the group's entries are collectively compatible.

Default-entry scaling also has a collective-group branch:

```java
if (selectionEntryGroup.isCollective()) {
    n *= selection.getNumber();
}
```

The BS bug is that propagation hits collective default entries through two paths at once:

1. flattened group contents
2. collective-group default-entry logic

That produces the `n²` behavior documented by `collective-group-default-scaling`.

#### Summary of BS arithmetic

For a normal nested collective child, BS's stored-number model can be summarized as:

| Operation | Stored/Exported formula | Example (`parent.number=3`, `weapon.cost=5pts`) |
|-----------|-------------------------|--------------------------------------------------|
| Select collective child | `child.number += parent.number` | `0 -> 3` |
| Deselect collective child | `child.number -= parent.number` | `6 -> 3` |
| Parent increases `2 -> 3` | add `ceil(child.number / oldParentNumber)` | `2 -> 3` |
| Parent decreases `3 -> 2` | remove `ceil(child.number / currentParentNumber)` | `3 -> 2` |
| `setSelectionCount(n)` | `child.number = n × parent.number` | `2 -> 6` |
| `getNumChanges(n)` | `n - floor(child.number / parent.number)` | `2 - floor(6/3) = 0` |
| Cost display | `entry.cost × child.number` | `5 × 3 = 15pts` |
| Constraint check | `child.number / parent.number` vs. limit | `6/3 = 2` |

## Spec Coverage

Spec defaults should follow NR. BS-only divergences are captured with `engines.battlescribe` overrides where appropriate.

| Category | Spec ID | Coverage |
|----------|---------|----------|
| `selection` | `collective-number-propagation` | Parent number changes scale collective children linearly up and back down. |
| `selection` | `collective-child-inherits-number` | Selecting a collective child under an existing parent count creates the child with the parent's scaled/exported number and cost. |
| `selection` | `collective-per-model-operations` | `setSelectionCount` on a collective child uses per-model semantics; deselect removes one per model. |
| `selection` | `collective-constraint-per-model` | Entry-level `scope=parent` constraint validation divides by parent number and reports errors only when per-model limits are exceeded. |
| `selection` | `collective-group-default-scaling` | NR linear default-entry scaling vs. BS `n²` group double-processing bug. |
| `selection` | `collective-group-constraint-per-model` | Group-level constraint uses per-model validation when all entries in the group are collective. |
| `selection` | `collective-group-no-default` | Collective group without `defaultSelectionEntryId` does not auto-select or auto-propagate entries. |
| `selection` | `collective-sibling-replication` | NR keeps collective children on the selected instanced parent only; BS replicates to sibling parent instances. |
| `selection` | `collective-root-ignored` | Root selections ignore collective behavior and remain ordinary separate root nodes. |
| `selection` | `collective-is-duplicate` | Merged-node vs. separate-instance behavior for entries with only collective descendants vs. entries with non-collective descendants. |
| `selection` | `collective-instance-amount` | Canonical NR behavior for scaling instanced entries via parent-chain multiplication; BS `setSelectionCount` no-op bug on duplicate entries. |
| `selection` | `collective-with-constraint` | Legacy smoke test: a parent that has a collective child still respects its own parent-scoped max-selection constraint. |
| `entry-group` | `entry-group-collective` | Collective `SelectionEntryGroup` counts its child selections as one collective for group-level constraint purposes. |

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
| `f.java` | 596-598 | `" each"` suffix in error messages |
| `f.java` | 948-970 | `getNumChanges` per-model semantics |
| `h.java` | 186-191 | `h.a()` — per-model count helper |
| `d.java` | 1126-1141 | `d2.f(SelectionEntry)` — isDuplicate |
| `d.java` | 1143-1153 | `d2.f(SelectionEntryGroup)` — all-collective check |
| `c.java` | 1643-1645 | `isRootSelection` — `parent instanceof Force` |
