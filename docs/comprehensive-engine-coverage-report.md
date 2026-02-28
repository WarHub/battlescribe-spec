# Research Report: Comprehensive BattleScribe Engine Coverage Strategy

## Problem Statement

We have 117 YAML conformance specs covering 9 categories of BattleScribe roster engine behavior.
The question: **How can we be certain our test suite covers the ENTIRE engine logic—every code path, every edge case, every enum value?**

This report systematically analyzes the decompiled BattleScribe Java engine to:
1. Enumerate every testable dimension exhaustively
2. Identify exact coverage gaps in our current 117 specs
3. Propose a methodology for achieving provable completeness

---

## 1. Exhaustive Enum Coverage Matrix

The engine's behavior is fundamentally controlled by enumerated types. Each enum value represents a distinct code path. Here is the complete inventory from decompiled source.

### 1.1 Condition.Type (8 values) — `model/data/Condition.java:56-64`

| Value | Code Path | Current Coverage | Gap? |
|-------|-----------|-----------------|------|
| `equalTo` | `actual == target` | ✅ `condition-equal-to.yaml` | |
| `notEqualTo` | `actual != target` | ✅ `condition-not-equal-to.yaml` | |
| `greaterThan` | `actual > target` | ✅ `condition-greater-than.yaml` | |
| `lessThan` | `actual < target` | ✅ `condition-less-than.yaml` | |
| `atLeast` | `actual >= target` | ✅ `condition-at-least.yaml` | |
| `atMost` | `actual <= target` | ✅ `condition-at-most.yaml` | |
| `instanceOf` | ancestry type check | ✅ `condition-instance-of.yaml` | Partially — only basic case |
| `notInstanceOf` | ancestry NOT type check | ❌ **MISSING** | **YES** |

**instanceOf/notInstanceOf** are completely different code paths from numerical conditions (c.java lines 1194-1227):
- They check if a parent element IS a specific entry type
- Can match against: SelectionEntry, SelectionEntryGroup, SelectionEntry.Type, CategoryEntry, ForceEntry, Catalogue
- Use ancestor scope to walk up the parent chain
- `notInstanceOf` uses AND semantics: ALL ancestors must NOT match

**Specs needed:**
- `condition-not-instance-of.yaml` — basic notInstanceOf
- `condition-instance-of-by-type.yaml` — instanceOf matching SelectionEntry.Type
- `condition-instance-of-by-category.yaml` — instanceOf matching CategoryEntry
- `condition-instance-of-ancestor.yaml` — instanceOf with ancestor scope (walks chain)
- `condition-instance-of-by-force-entry.yaml` — instanceOf matching ForceEntry

### 1.2 Constraint.Type (2 values) — `model/data/Constraint.java:82-84`

| Value | Code Path | Current Coverage | Gap? |
|-------|-----------|-----------------|------|
| `min` | `actual < limit` → error | ✅ Multiple specs | |
| `max` | `actual > limit` → error | ✅ Multiple specs | |

Both covered, but edge cases exist:
- **min=0**: skip validation (line 410) ✅ `constraint-min-zero-no-error.yaml`
- **max=-1**: unlimited, skip (line 410) ✅ `constraint-max-unlimited.yaml`
- **Constraint with cost field** ✅ `constraint-cost-field.yaml`
- **Constraint with percentValue** ✅ `constraint-percent-value.yaml`
- **Constraint on roster scope** — partially tested

**Missing constraint edge cases:**
- `constraint-modified-by-modifier.yaml` — modifier targeting constraint ID as field ❌
- `constraint-force-scope.yaml` — constraint at force scope (vs roster/parent) ❌
- `constraint-collective-entry.yaml` — collective entries divide by selection number ❌
- `constraint-hidden-entry-enforcement.yaml` — hidden entry with selections → error ❌
- `constraint-forces-field.yaml` — constraint on forces count ❌
- `constraint-include-child-forces.yaml` — includeChildForces flag on constraint ❌

