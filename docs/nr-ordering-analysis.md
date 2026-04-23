# NR Selection & Force Ordering — Analysis

> Research conducted via live Playwright investigation of [newrecruit.eu](https://newrecruit.eu)
> and NR JS bundle analysis. Verified against wh40k 10th Edition and Horus Heresy 3rd Edition.

## Summary

NR's item ordering is controlled by a single function: `Cs.initializeChilds()`.
The critical distinction is between **force nodes** and **non-force nodes**:

- **Force nodes** (`isForce()=true`): selectors maintain XML/source definition order. **No sort.**
- **Non-force nodes** (categories, entries, groups): selectors sorted by a 3-tier comparator:
  1. `sortIndex` (default 10000 if unset) — lower first
  2. `isGroup()` — non-groups (0) before groups (1)
  3. `getName()` via `localeCompare(undefined, { numeric: true })` — alphabetical

## Ordering Rules by Node Type

### Force → Categories

Force is `isForce()=true` → **no sort**. Categories appear in `forceEntry.categoryLinks`
XML order.

*Verified*: wh40k categories = Uncategorized, Configuration, Epic Hero, Character,
Battleline, Infantry, Swarm, Mounted, Beast, Monster, Vehicle, Drone, Dedicated Transport,
Fortification, Unit, Allied Units, Reference, Illegal Units — not alphabetical, matches XML.

### Category → Entries (root-level selections)

Category instance is `isForce()=false` → **sorted**. Entries typically have no `sortIndex`
(undefined → 10000) and are not groups, so effective sort is **alphabetical by name**.

*Verified*: Battleline entries = Aquila Kill Team, Assault Intercessor Squad,
Deathwatch Kill Team, Heavy Intercessor Squad, Imperial Navy Breachers,
Intercessor Squad, Tactical Squad, Vigilant Squad — alphabetical.

### Entry → Child Entries

Entry instance is `isForce()=false` → **sorted**. Children often have explicit
`sortIndex` values (e.g. 1, 2, 3 in wh40k BSData), so effective sort is **by sortIndex**
with alphabetical tiebreak.

*Verified*: Intercessors group children = Intercessor Sergeant (sortIndex=1),
Intercessor (sortIndex=2), Intercessor w/ Grenade Launcher (sortIndex=3).

### Force → Child Forces (nested)

Force is `isForce()=true` → **no sort**. Force selectors are **pre-created** during
`initializeChilds` from `selectionsIterator()`. Order follows XML definition.
Adding instances to different force types: forces appear in selector order (= XML order),
**not insertion order**. Same-type duplicates: insertion order within the same selector.

*Verified*: Horus Heresy — added Heavy Support (sortIndex=7) first, Primary Det
(sortIndex=1) second, Tactical Support (sortIndex=5) third → `getForces()` returned
Primary Det, Tactical Support, Heavy Support. Adding second instances: appeared adjacent
to first instance of same type.

### Auto-selections (min≥1 constraints)

Follow the same category→alphabetical ordering. May differ from BattleScribe which uses
constraint processing order.

*Example*: wh40k auto-selects Battle Size → Detachment → Show/Hide Options (NR, alphabetical
within Configuration category) vs Detachment → Show/Hide Options → Battle Size (BattleScribe).

## The Algorithm (decompiled pseudocode)

```js
// Cs.initializeChilds — the sole ordering function
initializeChilds(e, t) {
  if (this.childsInitialized) return;
  this.childsInitialized = true;

  // Step 1: Create selectors from source definition (XML order)
  for (const n of this.source.selectionsIterator()) {
    this.selectors.push(new _p(n, this, this.getRoot()));
  }

  // Step 2: Initialize each selector
  for (const sel of this.selectors) {
    if (/* not detached/lazy */) sel.initialize(t);
  }

  // Step 3: Sort or recurse
  if (this.isForce()) {
    // Forces: NO SORT — recurse into children
    for (const sel of this.selectors)
      sel.first()?.initializeChilds(undefined, t);
  } else {
    // Everything else: SORT by sortIndex → isGroup → name
    this.selectors.sort((a, b) => {
      const si_a = a.source.sortIndex ?? 10000;
      const si_b = b.source.sortIndex ?? 10000;
      if (si_a !== si_b) return si_a - si_b;

      const g_a = a.source.isGroup() ? 1 : 0;
      const g_b = b.source.isGroup() ? 1 : 0;
      if (g_a !== g_b) return g_a - g_b;

      return a.source.getName().localeCompare(
        b.source.getName(), undefined, { numeric: true }
      );
    });
  }
}
```

### `getSelections()` and `getForces()` — no additional sorting

Both iterate the pre-built selector tree in existing order:

- `getSelections()` skips force selectors (`this.isForce() && t.source.isForce()`)
- `getForces()` → `getChildInstances()` → iterates selectors in order → instances in order

## Where the alphabetical sort happens

**Not during data loading.** The `br` base class constructor just does
`Object.setPrototypeOf(e, Object.getPrototypeOf(this))` on the raw parsed JSON objects.
`OA.units === OA.childs` — same reference to the raw array.

The sort happens in `initializeChilds` **after** creating `_p` selectors from
`selectionsIterator()`, at tree-building time.

## BattleScribe (BattleScribe) Engine Ordering

BattleScribe's Java engine has **two layers** with different ordering:

### Data Layer (ArrayList — insertion/XML order)

The data model stores all collections in `java.util.ArrayList`:
- `BaseSelectionParent.selections` — `ArrayList<Selection>` (line 19)
- `Roster.forces` / `Force.forces` — `ArrayList<Force>`
- Selections are appended via `ArrayList.add()` — no sorting on add.

the BattleScribe engine adapter reads from this layer directly:
- `roster.getForces()` → insertion order (explicitly chosen over `HashMap.values()`)
- `force.getSelections()` → insertion order
- `sel.getSelections()` → insertion order

### Render Layer (alphabetical sort)

The render layer (`model.render.*`) sorts everything alphabetically for display:

| Class | Method | Sort |
|-------|--------|------|
| `RenderRoster` | `getRenderSelections()` | `Collections.sort(new f())` — case-insensitive alpha |
| `RenderRoster` | `getForces()` | Uses `rosterManager.l()` which flattens+sorts forces alpha |
| `RenderForce` | `getSelections()` | Via `getRenderSelections()` — alpha |
| `RenderForce` | `getCategories()` | Sorted by ForceEntry `categoryLinks` position (comparator `b`) |
| `RenderSelection` | `getSelections()` | Via `getRenderSelections()` — alpha (children too!) |
| `RenderCategory` | `getSelections()` | Via `getRenderSelections()` — alpha |

The comparator `f` (`engine.b.a.f`, implements `Comparator<INamed>`):
```java
public int a(INamed a, INamed b) {
    if (isEmpty(a.getName())) return -1;
    if (isEmpty(b.getName())) return 1;
    return a.getName().compareToIgnoreCase(b.getName());
}
```

Force flattening+sort (`engine.b.f.a(Roster)`):
```java
public static List<Force> a(Roster roster) {
    ArrayList<Force> list = new ArrayList<>(roster.getForces());
    Collections.sort(list, new f());  // alphabetical
    // then recursively flatten child forces (also sorted alpha)
    ...
}
```

### Key insight

**Both engines sort alphabetically in their display layers.** the BattleScribe engine adapter's
"definition order" output is an artifact of reading from the raw data layer, bypassing
BattleScribe's own render-layer sorting.

## Comparison: Both Engines' Display Order

| Aspect | BS Render Layer | NR | Difference |
|--------|----------------|-----|------------|
| Forces | alpha (`compareToIgnoreCase`) | alpha (`localeCompare({numeric:true})`) | Numeric handling¹ |
| Selections (root) | alpha (within category) | alpha (within category) | Same |
| Selections (children) | alpha | `sortIndex → isGroup → name` | sortIndex/isGroup² |
| Categories | ForceEntry `categoryLinks` order | XML order (no sort) | Same source |
| Auto-selections | alpha (render layer) | alpha | Same |

¹ BS: `"Unit 10" < "Unit 2"` (lexicographic). NR: `"Unit 2" < "Unit 10"` (numeric).
For typical spec names without embedded numbers, results are identical.

² NR's 3-tier sort only diverges from pure alphabetical when entries have explicit
`sortIndex` values or `isGroup` differences. Our synthetic spec data rarely sets these,
so the effective sort is usually just alphabetical — same as BS render layer.

## Adapter Workarounds (legacy)

These workarounds exist because the BattleScribe engine adapter reads BS's data layer (insertion order)
while NR returns display-layer order (alphabetical). Since both engines' display layers
sort the same way, these workarounds can be replaced by sorting the BattleScribe engine adapter's
output alphabetically.

| Workaround | Location | Purpose |
|-----------|----------|---------|
| `__bsspec_seq` tagging | `NewRecruitActions.cs` | Track insertion order for root selections |
| `entryOrder` map | `NewRecruitRosterEngine.cs` | Collect catalogue-defined entry order |
| `getSortedSelections()` | `JsHelpers.cs` | Re-sort: seq primary, entryOrder tiebreaker |
| `extractSelections()` dual mode | `JsHelpers.cs` | Root: seq+entryOrder; Children: entryOrder+name |
| `engines.newrecruit` overrides | spec YAML files | Per-engine expected state for ordering diffs |

## NR Internals Reference (minified class names)

| Class | Role |
|-------|------|
| `Vq` | Army/Roster (root) |
| `Cs` | Instance node (selection/force); owns `initializeChilds` |
| `_p` | Selector node; owns `addInstance`, `instances[]` |
| `OA` | Category (`units[] === childs[]`) |
| `dM` | CategoryLink (`selectionsIterator` → `target.units`) |
| `A0` | SelectionEntry base (`selectionsIterator` yields SE→SEG→EL) |
| `d0` | SelectionEntryGroup |
| `Ec` | EntryLink (delegates to target) |
| `tU` | ForceEntry (available force, has `sortIndex`) |
| `br` | Base class; constructor = `Object.setPrototypeOf(e, proto)` |
| `Ka()` | Sort helper: `localeCompare` with `{ numeric: true }` |
