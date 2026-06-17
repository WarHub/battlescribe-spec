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
- The BattleScribe engine never auto-adds forces — `new Roster()` starts empty

## createRoster requires force entries

`createRoster` will throw if **zero** force entries exist in the merged
catalogue tree. The adapter must always provide at least one force entry
in either the game system or catalogue protocol data.

## Publication references are resolved objects

NR resolves `publicationId` XML attributes into object references at catalogue
parse time. All entry types (rules, profiles, selections, categories, forces)
store `.publication` as an object, not `.publicationId` as a string.

**Discovered April 2026 via probing:**

```javascript
const rule = selection.getModifiedRules()[0];
rule.publicationId  // → undefined  (does NOT exist)
rule.publication    // → { id: "pub-core", name: "Core Rulebook", shortName: "CR", catalogue: {...} }
rule.publication.id // → "pub-core"
rule.page           // → 43  (number, NOT string — must stringify)
```

**Uniform pattern for all entry types:**
```javascript
obj.publication?.id || null     // publicationId
obj.publication?.name || null   // publicationName
obj.page != null ? String(obj.page) : null  // page (number → string)
```

**Key points:**
- `.publication` is the resolved reference — `.publicationId` is always `undefined`
- `.page` is a number in NR (BattleScribe XML stores it as a string)
- The publication object has a circular `.catalogue` back-reference — don't
  JSON.stringify it directly
- Vue proxy doesn't affect `.publication?.id` since `.id` is a primitive

## Selection mechanics: addInstance, incrementAmount, autocheck

NR's roster tree uses **selectors** (templates) and **instances** (materialized
selections). These have **completely disjoint** method sets:

**Selector methods:** `addInstance`, `delete`, `getSelections`, `getName`, `getId`
**Instance methods:** `incrementAmount`, `setAmount`, `getAmount`, `delete`,
`getSelections`, `getSelectedEntries`, `getName`, `getId`, `autocheck`, `dupe`

> Full API reference: [docs/nr-dual-tree-api.md](../../../docs/nr-dual-tree-api.md)

### addInstance()

Called on a **selector** to create a new instance (selection). Used for
force-level entry selectors — these have an `addInstance` method and create
a fresh child node each time. After creation, the new instance starts with
`amount=0` and child selectors/instances are not yet fully materialized.

**Category relocation:** When the entry has `categoryLinks`, `addInstance()`
creates the instance under the **correct category selector**, NOT under the
selector `findSelectorById` found (which may be in the `(Illegal Units)` staging
category). Always use before/after uid diff to find the new instance — never
check `selector.instances` directly.

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
  `addInstance()` to match BattleScribe's auto-selection behavior

**Strict adapter pattern:**

```javascript
// Force-level entry selection (adding a new unit)
if (typeof selector.addInstance !== 'function')
    return 'ERROR: not a selector node';
const before = new Set(getSelections(force).map(s => s.uid));
selector.addInstance();
// Find via uid diff — instance may be under a different category selector
const newSel = getSelections(force).find(s => s.uid && !before.has(s.uid));
if (!newSel) return 'ERROR: addInstance did not produce a new selection';
newSel.autocheck();
return newSel.uid;

// Child entry increment (already materialized, amount=0→1)
if (typeof child.incrementAmount !== 'function')
    return 'ERROR: not an instance node';
child.incrementAmount();
child.autocheck();

// Changing count on existing selection (e.g. 1→3)
// ✅ Use setAmount — matches NR UI behavior, proper cost propagation
sel.setAmount({}, 3);
// ❌ DON'T loop addInstance — creates siblings, costs don't aggregate
```

## Selection source pattern — page and publication

Selections don't have `.page` or `.publication` directly. These live on `sel.source`,
which is the backing catalogue entry definition:

```javascript
sel.page                     // → undefined (NEVER works)
sel.publication              // → undefined (NEVER works)
sel.source?.page             // → 42 (number, must stringify)
sel.source?.publication?.id  // → "pub-core"
sel.source?.publication?.name // → "Core Rulebook"
```