### 1.3 Modifier.DataType + ModifierType (4 × 11 = 11 distinct types)

#### STRING modifiers — `Modifier.java:186-191`
| Type | Behavior | Coverage | Gap? |
|------|----------|----------|------|
| `set` | Replace string | ✅ `modifier-set-name.yaml` | |
| `append` | Concatenate with space prefix | ✅ `modifier-append-name.yaml` | |
| `increment` | Parse as double, add | ✅ `modifier-increment-name.yaml` | |
| `decrement` | Parse as double, subtract | ✅ `modifier-decrement-name.yaml` | |

Edge cases:
- Increment/decrement on non-numeric strings → no-op (d.java lines 897-906)
- Append adds " " prefix, not just concatenation

#### NUMBER modifiers — `Modifier.java:150-154`
| Type | Behavior | Coverage | Gap? |
|------|----------|----------|------|
| `set` | Replace value | ✅ `modifier-set-cost.yaml` | |
| `increment` | Add value | ✅ `modifier-increment-cost.yaml` | |
| `decrement` | Subtract value | ✅ `modifier-decrement-cost.yaml` | |

Edge cases:
- Non-numeric value string → no-op (d.java line 914)
- Modifier targeting constraint ID (NUMBER field) — see constraint modification

#### BOOLEAN modifiers — `Modifier.java:116-118`
| Type | Behavior | Coverage | Gap? |
|------|----------|----------|------|
| `set` | Set boolean value | ✅ `modifier-set-boolean.yaml`, `modifier-set-hidden-true.yaml` | |

Only one boolean field exists: `hidden` (BaseModifyableData.ModifierField.HIDDEN)

#### CATEGORY modifiers — `Modifier.java:79-84`
| Type | Behavior | Coverage | Gap? |
|------|----------|----------|------|
| `add` | Add CategoryLink | ✅ `modifier-category-add.yaml` | |
| `remove` | Remove CategoryLink | ❌ **MISSING** | **YES** |
| `set-primary` | Set category as primary (adds if missing) | ❌ **MISSING** | **YES** |
| `unset-primary` | Unset category primary flag | ❌ **MISSING** | **YES** |

**Specs needed:**
- `modifier-category-remove.yaml`
- `modifier-category-set-primary.yaml`
- `modifier-category-unset-primary.yaml`

Note: SET_PRIMARY has a subtle behavior — if category doesn't exist, it ADDS it first, then sets all others to non-primary (d.java lines 969-978).

### 1.4 Modifier Field Targets — `d.java resolveModifierField()`

| Field Target | DataType | Source | Coverage | Gap? |
|-------------|----------|--------|----------|------|
| Entry `name` | STRING | `BaseData.ModifierField.NAME` | ✅ | |
| Entry `hidden` | BOOLEAN | `BaseModifyableData.ModifierField.HIDDEN` | ✅ | |
| CostType ID | NUMBER | `CostType` instance | ✅ | |
| Constraint ID | NUMBER | `Constraint` instance | ⚠️ `modifier-set-constraint.yaml` | Partial |
| Rule `description` | STRING | `Rule.ModifierField.DESCRIPTION` | ❌ **MISSING** | **YES** |
| Entry `page` | STRING | `BaseBookData.ModifierField.PAGE` | ❌ **MISSING** | **YES** |
| CharacteristicType ID | STRING | Profile characteristic | ❌ **MISSING** | **YES** |
| `categories` | CATEGORY | category operations | ✅ (add only) | Partial |

**Specs needed:**
- `modifier-rule-description.yaml` — modify Rule.description (STRING)
- `modifier-page.yaml` — modify page field (STRING)
- `modifier-profile-characteristic.yaml` — modify Profile characteristic value

### 1.5 BaseQuery.Scope (7 + entry ID) — `BaseQuery.java:149-157`

