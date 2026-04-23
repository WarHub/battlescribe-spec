# New Recruit vs BattleScribe: Behavioral Differences Report

> Based on conformance testing against [newrecruit.eu](https://newrecruit.eu)
> using the battlescribe-spec test suite.
>
> See also: [NR Ordering Analysis](nr-ordering-analysis.md) for a deep-dive into
> NR's native selection and force ordering algorithm.


| Category | Count | Severity | Description |
|----------|-------|----------|-------------|
| [Import ordering](#1-import-ordering) | 3 | Low | NR puts imported entries before faction entries |
| [Missing features](#2-missing-features) | 4 | Low | InfoLink pub/page override, page modifier, unset-primary |
| [Scope/condition evaluation](#3-scopecondition-evaluation) | 4 | Medium | NR evaluates child-force scope, ancestor scope, and null-childId conditions differently |
| [instanceOf scope limits](#instanceof-scope-limitations-both-engines) | 12 | Info | instanceOf only works with self/parent/ancestor scope — both engines agree |
| [Entry group behavior](#4-entry-group-behavior) | 2 | Low | Child ordering, category link propagation |
| [Other behavioral differences](#5-other-behavioral-differences) | 4 | Medium | Auto-select root entries, hidden selection filtering, forces-field, real-world data |

---

## 1. Import Ordering

**3 specs** — NR orders imported entries from CatalogueLinks BEFORE
faction-specific entries. BattleScribe puts faction entries first.

| Spec | Expected first selection | NR returns first |
|------|-------------------------|-----------------|
| `selection/catalogue-link-import` | Faction Unit | Common Unit |
| `selection/import-false-entry-hidden-via-link` | Faction Unit | Common Unit |
| `selection/import-true-entry-visible-via-link` | Squad | Veteran Squad |

**Impact**: Low — cosmetic ordering difference. Data is correct.

---

## 2. Missing Features

**4 specs** — NR doesn't implement or expose certain BattleScribe features.

### InfoLink publication/page override behavior (2 specs)

| Spec | Feature | Detail |
|------|---------|--------|
| `selection/infolink-publication-override` | InfoLink publication non-override | BattleScribe preserves target's `publicationId`; NR uses the infoLink's own publication instead |
| `selection/infolink-page-override` | InfoLink page non-override | BattleScribe preserves target's `page`; NR uses the infoLink's own page instead |

**Root cause**: NR resolves InfoLink publication/page from the link itself, not
the linked target. BattleScribe preserves the target entry's values. This is a
genuine behavioral difference in link resolution semantics.

### Page modifier not applied (1 spec)

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-entry-page` | Page modifier | NR doesn't apply `type: set, field: page` modifiers to selections |

### Unset-primary modifier (1 spec)

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-category-unset-primary` | Unset-primary modifier | NR ignores the `unset-primary` category modifier |

### Previously missing, now resolved

The following features were previously listed as missing but are now working
after discovering NR's publication object model (April 2026):

- **Selection publication/page** — NR stores these on `sel.source` (not `sel`
  directly). Reading `sel.source?.publication?.id` and `String(sel.source?.page)`
  now returns correct values.
- **Rule/profile publication** — NR resolves `publicationId` into a
  `.publication` object. Using `rule.publication?.id` works correctly.
- **GameSystem-level publication** — Resolved when publication is defined in the
  same scope as the entry. See [Publication Scope Resolution](#publication-scope-resolution).
- **Force publication/page** — Accessed via `f.source?.publication?.id` and
  `String(f.source?.page)`.

---

## 3. Scope/Condition Evaluation

**3 specs** — NR evaluates certain condition types differently, causing
modifiers to trigger when they shouldn't (or vice versa).

| Spec | Issue |
|------|-------|
| `scope/scope-include-child-forces` | Condition with `scope=force, childForces=true` triggers when it shouldn't |
| `scope/scope-include-child-forces-nested` | Same issue in nested force scenario |
| `scope/scope-ancestor` | Ancestor scope modifier fires in NR but not in BattleScribe |
| `condition/condition-null-childid` | Missing childId: NR counts all selections (condition fires), BS returns NaN (condition false) |

These specs test complex condition evaluation where NR's implementation
diverges from BattleScribe's. For scope specs, the modifier fires (changing the
selection name), proving the condition evaluates to true in NR but false in BS.
For the null-childId spec, BattleScribe's resolver returns null when childId is
absent, causing the query to return NaN and the condition to evaluate as false.
NR defaults missing childId based on node type: forces/groups use `"any"` (count
everything), other nodes use `"self"` (count self). See [NR Condition Engine](#nr-condition-engine-internals)
discovery section for the decompiled code analysis.

Two companion specs (`condition-null-childid-parent-scope`, `condition-null-childid-force-threshold`)
test NR's alternative defaults for missing childId on different scopes (parent → "self",
force with threshold → "any"). All three null-childid specs use per-engine `expectedState`
overrides to describe both engines' behavior.

### instanceOf Scope Limitations (both engines)

**12 specs** — `instanceOf`/`notInstanceOf` condition evaluation is limited to
specific scope values. This is a BattleScribe engine design limitation that
both engines share (NOT an NR-specific or synthetic data issue).

| Scope | Works? | Reason |
|-------|:------:|--------|
| `self` | ✅ | Resolves to current Selection |
| `parent` | ✅ | Resolves to parent Selection |
| `ancestor` | ✅ | Walks parent chain (all Selections) |
| `force` | ❌ | Resolves to Force (not a Selection) — c.java:1206-1210 |
| `roster` | ❌ | Hardcoded `return false` — c.java:1196-1197 |

Working childId types for instanceOf (with self/parent/ancestor scope):

| childId type | Works? | Example spec |
|--------------|:------:|--------------|
| SelectionEntry ID | ✅ | condition-instance-of-self |
| Type name (unit/model) | ✅ | condition-instance-of-self-type |
| CategoryEntry ID | ✅ | condition-instance-of-self-category |
| ForceEntry ID | ❌ | condition-instance-of-force-entry |
| Catalogue ID | ❌ | condition-instance-of-catalogue |

Specs tagged `undefined-behavior` document scope+childId combinations that
don't work on either engine. Each references its working counterpart.

---

## 4. Entry Group Behavior

**2 specs** — NR handles entry groups differently from BattleScribe.

### Child Ordering in Collective Groups
| Spec | Issue |
|------|-------|
| `entry-group/entry-group-collective` | NR sorts children alphabetically within collective groups |

When a `SelectionEntryGroup` has `collective=true`, its child selections should
appear in **catalogue definition order**. NR instead sorts them alphabetically
by name (e.g., "Axe" before "Sword" regardless of XML order).

### Category Link Propagation
| Spec | Issue |
|------|-------|
| `entry-group/entry-group-with-category-links` | NR doesn't propagate category links from entry groups to child selections |

When a `SelectionEntryGroup` has `categoryLinks`, the child selections within
that group should inherit those category assignments. NR ignores category links
on entry groups, so child selections don't appear under the expected categories.

---

## 5. Other Behavioral Differences

**4 specs** with distinct NR behavioral differences:

### Auto-Select with `field=forces` Constraint
| Spec | Issue |
|------|-------|
| `constraint/constraint-forces-field` | NR auto-selects entry whose only min constraint has `field=forces`; BS doesn't |

After `addForce`, spec expects 0 selections but NR has 1 (auto-selected entry
with `type=model, min=1, field=forces`). BattleScribe's auto-select mechanism
(`getDefaultAmount`) only considers `field=selections` constraints. A `field=forces`
constraint counts forces, not selections, so it doesn't trigger auto-selection.
NR doesn't distinguish `field` types and auto-selects based on any `min>=1`.

Note: BattleScribe _does_ auto-select root entries that have `min>=1` with
`field=selections` — see `constraint-hidden-enforcement` and real-world specs.

### Hidden Selection Filtering
| Spec | Issue |
|------|-------|
| `constraint/constraint-hidden-enforcement` | NR filters hidden selections entirely from the tree |

BattleScribe keeps hidden selections in the tree (visible to assertions) but
marks them hidden. NR removes them completely — `selectionCount` is 0 instead
of 1 for a hidden auto-selected entry.

### Real-World Data Source
| Spec | Issue |
|------|-------|
| `real-world/wh40k-10e-space-marines-army` | NR produces different auto-selections and cost calculations for complex multi-catalogue armies |

This real-world spec builds a Space Marines army and verifies auto-selections,
unit types, and points costs. NR's results differ from BattleScribe when
dealing with multi-catalogue data interactions and complex entry resolution
chains in production game systems.

Note: `real-world/wh40k-10e-create-army` previously failed but now passes on NR.

### Auto-Select with `field=forces` Skipped
| Spec | Issue |
|------|-------|
| `auto-select/auto-select-field-forces-skipped` | NR auto-selects entries with `field=forces` constraints; BS skips them |

BattleScribe's auto-select only triggers for `field=selections` constraints.
An entry with `min=1, field=forces` is NOT auto-selected. NR auto-selects
based on any `min>=1` regardless of field type.

### Selection Number with Min
| Spec | Issue |
|------|-------|
| `selection/selection-number-with-min` | NR returns different number/amount for min-constrained selections |

### setSelectionCount on Child Entries — Fixed

> **Previously**: The adapter used `addInstance()` in a loop, creating N separate
> instances instead of one node with number=N. This was an adapter bug, not an NR
> engine limitation.
>
> **Now fixed**: The adapter uses NR's native `sel.setAmount({}, count)` which
> correctly sets `amount=N` on a single node with proper cost propagation.
> Both engines now produce identical results for child count changes.

| Spec | Status |
|------|--------|
| `selection/selection-set-child-count-instance-model` | ✅ Both engines agree |
| `selection/selection-set-child-count-collective` | ✅ Both engines agree |

**Protocol validation**: `setSelectionCount` now rejects root selections (target must be
a child selection). Root selection count is managed via
`selectEntry`/`deselectSelection`. A lint rule (`SetSelectionCountTargetsChildOnly`)
enforces this in specs.

---

## Discoveries

Technical findings from reverse-engineering NR's internal API and comparing
with BattleScribe's decompiled Java engine.

### Publication and Page Field Resolution

BattleScribe stores `publicationId` and `page` on virtually every catalogue node
(except CostType and ProfileType). These fields are resolved during roster creation:

**Selection-level**: The selection inherits `publicationId` from its source entry
(`BaseSelectable.setPublicationId(baseEntry.getPublicationId())`). Both `page` and
`publicationId` are raw IDs/strings, not resolved names.

**Profile/Rule-level**: Profiles and rules inherit `publicationId` from their
definition via `BaseBookData.getPublicationId()`. The page field is also preserved.

**InfoLink behavior**: InfoLink `publicationId` and `page` do **NOT** override the
linked target's values. When an InfoLink references a shared rule with
`publicationId: pub-core` and the InfoLink itself has `publicationId: pub-faq`,
the resulting rule on the selection has `publicationId: pub-core` (the target's
value, not the link's).

**NR behavior** (updated April 2026 — major discovery):
- NR resolves `publicationId` XML attributes into `.publication` object references
  at catalogue parse time. The raw `.publicationId` property is always `undefined`.
- **Selections**: Page and publication live on `sel.source` (not `sel` directly).
  Access via `sel.source?.publication?.id` and `String(sel.source?.page)`.
- **Profiles**: `profile.publication?.id` and `String(profile.page)` work directly.
- **Rules**: `rule.publication?.id` and `String(rule.page)` work directly.
- **Forces**: `f.source?.publication?.id` and `String(f.source?.page)`.
- **Categories**: `cat.publication?.id` works directly.
- **InfoLinks**: NR uses the infoLink's own publication, NOT the target's (differs
  from BattleScribe). This is the remaining behavioral difference.
- **Page type**: NR stores page as a number (BattleScribe XML stores it as a
  string). Must stringify: `obj.page != null ? String(obj.page) : null`.

### Publication Scope Resolution

NR resolves `publicationId` references **within the same scope** at parse time.
A forceEntry in the gameSystem referencing a publication defined only in a
catalogue will NOT resolve — the `.publication` object will be `undefined`.
BattleScribe resolves cross-scope publication references.

**Rule**: Define publications in the same file (gameSystem or catalogue) as the
entries that reference them. A forceEntry in a gameSystem must reference a
publication also defined in that gameSystem.

### NR `setAmount()` — Signature Gotcha

> **Previously documented as corruption bugs**: The issues below were discovered
> using `setAmount(n)` with one arg, which silently corrupts state (`ctx=n,
> n=undefined`). With the correct two-arg form `setAmount({}, n)`, NR's UI
> uses this on all entry types without issues. The "corruption" was caused by
> the wrong calling convention, not by setAmount itself.

**Two args required**: `setAmount(ctx, n)` where `ctx` = checker context (pass `{}`).
`setAmount(5)` with one arg sets `ctx=5, n=undefined` → silent corruption.

**Protocol validation**: `setSelectionCount` now rejects root selections
(target must be a child selection). Root selection lifecycle is managed via
`selectEntry`/`deselectSelection` only.

### NR Hidden Cost Types

NR's `army.calcTotalCosts()` method omits hidden cost types from its results.
The adapter uses **uniform manual summation** for all cost types (hidden and
visible alike), walking the selection tree and multiplying `getCosts()` by
`getAmount()` per selection. This is simpler and produces correct totals for
all types regardless of visibility.

NR's `createRoster(costs)` sets cost limits to 0 (from `costs[].value`, which
is the starting total, not the limit). The adapter explicitly applies
`defaultCostLimit` via `setMaxCosts()` after roster creation so that NR's
native `checkConstraints()` correctly validates limits for both visible and
hidden cost types.

### NR Selection Ordering

NR sorts selections **alphabetically by name** within each category. BattleScribe
uses **insertion order** — selections appear in the order the user added them.

The adapter tracks insertion order by tagging new selections with a monotonically
increasing `__bsspec_seq` sequence number on the raw Vue object. The state reader
sorts by this tag, with auto-selected entries (untagged) sorting first in
catalogue definition order.

Child selections always sort by **catalogue definition order** (entryOrder) since
they're part of the entry definition, not user-ordered.

### NR Selection Model: `incrementAmount()` vs `addInstance()`

NR pre-creates **selector nodes** with `amount=0` for all child entries when a
parent is selected. These are placeholder objects representing available entries.

- **`addInstance()`** on a selector template creates a NEW node with `amount=0`
  (broken — produces duplicates, costs not aggregated)
- **`incrementAmount()`** on an existing child node sets amount from 0 to 1
  (correct — costs properly included in `calcTotalCosts()`)

This discovery resolved the **child cost aggregation** issue (8 specs fixed).

### BattleScribe Auto-Select Mechanism

Decompiled from `engine.a.f` (BattleScribe Java engine):

- Private method `x()` ("Select default root entries") at line 978
- Called during `setRoster(bl=true)` when creating a new roster
- Iterates all forces, auto-selects entries where `getDefaultAmount >= 1`
- `getDefaultAmount` returns the entry's `min` constraint value

the BattleScribe engine adapter replicates this via reflection: `_autoSelectMethod.Invoke()`.

### NR Error Extraction

NR validation errors are extracted by calling `checkConstraints()` on each
roster node, then reading the node's error arrays. Key findings:

- `checkConstraints()` must be called explicitly per node
- Can crash with undefined reference errors — wrapped in try-catch
- Errors on army node are cost limit violations
- Error structure: `{message, constraintId?, ownerType, ownerEntryId}`
- ConstraintId format: NR now maps cost limit errors to the `costLimits/`
  pseudo-entry convention (matching BattleScribe's format)
- Max constraint errors go on the selection (both BS BattleScribe adapter and NR now agree)

### Catalogue Expansion and Entry Links

BattleScribe resolves entry links during force creation:

1. Entry link references a shared selection entry
2. Engine copies the shared entry and merges the link's properties
3. Expanded copy gets composite ID: `linkId::sharedEntryId`
4. Registered as a regular (non-shared) selection entry
5. Both the shared entry's constraints and the link's constraints are evaluated

Key findings:
- `scope=parent` on entry link constraints refers to the catalogue root, not
  the force — use `scope=force` or `scope=roster` instead
- `shared=true` counting works across multiple entry links to the same shared
  target — the engine counts by `sharedEntryId`

### NR Pinia Store Access

Access NR's internal stores via:
```javascript
document.querySelector('#__nuxt')?.__vue_app__
  ?.config?.globalProperties?.$pinia._s.get('storeName')
```

Key stores: `lists`, `listsPage`, `systemsStore`, `gameStore`.

Roster access: `lists.getCurrentList()` returns `{row, army, book}`.

### NR Condition Engine Internals

Analysis of NR's minified JS bundles (`rfaH3HIo.js`) reveals the condition
evaluation chain for missing `childId`:

**Evaluation chain**: `Ty()` → `pR()` → `sj()` → `state.eval()`

```javascript
// state.eval — key method on roster node state
eval(e, t) {
    // ...
    // When node isGroup() and childId missing → defaults to "any"
    this.isGroup() && !e.childId
        ? n = this.hash({field: e.field, childId: "any"})
        : n = this.hash(e);
    return this.do_get(n) || 0;
}

// hash — builds lookup key, defaults childId
hash(e) {
    return `${prefix}::${field}::${e.childId || (this.isForce() ? "any" : "self")}`;
}
```

**Default childId by node type**:
| Node type | Missing childId defaults to | Effect |
|-----------|----------------------------|--------|
| Group (`isGroup()`) | `"any"` (in eval) | Counts all children |
| Force (`isForce()`) | `"any"` (in hash) | Counts all selections |
| Other (selection) | `"self"` (in hash) | Counts self |

**Comparison operator** (`Zl` function):
```javascript
case "atLeast": return scope === "self" && count === 0 ? false : count >= value;
```

NR has a special case: `atLeast` with `scope=self` and `count=0` returns `false`
regardless of value.

**BattleScribe comparison**: BattleScribe's `BaseFilteredQuery` (decompiled Java)
resolves `childId` via `h.d(string)` → returns `null` for empty/missing →
query returns `Double.NaN` → any comparison with NaN returns `false`. This means
BattleScribe silently ignores conditions with missing childId (always false).

### NR Data Loading

Three methods for loading game data:
1. **`sysStore.loadSystemFromFs(files)`** — accepts `[{name, path, data}]`
   array with XML strings (used for spec tests)
2. **`addGithubSystem()`** — downloads from BSData GitHub repos
3. **Mock `showDirectoryPicker()`** — intercepts folder upload UI

### `setAmount()` vs `addInstance()` Deep Dive

**Discovered April 2026 via live Playwright UI replay and method tracing.**

These are two fundamentally different operations in NR's selection tree:

| | `node.setAmount(ctx, n)` | `selector.addInstance()` |
|---|---|---|
| **What it does** | Changes `amount` property on an **existing** node | Creates a **new sibling node** (amount=0) |
| **Tree effect** | No structural change (property mutation) | Structural change (new node) |
| **Cost recalculation** | Full scope propagation via queue (correct) | New node doesn't trigger parent cost update (stale) |
| **Used by NR UI** | ✅ Spinbutton count changes | ✅ "Duplicate Unit", "Create Unit (+)" |

#### `setAmount(ctx, n)` — Signature Gotcha

**Two args required**: `ctx` = checker context object, `n` = new amount value.

```javascript
// ✅ Correct — NR UI passes {} as context
node.setAmount({}, 5);

// ❌ WRONG — sets ctx=5, n=undefined → amount becomes undefined (silent corruption)
node.setAmount(5);
```

The NR UI spinbutton calls `this.opt.setAmount({}, newValue)` where `this.opt`
is the tree node. The `{}` is an empty tracker context (normally contains
`{currentDepth, errorStack, warningStack, modStack, autofixUnit}`).

#### `setAmount` internal call chain (traced via method proxies)

When amount changes from 3→4 on a child "Trooper" node (323 total method calls):

```
DOM input/change event
  → setAmount({}, 4)
    → guard checks: isConstantRecursive, isHidden, isConstant
    → setLastChecked("add", tracker)
    → enable(4)                          // sets internal amount
      → state.setSelections(4)
      → scope.updateMultipliers(4, 4)    // propagates cost deltas UP
      → onTotalCostChanged("pts")        // notify cost system
      → scope.getQueue().empty()         // drain priority queue
        → Squad.state.onTotalCostChanged // parent listener fires
        → Force.state.onTotalCostChanged // grandparent fires
    → eachParent → Squad.onChildAmountChanged()
    → refreshErrors
    → initializeChilds
    → unsetLastChecked
  → Squad.applyModifications(tracker)
    → checkConstraints (×3 nodes)
    → fixReactivity (×6 calls)           // trigger Vue computed updates
    → updateDisplayStatus
    → calcTotalUnitSize
    → calcTotalCosts (×5 passes)         // NOT a loop — see below
    → autocheck
    → toJsonObject                       // serialize for save
  → lists.saveListLocally()              // persist to IndexedDB
  → lists.doSaveList()                   // queue server save
```

#### Why `calcTotalCosts` is called 5+ times (NOT a loop)

Three distinct phases, each reading the (already-updated) cost data:

1. **Scope propagation** (synchronous, bottom-up): `onTotalCostChanged` fires
   from child → parent → root via priority queue. Queue uses `Map<callback, args>`
   for deduplication — same listener can only be queued once.

2. **Vue re-rendering** (async): Vue computed properties (`totalCost`, `cost`,
   `displayedCost`) are dirtied by `vueCostsKey++`. Each UI location that
   displays costs triggers a `calcTotalCosts()` read.

3. **Auto-save**: `doSaveList` calls `getPointsCost()` which reads
   `calcTotalCosts()`.

**Stop guarantee** is structural (not convergence-based):
- Upward-only propagation: parent → parent → root (never back down)
- Queue deduplication: `Map<callback, args>` — re-enqueue replaces, doesn't accumulate
- Finite tree depth: O(depth) steps guaranteed
- `autocheck` guard: `!state.autochecked` ensures single execution per node

#### Where NR uses each method

| NR UI action | API called | Effect |
|-------------|-----------|--------|
| Spinbutton count change (±) | `node.setAmount({}, n)` | Mutates amount on existing node |
| "Duplicate Unit" button | `selector.addInstance()` + copy state | Creates new sibling |
| "Create Unit" (+) in catalogue panel | `force.insertUnit()` → `selector.addInstance()` | New selection node |
| `addSubUnit()`, `splitDown/Up` | `selector.addInstance()` | Structural operations |
| Mobile +/- buttons | `incrementAmount({})` / `decrementAmount({})` | Alternative path (0 call sites in bundle) |

#### Adapter fix (applied)

`NewRecruitActions.cs` `SetSelectionCountAsync` now uses `setAmount({}, count)`:
```javascript
sel.setAmount({}, count);
```

This produces the correct single-node-with-count behavior matching both
NR's own UI and BattleScribe's behavior. The old `addInstance()` loop
approach has been removed.

---

## Architecture Notes

### How NR Is Tested

The NR adapter uses **Playwright** to drive a headless Chromium browser loading
`newrecruit.eu`. Instead of UI interaction, it directly calls NR's internal
**Pinia store API** via JavaScript evaluation:

- **Data loading**: `loadSystemFromFs(files)` — injects BattleScribe XML
  (either synthetic from specs or real from DataSource repos like wh40k-10e)
- **Actions**: Direct Pinia store method calls (`insertForce`, `addInstance`,
  `incrementAmount`, `delete`, `setAmount`)
- **State reading**: `getCurrentList().army` tree traversal using NR's reactive
  object API (`getForces`, `getSelections`, `getName`, `getCosts`, etc.)
- **Validation**: Error extraction via `checkConstraints()` per node

### Test Infrastructure

- **Browser lifecycle**: `NewRecruitFixture` (xUnit collection fixture) shares
  one Playwright browser across all NR tests, which run serially
- **Live testing**: NR tests only run when `NR_ENGINE_URL` environment variable
  is set (on-demand via `workflow_dispatch` or `[nr-test]` commit message)
- **Frozen testing**: `FrozenNewRecruitFixture` loads HAR recordings from
  [WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har) for fully
  offline, deterministic replay via Playwright's `RouteFromHARAsync`
- **Expected failures**: Encoded directly in spec YAML files via the `engines`
  field (map of engine name → expectation: `pass`, `fail`, or `skip`).
  If a spec is expected to fail and does fail, the test passes. If an expected
  failure suddenly passes, the test FAILS (detecting behavior changes).
  Most specs now use **per-engine `expectedState` overrides** instead of
  `engines: {engineName: fail}` — the override describes the actual engine
  behavior, keeping both engines passing. Only 1 real-world spec still uses
  `newrecruit: fail` due to fundamental data incompatibilities.
- **BattleScribe**: All specs expected to pass except 2 NR-specific
  null-childId condition behavior specs. DataSource specs (real-world wh40k-10e)
  are fully supported via IKVM engine with DataUtils XML loading.

### Resolved Issues

| Issue | Fix | Specs Fixed |
|-------|-----|-------------|
| Error placement mismatch | BS BattleScribe adapter remaps category-level max/cost/hidden errors to selection-level (matching NR) | 4 |
| Selection ordering | Insertion-order tracking via `__bsspec_seq` tags | ~15 |
| Action/state index mismatch | `getSortedSelections()` helper for all action methods | 3 |
| Child cost aggregation | `incrementAmount()` instead of `addInstance()` | 8 |
| Error extraction | Tree-walking `checkConstraints()` with structured error parsing | ~10 |
| Entry link resolution | BattleScribe queries `_engine.e(force).R()` for expanded entries | 4 |
| Auto-select replication | BattleScribe adapter calls `x()` via reflection | ~15 |
| GameSystem entry resolution | `SelectEntry` now includes GS-level SelectionEntries and EntryLinks | 2 |
| SelectChildEntry flattening | `FlattenChildEntries` resolves EntryLinks and nested SelectionEntryGroups | 6 |
| FindEntryById scope | `FindEntryById` now searches GameSystem entries in addition to catalogue | 2 |
| Force-catalogue map state leak | `_forceCatalogueMap.Clear()` in Setup prevents cross-test contamination | 1 |
| NR cost limit false positives | Parse NR error messages + compare vs configured `defaultCostLimit` from spec | ~65 |
| NR generic hidden errors | Suppress "cannot be selected while hidden" without `constraint.id` | 4 |
| Publication field extraction | Use `.publication?.id` object pattern instead of `.publicationId` string | 7 |
| Selection page/pub on source | Read from `sel.source?.page` / `sel.source?.publication` instead of `sel` directly | 4 |
| Force page/pub on source | Read from `f.source?.page` / `f.source?.publication` | 1 |
| Hidden cost types omitted | Always use manual summation instead of `calcTotalCosts()` | 1 |
| setAmount corrupts NR state | Remove `setSelectionCount` on entries with children/min constraints | 1 |
| Publication scope in forceEntry | Move publication to gameSystem (same scope as forceEntry) | 1 |
