# NR new fields & query vocabularies

New attributes/fields and expanded string-valued vocabularies. Evidence:
`artifacts/discover/discovery-seed/enums.json` + `scaffold-cat-1.cat`, and the readable nr-editor UI
components at `D:\repos\nr-editor` for source-only items.

## modifier `field` vocabulary  — **confirmed**

The modifier "what to modify" picker (icon-select). Baseline targets: name, page, hidden, category,
cost types, constraints, defaultAmount, defaultSelectionEntryId.

NR additions captured (`enums.json` → `modifier.widgets[0] modifier`): **annotation**, and the
message pseudo-fields **error**, **warning**, **info** (a modifier with `type:add` on field
`error`/`warning`/`info` adds a validation message — recorded as **new-pseudo-field**, not a new
modifier type).

## query `field` vocabulary  — **confirmed**

The field a constraint/condition/repeat counts over. Baseline: selections, forces, + each cost type.

NR additions (`enums.json` → `constraint.widgets[0] query`): **Associations** (count associations),
**`<costType> Limit`** (serialised as `field="limit::<costTypeId>"` — query a cost *limit* rather
than a cost).

- **Spec'd** (`specs/gamedata/nr/nr-query-vocab.yaml`): `associations`, `root-entry` scope, and
  `limit::pts` (a constraint with `field: "limit::pts"`, against a gameSystem costType `pts`). Both NR
  engines pass; BattleScribe skipped. The `limit::<costTypeId>` form is a free-string query field that
  serialises verbatim as `field="limit::pts"` (confirmed via `discover xml` →
  `artifacts/discover/nr-query-vocab/`).

## query `scope` vocabulary  — **confirmed**

The scope a query resolves against. Baseline (per `docs/spec-schema.json` scopeValue): self, parent,
ancestor, force, roster, primary-category, primary-catalogue.

NR additions (`enums.json` → `modifier.widgets[1] Scope:` and `condition.widgets[1] query`):
**root-entry**, and **type scopes** — `Type: Unit`, `Type: Model`, `Type: Upgrade`,
`Type: Model or Unit` (scope the query to ancestors of a given selection-entry type).

## constraint flags  — **uncertain (source-only)**

- **`negative`** (boolean) — `Constraint.vue:20-21`.
- **`automatic`** (boolean) — `Constraint.vue:22-23`.
- **`message`** (string, custom violation message) — `Message.vue`; substitution tokens
  (`{self}`, `{parent}`, `{primary-category}`, `{roster}`, `{model}`, `{unit}`, …).
- Baseline: `negative`/`automatic`/`message` → 0 hits in v2.03 XSD.
- Backlog: not in the `discover enums` constraint dump (only the type select + query were rendered);
  set them via the UI + `discover xml` to confirm XML attribute names.

## characteristicType / profileType fields  — **confirmed (kind) / partial**

The catalogue editor exposes its own Profile Types / Cost Types root sections, so these are now
reachable via `discover enums` (add under the catalogue id, not the game-system id).

- **`kind`** (enum) — **confirmed** (`enums.json` → `characteristicType.selects[Kind:]` and
  `profileType.selects[Kind:]`). Values: **Not defined**, **Description / Long Text**, **Annotation**,
  **Cost**. Both characteristicType and profileType carry it. Source: `CharacteristicType.vue:9`.
- **`defaultValue`** — `CharacteristicType.vue:27` (`UtilEditableDiv v-model="item.defaultValue"`).
  Field exists; exact XML attribute name not yet round-tripped (set it + `discover xml`). Backlog.
- **`formatRules`** child — **UI confirmed** (`enums.json` → `…selects[Formatting Rules]` preset list:
  Empty→-, 0→-, +0→(empty), Bold, Prepend, Append, Prefix sign, Combine & sign). Exact XML not yet
  captured (apply a preset + `discover xml`). See [`new-nodes.md`](new-nodes.md). Backlog.
- Baseline: profileType/characteristicType round-trip confirmed (`scaffold-cat-1.cat`:
  `<profileTypes><profileType><characteristicTypes>`); `kind`/`defaultValue`/`formatRules` absent from
  the baseline v2.03 XSD type-def types.

## association fields  — **confirmed**

The whole `association` node is new (see [`new-nodes.md`](new-nodes.md)); its attributes
(`min`, `max`, `scope`, `childId`, `name`, `id`) are confirmed by `scaffold-cat-1.cat`.

---

### Backlog (live-confirm)

- constraint `negative`/`automatic`/`message` exact XML attribute names.
- characteristicType/profileType `kind`/`defaultValue` (+ kind enum values).
- modifier `affects`/`join`/`position`/`arg`, repeat `step` (claimed in earlier source survey;
  unverified here — confirm or drop via create + `discover xml`).