| Scope | Resolution | Coverage | Gap? |
|-------|-----------|----------|------|
| `self` | The entry itself | ✅ `scope-self.yaml` | |
| `parent` | Parent entry | ✅ `scope-parent.yaml` | |
| `ancestor` | Walk up entire path | ✅ `scope-ancestor.yaml` | |
| `force` | Containing Force | ✅ `scope-force.yaml` | |
| `roster` | Roster root | ✅ `scope-roster.yaml` | |
| `primary-category` | Primary category of selection | ❌ **MISSING** | **YES** |
| `primary-catalogue` | Primary catalogue (≈force) | ✅ `scope-primary-catalogue.yaml` | |
| specific entry ID | Direct entry reference | ✅ `scope-child-id-filter.yaml` | |

**Spec needed:**
- `scope-primary-category.yaml`

### 1.6 BaseQuery.Field (2 + CostType IDs) — `BaseQuery.java:114-117`

| Field | What it counts | Coverage | Gap? |
|-------|---------------|----------|------|
| `selections` | Count matching Selection instances | ✅ extensively used | |
| `forces` | Count matching Force instances | ❌ **MISSING** | **YES** |
| CostType ID | Sum cost values for matching selections | ✅ `constraint-cost-field.yaml` | |

**Spec needed:**
- `scope-forces-field.yaml` — condition/constraint using `forces` field

### 1.7 BaseFilteredQuery.Child — `BaseFilteredQuery.java:91-93`

| ChildId Type | Matches | Coverage | Gap? |
|-------------|---------|----------|------|
| `any` | Everything (wildcard) | ✅ used implicitly | |
| SelectionEntry ID | Specific entry | ✅ `scope-child-id-filter.yaml` | |
| SelectionEntryGroup ID | Entry group | ❌ **MISSING** | **YES** |
| SelectionEntry.Type | Entry type (unit/model/upgrade) | ✅ implicit | |
| CategoryEntry ID | Category membership | ❌ **MISSING** | **YES** |
| ForceEntry ID | Force entry | ❌ **MISSING** | **YES** |
| Catalogue ID | Catalogue origin | ❌ **MISSING** | **YES** |

**Specs needed:**
- `scope-child-id-selection-entry-group.yaml`
- `scope-child-id-category-entry.yaml`
- `scope-child-id-force-entry.yaml`

### 1.8 SelectionEntry.Type (3 values) — `SelectionEntry.java:59-63`

| Type | Coverage | Gap? |
|------|----------|------|
| `unit` | ✅ `selection-add-unit.yaml` | |
| `model` | ✅ `selection-model-type.yaml` | |
| `upgrade` | ✅ `selection-upgrade-type.yaml` | |

All covered.

### 1.9 ConditionGroup.Type (2 values) — `ConditionGroup.java:75-78`

| Type | Behavior | Coverage | Gap? |
|------|----------|----------|------|
| `and` | All conditions must pass | ✅ `condition-group-and.yaml` | |
| `or` | Any condition must pass | ✅ `condition-group-or.yaml` | |

Both covered. Missing:
- Nested ConditionGroup (group within group) ❌
- `condition-group-and-fails.yaml` covers AND failure

### 1.10 Query Flags

| Flag | Coverage | Gap? |
|------|----------|------|
| `includeChildSelections` | ✅ `constraint-include-child-selections.yaml` | |
| `includeChildForces` | ❌ **MISSING** | **YES** |
| `shared` | ✅ `constraint-shared.yaml` (partially) | |
| `percentValue` | ✅ `constraint-percent-value.yaml` | |

---

## 2. Code Path Analysis — Major Logic Blocks

### 2.1 Modifier Evaluation (c.java lines 1109-1165)

```
for each modifier/modifierGroup:
  1. evaluateConditions(modifier) → AND of all conditions
  2. evaluateConditionGroups(modifier) → AND of all conditionGroups  
  3. calculateRepeatCount(modifier) → multiply application count
  4. applyModifier(copy, modifier, count)
```