**Verified April 2026** by probing all NR frozen HAR snapshots: `sel.page` is
`undefined` in 100% of cases. Page and publication always live on `.source`.

No `__v_raw` unwrapping is needed — Vue proxy doesn't affect primitive property reads.

Forces follow the same pattern:
```javascript
f.source?.page               // → 42
f.source?.publication?.id    // → "pub-1"
f.source?.publication?.name  // → "Core Rules"
```

## Publication scope resolution

NR resolves `publicationId` references **within the same file scope** at parse time.
If a forceEntry in a gameSystem references a publication defined only in a catalogue,
the `.publication` object will be `undefined` (not resolved).

**Verified April 2026:**
```javascript
// gameSystem has forceEntry with publicationId: "pub-1"
// Publication "pub-1" is defined ONLY in catalogue
f.source?.publication  // → undefined ❌ (out of scope)

// Move publication to gameSystem
f.source?.publication?.id  // → "pub-1" ✅
```

**Rule**: Publications must be defined in the same file as entries that reference them.
BattleScribe resolves cross-scope, but NR's behavior is arguably more correct.

## `setAmount()` corruption bugs

**Discovered April 2026.** Calling `setAmount()` on certain entry types permanently
corrupts NR's internal state. Three distinct failure modes:

### 1. Entries with min≥1 constraints

Calling `setAmount(n)` on an entry that was auto-selected due to a `min: 1`
constraint triggers an unrecoverable validation error. Even `setAmount(1)` on
an entry already at count 1 causes corruption:

```javascript
// Entry has min:1, auto-selected to amount=1
sel.setAmount(1);  // ❌ Permanently breaks validation
// Error: "1 or more must be taken" appears and never resolves
```

### 2. Entries with child selections

Calling `setAmount()` on root unit entries that have child selection entries
corrupts conditional modifier evaluation. After the call, modifier conditions
stop being recalculated — conditional name changes, profile modifications, etc.
freeze in their current state:

```javascript
// Root unit has children (model, upgrade selections)
rootUnit.setAmount(2);  // ❌ Conditional modifiers stop updating
// Subsequent selection changes don't trigger modifier re-evaluation
```

### 3. Safe entries (leaf, no constraints)

`setAmount()` on leaf entries WITHOUT children or min constraints is silently
ignored — no corruption, but also no effect:

```javascript
leafEntry.setAmount(5);  // No error, no effect, no corruption
```

**Impact**: Specs must avoid `setSelectionCount` on entries with children or
min constraints. Use `selectEntry`/`selectChildEntry` (which use
`addInstance`/`incrementAmount`) instead.

## Hidden cost types in `calcTotalCosts()`

**Discovered April 2026.** NR's `army.calcTotalCosts()` method omits cost types
that have `hidden: true`. This causes roster-level cost assertions to fail when
hidden cost types are expected in totals.

```javascript
// Game system has two cost types: "pts" (visible) and "PL" (hidden: true)
army.calcTotalCosts()  // → only includes "pts", omits "PL"
```

**Fix**: Use uniform manual summation from individual selections for ALL cost types:
```javascript
function sumNode(node, result) {
    for (const sel of (node.getSelections?.() || [])) {
        const amount = sel.getAmount?.() ?? 0;
        if (amount <= 0) continue;
        for (const c of (sel.getCosts?.() || [])) {
            result[c.typeId] = (result[c.typeId] || 0) + c.value * amount;
        }
        sumNode(sel, result);
    }
}
```

This includes ALL cost types regardless of visibility.

## Hidden cost type VALIDATION (defaultCostLimit)

**Discovered April 2026.** NR's `createRoster(costs)` uses `costs[].value` (always 0)
as the limit, NOT `defaultCostLimit`. You must explicitly apply limits after creation:

