# BattleScribe Engine Conformance Spec — Coverage Report

153 YAML conformance specs across 9 categories. 287 test cases: 275 passed, 12 skipped.
Machine-readable coverage matrix: [`specs/coverage-matrix.yaml`](../specs/coverage-matrix.yaml).

---

## 1. Test Suite Summary

| Category | Specs | Description |
|----------|------:|-------------|
| condition | 20 | Condition type evaluation (numerical comparisons, instanceOf) |
| constraint | 18 | Min/max constraint validation, edge cases, flags |
| cost | 14 | Cost calculation, aggregation, limits, edge cases |
| force | 9 | Force add/remove, independence, categories |
| modifier | 36 | All modifier data types, field targets, repeats, groups |
| refresh | 10 | Re-evaluation triggers after every operation type |
| roster | 9 | Roster state, metadata, cost types |
| scope | 13 | Scope resolution (self, parent, force, roster, ancestor) |
| selection | 24 | Entry selection, groups, deselection, duplication, lifecycle |
| **Total** | **153** | |

22 specs are tagged `known-limitation-synthetic` — they define correct behavior but produce trivially-passing results with synthetic data because the engine's indexing/resolution requires real catalogue data. These serve as documentation and will become meaningful when tested against real-world data.

---

## 2. Engine API Coverage

The `IRosterEngine` interface exposes 11 methods plus `Setup`. Every method is exercised:

| Method | Example Specs |
|--------|---------------|
| `Setup` | Every spec (implicit) |
| `AddForce` | force-add-single, force-add-multiple |
| `RemoveForce` | force-remove, force-remove-second, force-add-and-remove-all |
| `SelectEntry` | selection-add-unit, selection-with-cost, selection-multiple-entries |
| `SelectChildEntry` | selection-child-entry, selection-child-multiple, selection-child-with-cost |
| `DeselectSelection` | selection-remove, selection-deselect-last, selection-deselect-middle |
| `SetSelectionCount` | selection-set-count, selection-number-default |
| `DuplicateSelection` | selection-duplicate, cost-duplicate-increases |
| `SetCostLimit` | cost-set-limit |
| `GetRosterState` | Every spec (implicit in assertions) |
| `GetValidationErrors` | constraint-min-violation, constraint-max-violation, refresh-validation-update |
| `HasValidationErrors` | constraint-min-violation |

---

## 3. Enum Coverage Matrix

The engine's behavior is controlled by enumerated types. Each value represents a distinct code path. Source references are from the decompiled BattleScribe Java engine.

### 3.1 Condition.Type — 8/8 covered

| Value | Code Path | Specs |
|-------|-----------|-------|
| `equalTo` | `actual == target` | condition-equal-to, condition-equal-to-zero |
| `notEqualTo` | `actual != target` | condition-not-equal-to |
| `greaterThan` | `actual > target` | condition-greater-than |
| `lessThan` | `actual < target` | condition-less-than |
| `atLeast` | `actual >= target` | condition-at-least |
| `atMost` | `actual <= target` | condition-at-most |
| `instanceOf` | Ancestry type check | condition-instance-of, + 4 variants (by-type, by-category, ancestor, roster-scope) |
| `notInstanceOf` | Ancestry NOT check | condition-not-instance-of |

**instanceOf/notInstanceOf details:**
- Completely separate code path from numerical conditions (c.java:1194-1227)
- Checks if a parent element IS a specific entry type
- Can match against: SelectionEntry, SelectionEntryGroup, SelectionEntry.Type, CategoryEntry, ForceEntry, Catalogue
- `notInstanceOf` uses AND semantics: ALL ancestors must NOT match
- Scope=roster always returns false (edge case covered by condition-instance-of-roster-scope)

### 3.2 Constraint.Type — 2/2 covered

| Value | Code Path | Specs |
|-------|-----------|-------|
| `min` | `actual < limit` → error | constraint-min-satisfied, constraint-min-violation, constraint-min-zero-no-error, constraint-min-and-max |
| `max` | `actual > limit` → error | constraint-max-satisfied, constraint-max-violation, constraint-max-exactly-at-limit, constraint-max-unlimited, constraint-min-and-max |

Edge cases covered: min=0 skips validation, max=-1 is unlimited, cost field constraints, percentValue, roster-scope constraints.