**Covered paths:**
- ✅ Unconditional modifier (empty conditions)
- ✅ Conditional modifier that passes
- ✅ Conditional modifier that fails
- ✅ Repeat-multiplied modifier
- ✅ Multiple modifiers on same entry
- ✅ ModifierGroup (basic)
- ✅ ModifierGroup with false condition

**Missing paths:**
- ❌ ModifierGroup with repeat multiplier
- ❌ Nested ModifierGroups (group within group)
- ❌ Multiple repeats (additive: `n += n2` at line 1367)
- ❌ Repeat with roundUp=true vs roundUp=false distinction
- ❌ Modifier on BaseInfo (profile/rule) — appends condition text to name (line 1131-1133)

### 2.2 Condition Evaluation (c.java lines 1185-1307)

Two completely separate paths:

**Path A: Numerical comparison (6 types)**
```
1. resolveScope → rosterElement
2. getQueryValue(condition, rosterElement) → actual
3. getConditionValue(condition) → target (possibly percentValue)
4. compare(actual, target)
```

Covered: ✅ All 6 numerical comparison types

**Path B: instanceOf/notInstanceOf (2 types)**
```
1. resolveScope → if ROSTER, return false
2. resolveChildFilter → IFilteredQueryChild
3. if ANCESTOR scope: collect all ancestors
4. else: resolve single rosterElement
5. for each element: check if matches childFilter
```

instanceOf matching logic (c.java lines 1263-1307):
- `BaseFilteredQuery.Child.ANY` → always true
- `Selection` vs `SelectionEntry` → match by entry ID (or sharedEntryId if shared)
- `Selection` vs `SelectionEntryGroup` → check if entry's parent groups match
- `Selection` vs `SelectionEntry.Type` → check selection.type == type.id
- Any vs `CategoryEntry` → check category membership
- `Force` vs `ForceEntry` → check entryId match
- `Force` vs `Catalogue` → check catalogueId match

**Missing paths:**
- ❌ instanceOf with scope=roster (returns false — edge case)
- ❌ instanceOf with ancestor scope (walks chain)
- ❌ instanceOf matching SelectionEntryGroup
- ❌ instanceOf matching SelectionEntry.Type
- ❌ instanceOf matching CategoryEntry
- ❌ instanceOf matching ForceEntry
- ❌ instanceOf matching Catalogue
- ❌ notInstanceOf (completely untested)
- ❌ instanceOf with shared entries

### 2.3 Repeat Calculation (c.java lines 1349-1370)

```
for each repeat:
  1. resolveScope → rosterElement
  2. getConditionValue(repeat) → threshold
  3. getQueryValue(repeat, rosterElement) → actual
  4. ratio = actual / threshold
  5. ratio = roundUp ? ceil(ratio) : floor(ratio)
  6. n2 = ratio × repeat.repeats
  7. n += n2  (ADDITIVE across multiple repeats)
```

**Covered:**
- ✅ Basic repeat (single repeat)

**Missing:**
- ❌ Multiple repeats (additive behavior)
- ❌ roundUp=true vs roundUp=false distinction
- ❌ Repeat with percentValue
- ❌ Repeat with zero threshold (skip — line 1359)
- ❌ Repeat with NaN query value (skip)

### 2.4 Query Value Resolution (c.java lines 1386-1444)

Three branches based on field type:
1. **CostType field** → sum costs for matching selections
   - Special: `CostType.isLimit()` → use roster cost limits instead
2. **Field.SELECTIONS** → count matching selections
3. **Field.FORCES** → count matching forces

For Constraint queries specifically, the childId is the BaseEntry itself (line 1397).

**Missing:**
- ❌ CostType with isLimit() flag
- ❌ Field.FORCES query resolution
- ❌ Constraint query childId resolution (implicit self)

### 2.5 Constraint Validation (f.java lines 356-662)