```javascript
const maxCosts = roster.getMaxCosts();  // includes defaultCostLimit metadata
const corrected = maxCosts.map(c => ({
    ...c,
    value: c.defaultCostLimit >= 0 ? c.defaultCostLimit : -1
}));
roster.setMaxCosts(corrected);
```

After this, `checkConstraints()` correctly produces errors for hidden cost types:
- Error: `{msg: "Roster has 1 CP too many (max 1)", constraint: {field: "cp", type: "max", id: "max::cp::CP"}}`
- `constraint.field` = the cost type ID → maps to `costLimits/{field}`

## NR `.page` is a number

BattleScribe XML stores page as a string attribute (`page="42"`), but NR parses
it to a JavaScript number during catalogue loading. The adapter must stringify:

```javascript
obj.page           // → 42 (number)
typeof obj.page    // → "number"
String(obj.page)   // → "42" (what BattleScribe expects)

// Safe pattern:
obj.page != null ? String(obj.page) : null
```

## `setAmount()` vs `addInstance()` — Selection Count Mechanics

**Discovered April 2026 via live Playwright UI method tracing.**

Two fundamentally different operations for changing selection counts:

### `setAmount(ctx, n)` — Counter Mutation (correct for count changes)

Sets the `amount` property on an **existing** node. Used by NR's UI spinbutton.
Triggers full cost propagation via scope queue.

**Signature**: Two args required — `ctx` = tracker context, `n` = new amount.

```javascript
// ✅ Correct — NR UI passes {} as context
node.setAmount({}, 5);

// ❌ WRONG — sets ctx=5, n=undefined → silent corruption
node.setAmount(5);
```

Deobfuscated `amount` setter:
```javascript
set amount(e) { e && this.enable(e); e || this.disable(0); }
```

**Internal chain** (323 method calls traced for Trooper 3→4):
1. Guard checks: `isConstantRecursive`, `isHidden`, `isConstant`
2. `setLastChecked("add", tracker)` — mark as being modified
3. `enable(4)` → `state.setSelections(4)` → `scope.updateMultipliers(4, 4)`
4. `onTotalCostChanged("pts")` — notify cost system
5. `scope.getQueue().empty()` — drain priority queue (parent listeners fire)
6. `eachParent` → `Squad.onChildAmountChanged()`
7. `applyModifications(tracker)` → `checkConstraints` → `fixReactivity`
8. `calcTotalCosts` (5 passes — NOT a loop, see below)
9. `toJsonObject` → `saveListLocally` → `doSaveList`

### `addInstance()` — Node Duplication (correct for adding new entries)

Creates a **new sibling node** with `amount=0`. Used by NR UI for:
- "Duplicate Unit" button: `node.dupe()` → `selector.addInstance()` + copy
- "Create Unit" (+) button: `force.insertUnit()` → `selector.addInstance()`
- `addSubUnit()`, `splitDown()`, `splitUp()` — structural operations

**Not used by NR UI** for changing child counts.

### When to use which

| Scenario | Correct API | Wrong API |
|----------|-------------|-----------|
| Change child count (3→5) | `setAmount({}, 5)` | ~~`addInstance()` × 2~~ |
| Add second Squad to force | `selector.addInstance()` | ~~`setAmount({}, 2)`~~ |
| Duplicate a unit | `selector.addInstance()` + copy | ~~`setAmount`~~ |

## Cost-Field Repeat Divergence (Infinite Loop Bug)

**Discovered May 2026.** When two entries have modifier repeats counting each
other's cost (mutual cross-reference), both engines fail to detect the loop:

- **NR**: Diverges to `Infinity` within a single operation (reactive fixed-point
  iteration with no recursion guard). Causes `JsonException` when serializing.
- **BS**: Costs escalate unboundedly across roster mutations (each add/remove
  triggers re-evaluation with inflated costs from previous passes). No error
  is reported.

### Reproduction

Two selection entries Alpha and Beta, each with a modifier repeat:
- Alpha: for each 10 pts of Beta → increment Alpha's cost by 50
- Beta: for each 10 pts of Alpha → increment Beta's cost by 50

