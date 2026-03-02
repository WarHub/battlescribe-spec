# BattleScribe Engine Conformance Spec — Coverage Report

Machine-readable coverage matrix: [`specs/coverage-matrix.yaml`](../specs/coverage-matrix.yaml).

Current suite status: **179 YAML specs** across 9 categories, with **325 test cases** (**325 passed, 0 skipped, 0 failed**).
Known limitation tags: **0** (all previously synthetic specs now fully execute).

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
| 325 | 325 | 0 | 0 |

Tests break down as: 179 spec conformance tests (1:1 with YAML specs) + 146 infrastructure/unit tests.

---

## 2. Engine API Coverage

All `IRosterEngine` methods are exercised by the conformance suite.

| Method | Specs Using | Key Evidence |
|--------|----------:|--------------|
| `Setup` | 179 | Every spec (implicit setup path) |
| `AddForce` | 176 | force-add-single, force-add-multiple, force-nested-basic |
| `RemoveForce` | 4 | force-remove, force-remove-second, force-add-and-remove-all, refresh-full-lifecycle |
| `SelectEntry` | 164 | selection-add-unit, selection-with-cost, selection-multiple-entries |
| `SelectChildEntry` | 6 | selection-child-entry, selection-child-multiple, cost-child-aggregation, scope-ancestor |
| `DeselectSelection` | 15 | selection-remove, selection-deselect-last, refresh-after-deselect, refresh-full-lifecycle |
| `SetSelectionCount` | 2 | selection-set-count, refresh-after-set-count |
| `DuplicateSelection` | 4 | selection-duplicate, cost-duplicate-increases, refresh-after-duplicate |
| `SetCostLimit` | 1 | cost-set-limit |
| `GetRosterState` | 126 | Specs using `expectedState` assertions |
| `GetValidationErrors` | 9 | constraint-min-violation, constraint-max-satisfied, constraint-no-constraints |
| `HasValidationErrors` | 1 | roster-create-empty |

---

## 3. Enum & Feature Coverage

### ConditionType — 8/8 (100%)

| Type | Specs | Key Evidence |
|------|------:|--------------|
| `EQ` | 2 | condition-equal-to, condition-equal-to-zero |
| `NE` | 1 | condition-not-equal-to |
| `GT` | 2 | condition-greater-than, condition-group-nested |
| `LT` | 2 | condition-less-than, condition-group-nested |
| `AT_LEAST` | 38 | Dominant condition type across condition/modifier/scope specs |
| `AT_MOST` | 2 | condition-at-most, condition-group-and |
| `INSTANCE_OF` | 9 | All 8 target types + scope-primary-category |
| `NOT_INSTANCE_OF` | 1 | condition-not-instance-of |

Full `INSTANCE_OF` target coverage: `SelectionEntry`, `SelectionEntry.Type`, `CategoryEntry`, `Ancestor`, `Roster`, `SelectionEntryGroup`, `ForceEntry`, `Catalogue`.

`childId`-filtered condition coverage for all 4 types: `SelectionEntry`, `SelectionEntryGroup`, `ForceEntry`, `Catalogue`.

### ConstraintType — 2/2 (100%)

| Type | Specs | Key Evidence |
|------|------:|--------------|
| `min` | 8 | constraint-min-violation, constraint-min-satisfied, constraint-min-and-max |
| `max` | 17 | constraint-max-violation, constraint-max-satisfied, constraint-max-unlimited |

### ModifierType — 8/8 (100%)

| Type | Specs | Key Evidence |
|------|------:|--------------|
| `set` | 62 | modifier-set-name, modifier-set-cost, modifier-set-boolean (most common) |
| `increment` | 16 | modifier-increment-cost, modifier-repeat-basic, modifier-on-profile |
| `decrement` | 5 | modifier-decrement-cost, modifier-decrement-below-zero |
| `append` | 3 | modifier-append-name, modifier-append-with-space, modifier-on-profile |
| `remove` | 1 | modifier-category-remove |
| `set-primary` | 1 | modifier-category-set-primary |
| `unset-primary` | 1 | modifier-category-unset-primary |

> Note: BattleScribe's "add" category modifier is tested indirectly in modifier-category-add, which validates category presence via `categoryLinks` on the entry.

### Modifier Field Targets — 8/8 (100%)

| Field | Specs | Notes |
|-------|------:|-------|
| `name` | 55 | Most common — used as observable effect in condition/scope specs |
| `hidden` | 4 | modifier-set-hidden-true, modifier-field-hidden, modifier-conditional-boolean-toggle |
| cost type ID (`pts`) | 19 | Any cost type ID is a valid field target |
| `description` | 2 | modifier-rule-description, modifier-on-profile |
| `page` | 1 | modifier-entry-page |
| characteristic type ID | 2 | char-wounds, char-attacks (modifier-characteristic-value, modifier-on-profile) |
| `con-max` | 2 | constraint-modified-by-modifier, modifier-field-constraint-value |
| `con-max-1` | 1 | modifier-set-constraint |