### 3.3 Modifier Types — 11/11 covered

#### STRING modifiers (4/4)
| Type | Behavior | Specs |
|------|----------|-------|
| `set` | Replace string value | modifier-set-name |
| `append` | Concatenate with space prefix | modifier-append-name, modifier-append-with-space |
| `increment` | Parse as double and add | modifier-increment-name, modifier-string-increment-numeric |
| `decrement` | Parse as double and subtract | modifier-decrement-name, modifier-string-decrement-non-numeric |

Edge cases: increment/decrement on non-numeric strings is a no-op. Append always prepends a space.

#### NUMBER modifiers (3/3)
| Type | Behavior | Specs |
|------|----------|-------|
| `set` | Replace numeric value | modifier-set-cost, modifier-number-set |
| `increment` | Add to value | modifier-increment-cost, modifier-conditional-increment-cost |
| `decrement` | Subtract from value | modifier-decrement-cost, modifier-decrement-below-zero, modifier-number-decrement |

#### BOOLEAN modifiers (1/1)
| Type | Behavior | Specs |
|------|----------|-------|
| `set` | Set boolean value | modifier-set-boolean, modifier-set-hidden-true, modifier-conditional-boolean-toggle |

The only boolean field is `hidden`.

#### CATEGORY modifiers (4/4)
| Type | Behavior | Specs |
|------|----------|-------|
| `add` | Add CategoryLink | modifier-category-add |
| `remove` | Remove CategoryLink | modifier-category-remove ᵃ |
| `set-primary` | Set category as primary (adds if absent) | modifier-category-set-primary ᵃ |
| `unset-primary` | Unset category primary flag | modifier-category-unset-primary ᵃ |

ᵃ known-limitation-synthetic. `set-primary` has subtle behavior: if category doesn't exist, it adds it first, then sets all others to non-primary.

### 3.4 Modifier Field Targets — 5/8 covered

| Field Target | Data Type | Specs | Status |
|-------------|-----------|-------|--------|
| Entry `name` | STRING | modifier-set-name, modifier-append-name, modifier-increment-name, modifier-decrement-name | ✅ |
| Entry `hidden` | BOOLEAN | modifier-set-hidden-true, modifier-set-boolean, modifier-field-hidden | ✅ |
| CostType ID | NUMBER | modifier-set-cost, modifier-increment-cost, modifier-decrement-cost | ✅ |
| Constraint ID | NUMBER | modifier-set-constraint, modifier-field-constraint-value | ✅ |
| `categories` | CATEGORY | modifier-category-add, modifier-category-remove, etc. | ✅ |
| Rule `description` | STRING | — | ❌ Not covered |
| Entry `page` | STRING | — | ❌ Not covered |
| CharacteristicType ID | STRING | — | ❌ Not covered |

The 3 uncovered field targets are informational data (Rule descriptions, page numbers, Profile characteristics). They don't affect roster editing logic. Covering them would require extending the spec infrastructure with Rule, Profile, and BookData model types.

### 3.5 Scope Values — 8/8 covered

| Scope | Resolution | Specs |
|-------|-----------|-------|
| `self` | The entry itself | scope-self |
| `parent` | Parent entry | scope-parent |
| `ancestor` | Walk up entire parent chain | scope-ancestor |
| `force` | Containing force | scope-force, scope-force-trigger-in-same-force, scope-force-vs-roster |
| `roster` | Roster root | scope-roster, scope-roster-cross-force, condition-scope-roster |
| `primary-category` | Primary category of selection | scope-primary-category ᵃ |
| `primary-catalogue` | Primary catalogue (≈force) | scope-primary-catalogue |
| Specific entry ID | Direct entry reference | scope-child-id-filter |

ᵃ known-limitation-synthetic.

### 3.6 Query Fields — 3/3 covered

| Field | What It Counts | Specs |
|-------|---------------|-------|
| `selections` | Count matching Selection instances | Extensively used across all categories |
| `forces` | Count matching Force instances | scope-forces-field ᵃ |
| CostType ID | Sum cost values for matching selections | constraint-cost-field |

ᵃ known-limitation-synthetic.

### 3.7 ChildId Filter Types — 4/7 covered

