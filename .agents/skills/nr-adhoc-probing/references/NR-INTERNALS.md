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
