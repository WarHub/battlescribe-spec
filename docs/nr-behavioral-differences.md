# New Recruit vs BattleScribe: Behavioral Differences Report

> Based on conformance testing against [newrecruit.eu](https://newrecruit.eu)
> using the battlescribe-spec test suite.
>
> Last validated: 2026-03-05

## Summary

| Metric | Value |
|--------|-------|
| Total specs run (NR) | 240 |
| NR passing | 200 (83%) |
| NR failing | 40 |
| Oracle (BattleScribe) baseline | 282/282 passing (100%) |

### Failure Breakdown

| Category | Count | Severity | Description |
|----------|-------|----------|-------------|
| [Structured error links](#1-structured-error-links) | 10 | Medium | NR returns errors but without structured from/on links |
| [Entry ordering](#2-entry-ordering) | 10 | Low | Selection order differs, data is correct |
| [Constraint behavior](#3-constraint-behavior-differences) | 6 | Medium | NR differs on entry-link constraints and hidden entries |
| [DataSource resolution](#4-datasource-resolution) | 5 | Infra | wh40k-10e v10.14.0 tag removed upstream |
| [Missing features](#5-missing-features) | 4 | Low | Page numbers, unset-primary, cost limits |
| [Auto-select edge cases](#6-auto-select-edge-cases) | 2 | Low | Residual auto-select side effects |
| [Adapter bugs](#7-adapter-bugs) | 2 | Adapter | Multi-catalogue entry lookup failures |
| [Flaky / timeout](#8-flaky-tests) | 1 | Infra | NR page load timeout |

---

## 1. Structured Error Links

**10 specs** — NR validates constraints but returns errors as simple strings,
not as structured objects with `from` (entry/constraint path) and `on` (roster
element) fields.

All 10 specs expect error assertions like:
```yaml
error:
  on: "category cat-troops"
  from: "se-unit-a/con-max-1"
```

NR's validation errors are present (it does enforce constraints) but can't be
matched because the adapter reads them as flat strings without structured links.

| Spec | Expected error link |
|------|-------------------|
| `constraint/constraint-cost-limit-linked` | `on='roster', from='costLimits/ct-pts'` |
| `constraint/constraint-cost-max-linked` | `on='category', from='se-unit-a/con-cost-max'` |
| `constraint/constraint-cost-min-linked` | `on='category', from='se-unit-a/con-cost-min'` |
| `constraint/constraint-hidden-violation-linked` | `on='category', from='se-unit-a/hidden'` |
| `constraint/constraint-max-violation-linked` | `on='category', from='se-unit-a/con-max-1'` |
| `constraint/constraint-min-violation-linked` | `on='category', from='se-unit-a/con-min-1'` |
| `constraint/constraint-min-on-force-linked` | `on='force', from='se-unit-a/con-min-force'` |
| `constraint/constraint-min-and-max` | `on='category', from='se-unit-a/con-max-2'` |
| `constraint/constraint-multiple-errors-linked` | Two errors with different links |
| `constraint/constraint-shared-linked` | `on='category', from='se-unit-a/con-min-shared'` |

**Root cause**: The NR state reader extracts validation errors from the NR
diagnostic store, but NR's error objects don't expose the source entry ID or
constraint ID in the same way BattleScribe does. Fixing this requires either
NR-side changes or adapter-side heuristic matching.

---

## 2. Entry Ordering

**10 specs** — NR returns selections in a different order than BattleScribe.

NR orders entries by its internal selector tree traversal (typically by category,
then by selector order), while BattleScribe uses the catalogue's declared
`selectionEntries` order. The data is identical — only position differs.

| Spec | Expected first | NR returns first |
|------|---------------|-----------------|
| `condition/condition-instance-of-by-type` | Detector | Basic Model |
| `condition/condition-percent-value` | Unit A | Percent Met |
| `scope/scope-include-child-forces` | Unit A | Child Forces Included |
| `scope/scope-include-child-forces-nested` | Squad | Veteran Squad |
| `selection/catalogue-link-import` | Faction Unit | Common Unit |
| `selection/import-false-entry-direct-use` | Public Unit | Internal Unit |
| `selection/import-false-entry-hidden-via-link` | Faction Unit | Shared Unit |
| `selection/import-true-entry-visible-via-link` | Faction Unit | Shared Unit |
| `selection/selection-child-entry` | Sergeant | Medic |
| `selection/selection-multiple-types` | Sergeant (model) | Power Sword (upgrade) |

**Impact**: Low. Consider adding `matchOrder: false` to the spec format for
position-insensitive assertions.

---

## 3. Constraint Behavior Differences

**6 specs** — NR doesn't fire validation errors for entry-link constraints or
produces different behavior for hidden entries.

### Entry Link Constraints (4 specs)

NR doesn't enforce constraints that were defined on entry links or on shared
entries accessed via entry links. In BattleScribe, catalogue expansion copies
constraints from shared entries and entry links into expanded entries. NR may
handle entry link resolution differently.

| Spec | Issue |
|------|-------|
| `constraint/constraint-entry-link-merged` | No error when both shared + link constraints violated |
| `constraint/constraint-entry-link-own` | No error for constraint on entry link itself |
| `constraint/constraint-entry-link-shared-counting` | Shared counting across entry links not working |
| `constraint/constraint-entry-link-shared-target` | Shared entry constraint not enforced via link |

### Min Violation (1 spec)

| Spec | Issue |
|------|-------|
| `constraint/constraint-min-violation` | `hasValidationErrors: expected True but got False` — NR may not validate min constraints in same scenarios |

### Hidden Entry Enforcement (1 spec)

| Spec | Issue |
|------|-------|
| `constraint/constraint-hidden-enforcement` | NR doesn't auto-select hidden entries or counts them differently — expected 1 selection, got 0 |

---

## 4. DataSource Resolution

**5 specs** — All wh40k-10e real-world specs fail because the upstream BSData
repository removed the `v10.14.0` tag.

```
fatal: Remote branch v10.14.0 not found in upstream origin
```

| Spec |
|------|
| `real-world/wh40k-10e-captain` |
| `real-world/wh40k-10e-create-army` |
| `real-world/wh40k-10e-multi-unit` |
| `real-world/wh40k-10e-points-cost` |
| `real-world/wh40k-10e-space-marines-intercessors` |

**Fix**: Update specs to reference a valid tag/commit SHA from the BSData repo.

---

## 5. Missing Features

**4 specs** — NR doesn't implement or expose certain BattleScribe features.

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-entry-page` | Page numbers | NR doesn't expose `page` on selections |
| `selection/rule-with-page` | Page numbers | NR doesn't expose `page` on selections |
| `modifier/modifier-category-unset-primary` | Unset-primary modifier | NR ignores the `unset-primary` category modifier |
| `cost/cost-default-limit-positive` | Cost limit validation | NR doesn't enforce `defaultCostLimit` the same way |

---

## 6. Auto-Select Edge Cases

**2 specs** — Both engines auto-select entries with `min >= 1` constraints, but
edge cases remain.

| Spec | Issue |
|------|-------|
| `constraint/constraint-forces-field` | Auto-selected entries counted when spec expects empty force |
| `refresh/refresh-full-lifecycle` | Costs doubled (150→210, 60→120) — auto-selected entries not properly accounted for in lifecycle |

---

## 7. Adapter Bugs

**2 specs** — Failures caused by the NR adapter's entry lookup logic, not by
NR itself.

| Spec | Error |
|------|-------|
| `force/force-multi-catalogue-two-forces` | `Entry 'se-a1' not found in force selector tree` — adapter can't find entry in second force's catalogue |
| `selection/catalogue-link-shared-entry` | `Entry index 0 out of range (catalogue has 0 entries)` — adapter doesn't find shared entries from linked catalogue |

---

## 8. Flaky Tests

**1 spec** — Intermittent timeout loading the NR web app.

| Spec | Error |
|------|-------|
| `selection/selection-entry-group-default` | `Timeout 30000ms exceeded` navigating to newrecruit.eu |

---

## Discoveries

Technical findings from reverse-engineering NR's internal API and comparing
with BattleScribe's decompiled Java engine.

### NR Selection Model: `incrementAmount()` vs `addInstance()`

NR pre-creates **selector nodes** with `amount=0` for all child entries when a
parent is selected. These are placeholder objects representing available entries.

- **`addInstance()`** on a selector template creates a NEW node with `amount=0`
  (broken — produces duplicates, costs not aggregated)
- **`incrementAmount()`** on an existing child node sets amount from 0 to 1
  (correct — costs properly included in `calcTotalCosts()`)

This discovery resolved the **child cost aggregation** issue (8 specs fixed).

Key NR node structure:
- `sel.selector` — back-reference to entry definition template
- `sel.selectors` — array of child entry templates
- `sel.state.costs.pts` — raw cost number
- `sel.state.totalCosts.pts` — total including children
- `sel.state.selections` — count of selected children
- Prototype methods: `getAmount()`, `incrementAmount()`, `decrementAmount()`,
  `getCosts()`, `getTotalCosts()`, `getSelections()`, `getEntries()`, etc.

### BattleScribe Auto-Select Mechanism

Decompiled from `engine.a.f` (BattleScribe Java engine):

- Private method `x()` ("Select default root entries") at line 978
- Called during `setRoster(bl=true)` when creating a new roster
- Iterates all forces, auto-selects entries where `getDefaultAmount >= 1`
- `getDefaultAmount` returns the entry's `min` constraint value

The Oracle adapter replicates this via reflection: `_autoSelectMethod.Invoke()`.

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

### NR Data Loading

Three methods for loading game data:
1. **`sysStore.loadSystemFromFs(files)`** — accepts `[{name, path, data}]`
   array with XML strings (used for spec tests)
2. **`addGithubSystem()`** — downloads from BSData GitHub repos
3. **Mock `showDirectoryPicker()`** — intercepts folder upload UI

### Debug Code and Pinia Reactivity

Adding extra JS function calls to NR's reactive APIs (e.g., `calcTotalCosts()`,
`getTotalCosts()`) during the SAME `EvaluateAsync` that reads state causes
Pinia reactivity interference — costs disappear, forces go missing, complete
state read failure.

**Solution**: Always probe NR's API in SEPARATE `EvaluateAsync` calls.

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
- **Validation**: Error extraction from NR's diagnostic store

### Test Infrastructure

- **Browser lifecycle**: `NewRecruitFixture` (xUnit collection fixture) shares
  one Playwright browser across all NR tests, which run serially
- **Gating**: NR tests only run when `NR_ENGINE_URL` environment variable is set
- **Expected failures**: `specs/expected-failures/newrecruit.json` lists known
  differences so they don't block CI
- **Oracle comparison**: All 282 Oracle (BattleScribe Java engine) tests pass
  as the reference baseline

### Resolved Issues

| Issue | Fix | Specs Fixed |
|-------|-----|-------------|
| Child cost aggregation | `incrementAmount()` instead of `addInstance()` | 8 |
| Auto-select not replicated | Oracle adapter calls `x()` via reflection | ~15 |
| Entry link resolution | Oracle queries `_engine.e(force).R()` for expanded entries | 4 |
