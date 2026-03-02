# BattleScribe Engine Conformance Spec — Coverage Report

Machine-readable coverage matrix: [`specs/coverage-matrix.yaml`](../specs/coverage-matrix.yaml).

Current suite status: **215 YAML specs** across 9 categories, with **361 test cases** (**361 passed, 0 skipped, 0 failed**).
Known limitation tags: **0** (all previously synthetic specs now fully execute).

---

## 1. Test Suite Summary

| Category | Specs |
|----------|------:|
| condition | 28 |
| constraint | 20 |
| cost | 19 |
| force | 11 |
| modifier | 48 |
| refresh | 10 |
| roster | 9 |
| scope | 14 |
| selection | 55 |
| **Total** | **215** |

| Test Cases | Passed | Skipped | Failed |
|------------|-------:|--------:|-------:|
| 361 | 361 | 0 | 0 |

Tests break down as: 215 spec conformance tests (1:1 with YAML specs) + 146 infrastructure/unit tests.

---

## 2. Engine API Coverage

All `IRosterEngine` methods are exercised by the conformance suite.

| Method | Specs Using | Key Evidence |
|--------|----------:|--------------|
| `Setup` | 205 | Every spec (implicit setup path) |
| `AddForce` | 212 | force-add-single, force-add-multiple, force-nested-basic, force-multi-catalogue-two-forces |
| `RemoveForce` | 4 | force-remove, force-remove-second, force-add-and-remove-all, refresh-full-lifecycle |
| `SelectEntry` | 190 | selection-add-unit, selection-with-cost, selection-multiple-entries |
| `SelectChildEntry` | 8 | selection-child-entry, selection-child-multiple, nested-children-deep, selection-model-with-cost |
| `DeselectSelection` | 15 | selection-remove, selection-deselect-last, refresh-after-deselect, refresh-full-lifecycle |
| `SetSelectionCount` | 2 | selection-set-count, refresh-after-set-count |
| `DuplicateSelection` | 4 | selection-duplicate, cost-duplicate-increases, refresh-after-duplicate |
| `SetCostLimit` | 1 | cost-set-limit |
| `GetRosterState` | 138 | Specs using `expectedState` assertions |
| `GetValidationErrors` | 11 | constraint-min-violation, constraint-include-child-forces, cost-default-limit-positive |
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

## 5. Known Limitations — None

All 179 specs fully execute against the BattleScribe reference engine via IKVM with no skips, workarounds, or synthetic tags. Previously 47 specs carried a `known-limitation-synthetic` tag; all were resolved.

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

| Gap | Status | Description |
|-----|--------|-------------|
| **InfoLinks** | ✅ 4 specs | `info-link-to-profile`, `info-link-to-rule`, `info-link-to-infogroup`, `info-link-with-modifiers`. Tests shared profile/rule/infoGroup link resolution with modifier support. |
| **InfoGroups** | ✅ 4 specs | `infogroup-basic`, `infogroup-hidden`, `infogroup-multiple-profiles`, `infogroup-with-modifiers`. Covers basic resolution, hidden filtering, multiple profiles, and modifier application on infoGroup contents. |
| **Catalogue Links** | ✅ 2 specs | `catalogue-link-import`, `catalogue-link-shared-entry`. Tests cross-catalogue entry import and shared entry resolution through catalogue links. |
| **Publications** | ✅ 1 spec | `publication-on-catalogue`. Tests publication metadata on catalogue (informational only, no behavioral effect). |

### HIGH — Features with minimal coverage that are heavily used in real data

| Gap | Status | Evidence |
|-----|--------|----------|
| **Profile/Rule in selection state** | ✅ 8 specs | `profile-multiple-on-entry`, `profile-hidden`, `profile-inherited-from-link`, `profile-with-multiple-characteristics`, `rule-multiple-on-entry`, `rule-hidden`, `rule-with-page`, plus existing modifier specs. Hidden profiles/rules correctly filtered from state. |
| **Entry Link edge cases** | ✅ 6 specs | `entry-link-basic`, `entry-link-with-modifier`, `entry-link-to-group`, `entry-link-with-cost`, `entry-link-with-constraint`, `entry-link-cascading-modifiers`. Documented: link costs don't override target, link modifiers tested. |
| **Shared entry pools** | ✅ 1 spec + model | `shared-entry-via-entry-link` tests shared selection entry resolution through EntryLink. CatalogueSpec now supports `sharedSelectionEntries`, `sharedRules`, `sharedProfiles`, `sharedInfoGroups`. `constraint-shared` and `constraint-shared-deduplication` test shared constraint behavior. |
| **Nested child selections** | ✅ Expanded | `nested-children-deep` (parent+child cost aggregation), `selection-child-entry`, `selection-child-multiple`, `selection-child-with-cost`, `selection-with-children`, plus 14 other specs using nested entries. |
| **Forces query field** | ✅ Expanded | `constraint-include-child-forces` (cross-force constraints), `constraint-forces-field`, `scope-forces-field`, `scope-include-child-forces`, `scope-include-child-forces-nested`. |

