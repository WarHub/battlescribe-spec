# NR Internals — Discovered Behaviors

Behaviors discovered by probing the live NR site via ad-hoc Playwright tests.

## createRoster auto-force insertion

**NR's `bookData.createRoster(costs)` auto-inserts a force when there is exactly
one force entry visible to the catalogue.**

Deobfuscated source:

```javascript
createRoster(costs) {
    const list = $B(this.catalogue);
    const root = list.first();
    root.first().setMaxCosts(costs);
    const mainCat = root.selectors.find(a => a.ids.includes(this.catalogue.id));
    if (!mainCat) throw Error("couldn't find main_catalogue inside list");
    const forceSel = mainCat.first().selectors.find(a => a.source.isForce());
    if (!forceSel) throw Error("couldn't find any force inside main_catalogue");
    // Auto-add only when exactly one child (force entry) exists
    if (mainCat.source.childs.length === 1)
        mainCat.first().insertForce(null, forceSel.getId());
    return root;
}
```

**Behavior matrix** (verified April 2026):

| GS force entries | CAT force entries | Total visible | Auto-added? |
|------------------|-------------------|---------------|-------------|
| 1 | 0 | 1 | ✅ Yes |
| 0 | 1 | 1 | ✅ Yes |
| 1 | 1 | 2 | ❌ No |
| 3 | 0 | 3 | ❌ No |

**Key points:**
- Location (GST vs CAT) doesn't matter — only the total count matters
- The check is `childs.length === 1`, not a min-constraint check
- `createRoster` **requires** at least one force entry or it throws
  "couldn't find any force inside main_catalogue"
- This is a NR UX convenience, not a BattleScribe data model behavior
- The Oracle engine never auto-adds forces — `new Roster()` starts empty

## createRoster requires force entries

`createRoster` will throw if **zero** force entries exist in the merged
catalogue tree. The adapter must always provide at least one force entry
in either the game system or catalogue protocol data.

## Selection mechanics: addInstance, incrementAmount, autocheck

NR's roster tree uses **selectors** (templates) and **instances** (materialized
selections). Understanding the three key operations is critical for the adapter:

### addInstance()

Called on a **selector** to create a new instance (selection). Used for
force-level entry selectors — these have an `addInstance` method and create
a fresh child node each time. After creation, the new instance starts with
`amount=0` and child selectors/instances are not yet fully materialized.

### incrementAmount()

Called on an existing **instance** (child node) to increment its count.
Used for child entries that already exist as pre-created nodes with
`amount=0` under a parent selection. Unlike `addInstance`, this doesn't
create a new node — it bumps the count on an existing one.

### autocheck()

Called on an **instance** after `addInstance()` creates it. This is NR's
internal cascading auto-selection method. It recursively walks child
selectors, finds entries with `min` constraints, and auto-selects them
(setting their amount to satisfy the minimum). Without `autocheck()`,
children with `min>=1` constraints remain at `amount=0` — they show up
as unselected despite the constraint.

**Key behaviors** (verified April 2026):

- `autocheck()` respects `min` constraints on child entries and entry links
- `autocheck()` does **NOT** respect `defaultSelectionEntryId` on groups —
  it selects the alphabetically first entry instead
- The cascade is recursive: autocheck on a parent triggers autocheck on
  auto-selected children, which triggers autocheck on their children, etc.
- All 5 selection methods in `NewRecruitActions.cs` call `autocheck()` after
  `addInstance()` to match Oracle's auto-selection behavior

**Adapter pattern:**

```javascript
// Force-level entry selection
if (typeof selector.addInstance === 'function') {
    selector.addInstance();
    // MUST call autocheck to cascade min-constraint auto-selection
    const insts = selector.instances || [];
    insts[insts.length - 1]?.autocheck?.();
}
// Child entry increment (already materialized)
else if (selector.getAmount?.() === 0) {
    selector.incrementAmount();
    selector.autocheck?.(); // also triggers cascade on children
}
```