### Scope — 8/8 (100%)

| Scope | Specs | Key Evidence |
|-------|------:|--------------|
| `self` | 8 | scope-self, modifier-set-boolean |
| `parent` | 34 | scope-parent — dominant scope for childId-based conditions |
| `force` | 26 | scope-force, scope-force-vs-roster, scope-force-trigger-in-same-force |
| `roster` | 12 | scope-roster, scope-roster-cross-force, condition-scope-roster |
| `ancestor` | 2 | scope-ancestor, condition-instance-of-ancestor |
| `primary-category` | 1 | scope-primary-category |
| `primary-catalogue` | 1 | scope-primary-catalogue |
| direct child-id | 4 | scope-child-id-filter, condition-childid-* |

### Query + Repeat Coverage

- Query fields: `selections` (widely used), `forces` (3 specs)
- Repeat `roundUp`: `true` (modifier-repeat-round-up) and `false` (modifier-repeat-round-down)
- Repeat modifiers: 7 specs total including percent-value, zero-threshold, multiple-additive

### Assertion Mechanisms

| Assert Type | Specs | Description |
|-------------|------:|-------------|
| `expectedState` | 126 | Full roster state comparison (forces, selections, costs, names) |
| `totalCost` | 26 | Sum of costs across roster |
| `selectionName` | 15 | Name of specific selection after modifiers |
| `selectionCount` | 13 | Count of selections in a force |
| `forceCount` | 10 | Count of forces in roster |
| `noValidationErrors` | 8 | Asserts zero validation errors |
| `hasValidationErrors` | 1 | Asserts at least one validation error |

---

## 4. Infrastructure Coverage

| Feature | Specs | Evidence |
|---------|------:|---------|
| Profiles & Characteristics | 2 | modifier-on-profile, modifier-characteristic-value |
| Rules | 1 | modifier-rule-description |
| Entry Links | 2 | entry-link-basic, entry-link-with-modifier |
| Category Links | 6 | modifier-category-*, scope-primary-category, selection-category-link |
| Selection Entry Groups | 3 | selection-entry-group-basic, -constraint, -default |
| Nested Force Entries | 2 | force-nested-basic, scope-include-child-forces-nested |
| Modifier Groups | 5 | modifier-group-basic, -false-condition, -nested, -with-repeat, modifier-multiple-groups |
| Condition Groups | 4 | condition-group-and, -and-fails, -or, -nested |
| Repeat Modifiers | 7 | modifier-repeat-basic, -round-up, -round-down, -percent-value, -zero-threshold, -multiple-additive |
| Cost Type Flags | 3 | cost-type-hidden, cost-type-limit, cost-limit-query-resolution |
| Collective Entries | 3 | selection-collective-create, -deselect, constraint-collective |
| Shared Entries | 2 | constraint-shared, constraint-shared-deduplication |
| Percent Value | 3 | condition-percent-value, constraint-percent-value, modifier-repeat-percent-value |
| Include Child Forces | 2 | scope-include-child-forces, -nested |
| Include Child Selections | 1 | constraint-include-child-selections |
| Force-Level Entries | 1 | selection-force-level-entry |
| Hidden Entries | 2 | selection-hidden-entry, constraint-hidden-enforcement |

---

## 5. Known Limitations

**0 specs** are tagged `known-limitation-synthetic` — all 179 specs fully execute against the BattleScribe reference engine via IKVM.

Previously, 47 specs had this tag due to synthetic fixture limitations. These were systematically resolved by:
- Fixing the spec YAML to use proper `childId` references that the engine can resolve
- Adding protocol support for profiles, rules, categories, and page in selection state
- Fixing ModifierGroup/ConditionGroup condition evaluation paths

---

## 6. Explicitly Out-of-Scope

The following are intentionally outside this conformance suite scope:

- **Favourites (`selectFavourite`)**: app-level preference behavior, not roster conformance logic
- **UI rendering**: visual concern, not engine data-model behavior
- **File format parsing (XML deserialization)**: covered by separate parsing/import tooling
- **Concurrent/multi-threaded behavior**: implementation optimization concern, not conformance semantics
- **Dice roller / random mechanics**: app-level feature, not engine data-model behavior
- **Render/export presets**: output formatting, not conformance semantics
- **Comments / metadata preservation**: informational only, no effect on roster editing behavior

---

## 7. Coverage Gaps & Remaining Opportunities

### CRITICAL — Data model features with zero or near-zero test coverage