Five validation types:
1. **Roster cost limits** (lines 361-366): `actualCost > costLimit` → error
2. **Roster-scope constraints** (lines 402-413): constraints with scope=roster
3. **Force/Category/Selection-scope constraints** (lines 416-504): per-element validation
4. **Hidden entry enforcement** (lines 617-625): hidden entry with selections → error
5. **Collective entry enforcement** (lines 641-661): sibling selections must have equal counts

**Covered:**
- ✅ Basic min/max validation
- ✅ Cost limit validation (cost-set-limit.yaml)
- ✅ Unlimited (max=-1) and not-required (min=0)

**Missing:**
- ❌ Hidden entry enforcement ("cannot have any selections of {name} (hidden)")
- ❌ Collective entry enforcement ("All {name}s must have the same number...")
- ❌ Constraint validation error message format verification
- ❌ Validation error deduplication (shared entries, line 541-548)

### 2.6 Selection Operations (f.java lines 929-1143)

**Select Entry (f.java lines 989-1053):**
```
1. If force-level or "always new" (d2.f(selectionEntry)): create new Selection
2. Else if existing selection exists: increment number
3. If new: auto-add child entries based on min constraints
4. If new: auto-add default selections from SelectionEntryGroups
5. If collective: replicate across sibling models
```

**Covered:**
- ✅ New selection creation
- ✅ Number increment
- ✅ Child entry auto-addition
- ✅ Deselect (remove)
- ✅ Duplicate

**Missing:**
- ❌ SelectionEntryGroup default selection (defaultSelectionEntryId)
- ❌ Collective entry creation (replicates across siblings)
- ❌ Collective entry deselection
- ❌ Force-level entry ("always create new") flag
- ❌ Favourites (selectFavourite — f.java line 1167)
- ❌ Nested force selection (force within force)

### 2.7 Cost Calculation (f.java lines 717-780)

```
1. Get SelectionEntry for selection
2. Apply modifiers to get effective costs
3. Multiply by selection.number (implicit in cost values)
4. Aggregate into roster.costs
```

**Covered:**
- ✅ Base cost calculation
- ✅ Cost aggregation
- ✅ Multi-type costs
- ✅ Cost after modifier
- ✅ Cost after deselect

**Missing:**
- ❌ Cost with hidden flag (CostType.isHidden → filtered from display)
- ❌ Cost limit CostType (CostType.isLimit) vs regular CostType

---

## 3. Untested Engine Features (Major Gaps)

### 3.1 SelectionEntryGroup (MAJOR)

SelectionEntryGroups control how entries are grouped for selection:
- Have `defaultSelectionEntryId` — auto-selects a default entry
- Can nest SelectionEntries and other SelectionEntryGroups
- Constraints on groups control how many entries can be selected
- Collective flag on groups works differently from entries

Our current test harness does NOT model SelectionEntryGroups in YAML specs.
This requires extending `SetupDef` and `BattleScribeOracle` to support groups.

### 3.2 EntryLink (MAJOR)

EntryLinks are references to shared entries:
- Type: `selectionEntry` or `selectionEntryGroup`
- Resolved at runtime by targetId
- Can have their own modifiers/constraints that override the target
- ID prefixing: `h.a(entryLink.getId(), targetEntry.getId())`

Not modeled in our YAML specs.

### 3.3 Profiles, Rules, InfoGroups

These are informational data attached to entries:
- Modifiers can modify profile characteristics (STRING field = CharacteristicType ID)
- Rules can have description modified
- InfoGroups can be hidden
- During refresh, profiles/rules are collected and modifier-applied

Not directly relevant to roster editing logic but ARE part of the refresh cycle.

### 3.4 Nested Forces

Forces can contain sub-forces:
- `Force.getForces()` returns nested forces
- `includeChildForces` flag on queries traverses nested forces
- Sub-forces have independent catalogues
- Constraint validation recurses into nested forces

---

## 4. Coverage Gaps Summary

