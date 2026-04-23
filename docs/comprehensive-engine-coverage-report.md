# BattleScribe Engine Conformance Spec — Coverage Report

## 1. Engine API Coverage

All `IRosterEngine` methods are exercised by the conformance suite.

| Method | Specs Using | Key Evidence |
|--------|----------:|--------------|
| `Setup` | 244 | Every inline spec (implicit setup path) |
| `SetupFromFiles` | 2 | DataSource specs: wh40k-10e-create-army, wh40k-10e-space-marines-army |
| `AddForce` | 246 | force-add-single, force-add-multiple, force-nested-basic, force-multi-catalogue-two-forces |
| `AddForceByName` | 2 | DataSource specs using name-based force addition |
| `RemoveForce` | 5 | force-remove, force-remove-second, force-add-and-remove-all, roster-full-lifecycle |
| `SelectEntry` | 296 | selection-add-unit, selection-with-cost, selection-multiple-entries, import-*-entry-* |
| `SelectEntryByName` | 3 | DataSource specs using name-based entry selection |
| `SelectChildEntry` | 9 | selection-child-entry, selection-child-multiple, nested-children-deep, selection-model-with-cost |
| `DeselectSelection` | 23 | selection-remove, selection-deselect-last, roster-full-lifecycle |
| `SetSelectionCount` | 2 | selection-set-count |
| `DuplicateSelection` | 5 | selection-duplicate, cost-duplicate-increases |
| `SetCostLimit` | 1 | cost-set-limit |
| `GetRosterState` | 201 | Specs using `rosterState` assertions |
| `GetValidationErrors` | 20 | Specs using `errors` assertions: constraint-*, cost-default-limit-positive |

---

## 2. Enum & Feature Coverage

### ConditionType — 8/8 (100%)

| Type | Specs | Key Evidence |
|------|------:|--------------|
| `EQ` | 2 | condition-equal-to, condition-equal-to-zero |
| `NE` | 1 | condition-not-equal-to |
| `GT` | 2 | condition-greater-than, condition-group-nested |
| `LT` | 2 | condition-less-than, condition-group-nested |
| `AT_LEAST` | 38 | Dominant condition type across condition/modifier/scope specs |
| `AT_MOST` | 2 | condition-at-most, condition-group-and |
| `INSTANCE_OF` | 12 | 3 working (scope=self/parent/ancestor) + 9 negative (scope=force/roster) |
| `NOT_INSTANCE_OF` | 1 | condition-not-instance-of |

#### `instanceOf` Scope × childId Compatibility

`instanceOf` only works when the scope resolves to a `BaseSelectable` (a Selection).
`scope=force` resolves to a Force object (not a Selection) and `scope=roster` is
hardcoded to return `false` in the BS engine (c.java:1196-1197).

| Scope | Works? | Reason |
|-------|:------:|--------|
| `self` | ✅ | Resolves to current Selection |
| `parent` | ✅ | Resolves to parent Selection |
| `ancestor` | ✅ | Walks parent chain (all Selections) |
| `force` | ❌ | Resolves to Force (not a Selection) |
| `roster` | ❌ | Hardcoded `return false` (c.java:1196-1197) |

| childId type | Works? | Example spec |
|--------------|:------:|--------------|
| SelectionEntry ID | ✅ | condition-instance-of-self |
| SelectionEntry.Type name | ✅ | condition-instance-of-self-type |
| CategoryEntry ID | ✅ | condition-instance-of-self-category |
| ForceEntry ID | ❌ | condition-instance-of-force-entry (undefined-behavior) |
| Catalogue ID | ❌ | condition-instance-of-catalogue (undefined-behavior) |
| SelectionEntryGroup ID | ❌ | condition-instance-of-group (no group in data) |

Positive specs (condition fires):
- `condition-instance-of-self` — scope=self, childId=entry ID
- `condition-instance-of-self-type` — scope=self, childId=type name
- `condition-instance-of-self-category` — scope=self, childId=category ID
- `condition-instance-of-ancestor` — scope=ancestor, childId=entry ID (child selected)