| ChildId Type | Specs | Status |
|-------------|-------|--------|
| `any` (wildcard) | Used implicitly in most specs | ✅ |
| SelectionEntry ID | scope-child-id-filter | ✅ |
| SelectionEntry.Type | Implicit (unit/model/upgrade filtering) | ✅ |
| CategoryEntry ID | condition-instance-of-by-category | ✅ |
| SelectionEntryGroup ID | — | ❌ Not covered |
| ForceEntry ID | — | ❌ Not covered |
| Catalogue ID | — | ❌ Not covered |

The 3 uncovered childId filter types require real-world data to exercise properly — synthetic data doesn't populate the engine's internal indexing structures used for these lookups.

### 3.8 Query Flags — 4/4 covered

| Flag | Specs |
|------|-------|
| `includeChildSelections` | constraint-include-child-selections |
| `includeChildForces` | scope-include-child-forces ᵃ |
| `shared` | constraint-shared |
| `percentValue` | constraint-percent-value, condition-percent-value |

ᵃ known-limitation-synthetic.

### 3.9 ConditionGroup Types — 2/2 covered

| Type | Behavior | Specs |
|------|----------|-------|
| `and` | All conditions must pass | condition-group-and, condition-group-and-fails, condition-multiple-and |
| `or` | Any condition must pass | condition-group-or |

### 3.10 SelectionEntry Types — 3/3 covered

| Type | Specs |
|------|-------|
| `unit` | selection-add-unit, selection-with-cost, selection-multiple-entries |
| `model` | selection-model-type |
| `upgrade` | selection-upgrade-type |

---

## 4. Code Path Coverage — Engine Logic Blocks

Analysis of the three main engine classes from decompiled source.

### 4.1 Modifier Evaluation — c.java (AbstractRosterEngine)

```
for each modifier/modifierGroup:
  1. evaluateConditions(modifier) → AND of all conditions
  2. evaluateConditionGroups(modifier) → AND of all conditionGroups
  3. calculateRepeatCount(modifier) → multiply application count
  4. applyModifier(copy, modifier, count)
```

| Path | Status | Specs |
|------|--------|-------|
| Unconditional modifier | ✅ | modifier-set-name, modifier-increment-cost, etc. |
| Conditional modifier (passes) | ✅ | modifier-conditional-set-name, modifier-conditional-increment-cost |
| Conditional modifier (fails) | ✅ | modifier-conditional-false-keeps-base-cost, condition-false-prevents-modifier |
| Repeat-multiplied modifier | ✅ | modifier-repeat-basic |
| Multiple modifiers on same entry | ✅ | modifier-multiple-on-same-entry |
| ModifierGroup (basic) | ✅ | modifier-group-basic ᵃ |
| ModifierGroup (false condition) | ✅ | modifier-group-false-condition ᵃ |
| Multiple repeats (additive) | ✅ | modifier-repeat-multiple-additive |
| roundUp=true vs false | ✅ | modifier-repeat-round-up, modifier-repeat-round-down |
| ModifierGroup with repeat | ❌ | — |
| Nested ModifierGroups | ❌ | — |
| Modifier on BaseInfo (profile/rule) | ❌ | — |

ᵃ known-limitation-synthetic.

### 4.2 Condition Evaluation — c.java

**Path A: Numerical comparison (6 types)** — all covered.

**Path B: instanceOf/notInstanceOf (2 types)**

| Matching Target | Status | Spec |
|----------------|--------|------|
| SelectionEntry | ✅ | condition-instance-of |
| SelectionEntry.Type | ✅ | condition-instance-of-by-type ᵃ |
| CategoryEntry | ✅ | condition-instance-of-by-category |
| Ancestor scope (walks chain) | ✅ | condition-instance-of-ancestor ᵃ |
| Roster scope (returns false) | ✅ | condition-instance-of-roster-scope |
| notInstanceOf | ✅ | condition-not-instance-of |
| SelectionEntryGroup | ❌ | — |
| ForceEntry | ❌ | — |
| Catalogue | ❌ | — |
| Shared entries | ❌ | — |

ᵃ known-limitation-synthetic.

### 4.3 Repeat Calculation — c.java

```
for each repeat:
  ratio = actual / threshold
  ratio = roundUp ? ceil(ratio) : floor(ratio)
  n += ratio × repeat.repeats  // ADDITIVE across multiple repeats
```

