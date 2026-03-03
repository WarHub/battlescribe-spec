# New Recruit vs BattleScribe: Behavioral Differences Report

> Based on conformance testing against [newrecruit.eu](https://newrecruit.eu)
> using the battlescribe-spec test suite.

## Summary

| Metric | Value |
|--------|-------|
| Total specs applicable to NR | 233 |
| Passing | 206 |
| Known behavioral differences | 27 |
| Conformance rate | 88.4% |

## Difference Categories

| Category | Count | Severity |
|----------|-------|----------|
| [Entry ordering](#1-entry-ordering) | 13 | Low — cosmetic, data is correct |
| [Child cost aggregation](#2-child-cost-aggregation) | 5 | Medium — affects total cost display |
| [Auto-select on min constraints](#3-auto-select-on-min-constraints) | 5 | Medium — different UX model |
| [Missing page number support](#4-missing-page-number-support) | 2 | Low — feature gap |
| [Other behavioral differences](#5-other-behavioral-differences) | 2 | Low–Medium |

---

## 1. Entry Ordering

**13 specs affected** — category `nr-entry-order`

### Description

New Recruit returns selections and child selections in a different order than
BattleScribe when iterating a force's entries. This affects:

- **Top-level selections within a force**: NR orders entries by category and
  internal selector tree traversal order, while BattleScribe uses the catalogue's
  declared `selectionEntries` order.
- **Child selections under a parent**: NR may reorder children based on its
  internal object tree structure.
- **Catalogue link / import resolution**: When entries are imported from linked
  catalogues, NR resolves and orders them differently from BattleScribe.
- **Multi-catalogue force association**: In multi-catalogue setups with multiple
  forces, NR associates forces to catalogues differently.

### Impact

**Low.** The selections contain the same data — only the ordering differs.
Roster building workflows that depend on positional indices (e.g., "the 3rd
entry in the force") may see different results, but name-based lookups work
identically.

### Affected Specs

| Spec | Specific Difference |
|------|---------------------|
| `scope/scope-include-child-forces` | Selection order in force |
| `scope/scope-include-child-forces-nested` | Selection order in force |
| `condition/condition-percent-value` | Selection order in force |
| `condition/condition-instance-of-by-type` | Selection order in force |
| `selection/catalogue-link-import` | Imported entry ordering |
| `selection/catalogue-link-shared-entry` | Multi-catalogue resolution |
| `selection/import-true-entry-visible-via-link` | Imported entry ordering |
| `selection/import-false-entry-hidden-via-link` | Imported entry ordering |
| `selection/import-false-entry-direct-use` | Imported entry ordering |
| `selection/selection-multiple-types` | Child selection ordering |
| `selection/selection-child-multiple` | Child selection ordering |
| `selection/selection-child-entry` | Child selection ordering |
| `force/force-multi-catalogue-two-forces` | Force-catalogue association |

---

## 2. Child Cost Aggregation

**5 specs affected** — category `nr-child-cost`

### Description

BattleScribe aggregates costs from child selections (sub-selections, models,
upgrades) into the roster's total cost. New Recruit handles cost aggregation
differently:

- **Child model costs**: When a unit contains models with their own cost
  (e.g., a model with per-model points), NR may not aggregate the child's cost
  into the roster's `calcTotalCosts()` result.
- **Nested children**: For deeply nested selection trees (parent → child →
  grandchild), NR's cost rollup can differ from BattleScribe's flat aggregation.
- **Refresh after child select**: After selecting a child entry, the total cost
  update in NR may not match BattleScribe's immediate recalculation.

### Impact

**Medium.** This can cause visible differences in displayed point totals. In
practice, for simple rosters the totals usually match; the difference appears
with complex nested selections that have costs at multiple tree levels.

### Affected Specs

| Spec | Specific Difference |
|------|---------------------|
| `cost/cost-child-aggregation` | Child costs not in roster total |
| `selection/selection-child-with-cost` | Child cost not aggregated |
| `selection/nested-children-deep` | Deep nesting cost rollup |
| `refresh/refresh-after-child-select` | Cost not updated after child select |
| `selection/selection-model-with-cost` | Model cost not in roster total |

---

## 3. Auto-Select on Min Constraints

**5 specs affected** — category `nr-auto-select`

### Description

When a force is added, BattleScribe creates the force with zero selections and
waits for the user to manually add entries. New Recruit **automatically selects
entries that have `min >= 1` constraints**, pre-populating the force.

This means:
- After `addForce`, NR already has selections present (BS has 0).
- Selection count assertions immediately after `addForce` differ.
- Cost totals differ because auto-selected entries contribute costs.
- The `refresh-full-lifecycle` spec sees doubled costs because the spec
  manually selects an entry that NR already auto-selected.

### Impact

**Medium.** This is a deliberate UX design choice by New Recruit — it's more
user-friendly to pre-select required entries. However, it changes the
observable state at each step compared to BattleScribe.

### Affected Specs

| Spec | Specific Difference |
|------|---------------------|
| `refresh/refresh-after-select` | Unexpected selections after addForce |
| `refresh/refresh-validation-update` | Unexpected selections after addForce |
| `refresh/refresh-full-lifecycle` | Doubled costs from auto-select |
| `constraint/constraint-min-and-max` | Unexpected selections after addForce |
| `constraint/constraint-forces-field` | Unexpected selections after addForce |

---

## 4. Missing Page Number Support

**2 specs affected** — category `nr-missing-feature`

### Description

BattleScribe exposes `page` metadata on selections and rules (referencing the
physical book page number). New Recruit does not surface this field through its
internal API — `page` is always `null` or empty.

### Impact

**Low.** Page numbers are metadata for user reference and don't affect roster
building or validation logic.

### Affected Specs

| Spec | Specific Difference |
|------|---------------------|
| `modifier/modifier-entry-page` | Page number not set after modifier |
| `selection/rule-with-page` | Rule page number not exposed |

---

## 5. Other Behavioral Differences

**2 specs affected** — category `nr-behavior`

### 5a. Category Primary Flag — Modifier Unset

**Spec:** `modifier/modifier-category-unset-primary`

BattleScribe supports a modifier that can **unset** a category's `primary` flag
(changing it from `true` to `false`). New Recruit does not implement this
modifier action — the primary flag remains unchanged.

### 5b. Cost Limit Validation

**Spec:** `cost/cost-default-limit-positive`

BattleScribe validates that total costs do not exceed defined cost limits and
reports validation errors when exceeded. New Recruit handles cost limit
validation differently and may not produce the same validation error messages
or may not enforce limits in the same scenarios.

---

## Architecture Notes

### How NR Was Tested

The NR adapter uses **Playwright** to drive a headless Chromium browser loading
`newrecruit.eu`. Instead of UI interaction, it directly calls NR's internal
**Pinia store API** via JavaScript evaluation:

- **Data loading**: `loadSystemFromFs(files)` — injects BattleScribe XML
  (either synthetic from specs or real from DataSource repos like wh40k-10e)
- **Actions**: Direct Pinia store method calls (`insertForce`, `addInstance`,
  `delete`, `setAmount`)
- **State reading**: `getCurrentList().army` tree traversal using NR's reactive
  object API (`getForces`, `getSelections`, `getName`, `getCosts`, etc.)

### DataSource Support

Real-world specs (e.g., wh40k-10e) use the `dataSource` field to reference
BSData GitHub repositories. The test infrastructure:

1. Resolves `github:BSData/wh40k-10e@v10.14.0` via git clone to local cache
2. Reads all `.gst`/`.cat` files as raw XML
3. Loads into NR via the same `loadSystemFromFs` path
4. Uses name-based entry selection (NR's selector tree searched by name)

All 5 real-world wh40k-10e specs pass successfully.

---

## Recommendations

1. **Entry ordering**: Consider adding order-independent assertion modes to the
   spec format (e.g., `matchOrder: false`) for specs where ordering is not
   semantically significant.

2. **Child cost aggregation**: Investigate whether NR's behavior is a bug or
   intentional. If intentional, document the expected cost model difference in
   the spec format.

3. **Auto-select behavior**: Consider adding an `expectAutoSelect: true` flag
   or alternative expected states for engines that pre-select required entries.

4. **Page numbers**: Low priority. Could be added to NR's state reader if the
   data is available internally but just not exposed.