### 4.1 Priority 1 — New Condition Types (5 specs)
- `condition-not-instance-of` — untested condition type
- `condition-instance-of-by-type` — instanceOf matching SelectionEntry.Type
- `condition-instance-of-by-category` — instanceOf matching CategoryEntry  
- `condition-instance-of-ancestor` — instanceOf with ancestor scope
- `condition-instance-of-roster-scope` — edge case: returns false

### 4.2 Priority 1 — Missing Modifier Types (3 specs)
- `modifier-category-remove` — remove CategoryLink
- `modifier-category-set-primary` — set primary category
- `modifier-category-unset-primary` — unset primary category

### 4.3 Priority 1 — Missing Scope/Field (3 specs)
- `scope-primary-category` — primary-category scope
- `scope-forces-field` — counting forces instead of selections
- `scope-include-child-forces` — includeChildForces flag

### 4.4 Priority 2 — Missing Constraint Edge Cases (4 specs)
- `constraint-modified-by-modifier` — modifier targets constraint.value
- `constraint-collective` — collective entry divides by number
- `constraint-hidden-enforcement` — hidden entry enforcement
- `constraint-forces-field` — constraint on forces count

### 4.5 Priority 2 — Missing Repeat Edge Cases (3 specs)
- `modifier-repeat-round-up` — roundUp=true
- `modifier-repeat-round-down` — roundUp=false
- `modifier-repeat-multiple` — multiple repeats (additive)

### 4.6 Priority 3 — Modifier Field Targets (3 specs)
- `modifier-rule-description` — modify rule description
- `modifier-page` — modify page field
- `modifier-profile-characteristic` — modify profile characteristic

### 4.7 Priority 3 — SelectionEntryGroup Support (~5 specs)
- Requires infrastructure changes (YAML model + oracle)
- `selection-entry-group-basic` — group with multiple entries
- `selection-entry-group-default` — defaultSelectionEntryId
- `selection-entry-group-constraint` — min/max on group
- `selection-entry-group-collective` — collective group
- `selection-entry-group-nested` — group within group

### 4.8 Priority 4 — Structural Features (~5 specs)
- EntryLink resolution
- Nested forces
- Nested condition groups
- Favourites
- Force-level entry flag

### Total: ~31 additional specs needed

---

## 5. Strategies for Achieving Coverage Certainty

### 5.1 Systematic Enum Coverage Matrix

Create a machine-readable coverage matrix mapping every enum value to spec files:

```yaml
# coverage-matrix.yaml
condition_types:
  equalTo: [condition-equal-to]
  notEqualTo: [condition-not-equal-to]
  instanceOf: [condition-instance-of, condition-instance-of-by-type]
  notInstanceOf: [condition-not-instance-of]
  # ...

modifier_types:
  string_set: [modifier-set-name]
  category_remove: [modifier-category-remove]
  # ...
```

Each enum value must have at least one spec. This is mechanically verifiable.

### 5.2 Code Path Tracing

For each method in the engine (c.java, d.java, f.java), identify:
1. Branch conditions (if/else, switch cases)
2. Loop iterations (for each modifier, for each condition)
3. Early returns (null checks, empty checks)
4. Error paths (throws)

Map each branch to a spec that exercises it. This is the most thorough approach but requires decompiled source analysis.

### 5.3 Boundary Value Analysis

For each numeric comparison, test:
- Exactly at boundary (equalTo)
- Just above boundary (greaterThan by 1)
- Just below boundary (lessThan by 1)
- Zero
- Negative values
- Very large values

For each collection, test:
- Empty (no entries)
- Single entry
- Multiple entries

### 5.4 Combinatorial Testing

Key dimensions to combine:
1. **Condition type** × **Scope** × **Field** × **ChildId type**
2. **Modifier type** × **Modifier field** × **Repeat** × **Condition**
3. **Constraint type** × **Scope** × **Field** × **Flags**

Full combinatorial is impractical (thousands of combinations), but pairwise testing (every pair of dimensions appears at least once) is feasible.

### 5.5 Real-World Data Oracle Testing