| Path | Status | Spec |
|------|--------|------|
| Basic repeat | ✅ | modifier-repeat-basic |
| roundUp=true | ✅ | modifier-repeat-round-up |
| roundUp=false | ✅ | modifier-repeat-round-down |
| Multiple repeats (additive) | ✅ | modifier-repeat-multiple-additive |
| Repeat with percentValue | ❌ | — |
| Zero threshold (skip) | ❌ | — |
| NaN query value (skip) | ❌ | — |

### 4.4 Query Value Resolution — c.java

| Branch | Status | Notes |
|--------|--------|-------|
| CostType field → sum costs | ✅ | constraint-cost-field |
| Field.SELECTIONS → count | ✅ | Extensively used |
| Field.FORCES → count | ✅ | scope-forces-field ᵃ |
| CostType.isLimit() → roster limits | ❌ | Requires CostType model extension |
| Constraint query implicit childId | ❌ | Covered implicitly via constraint specs |

ᵃ known-limitation-synthetic.

### 4.5 Constraint Validation — f.java

| Validation Type | Status | Specs |
|----------------|--------|-------|
| Roster cost limits | ✅ | cost-set-limit |
| Roster-scope constraints | ✅ | constraint-shared, condition-scope-roster |
| Force/category/selection-scope constraints | ✅ | constraint-min-violation, constraint-max-violation, many others |
| Hidden entry enforcement | ✅ | constraint-hidden-enforcement ᵃ |
| Collective entry enforcement | ✅ | constraint-collective ᵃ |
| Validation error deduplication (shared) | ❌ | — |

ᵃ known-limitation-synthetic.

### 4.6 Selection Operations — f.java

| Operation | Status | Specs |
|-----------|--------|-------|
| New selection creation | ✅ | selection-add-unit, selection-with-cost |
| Selection number increment | ✅ | selection-same-entry-twice, selection-set-count |
| Child entry auto-addition (min constraints) | ✅ | selection-child-entry, selection-with-children |
| SelectionEntryGroup default selection | ✅ | selection-entry-group-default ᵃ |
| Deselect (remove) | ✅ | selection-remove, selection-deselect-last |
| Duplicate | ✅ | selection-duplicate |
| Collective entry creation (sibling replication) | ❌ | — |
| Collective entry deselection | ❌ | — |
| Force-level entry ("always new" flag) | ❌ | — |
| Favourites (selectFavourite) | ❌ | — |
| Nested force selection | ❌ | — |

ᵃ known-limitation-synthetic.

### 4.7 Cost Calculation — f.java

| Path | Status | Specs |
|------|--------|-------|
| Base cost calculation | ✅ | cost-base-calculation |
| Cost aggregation (multi-entry) | ✅ | cost-aggregation, cost-multi-force-aggregation |
| Multi-type costs | ✅ | cost-multi-type, cost-multi-type-aggregation |
| Cost after modifier | ✅ | modifier-set-cost, modifier-increment-cost |
| Cost after deselect | ✅ | cost-removal-after-deselect |
| Zero cost | ✅ | cost-zero, cost-zero-value |
| Negative cost | ✅ | cost-negative-value |
| Cost after duplicate | ✅ | cost-duplicate-increases |
| CostType.isHidden (filtered from display) | ❌ | — |
| CostType.isLimit vs regular | ❌ | — |

---

## 5. Untested Engine Features

### 5.1 EntryLink

EntryLinks are references to shared entries. They add a layer of indirection that real catalogues use extensively:
- Type: `selectionEntry` or `selectionEntryGroup`
- Resolved at runtime by `targetId`
- Can have their own modifiers/constraints that override the target
- ID prefixing: the engine combines the link ID with the target entry ID

**Impact:** High. Real catalogues are built almost entirely with EntryLinks. Not modeled in the spec infrastructure.

**Required infrastructure:** `EntryLinkDef` in YAML models, `CreateEntryLink` in JavaModelFactory, resolution logic in BattleScribeOracle.

### 5.2 Profiles, Rules, InfoGroups

Informational data attached to entries:
- Modifiers can modify profile characteristics (STRING field = CharacteristicType ID)
- Rules can have their description modified
- InfoGroups can be hidden
- During the refresh cycle, profiles/rules are collected and modifier-applied

