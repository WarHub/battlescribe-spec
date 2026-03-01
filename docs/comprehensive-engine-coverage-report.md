# BattleScribe Engine Conformance Spec — Coverage Report

Machine-readable coverage matrix: [`specs/coverage-matrix.yaml`](../specs/coverage-matrix.yaml).

Current suite status: **179 YAML specs** across 9 categories, with **314 test cases** (**314 passed, 0 skipped, 0 failed**).

---

## 1. Test Suite Summary

| Category | Specs |
|----------|------:|
| condition | 27 |
| constraint | 19 |
| cost | 17 |
| force | 10 |
| modifier | 44 |
| refresh | 10 |
| roster | 9 |
| scope | 14 |
| selection | 29 |
| **Total** | **179** |

| Test Cases | Passed | Skipped | Failed |
|------------|-------:|--------:|-------:|
| 314 | 314 | 0 | 0 |

---

## 2. Engine API Coverage

All `IRosterEngine` methods are exercised by the conformance suite.

| Method | Coverage Evidence |
|--------|-------------------|
| `Setup` | Every spec (implicit setup path) |
| `AddForce` | force-add-single, force-add-multiple |
| `RemoveForce` | force-remove, force-remove-second, force-add-and-remove-all |
| `SelectEntry` | selection-add-unit, selection-with-cost, selection-multiple-entries |
| `SelectChildEntry` | selection-child-entry, selection-child-multiple, selection-child-with-cost |
| `DeselectSelection` | selection-remove, selection-deselect-last, selection-deselect-middle |
| `SetSelectionCount` | selection-set-count, selection-number-default |
| `DuplicateSelection` | selection-duplicate, cost-duplicate-increases |
| `SetCostLimit` | cost-set-limit |
| `GetRosterState` | Every spec (state assertions) |
| `GetValidationErrors` | constraint-min-violation, constraint-max-violation, refresh-validation-update |
| `HasValidationErrors` | constraint-min-violation |

---

## 3. Enum Coverage Matrix

### ConditionType — 11/11 (100%)

- Comparison types covered: `EQ`, `NE`, `GT`, `LT`, `AT_LEAST`, `AT_MOST`
- Type-based checks covered: `INSTANCE_OF`, `NOT_INSTANCE_OF`
- Full target coverage for `INSTANCE_OF`/`NOT_INSTANCE_OF`: `SelectionEntry`, `SelectionEntry.Type`, `CategoryEntry`, `Ancestor`, `Roster`, `SelectionEntryGroup`, `ForceEntry`, `Catalogue`
- Additional childId-filtered condition coverage: `SelectionEntry`, `SelectionEntryGroup`, `ForceEntry`, `Catalogue`

### ModifierType — 8/8 (100%)

- String: `Set`, `Increment`, `Decrement`, `Append`
- Number: `Set`, `Increment`, `Decrement`
- Boolean: `Set`
- Category: `Add`, `Remove`, `SetPrimary`, `UnsetPrimary`

### Modifier Field Targets — 8/8 (100%)

Covered targets:

- `name`
- `hidden`
- `minSelections`
- `maxSelections`
- `minInForce`
- `maxInForce`
- `description` (Rule)
- `page`
- `characteristicType`

### Scope — 8/8 (100%)

Covered scopes: `self`, `parent`, `force`, `roster`, `ancestor`, `primary-category`, `primary-catalogue`, direct child-id targeting.

### Query + Repeat Coverage

- Query fields: `selections`, `forces` (both tested)
- Repeat `roundUp`: `true` and `false` (both tested)

---

## 4. Infrastructure Coverage

The suite now includes explicit model/infrastructure coverage for:

- Rules, Profiles, Characteristics, and InfoGroups
- EntryLinks (basic resolution + modifiers)
- Nested `ForceEntry` structures
- Nested `ModifierGroup` structures
- Nested `ConditionGroup` structures
- `CostType` flags (`hidden`, `limit`)
- Collective entries (`selection-collective-*`)
- Force-level entries (`selection-force-level-entry`)

---

## 5. Known Limitations (`known-limitation-synthetic`)

**47 specs** are tagged `known-limitation-synthetic`.

These specs remain valuable, but with synthetic fixtures they do not populate the engine's full internal indexing/resolution state, so they validate baseline behavior and expected outcomes rather than deeply exercising the full runtime modifier/condition paths.

They primarily cover:

- ModifierGroups (including nested/repeat combinations)
- ConditionGroups
- Category modifiers (`remove`, `set-primary`, `unset-primary`)
- `instanceOf` checks by type and advanced target variants
- EntryLink resolution paths
- `childId` filters for non-`SelectionEntry` target types

---

## 6. Explicitly Out-of-Scope

The following are intentionally outside this conformance suite scope:

- **Favourites (`selectFavourite`)**: app-level preference behavior, not roster conformance logic
- **UI rendering**: visual concern, not engine data-model behavior
- **File format parsing (XML deserialization)**: covered by separate parsing/import tooling
- **Multi-catalogue linking**: requires real catalogue graph infrastructure
- **Concurrent/multi-threaded behavior**: implementation optimization concern, not conformance semantics

---

## 7. Remaining Opportunities

High-value next improvements:

1. Run the same specs against real-world catalogues (for example, `wh40k-9e`) to activate synthetic-tagged paths with full indexing.
2. Expand `SelectionEntryGroup` default-selection scenarios with deeper nested structures.
3. Add complex modifier-chain scenarios for constraint validation interactions.
4. Add cost aggregation edge cases involving limit-type and hidden-type interactions.