Use wh40k-9e data to:
1. Load a complex catalogue (e.g., Space Marines)
2. Create a realistic roster
3. Snapshot state at each step
4. Verify our synthetic specs match real-world behavior

This catches edge cases that synthetic data misses due to indexing/resolution differences.

### 5.6 Mutation Testing (Conceptual)

If we had a second engine implementation:
1. Introduce deliberate bugs (mutations)
2. Verify the spec suite catches each mutation
3. Mutations that survive indicate coverage gaps

This is the gold standard for test suite quality measurement.

---

## 6. Infrastructure Changes Needed

### 6.1 YAML Spec Model Extensions

To test SelectionEntryGroups, the spec format needs:

```yaml
setup:
  entries:
    - id: se-1
      name: "Parent"
      type: unit
      selectionEntryGroups:
        - id: seg-1
          name: "Weapons"
          defaultSelectionEntryId: se-w1
          entries:
            - id: se-w1
              name: "Weapon A"
              type: upgrade
            - id: se-w2
              name: "Weapon B"
              type: upgrade
```

### 6.2 IRosterEngine Extensions

May need new methods:
- `SelectEntryFromGroup(forceIndex, selectionIndex, groupIndex, entryIndex)`
- `GetSelectionCategories(forceIndex, selectionIndex)` — to verify category modifiers
- `GetValidationErrorDetails()` — structured error info (source element, constraint)

### 6.3 Category State in Assertions

To verify category modifiers (add/remove/set-primary), need to expose categories on selections:

```yaml
expectedState:
  selections:
    - entryId: se-1
      categories:
        - entryId: cat-1
          primary: true
```

### 6.4 Profile/Rule State in Assertions

To verify profile/rule modifiers:

```yaml
expectedState:
  selections:
    - entryId: se-1
      profiles:
        - name: "Unit Stats"
          characteristics:
            - name: "Attacks"
              value: "4"
```

---

## 7. Recommended Implementation Plan

### Phase 1: Close Critical Enum Gaps (~11 specs)
- All missing condition types (notInstanceOf, instanceOf variants)
- All missing category modifier types (remove, set-primary, unset-primary)
- Missing scope (primary-category)
- Missing field (forces)
- Missing flag (includeChildForces)
- **Estimate**: These are straightforward YAML additions, may need minor oracle extensions

### Phase 2: Close Constraint & Repeat Gaps (~7 specs)
- Constraint modification by modifier
- Hidden entry enforcement
- Collective entry constraints
- Repeat edge cases (roundUp, multiple repeats)
- **Estimate**: May need oracle wrapper extensions

### Phase 3: SelectionEntryGroup Support (~5 specs)
- Extend YAML model with `selectionEntryGroups` on entries
- Extend `JavaModelFactory` to create `SelectionEntryGroup` objects
- Extend `BattleScribeOracle.SetupFromSpec` to register groups
- Write group-specific specs
- **Estimate**: Significant infrastructure work

### Phase 4: Modifier Field Targets (~3 specs)
- Rule description, page, profile characteristic modifications
- Extend assertions to capture profile/rule state
- **Estimate**: Moderate infrastructure + oracle changes

### Phase 5: Coverage Matrix & Verification
- Create `coverage-matrix.yaml` mapping every enum value to specs
- Create automated check that validates no enum values are unmapped
- Create dashboard/summary in CI output
- **Estimate**: Tooling work

### Grand Total: ~148 specs (117 existing + 31 new)

---

## 8. What "100% Coverage" Means

Given the engine's architecture, true 100% coverage means:

1. **Every enum value** exercised in at least one spec ✅ (achievable with Phase 1-2)
2. **Every code branch** in modifier evaluation, condition evaluation, constraint validation, and selection operations ✅ (achievable with Phase 1-4)
3. **Every query resolution path** (scope × field × childId × flags) at least pairwise ✅ (largely covered, some gaps)
4. **Every error message format** validated ❌ (requires structured error assertions)
5. **Every infrastructure pattern** (EntryLink, nested forces, shared entries) ✅ (Phase 3-4)