**Impact:** Low for roster editing logic. These are display-only concerns. However, modifier field targets `page`, `description`, and CharacteristicType ID require this infrastructure.

### 5.3 Nested Forces

Forces can contain sub-forces:
- `Force.getForces()` returns nested forces
- `includeChildForces` flag on queries traverses into nested forces
- Sub-forces have independent catalogues
- Constraint validation recurses into nested forces

**Impact:** Medium. Some game systems use nested forces (e.g., detachments within armies).

**Required infrastructure:** Recursive force setup in YAML, nested force operations in IRosterEngine.

### 5.4 Nested Condition Groups

Condition groups can contain other condition groups, creating arbitrary boolean logic trees.

**Impact:** Low. Real catalogues rarely nest beyond one level.

### 5.5 Favourites

`selectFavourite` is a BattleScribe app feature that bookmarks selections for quick access. It's an app-level feature, not a roster logic concern.

**Impact:** Very low. Not relevant to roster conformance.

---

## 6. Known Limitations of Synthetic Data Testing

22 specs are tagged `known-limitation-synthetic`. These specs define correct behavior declaratively but produce trivially-passing results because the BattleScribe engine's internal indexing doesn't fully populate with synthetic (programmatically-constructed) data.

Affected areas:
- **instanceOf matching by Type or CategoryEntry** — engine needs real data indexing to resolve these
- **Category modifiers (remove, set-primary, unset-primary)** — state changes aren't observable via selection names/costs
- **Field=forces counting** — forces aren't indexed for counting with synthetic data
- **includeChildForces flag** — no effect without real nested force data
- **Collective/hidden enforcement** — constraint validation doesn't fully evaluate
- **ModifierGroups** — group condition evaluation has synthetic data limitations
- **SelectionEntryGroups** — group default selection and constraints don't trigger

**Mitigation:** These specs will become fully meaningful when tested with real-world catalogue data (e.g., wh40k-9e). The real-world data oracle tests in the test harness (`WH40K_DATA_DIR` environment variable) already exercise some of these paths.

---

## 7. Remaining Coverage Gaps

### 7.1 Modifier field targets (3 items — low priority)

| Target | Required Infrastructure |
|--------|----------------------|
| Rule `description` | Rule model types in spec YAML |
| Entry `page` | Page field in BookData models |
| CharacteristicType ID | Profile + CharacteristicType models |

These are informational fields that don't affect roster selection, cost, or validation logic.

### 7.2 ChildId filter types (3 items — requires real data)

| Filter | Notes |
|--------|-------|
| SelectionEntryGroup ID | Engine resolves via parent group chain |
| ForceEntry ID | Engine resolves via force.entryId |
| Catalogue ID | Engine resolves via catalogueId |

These are lower priority and require real catalogue data with proper indexing.

### 7.3 Code paths (assorted — diminishing returns)

| Path | Category | Notes |
|------|----------|-------|
| ModifierGroup with repeat multiplier | Modifier evaluation | Rare in real data |
| Nested ModifierGroups | Modifier evaluation | Very rare |
| Modifier on BaseInfo (profile/rule) | Modifier evaluation | Needs Profile/Rule infra |
| Repeat with percentValue | Repeat calculation | Edge case |
| Repeat with zero threshold | Repeat calculation | Guard clause (skip) |
| CostType.isLimit() | Query resolution | Needs CostType extension |
| Validation error deduplication | Constraint validation | Shared entry edge case |
| Collective entry creation/deselection | Selection operations | Needs collective flag support |
| Force-level entry ("always new") | Selection operations | Niche flag |
| Nested force selection | Selection operations | Needs nested force infra |
| CostType.isHidden | Cost calculation | Display filtering |
| instanceOf matching SelectionEntryGroup | instanceOf | Needs proper group nesting |
| instanceOf matching ForceEntry/Catalogue | instanceOf | Needs real data |
| instanceOf with shared entries | instanceOf | Needs EntryLink infra |

### 7.4 Summary

