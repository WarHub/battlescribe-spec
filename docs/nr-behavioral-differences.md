# New Recruit vs BattleScribe: Behavioral Differences Report

> Based on conformance testing against [newrecruit.eu](https://newrecruit.eu)
> using the battlescribe-spec test suite.
>
> Last validated: 2026-03-07

## Summary

| Metric | BattleScribe | New Recruit |
|--------|-------------|-------------|
| Total specs | 246 | 246 |
| Expected to pass | 244 | 220 |
| Expected to fail | 2 | 26 |

**BattleScribe expected failures** (2): NR-specific condition specs where BS
returns NaN for null childId (`condition-null-childid-nr-force`,
`condition-null-childid-nr-self`).

Expected failures are encoded in each spec's `engines` YAML field:
```yaml
engines:
  newrecruit: fail    # expected to fail on NR
  battlescribe: fail  # expected to fail on BS
  # unlisted engines default to "pass"
```

### NR Failure Breakdown

| Category | Count | Severity | Description |
|----------|-------|----------|-------------|
| [Error placement](#1-error-placement) | 4 | Medium | NR places constraint errors on selection, not category |
| [Import ordering](#2-import-ordering) | 3 | Low | NR puts imported entries before faction entries |
| [Missing features](#3-missing-features) | 11 | Low | Page numbers, publicationId on selections/rules/profiles, unset-primary |
| [Scope/condition evaluation](#4-scopecondition-evaluation) | 3 | Medium | NR evaluates child-force scope and null-childId conditions differently |
| [Entry group behavior](#5-entry-group-behavior) | 2 | Low | Child ordering, category link propagation |
| [Other behavioral differences](#6-other-behavioral-differences) | 3 | Medium | Auto-select root entries, hidden selection filtering, real-world data |

---

## 1. Error Placement

**4 specs** — NR places max/cost constraint errors on the **selection** node,
while BattleScribe places them on the **category** node.

All 4 specs expect error assertions like:
```yaml
error:
  on: "category cat-troops"    # BattleScribe puts error here
  from: "se-unit-a/con-max-1"
```

NR fires the same validation error but attaches it to the selection that violated
the constraint, not the category the selection belongs to.

| Spec | Expected `on` | NR `on` |
|------|--------------|---------|
| `constraint/constraint-cost-max-linked` | `category cat-troops` | `selection se-unit-a` |
| `constraint/constraint-hidden-violation-linked` | `category cat-troops` | `selection se-unit-a` |
| `constraint/constraint-max-violation-linked` | `category cat-troops` | `selection se-unit-a` |
| `constraint/constraint-min-and-max` | `category cat-troops` | `selection se-unit-a` |

**Root cause**: NR's `checkConstraints()` method reports errors on the node
being checked. For max constraints with `scope=parent`, NR reports on the
selection. BattleScribe's engine collects errors per-category during force
validation and attributes them to the category node.

---

## 2. Import Ordering

**3 specs** — NR orders imported entries from CatalogueLinks BEFORE
faction-specific entries. BattleScribe puts faction entries first.

| Spec | Expected first selection | NR returns first |
|------|-------------------------|-----------------|
| `selection/catalogue-link-import` | Faction Unit | Common Unit |
| `selection/import-false-entry-hidden-via-link` | Faction Unit | Common Unit |
| `selection/import-true-entry-visible-via-link` | Squad | Veteran Squad |

**Impact**: Low — cosmetic ordering difference. Data is correct.

---

## 3. Missing Features

**11 specs** — NR doesn't implement or expose certain BattleScribe features.

### Selection-level publication/page (4 specs)

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-entry-page` | Page numbers | NR doesn't expose `page` on selections |
| `selection/selection-page` | Page numbers | NR doesn't expose `page` on selections |
| `selection/selection-publication` | PublicationId | NR doesn't expose `publicationId` on selections |
| `selection/selection-publication-and-page` | PublicationId + Page | NR doesn't expose `publicationId` or `page` on selections |

### Rule/profile publication and page (6 specs)

| Spec | Feature | Detail |
|------|---------|--------|
| `selection/rule-publication` | Rule publicationId | Rule on a selection should preserve its publicationId |
| `selection/profile-publication` | Profile publicationId | Profile on a selection should preserve its publicationId |
| `selection/infolink-profile-publication` | InfoLink profile publicationId | Profile linked via InfoLink should preserve target's publicationId |
| `selection/infolink-publication-override` | InfoLink publication non-override | InfoLink publicationId should NOT override linked target's own publicationId |
| `gamesystem/gamesystem-publication` | GameSystem publication | Publication defined at GameSystem level should be referenceable by entries |
| `selection/infolink-page-override` | InfoLink page non-override | InfoLink page should NOT override linked rule's own page |

**Root cause**: NR's data model doesn't fully wire publication resolution
through the InfoLink chain. While NR does expose `publicationId` and `page` on
some profiles and rules, it fails when: (a) the publication is defined at the
GameSystem level rather than catalogue level, (b) an InfoLink references a
shared rule/profile that has its own publicationId — NR loses the target's
publication during link resolution.

### Unset-primary modifier (1 spec)

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-category-unset-primary` | Unset-primary modifier | NR ignores the `unset-primary` category modifier |

---

## 4. Scope/Condition Evaluation

**3 specs** — NR evaluates certain condition types differently, causing
modifiers to trigger when they shouldn't (or vice versa).

| Spec | Issue |
|------|-------|
| `scope/scope-include-child-forces` | Condition with `scope=force, childForces=true` triggers when it shouldn't |
| `scope/scope-include-child-forces-nested` | Same issue in nested force scenario |
| `condition/condition-null-childid` | Missing childId: NR counts all selections (condition fires), BS returns NaN (condition false) |

These specs test complex condition evaluation where NR's implementation
diverges from BattleScribe's. For scope specs, the modifier fires (changing the
selection name), proving the condition evaluates to true in NR but false in BS.
For the null-childId spec, BattleScribe's resolver returns null when childId is
absent, causing the query to return NaN and the condition to evaluate as false.
NR defaults missing childId based on node type: forces/groups use `"any"` (count
everything), other nodes use `"self"` (count self). See [NR Condition Engine](#nr-condition-engine-internals)
discovery section for the decompiled code analysis.

Two companion specs (`condition-null-childid-nr-force`, `condition-null-childid-nr-self`)
assert NR's alternative defaults for missing childId. These pass on NR but are
expected to fail on BS (`engines: {battlescribe: fail}`).

---

## 5. Entry Group Behavior

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

## 6. Other Behavioral Differences

**3 specs** with distinct NR behavioral differences:

### Auto-Select Root Entries
| Spec | Issue |
|------|-------|
| `constraint/constraint-forces-field` | NR auto-selects root entry with `min>=1` when adding force; BS doesn't |

After `addForce`, spec expects 0 selections but NR has 1 (auto-selected entry
with `type=model, min=1`). BattleScribe only auto-selects child entries, not
root entries in the forces field.

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

**NR behavior**:
- Selections: NR does not expose `publicationId` or `page`
- Profiles: NR preserves `publicationId` and `page` from the data definition
- Rules: NR preserves `publicationId` and `page` from the data definition

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

The Oracle adapter replicates this via reflection: `_autoSelectMethod.Invoke()`.

### NR Error Extraction

NR validation errors are extracted by calling `checkConstraints()` on each
roster node, then reading the node's error arrays. Key findings:

- `checkConstraints()` must be called explicitly per node
- Can crash with undefined reference errors — wrapped in try-catch
- Errors on army node are cost limit violations
- Error structure: `{message, constraintId?, ownerType, ownerEntryId}`
- ConstraintId format: NR now maps cost limit errors to the `costLimits/`
  pseudo-entry convention (matching BattleScribe's format)
- Max constraint errors go on the selection, not the category (unlike BS)

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
  Previously tracked in separate JSON files (`specs/expected-failures/*.json`)
  which have been removed in favor of this single-source-of-truth approach.
- **Oracle (BattleScribe)**: 244 specs expected to pass, 2 expected to fail
  (NR-specific null-childId condition behavior). DataSource specs (2 real-world
  wh40k-10e) are fully supported via IKVM engine with DataUtils XML loading.

### Resolved Issues

| Issue | Fix | Specs Fixed |
|-------|-----|-------------|
| Selection ordering | Insertion-order tracking via `__bsspec_seq` tags | ~15 |
| Action/state index mismatch | `getSortedSelections()` helper for all action methods | 3 |
| Child cost aggregation | `incrementAmount()` instead of `addInstance()` | 8 |
| Error extraction | Tree-walking `checkConstraints()` with structured error parsing | ~10 |
| Entry link resolution | Oracle queries `_engine.e(force).R()` for expanded entries | 4 |
| Auto-select replication | Oracle adapter calls `x()` via reflection | ~15 |
| GameSystem entry resolution | `SelectEntry` now includes GS-level SelectionEntries and EntryLinks | 2 |
| SelectChildEntry flattening | `FlattenChildEntries` resolves EntryLinks and nested SelectionEntryGroups | 6 |
| FindEntryById scope | `FindEntryById` now searches GameSystem entries in addition to catalogue | 2 |
| Force-catalogue map state leak | `_forceCatalogueMap.Clear()` in Setup prevents cross-test contamination | 1 |