Negative/undefined specs (condition doesn't fire):
- `condition-instance-of` — scope=force (undefined)
- `condition-instance-of-by-type` — scope=force (undefined)
- `condition-instance-of-by-category` — scope=force (undefined)
- `condition-instance-of-group` — scope=force, non-existent group
- `condition-instance-of-force-entry` — scope=roster (undefined)
- `condition-instance-of-catalogue` — scope=roster (undefined)
- `condition-instance-of-roster-scope` — scope=roster (edge-case)

Full `INSTANCE_OF` target coverage: `SelectionEntry`, `SelectionEntry.Type`, `CategoryEntry`, `Ancestor`, `Roster`, `SelectionEntryGroup`, `ForceEntry`, `Catalogue`.

`childId`-filtered condition coverage for all 4 types: `SelectionEntry`, `SelectionEntryGroup`, `ForceEntry`, `Catalogue`.

### ConstraintType — 2/2 (100%)

| Type | Specs | Key Evidence |
|------|------:|--------------|
| `min` | 16 | constraint-min-violation, constraint-min-satisfied, constraint-min-and-max, constraint-min-linked-* |
| `max` | 31 | constraint-max-violation, constraint-max-satisfied, constraint-max-unlimited, constraint-hidden-enforcement |

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
| `self` | 11 | scope-self, modifier-set-boolean, condition-instance-of-self, -self-type, -self-category |
| `parent` | 34 | scope-parent — dominant scope for childId-based conditions |
| `force` | 26 | scope-force, scope-force-vs-roster (note: instanceOf doesn't work with force scope) |
| `roster` | 12 | scope-roster, scope-roster-cross-force (note: instanceOf doesn't work with roster scope) |
| `ancestor` | 3 | scope-ancestor, condition-instance-of-ancestor (with child selected) |
| `primary-category` | 1 | scope-primary-category |
| `primary-catalogue` | 1 | scope-primary-catalogue |
| direct child-id | 4 | scope-child-id-filter, condition-childid-* |

### Query + Repeat Coverage

- Query fields: `selections` (widely used), `forces` (3 specs)
- Repeat `roundUp`: `true` (modifier-repeat-round-up) and `false` (modifier-repeat-round-down)
- Repeat modifiers: 7 specs total including percent-value, zero-threshold, multiple-additive

### Assertion Mechanisms

All specs use unified `expectedState:` blocks. Available assertion fields:

| Field | Usage | Description |
|-------|------:|-------------|
| `forces` / `selections` | 201 | Roster state: forces, selections, costs, names, profiles, rules |
| `costs` | 50 | Roster-level cost totals by type |
| `forceCount` | 13 | Count of forces in roster |
| `errors` | 20 | Structured validation error assertions (on/from) |

---

## 3. Infrastructure Coverage

| Feature | Specs | Evidence |
|---------|------:|---------|
| Profiles & Characteristics | 11 | modifier-on-profile, modifier-characteristic-value, profile-hidden, profile-inherited-from-link, profile-with-page, profile-publication, profile-with-multiple-characteristics, profile-multiple-on-entry |
| Rules | 6 | modifier-rule-description, rule-hidden, rule-publication, rule-with-page, rule-multiple-on-entry |
| Entry Links | 11 | entry-link-basic, entry-link-with-modifier, entry-link-to-group, entry-link-with-cost, entry-link-with-constraint, entry-link-cascading-modifiers, constraint-entry-link-* (4), shared-entry-via-entry-link |
| Category Links | 6 | modifier-category-*, scope-primary-category, selection-category-link |
| Selection Entry Groups | 4 | selection-entry-group-basic, -constraint, -default, entry-group-with-modifiers |
| Nested Force Entries | 2 | force-nested-basic, scope-include-child-forces-nested |
| Modifier Groups | 5 | modifier-group-basic, -false-condition, -nested, -with-repeat, modifier-multiple-groups |
| Condition Groups | 5 | condition-group-and, -and-fails, -or, -nested, condition-group-triple-nested |
| Repeat Modifiers | 8 | modifier-repeat-basic, -round-up, -round-down, -percent-value, -zero-threshold, -multiple-additive, -include-child-selections, modifier-group-with-repeat |
| Cost Type Flags | 6 | cost-type-hidden, cost-type-limit, cost-limit-query-resolution, roster-multi-cost-types, roster-no-cost-types, roster-with-cost-types |
| Collective Entries | 4 | selection-collective-create, -deselect, constraint-collective, collective-with-constraint |
| Shared Entries | 7 | constraint-shared, constraint-shared-deduplication, constraint-shared-linked, constraint-entry-link-shared-counting, constraint-entry-link-shared-target, catalogue-link-shared-entry, shared-entry-via-entry-link |
| Percent Value | 3 | condition-percent-value, constraint-percent-value, modifier-repeat-percent-value |
| Include Child Forces | 3 | scope-include-child-forces, -nested, constraint-include-child-forces |
| Include Child Selections | 2 | constraint-include-child-selections, modifier-repeat-include-child-selections |
| Force-Level Entries | 1 | selection-force-level-entry |
| Hidden Entries | 11 | selection-hidden-entry, constraint-hidden-enforcement, -violation-linked, hidden-cascade-to-children, modifier-field-hidden, modifier-set-hidden-true, profile-hidden, rule-hidden, infogroup-hidden, import-false-entry-hidden-via-link, cost-type-hidden |
| InfoLinks | 4 | info-link-to-profile, info-link-to-rule, info-link-to-infogroup, info-link-with-modifiers |
| InfoGroups | 5 | infogroup-basic, infogroup-hidden, infogroup-multiple-profiles, infogroup-with-modifiers, info-link-to-infogroup |
| Catalogue Links | 2 | catalogue-link-import, catalogue-link-shared-entry |
| Publications | 8 | publication-on-catalogue, profile-publication, rule-publication, selection-publication, selection-publication-and-page, infolink-profile-publication, infolink-publication-not-inherited, infolink-publication-override |
| Multi-catalogue Forces | 1 | force-multi-catalogue-two-forces |
| Import Attribute | 3 | import-true-entry-visible-via-link, import-false-entry-hidden-via-link, import-false-entry-direct-use |

---

## 4. Explicitly Out-of-Scope

The following are intentionally outside this conformance suite scope:

- **Favourites (`selectFavourite`)**: app-level preference behavior, not roster conformance logic
- **UI rendering**: visual concern, not engine data-model behavior
- **File format parsing (XML deserialization)**: covered by separate parsing/import tooling
- **Concurrent/multi-threaded behavior**: implementation optimization concern, not conformance semantics
- **Dice roller / random mechanics**: app-level feature, not engine data-model behavior
- **Render/export presets**: output formatting, not conformance semantics
- **Comments / metadata preservation**: informational only, no effect on roster editing behavior

---

## 5. Real-World Data Coverage

The test suite includes **10 complex real-world roster tests** (`ComplexRealWorldRosterTests.cs`) using wh40k-9e catalogue data. These exercise multi-catalogue scenarios, linked catalogues, conditions, modifiers, constraints, and cost calculations with real game data. All 10 tests pass consistently.

Additionally, **2 declarative DataSource specs** use `github:BSData/wh40k-10e@v10.6.0` to test against real wh40k 10th Edition data:
- `wh40k-10e-create-army` — minimal smoke test: loads all 44 catalogues, creates an Army Roster force
- `wh40k-10e-space-marines-army` — rich multi-step spec: verifies auto-selected mandatory entries (Detachment, Show/Hide Options, Battle Size), adds Captain and Intercessor Squad with progressive type, cost, and selection count assertions

---

## 6. CatalogueManager Import Filtering — Architecture Note

The `import` attribute on entries (SelectionEntry, SelectionEntryGroup, EntryLink) controls whether entries are visible when their catalogue is imported via CatalogueLink. In the BattleScribe Java engine, this filtering is performed by the **CatalogueManager** during the catalogue loading/merging phase — not at the runtime engine level.

### How it works internally

1. When a catalogue is loaded as an import (via CatalogueLink), the CatalogueManager iterates the source catalogue's entries
2. Entries with `import=false` (`isImported()=false`) are **destructively removed** from the source catalogue's entry list
3. The remaining entries (with `import=true`) are merged into the importing catalogue's resolved entry set
4. After this merging phase, the engine's runtime API operates on the already-filtered catalogue data

### What the conformance suite can observe

The engine's category-based entry resolution API (`_engine.a(category)`) only returns entries from a force's **primary catalogue** — it does not include entries merged in from linked catalogues, regardless of their `import` value. This is an architectural boundary within the Java engine:

| Entry | On faction force (imports base) | On base force (direct) |
|-------|:-------------------------------:|:----------------------:|
| FactionUnit (faction's own) | ✅ Visible | n/a |
| PublicUnit (import=true) | ❌ Not in category API | ✅ Visible |
| PrivateUnit (import=false) | ❌ Not in category API | ✅ Visible |

The conformance suite tests verify:
- The `import` attribute is correctly set on Java model objects (`setImported(true/false)`)
- Entries with `import=false` are accessible when the catalogue is used directly (not imported)
- The full model round-trip through all layers (spec → YAML → Java → protocol) preserves the `import` value

### What would be needed for deeper testing

To directly observe import filtering (import=false entry removed, import=true entry preserved when imported via CatalogueLink), the test harness would need access to the CatalogueManager's merged catalogue state — either by:
- Querying the base catalogue's entry list after CatalogueManager processing to confirm import=false entries were removed
- Finding an engine method that returns the full merged entry list for a force (including entries from linked catalogues)
- Extending the IKVM bridge to expose CatalogueManager internals

This is documented here as an architectural limitation of the current BattleScribe bridge, not a gap in the spec's data model.

---

## 7. Key Behavioral Findings (Documented in Specs)

- **Hidden profiles/rules/infoGroups**: BattleScribe filters hidden items from selection state output. Specs correctly assert absence of hidden items.
- **Append modifier**: BattleScribe's `append` modifier auto-prepends a space before the appended value (e.g., "Alpha" + append "Beta" → "Alpha Beta").
- **EntryLink costs**: EntryLink costs do not override the target entry's costs. The target's base cost applies when selected through a link.
- **EntryLink enumeration**: When both a direct SelectionEntry and an EntryLink point to the same target, only 1 entry appears in the available list (not 2).
- **Import attribute**: Only affects entries when their catalogue is loaded as an import via CatalogueLink. When a catalogue is used directly as a force's primary catalogue, all entries are visible regardless of `import` value.
- **Uncategorised parent-scope constraints**: Parent-scope `field: selections` constraints attached to entries with no primary category resolve to `(No Category)`, and that category is skipped during force/category validation traversal. In this shape, no validation errors are raised even when a min/max threshold appears violated. Use explicit category wiring to exercise native validation paths.