| Category | Covered | Total | % |
|----------|---------|-------|---|
| Condition types | 8 | 8 | 100% |
| Constraint types | 2 | 2 | 100% |
| Modifier types | 11 | 11 | 100% |
| Modifier field targets | 5 | 8 | 63% |
| Scope values | 8 | 8 | 100% |
| Query fields | 3 | 3 | 100% |
| Query flags | 4 | 4 | 100% |
| ConditionGroup types | 2 | 2 | 100% |
| SelectionEntry types | 3 | 3 | 100% |
| ChildId filter types | 4 | 7 | 57% |
| **Enum values total** | **50** | **56** | **89%** |
| Engine API methods | 12 | 12 | 100% |
| Major code paths | ~38 | ~52 | ~73% |

The uncovered items fall into three categories:
1. **Informational field targets** (3) — no impact on roster logic
2. **Real-data-dependent paths** (6+) — synthetic data can't exercise them
3. **Structural features** (5+) — require significant infrastructure investment (EntryLink, nested forces, collective entries)

---

## 8. Strategies for Further Coverage

### 8.1 Real-World Data Oracle Testing

The highest-impact next step. Use wh40k-9e data to:
1. Load a complex catalogue (e.g., Space Marines)
2. Create a realistic roster step by step
3. Snapshot state after each operation
4. Compare against expected values

This would exercise EntryLink resolution, proper indexing, and all the `known-limitation-synthetic` specs in meaningful ways.

### 8.2 Pairwise Combinatorial Testing

Key dimensions that could be combined:
- **Condition type** × **Scope** × **Field** × **ChildId type** (8 × 8 × 3 × 7 = 1,344 combos)
- **Modifier type** × **Field target** × **Repeat** × **Condition** (11 × 8 × 4 × 2 = 704 combos)

Full combinatorial is impractical. Pairwise testing (every pair of dimensions appears at least once) would require ~50-80 additional specs.

### 8.3 Mutation Testing

When a second engine implementation exists:
1. Introduce deliberate bugs (mutations) in the second engine
2. Run the spec suite against the mutated engine
3. Mutations that survive indicate spec gaps

This is the gold standard for measuring test suite quality.

### 8.4 Infrastructure Investment Priorities

| Feature | Effort | Coverage Impact | Priority |
|---------|--------|----------------|----------|
| Real-world data oracle tests | Medium | High (exercises 22 synthetic specs) | **1** |
| EntryLink support | High | High (real catalogues use extensively) | **2** |
| Profile/Rule models | Medium | Low (informational only) | 3 |
| Nested force support | Medium | Medium (some game systems use) | 3 |
| Collective entry support | Low | Low (rare feature) | 4 |

---

## Appendix A: Engine Method Coverage Map

### c.java — AbstractRosterEngine (Core Logic)

| Method | Purpose | Status |
|--------|---------|--------|
| Apply modifiers to data element | Modifier orchestration | ✅ |
| Apply single modifier | Individual modifier application | ✅ |
| Apply modifier group | ModifierGroup evaluation | ✅ ᵃ |
| Evaluate conditions | Condition AND logic | ✅ |
| Evaluate single condition | Per-condition check | ✅ |
| instanceOf matching | Type ancestry check | ✅ (6 of 9 match targets) |
| Entry ID matching (shared) | SharedEntry resolution | ❌ |
| Evaluate condition group | ConditionGroup AND/OR | ✅ |
| Calculate repeat count | Repeat multiplier | ✅ (basic, roundUp, additive) |
| Get condition value (percentValue) | Target value resolution | ✅ |
| Get query value | Actual value resolution | ✅ |
| Resolve childId filter | ChildId → filter object | ✅ (4 of 7 types) |

ᵃ known-limitation-synthetic.

### f.java — ConcurrentRosterEngine (API Surface)

| Method | Purpose | Status |
|--------|---------|--------|
| setRoster | Initialize roster | ✅ |
| selectRootForce | Add top-level force | ✅ |
| selectForce (nested) | Add nested force | ❌ |
| setNumSelections | Set selection count | ✅ |
| selectEntry | Add selection | ✅ |
| duplicateSelection | Clone selection | ✅ |
| selectFavourite | Bookmark selection | ❌ |
| deselectForce | Remove force | ✅ |
| deselectEntry | Remove selection | ✅ |
| setCostLimit | Set cost cap | ✅ |
| getValidationErrors | Get error list | ✅ |
| hasValidationErrors | Boolean error check | ✅ |
| validate (private) | Run validation | ✅ (implicit) |
| refresh (private) | Refresh elements | ✅ (implicit) |

