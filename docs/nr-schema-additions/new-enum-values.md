# NR new enum values

New values NewRecruit added to enums that exist in original BattleScribe v2.03. Baseline values are
from `.deps/wham/src/WarHub.ArmouryModel.Source/*Kind.cs`; **bold** = NR-new. Evidence:
`artifacts/discover/discovery-seed/enums.json` unless noted. All serialise as the corresponding XML
attribute value (e.g. `type="exactly"`), confirmed by `scaffold-cat-1.cat` / the `discover xml` spike.

## selectionEntry `type`  — **confirmed**

`SelectionEntryKind` baseline: unit, model, upgrade.

NR: unit, model, upgrade, **unit-group**, **mount**, **crew**.

- Evidence: `enums.json` → `selectionEntry.selects[Type:]`.

## constraint `type`  — **confirmed**

`ConstraintKind` baseline: min, max.

NR: min, max, **exactly**.

- Evidence: `enums.json` → `constraint.selects[Constraint]`. Baseline: `exactly` → 0 hits in v2.03 XSD.
- **Spec'd** (`specs/gamedata/nr/nr-association-and-exactly.yaml`): both NR engines pass; BattleScribe
  skipped. **Round-trip note:** NR keeps `exactly` as a first-class value in its editor model/store
  (asserts as `type: exactly`), but its serializer (`convertToXml`) **expands `exactly` to a
  min+max pair** (`<id>-min`/`<id>-max`) on export for BattleScribe compatibility — visible by diffing
  `generated-cat-1.cat` (input `type="exactly"`) vs the NR-re-serialized `cat-1.cat` (min+max) under
  `artifacts/discover/`.

## condition `type`  — **confirmed**

`ConditionKind` baseline: lessThan, greaterThan, equalTo, notEqualTo, atLeast, atMost, instanceOf,
notInstanceOf.

NR adds: **always**, **never** (`enums.json` → `condition.selects[Condition]`), and the positional
**before** ("Is Before") — source `Condition.vue:21`, valid only inside a `localConditionGroup`.

- **Spec'd:** `always`/`never` in `specs/gamedata/nr/nr-condition-types.yaml`; `before` in
  `specs/gamedata/nr/nr-condition-before.yaml` (a modifier's `localConditionGroup` with `type: before`).
  Both NR engines pass; BattleScribe skipped. Serialises verbatim as `type="before"` on the
  `<localConditionGroup>` (confirmed via `discover xml` → `artifacts/discover/nr-condition-before/`).

## conditionGroup `type`  — **confirmed**

`ConditionGroupKind` baseline: and, or.

NR (full set): and, or, **not**, **count**, **add**, **subtract**, **multiply**, **divide**,
**modulo**, **power**, **min**, **max**, **greater**, **greaterOrEqual**, **less**, **lessOrEqual**,
**equal**, **notEqual**.

- Evidence: `enums.json` → `conditionGroup.selects[Type]` (the `== Numeric ==` / `== Comparison ==`
  entries are `<optgroup>` separators, not values). Baseline: a conditionGroup is now both a boolean
  combinator **and** a numeric/comparison expression node.

## modifier `type`  — **confirmed**

`ModifierKind` baseline: set, increment, decrement, append, add, remove, set-primary, unset-primary.

NR gates the type dropdown on the modified field's data type. Captured per field data-type
(`enums.json` → `modifier.typeByField`):

| Field data-type | Modifier types offered |
|-----------------|------------------------|
| numeric | set, increment, decrement, **multiply**, **divide**, **modulo**, **power**, **exponent**, **triangular**, **ceil**, **floor**, **cumulative-add**, **cumulative-multiply**, **cumulative-power** |
| string  | set, append, **prepend**, **replace** |
| boolean | set |
| category | add, remove, set-primary, unset-primary |

NR-new modifier types (union): **multiply, divide, modulo, power, exponent, triangular, ceil, floor,
cumulative-add, cumulative-multiply, cumulative-power, prepend, replace**. (Source also lists
**hide** — `Modifier.vue`; not surfaced for the field set probed → backlog.)

---

### Backlog (live-confirm)

- `hide` modifier type (source `Modifier.vue`) — reach with the field/condition that enables it.
- profileType / characteristicType `kind` enums (the type-def panels weren't reached — see
  [`new-fields.md`](new-fields.md)).