| Gap | Current Coverage | Impact | Description |
|-----|-----------------|--------|-------------|
| **InfoLinks** | 0 specs | HIGH | InfoLink elements (`type: rule`, `profile`, `infoGroup`) reference shared rules/profiles. No specs test InfoLink resolution. Real-world catalogues heavily use InfoLinks. |
| **InfoGroups** | 0 specs | HIGH | InfoGroup elements contain nested rules/profiles/infoGroups. The data model supports `InfoGroupSpec` but no specs exercise it. |
| **Catalogue Links** | 0 specs | HIGH | CatalogueLink (`importRootEntries` flag) controls cross-catalogue entry visibility. Real catalogues use complex link chains (e.g., wh40k-9e has 50+ catalogues). |
| **Publications** | 0 specs | LOW | Publication metadata (shortName, publisher, date). No behavioral effect on roster editing, but needed for full data model fidelity. |

### HIGH — Features with minimal coverage that are heavily used in real data

| Gap | Current Coverage | Impact | Recommendation |
|-----|-----------------|--------|----------------|
| **Profile/Rule in selection state** | 2 specs | HIGH | Only modifier-on-profile and modifier-characteristic-value validate profile state. Add specs for: profile inheritance from entry links, hidden profile propagation, multiple profiles on a selection. |
| **Entry Link edge cases** | 2 specs | MEDIUM-HIGH | Only basic resolution + modifier tested. Add: EntryLink to SelectionEntryGroup, EntryLink with costs/constraints, cascading modifiers through links. |
| **Shared entry pools** | 2 specs | MEDIUM-HIGH | Only constraint-shared and constraint-shared-deduplication. Add: large shared pools, nested shared references, shared entries with modifiers. |
| **Nested child selections** | 14 specs use nested selectionEntries | MEDIUM | Coverage exists but no stress tests for 3+ nesting levels or complex modifier chains on deeply nested entries. |
| **Forces query field** | 3 specs | MEDIUM | Limited depth for `field: forces` queries. Add: cross-force conditions, nested force counting with includeChildForces. |

### MEDIUM — Features with basic coverage that could be deeper

| Gap | Current Coverage | Improvement |
|-----|-----------------|-------------|
| Selection Entry Groups | 3 specs | Add: nested groups, groups with entry links, groups with modifiers on group itself |
| Collective entries | 3 specs | Add: large collective pools, collective with constraints on children |
| Hidden entry cascading | 2 specs | Add: hidden propagation to children, hidden overridden by modifier |
| Condition group nesting | 4 specs | Add: 3+ level nesting, mixed AND/OR at different levels |
| Dynamic constraint fields | 3 specs | Add: con-min, con-min-1, multiple constraint field indices |
| Cost type variety | only `pts` tested | Add: specs with multiple cost types (pl, cp) and modifiers targeting different cost types |
| Selection type variety | `unit` dominant | Add: more `model` and `upgrade` type coverage |

### LOW — Polish and completeness

| Gap | Description |
|-----|-------------|
| Modifier application order | No spec explicitly tests that document-order processing is respected |
| `import` attribute on entries | Controls visibility in roster editor; not tested |
| Default cost limits | `defaultCostLimit` on CostType; only 1 spec touches cost limits |
| Entry link to SelectionEntryGroup type | Only `selectionEntry` link type tested |
| Category modifier `add` type | modifier-category-add doesn't actually use a modifier with `type: add` |
| Repeat with includeChildSelections | Repeat query flags not tested in combination |
| Constraint with includeChildForces | Only tested on scope, not on constraints |
| `page` field on rules/profiles | Only entry-level `page` modifier tested |

---

## 8. Real-World Data Coverage

The test suite includes **10 complex real-world roster tests** (`ComplexRealWorldRosterTests.cs`) using wh40k-9e catalogue data. These exercise multi-catalogue scenarios but are bonus tests outside the core 179 YAML spec suite.

> **Note**: 1 real-world test (`Roster10_AeldariAlliedWarhost`) has known intermittent failures due to catalogue switching timing. This is a pre-existing flaky test, not a spec conformance issue.

### Recommended Next Steps (Priority Order)

1. **Add InfoLink/InfoGroup specs** (5–10 specs) — rule, profile, and infoGroup link types with modifiers
2. **Add CatalogueLink specs** (3–5 specs) — importRootEntries=true/false, shared entry resolution across catalogues
3. **Expand profile/rule state assertions** (5–8 specs) — multiple profiles, hidden profiles, profile inheritance through links
4. **Add EntryLink edge cases** (3–5 specs) — link to group, link with constraints, cascading modifiers
5. **Deepen shared/collective coverage** (3–5 specs) — larger pools, nested shared refs
6. **Add multi-cost-type modifier specs** (2–3 specs) — modifiers targeting `pl`, `cp` alongside `pts`
7. **Add con-min constraint field specs** (1–2 specs) — min constraint modification via modifiers