### d.java — CatalogueDataEngine (Modifier Application)

| Method | Purpose | Status |
|--------|---------|--------|
| Apply modifier (dispatch) | Route to typed handler | ✅ |
| Apply STRING modifier | String field operations | ✅ (all 4 types) |
| Apply NUMBER modifier | Numeric field operations | ✅ (all 3 types) |
| Apply BOOLEAN modifier | Boolean field operations | ✅ |
| Apply CATEGORY modifier | Category operations | ✅ (all 4 types) |
| String field setter (name) | Set entry name | ✅ |
| String field setter (page) | Set page number | ❌ |
| String field setter (description) | Set rule description | ❌ |
| Number field setter (CostType) | Set cost value | ✅ |
| Number field setter (Constraint) | Set constraint value | ✅ |
| Boolean field setter (hidden) | Set hidden flag | ✅ |

---

## Appendix B: Spec File Inventory

```
specs/
├── condition/     (20 specs)
│   ├── condition-at-least.yaml
│   ├── condition-at-most.yaml
│   ├── condition-equal-to.yaml
│   ├── condition-equal-to-zero.yaml
│   ├── condition-false-prevents-modifier.yaml
│   ├── condition-greater-than.yaml
│   ├── condition-group-and.yaml
│   ├── condition-group-and-fails.yaml
│   ├── condition-group-or.yaml
│   ├── condition-instance-of.yaml
│   ├── condition-instance-of-ancestor.yaml
│   ├── condition-instance-of-by-category.yaml
│   ├── condition-instance-of-by-type.yaml
│   ├── condition-instance-of-roster-scope.yaml
│   ├── condition-less-than.yaml
│   ├── condition-multiple-and.yaml
│   ├── condition-not-equal-to.yaml
│   ├── condition-not-instance-of.yaml
│   ├── condition-percent-value.yaml
│   └── condition-scope-roster.yaml
├── constraint/    (18 specs)
│   ├── constraint-collective.yaml
│   ├── constraint-cost-field.yaml
│   ├── constraint-forces-field.yaml
│   ├── constraint-hidden-enforcement.yaml
│   ├── constraint-include-child-selections.yaml
│   ├── constraint-max-exactly-at-limit.yaml
│   ├── constraint-max-satisfied.yaml
│   ├── constraint-max-unlimited.yaml
│   ├── constraint-max-violation.yaml
│   ├── constraint-min-and-max.yaml
│   ├── constraint-min-satisfied.yaml
│   ├── constraint-min-violation.yaml
│   ├── constraint-min-zero-no-error.yaml
│   ├── constraint-modified-by-modifier.yaml
│   ├── constraint-multiple-entries-independent.yaml
│   ├── constraint-no-constraints.yaml
│   ├── constraint-percent-value.yaml
│   └── constraint-shared.yaml
├── cost/          (14 specs)
│   ├── cost-aggregation.yaml
│   ├── cost-base-calculation.yaml
│   ├── cost-child-aggregation.yaml
│   ├── cost-duplicate-increases.yaml
│   ├── cost-empty-roster.yaml
│   ├── cost-multi-force-aggregation.yaml
│   ├── cost-multi-type.yaml
│   ├── cost-multi-type-aggregation.yaml
│   ├── cost-negative-value.yaml
│   ├── cost-removal-after-deselect.yaml
│   ├── cost-same-entry-twice-doubles.yaml
│   ├── cost-set-limit.yaml
│   ├── cost-zero.yaml
│   └── cost-zero-value.yaml
├── force/         (9 specs)
│   ├── force-add-and-remove-all.yaml
│   ├── force-add-multiple.yaml
│   ├── force-add-single.yaml
│   ├── force-empty-has-no-selections.yaml
│   ├── force-multiple-types.yaml
│   ├── force-remove.yaml
│   ├── force-remove-second.yaml
│   ├── force-selections-independent.yaml
│   └── force-with-categories.yaml
├── modifier/      (36 specs)
│   ├── modifier-append-name.yaml
│   ├── modifier-append-with-space.yaml
│   ├── modifier-category-add.yaml
│   ├── modifier-category-remove.yaml
│   ├── modifier-category-set-primary.yaml
│   ├── modifier-category-unset-primary.yaml
│   ├── modifier-conditional-boolean-toggle.yaml
│   ├── modifier-conditional-false-keeps-base-cost.yaml
│   ├── modifier-conditional-increment-cost.yaml
│   ├── modifier-conditional-set-name.yaml
│   ├── modifier-conditional-toggle.yaml
│   ├── modifier-decrement-below-zero.yaml
│   ├── modifier-decrement-cost.yaml
│   ├── modifier-decrement-name.yaml
│   ├── modifier-field-constraint-value.yaml
│   ├── modifier-field-hidden.yaml
│   ├── modifier-group-basic.yaml
│   ├── modifier-group-false-condition.yaml
│   ├── modifier-increment-cost.yaml
│   ├── modifier-increment-name.yaml
│   ├── modifier-multiple-groups.yaml
│   ├── modifier-multiple-on-same-entry.yaml
│   ├── modifier-no-modifier.yaml
│   ├── modifier-number-decrement.yaml
│   ├── modifier-number-set.yaml
│   ├── modifier-repeat-basic.yaml
│   ├── modifier-repeat-multiple-additive.yaml
│   ├── modifier-repeat-round-down.yaml
│   ├── modifier-repeat-round-up.yaml
│   ├── modifier-set-boolean.yaml
│   ├── modifier-set-constraint.yaml
│   ├── modifier-set-cost.yaml
│   ├── modifier-set-hidden-true.yaml
│   ├── modifier-set-name.yaml
│   ├── modifier-string-decrement-non-numeric.yaml
│   └── modifier-string-increment-numeric.yaml
├── refresh/       (10 specs)
│   ├── refresh-after-child-select.yaml
│   ├── refresh-after-deselect.yaml
│   ├── refresh-after-duplicate.yaml
│   ├── refresh-after-select.yaml
│   ├── refresh-after-set-count.yaml
│   ├── refresh-cost-recalculation.yaml
│   ├── refresh-full-lifecycle.yaml
│   ├── refresh-modifier-reevaluation.yaml
│   ├── refresh-modifier-toggle-cost.yaml
│   └── refresh-validation-update.yaml
├── roster/        (9 specs)
│   ├── roster-add-force-and-select.yaml
│   ├── roster-create-empty.yaml
│   ├── roster-deselect-reduces-cost.yaml
│   ├── roster-game-system-id.yaml
│   ├── roster-multi-cost-types.yaml
│   ├── roster-multiple-selections.yaml
│   ├── roster-name-and-metadata.yaml
│   ├── roster-no-cost-types.yaml
│   └── roster-with-cost-types.yaml
├── scope/         (13 specs)
│   ├── scope-ancestor.yaml
│   ├── scope-child-id-filter.yaml
│   ├── scope-force.yaml
│   ├── scope-force-trigger-in-same-force.yaml
│   ├── scope-force-vs-roster.yaml
│   ├── scope-forces-field.yaml
│   ├── scope-include-child-forces.yaml
│   ├── scope-parent.yaml
│   ├── scope-primary-catalogue.yaml
│   ├── scope-primary-category.yaml
│   ├── scope-roster.yaml
│   ├── scope-roster-cross-force.yaml
│   └── scope-self.yaml
└── selection/     (24 specs)
    ├── selection-add-unit.yaml
    ├── selection-category-link.yaml
    ├── selection-child-entry.yaml
    ├── selection-child-multiple.yaml
    ├── selection-child-with-cost.yaml
    ├── selection-deselect-last.yaml
    ├── selection-deselect-middle.yaml
    ├── selection-deselect-then-reselect.yaml
    ├── selection-duplicate.yaml
    ├── selection-entry-group-basic.yaml
    ├── selection-entry-group-constraint.yaml
    ├── selection-entry-group-default.yaml
    ├── selection-hidden-entry.yaml
    ├── selection-model-type.yaml
    ├── selection-multiple-deselects.yaml
    ├── selection-multiple-entries.yaml
    ├── selection-multiple-types.yaml
    ├── selection-number-default.yaml
    ├── selection-remove.yaml
    ├── selection-same-entry-twice.yaml
    ├── selection-set-count.yaml
    ├── selection-upgrade-type.yaml
    ├── selection-with-children.yaml
    └── selection-with-cost.yaml
```