### MEDIUM — Features with basic coverage that could be deeper

| Gap | Status | Notes |
|-----|--------|-------|
| Selection Entry Groups | ✅ 4 specs | `selection-entry-group-basic`, `-constraint`, `-default`, `entry-group-with-modifiers`. Group modifiers tested. |
| Collective entries | ✅ 4 specs | `selection-collective-create`, `-deselect`, `constraint-collective`, `collective-with-constraint`. |
| Hidden entry cascading | ✅ 4 specs | `selection-hidden-entry`, `hidden-cascade-to-children`, `profile-hidden`, `rule-hidden`. Hidden filtering behavior documented. |
| Condition group nesting | ✅ 5 specs | `condition-group-and`, `-or`, `-and-fails`, `-nested`, `condition-group-triple-nested` (3-level AND→OR→AND). |
| Dynamic constraint fields | ✅ Covered | `modifier-set-constraint`, `modifier-field-constraint-value`. Modifier targeting constraint IDs tested. |
| Cost type variety | ✅ 4 specs | `cost-multi-type`, `cost-multi-type-aggregation`, `cost-multi-force-aggregation`, `cost-three-types` (pts+PL+CP). |
| Selection type variety | ✅ Covered | `selection-model-type`, `selection-upgrade-type`, `selection-multiple-types`, `selection-model-with-cost`. |

### LOW — Polish and completeness

| Gap | Status | Notes |
|-----|--------|-------|
| Modifier application order | ✅ `modifier-order-set-then-append` | Verifies set+append ordering produces expected result |
| `import` attribute | ✅ 3 specs | `import-true-entry-visible-via-link`, `import-false-entry-hidden-via-link`, `import-false-entry-direct-use`. Tests import visibility control on entries. |
| Default cost limits | ✅ `cost-default-limit-positive` | Verifies positive limit triggers validation error |
| Entry link to group | ✅ `entry-link-to-group` | Tests EntryLink with type=selectionEntryGroup |
| Category modifier `add` | ✅ `modifier-category-add` | Tests category addition modifier |
| Repeat with includeChildSelections | ✅ `modifier-repeat-include-child-selections` | Tests repeat counting child selections |
| Constraint with includeChildForces | ✅ `constraint-include-child-forces` | Tests constraint across nested forces |
| `page` field on rules/profiles | ✅ `rule-with-page` | Tests page field on selection entry |

---

## 8. Real-World Data Coverage

The test suite includes **10 complex real-world roster tests** (`ComplexRealWorldRosterTests.cs`) using wh40k-9e catalogue data. These exercise multi-catalogue scenarios and are bonus tests outside the core 215 YAML spec suite. All 10 tests pass consistently.

### Remaining Model Gaps (Require Code Changes)

Most gaps from the original report have been addressed:

| Feature | Status | Notes |
|---------|--------|-------|
| **InfoLinkSpec** | ✅ Implemented | 4 specs covering profile/rule/infoGroup links with modifiers |
| **CatalogueLinkSpec** | ✅ Implemented | 2 specs covering cross-catalogue import and shared entry resolution |
| **Shared pools** | ✅ Implemented | CatalogueSpec now has sharedSelectionEntries/Rules/Profiles/InfoGroups |
| **PublicationSpec** | ✅ Implemented | 1 spec covering catalogue publications |
| **Multi-catalogue** | ✅ Implemented | `force-multi-catalogue-two-forces` tests multi-catalogue force creation |
| **`import` attribute** | ✅ Implemented | 3 specs covering import visibility control on entries via CatalogueLinks |

### Key Behavioral Findings (Documented in Specs)

- **Hidden profiles/rules/infoGroups**: BattleScribe filters hidden items from selection state output. Specs correctly assert absence of hidden items.
- **Append modifier**: BattleScribe's `append` modifier auto-prepends a space before the appended value (e.g., "Alpha" + append "Beta" → "Alpha Beta").
- **EntryLink costs**: EntryLink costs do not override the target entry's costs. The target's base cost applies when selected through a link.
- **EntryLink enumeration**: When both a direct SelectionEntry and an EntryLink point to the same target, only 1 entry appears in the available list (not 2).