Both start at base cost 100.

**NR behavior** (single operation):
```
Iteration 1: Beta sees Alpha=100 → 10 reps → 600
             Alpha sees Beta=600 → 60 reps → 3100
Iteration 2: Beta sees Alpha=3100 → 310 reps → 15600
             Alpha sees Beta=15600 → ...
→ Both costs diverge to Infinity
```

**BS behavior** (escalation across mutations):
```
+Alpha       → Alpha=100
+Beta        → Alpha=100, Beta=600         (total: 700)
+Beta#2      → Alpha=3100, Betas=15600     (total: 34,300)
+Beta#3      → Alpha=156100, Betas=780600  (total: 2,497,900)
```

Each roster mutation amplifies costs further. No upper bound.

### Impact

Neither engine has a guard against unbounded cost escalation from mutual
cost-field repeat references. Engines must terminate safely in all cases —
unbounded loops/escalation are never acceptable. Both should detect the
cycle and report a validation error instead of applying the looping modifiers.

### Related

- Spec: `modifier/modifier-repeat-cost-mutual-reference` (tagged
  `newrecruit-bug` + `battlescribe-bug`, skipped for NR, BS overrides
  show escalating values)
- Self-referencing repeats (single entry type counting its own cost) do converge
  in NR — see `modifier/modifier-repeat-cost-self-reference` for the fixed-point
  values vs BS's single-pass results.

## Custom Name & Notes — Premium Feature

NR supports `customName` and `note` on instance nodes. These are premium
(supporter-only) features. See [docs/nr-custom-name-notes.md](../../../docs/nr-custom-name-notes.md)
for the full investigation.

**Key facts:**
- NR uses `.note`, NOT `.customNotes` — the adapter maps `note` → `customNotes`
- Default value is `undefined` (not null, not "")
- `getName()` returns the definition name, NOT the custom name
- UI renders as "CustomName - OriginalName"
- Selection notes visible in expanded detail panel; force notes NOT visible in UI

**Paywall bypass** for testing:
```javascript
const userStore = pinia._s.get('userStore');
userStore.user = { supporter: true, name: 'Test', _id: 'fake' };
userStore.isSupporter(); // → true — paywall bypassed
```

## Cost Recalculation Cascade — Not a Loop

When `setAmount` changes a count, `calcTotalCosts()` is called 5+ times.
This is **not** a convergence loop — it's three independent phases:

### Phase 1: Scope Propagation (synchronous, bottom-up)

```
enable(n) → state.setSelections(n)
  → scope.updateMultipliers(n, n)     // cost deltas propagated UP
  → onTotalCostChanged("pts")         // fires on child
  → scope.getQueue().empty()          // drains priority queue
    → parent.state.onTotalCostChanged // queue listener
    → grandparent.state.onTotalCostChanged
```

### Phase 2: Vue Re-rendering (async, reads dirty computed properties)

Vue computed properties (`totalCost`, `cost`, `displayedCost`) are dirtied
by `vueCostsKey++`. Each UI component that displays costs reads
`calcTotalCosts()` independently.

### Phase 3: Auto-save

`doSaveList` calls `getPointsCost()` → reads `calcTotalCosts()`.

### Stop guarantee (structural, not convergence)

```javascript
// Queue mechanism — deduplication via Map
empty() {
  for (; this.highest_priority >= 0;) {
    const e = this.queues[this.highest_priority];
    if (e && e.size > 0) {
      for (const [callback, args] of e) {
        e.delete(callback);
        callback(...args);
        if (this.highest_priority > t) continue outer; // restart if higher-prio appeared
      }
    } else this.highest_priority--;
  }
}
```

- **Upward-only**: parent chain walk, never back down
- **Queue dedup**: `Map<callback, args>` — same listener queued once
- **Finite depth**: O(tree depth) steps
- **autocheck guard**: `!state.autochecked` prevents re-entry
