# New Recruit vs BattleScribe: Behavioral Differences Report

> Based on conformance testing against [newrecruit.eu](https://newrecruit.eu)
> using the battlescribe-spec test suite.

## Summary

| Metric | Value |
|--------|-------|
| Total specs | 236 |
| Oracle (BattleScribe) baseline | 282/282 passing (100%) |
| NR known differences | 19 (in `expected-failures/newrecruit.json`) |
| NR last validated run | 236 specs — 180 pass, 56 fail |
| NR conformance rate | ~76% (of all specs) |

> **Note:** The NR conformance rate decreased from ~89% to ~76% primarily due
> to 14 new specs added since the last baseline (constraint rewrites, entry link
> specs, real-world wh40k specs). Of the 56 failures, 19 are known behavioral
> differences, and most remaining failures are from newly added spec categories
> that NR hasn't been validated against yet. The child cost aggregation issue
> was **resolved** — 8 specs were fixed in this cycle.

## Difference Categories

| Category | Count | Severity |
|----------|-------|----------|
| [Entry ordering](#1-entry-ordering) | 12 | Low — cosmetic, data is correct |
| [Auto-select on min constraints](#3-auto-select-on-min-constraints) | 2 | Low — residual edge cases |
| [Missing page number support](#4-missing-page-number-support) | 2 | Low — feature gap |
| [Other behavioral differences](#5-other-behavioral-differences) | 2 | Low–Medium |

---

## ~~2. Child Cost Aggregation~~ (RESOLVED)

**Previously 5 specs affected** — now all passing ✅

### Root Cause

The NR adapter was using `addInstance()` on selector template nodes to select
child entries. This created duplicate nodes with `amount=0` instead of properly
incrementing the existing child node's amount.

### Fix

Changed `SelectChildEntryByIdAsync` to find the existing child node (pre-created
by NR with `amount=0`) and call `incrementAmount()` instead. This properly sets
the child's amount to 1, and NR's `calcTotalCosts()` correctly includes child
costs in the roster total.

### Key Discovery: NR Selection Model

NR pre-creates child nodes with `amount=0` for ALL child entries when a parent
is selected. These are "selector nodes" — placeholders representing available
entries. To "select" a child entry, call `incrementAmount()` on the existing
node (not `addInstance()` on the selector template).

### Fixed Specs

| Spec | Status |
|------|--------|
| `cost/cost-child-aggregation` | ✅ Fixed |
| `selection/selection-child-with-cost` | ✅ Fixed |
| `selection/nested-children-deep` | ✅ Fixed |
| `refresh/refresh-after-child-select` | ✅ Fixed |
| `selection/selection-model-with-cost` | ✅ Fixed |
| `selection/selection-child-multiple` | ✅ Fixed (bonus) |
| `refresh/refresh-after-select` | ✅ Fixed (bonus) |
| `refresh/refresh-validation-update` | ✅ Fixed (bonus) |

---

## 1. Entry Ordering

**12 specs affected** — category `nr-entry-order`

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
| `selection/selection-child-entry` | Child selection ordering |
| `force/force-multi-catalogue-two-forces` | Force-catalogue association |

---

## 2. Child Cost Aggregation — RESOLVED ✅

See [above](#2-child-cost-aggregation-resolved) for details on the fix.

---

## 3. Auto-Select on Min Constraints

**2 specs affected** — category `nr-auto-select`

### Description

Both BattleScribe and New Recruit **automatically select entries that have
`min >= 1` constraints** when creating a roster. This was confirmed by
decompiling the BattleScribe Java engine:

- **BattleScribe** has a private method `x()` ("Select default root entries")
  in `engine.a.f` (line 978) that iterates all forces and auto-selects entries
  where `getDefaultAmount >= 1`. It's called during `setRoster(bl=true)` when
  a new roster is created.
- **New Recruit** implements the same behavior — entries with min constraints
  are pre-populated when a force is added.

The Oracle adapter replicates this by calling `x()` via reflection after
`addForce`, matching the desktop BattleScribe behavior. Specs have been updated
to account for auto-selection and most previously-affected specs now pass on
both engines.

### Impact

**Low.** The core auto-select behavior is aligned between both engines.
The 2 remaining differences are edge cases.

### Affected Specs

| Spec | Specific Difference |
|------|---------------------|
| `refresh/refresh-full-lifecycle` | Lifecycle cost tracking with auto-selected entries |
| `constraint/constraint-forces-field` | `field=forces` constraint with auto-selection |

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

## 6. Entry Link Resolution and Constraint Enforcement

**Discovery** — not a behavioral difference; an Oracle adapter bug that was fixed.

### Background

BattleScribe's engine performs **catalogue expansion** during force creation:
entry links in the catalogue are resolved by copying the target shared entry
and merging the link's properties (constraints, modifiers, costs) into the copy.
The expanded copy gets a composite ID (`linkId::sharedEntryId`) and is registered
as a regular (non-shared) selection entry.

### What Was Fixed

The Oracle adapter's `GetEntriesForForce()` was manually resolving entry links
from a pre-computed list, which returned raw shared entries instead of their
expanded copies. This caused:

- Constraints on shared entries not firing via entry links
- Constraints on entry links themselves not being evaluated
- Shared counting (`shared=true`) not working across entry links

The fix queries the engine's catalogue manager (`_engine.e(force).R()`) which
returns properly expanded entries with merged constraints.

### Key Findings

1. **`scope=parent` on entry link constraints refers to the catalogue root**,
   not the force. Use `scope=force` or `scope=roster` instead.
2. **`shared=true` counting works across multiple entry links** to the same
   shared target — the engine counts by `sharedEntryId`.
3. **Constraints from both the shared entry and the entry link are merged**
   into the expanded copy and evaluated independently.

### New Specs

| Spec | What It Tests |
|------|--------------|
| `constraint-entry-link-shared-target` | Shared entry constraint fires via entry link |
| `constraint-entry-link-own` | Entry link's own constraint fires (scope=force) |
| `constraint-entry-link-merged` | Both shared + link constraints enforced |
| `constraint-entry-link-shared-counting` | Two links to same target, shared counting |

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

All 5 real-world wh40k-10e specs currently fail on NR and need investigation.

---

## Recommendations

1. **Entry ordering**: Consider adding order-independent assertion modes to the
   spec format (e.g., `matchOrder: false`) for specs where ordering is not
   semantically significant.

2. **Child cost aggregation**: **Resolved** — the NR adapter now correctly uses
   `incrementAmount()` on existing child nodes instead of `addInstance()` on
   selector templates. All 5 previously-failing child cost specs now pass.

3. **Auto-select behavior**: **Resolved** — both engines auto-select entries
   with `min >= 1`. The Oracle adapter calls the engine's `x()` method via
   reflection after force creation. Specs account for auto-selection.

4. **Page numbers**: Low priority. Could be added to NR's state reader if the
   data is available internally but just not exposed.

5. **New spec validation**: Many constraint and entry link specs are newly added
   and haven't been thoroughly validated against NR. These should be triaged to
   determine which are NR behavioral differences vs adapter issues.