**The realistic target: ~95% code path coverage with ~150 specs** is achievable with the plan above. The remaining 5% covers extremely niche paths (CostType.isLimit, force-level entry flags, favourites) that may not be worth the infrastructure investment.

---

## Appendix: Engine Method Coverage Map

### c.java (AbstractRosterEngine) — Core Logic

| Method Signature | Purpose | Tested? |
|-----------------|---------|---------|
| `a(d, BaseSelectable, T, boolean)` | Apply modifiers to data element | ✅ |
| `a(d, BaseSelectable, T, T, Modifier, ...)` | Apply single modifier | ✅ |
| `a(d, BaseSelectable, T, T, ModifierGroup, ...)` | Apply modifier group | ⚠️ synthetic limitation |
| `a(d, BaseSelectable, BaseModifyableData, BaseModifier, ...)` | Evaluate conditions | ✅ |
| `a(d, BaseSelectable, BaseModifyableData, Condition, ...)` | Evaluate single condition | ✅ |
| `a(d, BaseSelectable, IFilteredQueryChild, boolean)` | instanceOf matching | ⚠️ basic only |
| `a(BaseSelectionEntry, BaseSelectionEntry)` | Entry ID matching (shared) | ❌ |
| `a(d, BaseSelectable, BaseModifyableData, ConditionGroup, ...)` | Evaluate condition group | ✅ |
| `a(d, BaseRosterElement, BaseModifyableData, BaseModifier, ...)` | Calculate repeat count | ✅ basic |
| `a(d, BaseModifyableData, BaseQuery, BaseRosterElement, boolean)` | Get condition value (percentValue) | ✅ |
| `a(d, BaseModifyableData, BaseQuery, BaseRosterElement, boolean, boolean)` | Get query value | ✅ |
| `a(d, boolean, BaseFilteredQuery)` | Resolve childId filter | ⚠️ partial |

### f.java (ConcurrentRosterEngine) — API Surface

| Method | Purpose | Tested? |
|--------|---------|---------|
| `a(Roster, GameSystem, Map, Map, Map, boolean)` | setRoster | ✅ |
| `b(GameSystem, Catalogue, Map, ForceEntry, List, List)` | selectRootForce | ✅ |
| `b(Force, GameSystem, Catalogue, Map, ForceEntry, List, List)` | selectForce (nested) | ❌ |
| `a(BaseSelectionParent, SelectionEntry, int)` | setNumSelections | ✅ |
| `b(BaseSelectionParent, SelectionEntry)` | selectEntry | ✅ |
| `k(Selection)` | duplicateSelection | ✅ |
| `l(Selection)` | selectFavourite | ❌ |
| `g(Force)` | deselectForce | ✅ |
| `m(Selection)` | deselectEntry | ✅ |
| `a(CostType, double)` | setCostLimit | ✅ |
| `q()` | getValidationErrors | ✅ |
| `r()` | hasValidationErrors | ✅ |
| `v()` | validate (private) | ✅ implicitly |
| `a(boolean, boolean)` | refresh elements (private) | ✅ implicitly |
| `w()` | clear changed (private) | ✅ implicitly |

### d.java (CatalogueDataEngine) — Modifier Application

| Method | Purpose | Tested? |
|--------|---------|---------|
| `a(BaseModifyableData, Modifier, int)` | Apply modifier | ✅ |
| `a(String, Modifier)` | Apply STRING modifier | ✅ |
| `a(double, Modifier)` | Apply NUMBER modifier | ✅ |
| `a(Boolean, Modifier)` | Apply BOOLEAN modifier | ✅ |
| `a(ICategorised, Modifier)` | Apply CATEGORY modifier | ⚠️ add only |
| String field setter | `name`, `page`, `description` | ⚠️ name only |
| Number field setter | CostType, Constraint | ⚠️ CostType only |
| Boolean field setter | `hidden` | ✅ |
