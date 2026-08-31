# GameData spec coverage matrix

Tracks progress toward **100% coverage of the BattleScribe GameData model surface** — every
data-model entity type and every settable field — verified against the two BattleScribe anchor
engines (`battlescribe` in-process reference + `battlescribe-ui` Data Editor). The suite is **120
GameData specs**. NewRecruit (`newrecruit`, `newrecruit-ui`) is not a gate, but **both NR engines
drive the real NR Editor's Pinia store**: store-direct `newrecruit` mutates the real
`loadedCatalogues` objects via direct JS, and `newrecruit-ui` mutates the same store through rendered
widgets. **`newrecruit` runs all but the seven `load/` specs; `newrecruit-ui` runs all but one**
(`load-missing-game-system-id`). Both are explained under "Load failures" below and neither is a
capability gap: the store-direct lane is out because its parser is ours rather than NewRecruit's, and
that one spec because its payload leaves the NR Editor unable to render its file list.
The validation specs (see "Validation / data-integrity errors" below) stage data and read the
engine's error list. Where a single field is a genuine NR limitation (it derives or does not
model the value), or an error class NR's reference validator doesn't model, the
spec still runs on the NR engines and overrides just that field / error via a per-engine
`expectedState`.

## NewRecruit schema additions (NR-superset specs)

Beyond the BattleScribe-anchored surface above, NewRecruit **evolved** the data format with nodes,
enum values, and attributes that original BattleScribe never had. These are catalogued in
[`nr-schema-additions/`](nr-schema-additions/README.md) and encoded as executable specs under
`specs/gamedata/nr/` that **invert the gate**: `engines: { battlescribe: skip, battlescribe-ui: skip }`
(original BattleScribe can't represent them), and **both `newrecruit` and `newrecruit-ui` must pass**.
The model surface (wham AST + `CatXmlGenerator` + Protocol + both NR read paths + the assertion model)
was extended to the NR-superset to support them.

Covered (9 specs, all green on both NR engines):

| Addition | Spec |
|----------|------|
| `associations`/`association` node; constraint `exactly` | `association-and-exactly` |
| selection-entry types `unit-group`/`mount`/`crew` | `selection-entry-types` |
| `attributeType`; characteristicType/profileType `kind`; characteristicType `defaultValue` | `type-def-additions` |
| `localConditionGroup` node; constraint `negative`/`automatic` | `modifier-local-condition-group` |
| conditionGroup kinds (`not`/`count`/comparison/numeric) | `condition-group-types` |
| modifier types (`multiply`/`divide`/`cumulative-add`/`prepend`/`replace`/…) | `modifier-extended-types` |
| condition types `always`/`never` | `condition-types` |
| query `field=associations`, `scope=root-entry` | `query-vocab` |
| catalogue-root `sharedForceEntries`/`sharedAssociations` | `shared-collections` |

> Note: NR keeps `exactly` in its editor model but **serializes it to a min+max pair** on export.
> Remaining: roster-format (`.ros`) additions; the niche `before` condition and `field=limit::<costType>`.

## NewRecruit engine status

- **`newrecruit` (store-direct, frozen static bundle + live NR Editor): all 109 GameData specs pass.**
  Setup loads the spec's generated XML through NR's real upload+open pipeline (shared `NrEditorStore`),
  populating the editor's Pinia `loadedCatalogues`. Mutations are fast **direct-JS** writes to those
  real store objects (the distinction from `newrecruit-ui`'s widget clicks); state, validation, export
  and reload all read/serialize through the same real store, so its serialized output matches
  `newrecruit-ui` byte-for-byte. It exercises the full action surface — `addEntry`/`addLink`/`setFields`
  (incl. `costs`/`characteristics` written to the store's real arrays), root metadata fields,
  shared-root and type-def containers, catalogue links across multiple catalogues, and link-target
  validation (`EntryLink/CatalogueLink must have a target that exists`). Child ordering matches the BS
  reference engine's fixed container order.
- **`newrecruit-ui` (real NR Editor, Playwright): pure-UI driven, no store writes.** All data
  mutations go through rendered widgets (context menus + submenus, property tables, selects,
  checkboxes, contenteditable fields, autocompletes) — the Pinia store is only ever read.
  **All but one GameData spec run on the real NR Editor UI** (only `export/openfile-inline` skips —
  mid-spec file load via the SPA file-list is flaky). Covered families: every basic
  entry/group/force/category spec; constraints; root fields; publication; costs; profiles +
  characteristics; **the full query-editor tier** (modifier types incl. the list/category types,
  conditions incl. condition groups, repeats, modifier groups, modifier-on-rule); info groups; type
  defs (incl. nested characteristicType); shared-root entries; entry-link and info-link types and
  fields; category links on force entries; catalogue links (incl. the dangling-target re-point via
  the raw "Target ID" input); and broken-link validation.
  - **Per-field divergences** (the spec still runs on every engine; just the one field a given
    engine's editor can't drive is omitted there via a per-engine `expectedState` override, and
    asserted on the engines that can):
    - a cost type's `defaultCostLimit` has no widget in the BattleScribe Data Editor (its CostType
      panel edits only `hidden`), so `type-defs-create-and-fields` omits it for `battlescribe-ui`;
      the other three engines cover it.
    - an entry link's `collective` checkbox is disabled — NR derives it (driven for the rest of
      `link-fields`).
    - a force-entry `categoryLink.primary` is not modelled by NR's editor (no widget, not
      serialized) and defaults `false` on all three BS engines, so `links-create-and-fields` asserts
      `categoryLink` by `targetId` + `hidden` only (both consistent and settable on every engine).
- **`setup.edit` (required active file)**: every spec declares the file it edits — a catalogue id or
  the game system id — because engines disagree on the default (the reference and Data Editor open the
  first catalogue, NR the last). The runner opens it after setup so the active file is deterministic.
  An `openFile` spec action may switch it later, or load a file mid-spec from inline `content:` XML or a
  side-file. (On the BS Data Editor wire `openFile` is the `gamedataOpenFileAction` JSON-RPC method; the
  file type is derived from the XML root element.)
- **`export/completeness`**: a single setup-driven catalogue (plus a library catalogue + a reference
  game system) exercising every common BattleScribe v2.03 node type, deep nesting, varied fields, ids,
  and nested/cross-catalogue links + a `catalogueLink`, then byte-asserts the exported `.cat` on all
  four engines. A serializer/schema regression check distinct from the action-driven, state-asserted
  `entry/kitchen-sink`. Two snapshots: a NewRecruit base (shared by both NR engines) and a
  `.battlescribe.` family override (shared by `battlescribe` + `battlescribe-ui`). The in-process
  `battlescribe` engine replicates the Data Editor's on-load cost normalization (a zero cost per cost
  type, names resolved) so it matches `battlescribe-ui` byte-for-byte.

The authoritative surface is the decompiled model under
`../battlescribe-decompiled/BattleScribeEngine/sources/net/battlescribe/model/data/` (51 classes).

## Legend
- ✅ covered by a spec, passing on both BS anchors
- 🟦 enabled in both BS engines (reference + Data Editor UI) but not yet spec-covered
- ⬜ not yet covered
- ➖ not applicable to this entity

## Engine action/field support (harness status)

Both BS anchors now support the full action/field surface (W2 + W3 done). Setting values is
unified under a single **`setFields`** action carrying three optional maps — `fields`
(scalar fields), `costs` (cost values by type) and `characteristics` (characteristic values).
The runner applies `fields` first, then `costs`, then `characteristics`. The `newrecruit` adapter
supports all three maps; `newrecruit-ui` covers the basic + a documented subset of advanced
families via pure UI (the rest carry `newrecruit-ui: skip`).

| Capability | battlescribe (in-proc) | battlescribe-ui | newrecruit (frozen+live) | newrecruit-ui |
|---|---|---|---|---|
| addEntry: core (se, seg, rule, profile, entryLink, forceEntry, categoryEntry) | ✅ | ✅ | ✅ | ✅ |
| addEntry: constraint, modifier, modifierGroup, condition, conditionGroup, repeat | ✅ (W2) | ✅ (W3) | ✅ | ✅ |
| addEntry: infoGroup, infoLink, categoryLink, catalogueLink | ✅ (W2) | ✅ (W3) | ✅ | ✅ |
| addEntry: costType, profileType, characteristicType, publication | ✅ (W2) | ✅ (W3) | ✅ | ✅ |
| addEntry: shared* root variants | ✅ (W2) | ✅ (W3) | ✅ | ✅ |
| setFields → `fields` (scalar fields, generic reflective/UI) | ✅ | ✅ | ✅ | ✅ |
| setFields → `costs` (cost values by type) | ✅ (W2) | ✅ (W3) | ✅ | ✅ |
| setFields → `characteristics` (characteristic values) | ✅ (W2) | ✅ (W3) | ✅ | ✅ |
| state: full query/modifier/cost/characteristic field serialization | ✅ (W2) | ✅ (W3) | ✅ | ✅ |

## Entity × field coverage

Each row = an entity type. Cells mark whether a spec exercises **creation** and **each settable
field** on both BS anchors.

### SelectionEntry  (`selection`/`entry`, `comment/comment-fields`)
create ✅ · name ✅ · hidden ✅ · type ✅ · collective ✅ · import ✅ · page ✅ · publicationId ✅ · comment ✅

> `comment` is a `BaseData` field common to every entity (serialized by both engines);
> `comment/comment-fields` covers it on a selection entry and a rule.
costs ✅ (`cost/cost-set-values`) · constraints ✅ (`constraint/constraint-create-and-fields`) · modifiers ✅ (`entry/selection-entry-containers`) · profiles ✅ (`profile/…`) · rules ✅ (`entry/selection-entry-containers`) · categoryLinks ➖ (force-entry only) · infoGroups ✅ (`entry/selection-entry-containers`) · infoLinks ✅ (`entry/selection-entry-containers`)

### SelectionEntryGroup
create ✅ · name ✅ · hidden ✅ · collective ✅ · import ✅ · defaultSelectionEntryId ✅ · page ✅ · publicationId ✅ · comment ✅ (`comment/comment-fields`)

### EntryLink  (`links/links-create-and-fields`, `links/link-fields`, `links/link-types`)
create ✅ · targetId ✅ · type ✅ · collective ✅ · import ✅ · hidden ✅

> `type` round-trips both enum values (`link-types`): a root entry link targets a `selectionEntry`
> (root links cannot target a group), and an entry link **inside a selection entry** is re-typed to
> `selectionEntryGroup` — NR only offers the group link type for non-root links.

### ForceEntry  (`force/force-create-and-nest`, `links/…`)
create ✅ · name ✅ · hidden ✅ · page ✅ · publicationId ✅ · comment ✅ (`comment/comment-fields`) · nested forceEntries ✅ · categoryLinks ✅ · constraints ✅

### CategoryEntry  (`category/category-entry-with-constraint`)
create ✅ · name ✅ · hidden ✅ · page ✅ · publicationId ✅ · comment ✅ · constraints ✅ · modifiers ✅

### CategoryLink  (`links/links-create-and-fields` — attaches to force entries)
create ✅ · targetId ✅ · hidden ✅ · name ➖ (derived from target) · primary ➖ (see below)

> A force-entry category link's only meaningful settable field is `targetId` (which category it
> points at); `hidden` is also settable and persists on every engine. `name` is derived from the
> target (NR overwrites a set value; the BS engines default it), so it is not asserted. `primary`
> is **not modelled by NR's editor** (no widget, never serialized) and defaults `false` on all three
> BS engines (in-proc reference, frozen NR, and the real Data Editor) — i.e. a force category link is
> not implicitly primary — so it is not asserted.

### Cost / CostType  (`cost/…`, `type-def/…`)
Cost: value-by-type ✅ (`cost/cost-set-values`) · hidden ➖ (not editable per-cost; hide via `costType.hidden`)
CostType: create ✅ · name ✅ · defaultCostLimit ✅ (no widget in the BS Data Editor → asserted on the other 3 engines via a `battlescribe-ui` override) · hidden ✅

### Profile / Characteristic  (`profile/…`)
Profile: create ✅ · name ✅ · typeId ✅ · typeName ✅ · hidden ✅ · page ✅ · publicationId ✅
Characteristic: value-by-name ✅ (`profile/profile-create-with-characteristics`)

### ProfileType / CharacteristicType  (`type-def/…`)
ProfileType: create ✅ · name ✅ · characteristicTypes ✅
CharacteristicType: create ✅ · name ✅

### Rule  (`rule/rule-create-and-fields`, `rule/rule-with-modifier`)
create ✅ · name ✅ · hidden ✅ · page ✅ · publicationId ✅ · description ✅ · modifiers ✅

### Constraint  (`constraint/constraint-create-and-fields`, `constraint/constraint-advanced-fields`)
create ✅ · type (min/max) ✅ · value ✅ · field ✅ · scope ✅ · childId ➖ (n/a) · shared ✅ · percentValue ✅ · includeChildSelections ✅ · includeChildForces ✅

### Modifier / ModifierGroup  (`modifier/…`, `modifier-group/…`)
Modifier: create ✅ · type (all 8: set/increment/decrement/append/add/remove/set-primary/unset-primary ✅) · field ✅ · value ✅ · conditions ✅ · repeats ✅
ModifierGroup: create ✅ · modifiers ✅ · conditions ✅

> The modifier `type` belongs to a data-type-specific enum keyed by the modifier's `field`:
> boolean→`set`, number→`increment`/`decrement`/`set`, string→those + `append`, and **category→
> `add`/`remove`/`set-primary`/`unset-primary`**. `modifier-all-types` sets a field that admits each
> string type; `modifier-list-types` sets `field: category` + `value: <categoryId>` for the category
> types (a type-only modifier with no field is a degenerate form NR's editor correctly won't produce).

### Condition / ConditionGroup  (`condition/condition-types-and-group`, `condition/condition-all-types`)
Condition: create ✅ · type (all 8 ✅) · value ✅ · field ✅ · scope ✅ · childId ✅ · shared ✅ · percentValue ✅ · includeChildSelections ✅
ConditionGroup: create ✅ · type (and/or) ✅

### Repeat  (`repeat/repeat-create-and-fields`)
create ✅ · repeats ✅ · roundUp ✅ · value ✅ · field ✅ · scope ✅ · childId ✅

### InfoLink / InfoGroup  (`links/…`, `info-group/info-group-create-and-nest`)
InfoLink: create ✅ · targetId ✅ · type (profile/rule/infoGroup) ✅ · name ✅ · hidden ✅
InfoGroup: create ✅ · name ✅ · hidden ✅ · profiles ✅ · rules ✅ · infoLinks ✅

### Shared root containers  (`shared/shared-root-entries`)
sharedSelectionEntry ✅ · sharedSelectionEntryGroup ✅ · sharedRule ✅ · sharedProfile ✅ · sharedInfoGroup ✅

### CatalogueLink  (`links/catalogue-link`)
create ✅ · targetId ✅ · importRootEntries ✅

> The spec stages a second `library` catalogue in the same game system as the link target.
> The Data Editor indexes all same-system catalogues on load, so the link resolves on both
> anchors (targetId is retained, no error). Re-pointing the link at a non-existent catalogue
> is flagged ("CatalogueLink must have a target that exists") — verified on `battlescribe-ui`.

### Publication  (`publication/publication-create-and-fields`)
create ✅ · name ✅ · shortName ✅ · publisher ✅ · publicationDate ✅ · publisherUrl ✅

### GameSystem / Catalogue (root)  (`root/root-fields-gamesystem`, `root/root-fields-catalogue`)
name ✅ · revision ✅ · authorName ✅ · authorContact ✅ · authorUrl ✅ · readme ✅ · (catalogue) gameSystemId ✅ · library ✅

> `battleScribeVersion` is intentionally **not** a spec field: it is a save-stamp written by the
> serializer, never user-editable in any editor. The XML attribute is still emitted (files require
> it); no engine sets or asserts it as a data field.

> Root metadata is asserted via a generic `fields:` map on `gameSystem:` / catalogue entries
> (added to the state records + runner in this work).

## Validation / data-integrity errors  (`validation/…`)  — issues #31 + #173
Specs can assert the editor's validation error list via an `errors:` key on `expectedState`
(empty list = expect no errors; entries match error messages as case-insensitive substrings).
All validation specs run on **all four engines** (both BS anchors + both NR engines).

**Referential integrity (dangling targets)**
- `validation/error-broken-entry-link` — a dangling entry-link target is flagged
  ("EntryLink must have a target that exists").
- `validation/error-broken-info-link` — a dangling info-link target is flagged
  ("InfoLink must have a target that exists").
- `links/catalogue-link` — a valid cross-catalogue link reports no errors, while **re-pointing**
  it at a non-existent catalogue is flagged ("CatalogueLink must have a target that exists").
- `validation/error-broken-after-delete` — an entry link that resolves cleanly becomes a dangling
  error once its target is **deleted** by an edit (the dynamic counterpart of `error-broken-entry-link`).
  Modelled by every engine including NR, so it asserts the error on all four.

**Duplicate IDs**
- `validation/error-duplicate-entry-ids` — two entries sharing an id within one catalogue are
  flagged ("All data element ids must be unique").

**Semantic errors (query resolution — scope / field / type / child axes)**
- `validation/error-constraint-bad-scope` — a constraint whose `scope` resolves to neither a
  scope keyword nor an existing ancestor id is flagged ("Constraint must have a scope that exists").
- `validation/error-constraint-bad-field` — a constraint whose `field` is unresolvable is flagged
  ("Constraint must have a field that exists").
- `validation/error-condition-bad-scope` — a condition (inside a modifier) with an unresolvable
  `scope` ("Condition must have a scope that exists").
- `validation/error-condition-bad-child` — a condition whose `childId` references a missing entry
  ("Condition must have a child that exists").
- `validation/error-modifier-bad-field` — a modifier whose `field` is unresolvable; BattleScribe
  flags **both** "Modifier must have a field that exists" and "Modifier must have a type that exists"
  (the unresolvable field cascades into type resolution). A valid field reports neither.

**Error lifecycle (#173 acceptance criteria)**
- `validation/no-errors-clean-state` — a valid system/catalogue reports no errors.
- `validation/error-cleared-after-fix` — a dangling entry link is flagged, then re-pointed at an
  entry that exists (via `setFields targetId`); the error clears on every engine.
- `validation/error-multiple-in-one-file` — a dangling entry link **and** a dangling info link
  coexist and both are reported.

Both BS anchors surface validation. The **battlescribe-ui** engine reads the Data Editor's
live error list. The **in-process reference engine** constructs the same BattleScribe
data manager directly (`engine.a.d` for a catalogue, `engine.a.e` for a game system; both
built with the DESKTOP platform constant, a no-op logger, and a perf tracker) and calls its
`a(true)` validation method — so `errors:` assertions run on **both** anchors. Construction is
defensive: if the obfuscated classes ever drift, validation degrades to an empty list rather
than throwing.

**NewRecruit reference validator scope.** NR has no equivalent of the BattleScribe Data Editor's
error list, so the NR engines compute a small *reference* validation in JS over the editor store:
they flag **entry-link** and **catalogue-link** dangling targets only. Validation specs whose
error class NR does not model (info-link dangling, duplicate ids, query-scope) keep the spec
running on every engine via a per-engine `errors:` override (`newrecruit` / `newrecruit-ui`)
rather than a `skip` — the structural state is still asserted everywhere, and the error is
asserted on the engines that model it. `error-cleared-after-fix` and the entry-link half of
`error-multiple-in-one-file` use the NR-modelled classes, so they assert on NR too.

**BattleScribe auto-cleans some dangling references at load** (so they are *not* flagged):
- A **CategoryLink** with a non-existent target is removed by the data manager during load — the
  link does not survive to be validated. (The in-process engine reproduces this: building the data
  manager from the live model drops the dangling category link.) No load-time spec asserts a
  category-link dangling error because no engine produces one.
- A **defaultSelectionEntryId** that is not a child of its group is cleared at load, so
  "Default SelectionEntry must exist within the SelectionEntryGroup" is likewise not surfaced
  from a loaded file.
- A **Profile** with a dangling `typeId` and a **CostValue** with a dangling `typeId` produce no
  validation error from a loaded file (the unresolved type reference is not surfaced).
- **Duplicate cost-type ids** and **duplicate profile-type ids** are not flagged — unlike duplicate
  *entry* ids ("All data element ids must be unique"), the uniqueness check does not extend to those
  type-definition collections at load.
- **Circular catalogue links** (two library catalogues importing each other) load without a
  validation error.
- An **unknown condition/modifier `type`** (an out-of-enum `ConditionKind`/modifier kind) is
  unreachable through any real path: the XML serializer (`CatXmlGenerator`, used by `battlescribe-ui`)
  and NR's store both enum-validate and reject it at staging time, so no editor can author it. The
  in-process reference flags "Condition must have a type that exists" only because it builds the
  model directly — there is no cross-engine spec for it (it would run on the reference alone). The
  reachable type-resolution gaps that *are* specced sit on the `field`/`child`/`scope` axes above.
- Note the **load vs. edit** distinction: a catalogue link that is *loaded* dangling is cleaned,
  but one *edited* to a dangling target in a live session is retained and flagged (see
  `links/catalogue-link`, which creates the dangling state by edit).

**Malformed / schema-invalid `.gst`/`.cat` load is covered (#268), in `load/…` rather than here.**
It needed no `setupFromFiles` infra after all: the `openFile` action already takes inline `content:`
XML, so a spec hands the engine the raw bytes as a step and asserts the refusal with `expectFailure`
(#461). It stays out of this matrix because it is a different question — these are validation-list
assertions about files the editor *opened*, and a file it refuses to parse produces no validation
list at all. See "Load failures" below.

## Round-trip (save → reload)  (`roundtrip/…`)  — issue #30
The `reload` action serializes the current edited state to its on-disk `.cat`/`.gst` form and loads
it back, replacing the in-memory model. A round-trip spec mutates, asserts an `expectedState`, runs
`reload`, and asserts the **same** `expectedState` again — so a repeated assertion that still holds
proves persistence preserved the data. No new comparator: the existing partial-match `expectedState`
is reused.

- `roundtrip/roundtrip-add-entry` — an added/named entry survives.
- `roundtrip/roundtrip-set-fields` — a scalar field (`hidden`), a cost value, and a profile
  characteristic survive.
- `roundtrip/roundtrip-link` — an entry link keeps its target.
- `roundtrip/roundtrip-nested` — a selection-entry → group → child subtree survives intact.
- `roundtrip/roundtrip-root-metadata` — author/revision on both the game system (`.gst`) and the
  catalogue (`.cat`) survive.

**Engine coverage.** Round-trip is verified on **all four** engines — each serializes to `.cat`/`.gst`
and loads back.
- **`battlescribe`** (reference) round-trips each model object through BattleScribe's own DataUtils
  serializer in memory (`a(GameSystem/Catalogue, OutputStream)` → `e`/`f(InputStream)`).
- **`battlescribe-ui`** (real Data Editor) serializes the open document's live model back to its
  file via the same DataUtils serializer (Java-agent `gamedataSaveAndReloadAction`) and re-opens it
  through the editor's real open path. (Rewriting the open file trips the editor's external-change
  watcher; the agent answers that specific `Confirm` dialog, answers the load-failure `Error` dialog
  by raising it as a refusal, and fails loudly on any other modal.)
- **`newrecruit`** (store-direct) and **`newrecruit-ui`** (real NR Editor) both serialize the real NR
  store via NR's own writer (`saveCatalogueInFiles`) and feed the XML back through NR's `BSXmlToJson`
  parse, reopening the active file — so the two NR engines round-trip identically (byte-for-byte).

## Load failures  (`load/…`)  — issue #268
The other half of "something is wrong with this file", and structurally not the validation matrix
above: a file the editor **refuses to parse** never becomes a model, so it has no validation list to
assert against. The refusal itself is the assertion, declared with `expectFailure` on the `openFile`
step (#461); the payload is inline `content:` XML, so no raw-file setup mode was needed. Every spec
also asserts what the refusal left behind — a rejected load must leave the open file alone.

Seven specs. Both BattleScribe engines — the in-process reference reader and the real Data Editor —
run all seven and agree on every row. `newrecruit-ui` runs six of them, against NR's own importer.

| Spec | Payload | BattleScribe (both) | `newrecruit-ui` |
|------|---------|---------------------|-----------------|
| `load-malformed-catalogue` | truncated `.cat` | refused | refused |
| `load-malformed-game-system` | truncated `.gst` | refused | refused |
| `load-not-well-formed-catalogue` | wrong end tag | refused | **accepts** |
| `load-not-well-formed-catalogue` | bare `&` | refused | **accepts** |
| `load-not-well-formed-catalogue` | empty document | refused | refused |
| `load-missing-required-attribute` | `.cat` without `id` | refused, binder names the field | refused, hashing the absent id |
| `load-missing-required-attribute` | `.gst` without `revision` | refused at the index scan | **accepts**, and switches the session to that system |
| `load-missing-required-attribute` | `.cat` without `name` | **loads**, nameless | **refused** — it derives a short name first |
| `load-missing-game-system-id` | `.cat` without `gameSystemId` | refused | **accepts**, then breaks — see below |
| `load-wrong-attribute-type` | `revision="not-a-number"` | refused, quotes the string | **accepts** |
| `load-wrong-attribute-type` | `constraint value="lots"` | refused, quotes the string | **accepts** |
| `load-wrong-attribute-type` | `library="perhaps"`, `collective="maybe"` | **loads**, every boolean `false` | **loads**, both kept as written |
| `load-wrong-root-element` | `<roster>` root, attributes incomplete | refused | refused |
| `load-wrong-root-element` | `<roster>` root, attributes complete | refused | refused |

**Two layers refuse, and a third does not.** XML well-formedness is decided before anything
BattleScribe wrote sees the bytes; both BS engines reach the same parser sentence there, one wrapped
in `ParseError at [row,col]` and the other in a `SAXParseException`, which is why the specs pin the
sentence and not the wrapper. Below that is the model binder: a missing required attribute names the
field, a bad number quotes the value and names nothing. Below the binder there is no type checking at
all — a boolean is compared rather than parsed, so anything that is not `"true"` becomes `false` and
the file loads carrying a setting nobody wrote. The acceptances are recorded with
`expectFailure: false` rather than skipped: "we looked and it loads" and "we did not look" are
different results.

**Where the check actually sits, for two of them.** Before the binder runs, both BS engines take a
shallow read of the root to build the file-list index entry, and two refusals happen there rather
than in the binder: a `.gst` missing `revision`, because that read parses it as an int and dies at
`Integer.parseInt(null)` with no message of its own; and any root element that is not
`catalogue`/`gameSystem`, which the binder never asks about — it binds a root by its attributes, so
a `<roster>` carrying a catalogue's attributes would otherwise load as a catalogue. The in-process
adapter used to skip that shallow read, and did load it. Running the UI lane found the divergence and
`BattleScribeGameDataEngine.LoadFile` now takes the same pre-flight, so the two engines agree and
`load-wrong-root-element` carries no per-engine override — the shape #450 ended in for the roster
reader, and the outcome AGENTS.md asks for: one fewer override, not one more.

**NewRecruit is a good deal more permissive, and disagrees in both directions.** It recovers a
document from a mismatched end tag and from a bare `&`; it does not type-check `revision` or a
constraint's `value`; and where BattleScribe *decides* a malformed boolean is `false`, NR keeps what
was written — so the same accepted file leaves the two engines holding different values for the same
attribute. It is also stricter in one place: a catalogue with no `name` is refused, because NR
derives a short name from it before checking there is one. And loading a `.gst` **switches the
session to that game system**, which is why `load-missing-required-attribute` carries an NR
`expectedState` naming a different system rather than the seed catalogue.

**How each editor refuses, and how the driver gets it.**

- **Data Editor**, two routes. A payload its data source cannot read throws straight out of that
  reader. One that gets further is reported the way it is reported to a user — a modal `Error` dialog
  saying `File was corrupted and has been deleted`, with the underlying exception in its Details pane
  — and `DataEditorActions` dismisses it and raises its text, because a dialog left on screen is not
  a result (the same move `RosterActions.loadRosterAction` makes with `LoadDataParams`). Any *other*
  modal still fails loudly and is now marked as an adapter gap, so an unexamined dialog cannot be
  mistaken for the app refusing a file.
- **NR Editor**, one route and it is quiet. Its upload handler parses each file and then keeps only
  what came back as a catalogue or a game system, dropping the rest with no dialog, no toast and no
  store state; the single place it says anything is a `console.error` from the one `catch` in that
  handler. So the driver detects the refusal structurally — by diffing NR's file set across the
  upload — and describes it with the console error NR emitted. Two traps worth knowing: a file NR
  *accepts* can take **seconds** to appear (IndexedDB plus Vue reactivity), so a short poll reads
  acceptance as refusal; and `loadedCatalogues` holds only files the editor has **opened**, so the
  file list lives in `catalogueFiles`.

**The NR file list is built when its route is entered, not from the store.** NR's upload handler ends
in `$router.push('/?id=' + gameSystemIds)`, which for a catalogue-only import is a push to the route
already showing — no route update fires, and the imported file has no row. This is what read for a
long time as "mid-spec file load through the SPA file-list is flaky"; it is not flaky, it is
deterministic, and waiting does not help (measured: still absent after twelve seconds) while
reloading is worse (the rows collapse to the game system alone). The driver now always *re-enters*
the list route, with a query that differs from the last one so it is an update rather than a no-op.
That replaced the older "am I on the editor route? then go back" question at both call sites, so
`GoBackAsync` — which navigated by history depth rather than to a destination — is gone from this
driver. `export/openfile-inline`, opted out of `newrecruit-ui` since it was written, runs there now
with no per-engine block at all.

**One spec is still opted out of `newrecruit-ui`, and it is not a skip for convenience.**
`load-missing-game-system-id` gives NR a catalogue with no `gameSystemId`. NR reads the absent
attribute as the string `undefined`, files the catalogue under a game system by that name and opens
it — after which its editor throws `Cannot read properties of null (reading 'showImported')` and its
file list can no longer render, so nothing later in the session can be driven. That is an NR defect,
not a difference of policy; the payload has a spec of its own precisely so the rest of the
missing-attribute family can run on NR instead of being lost behind it.

**Store-direct `newrecruit` stays opted out of all seven**, for a different reason: its `LoadFile`
parses the payload with a `DOMParser` call inside our own adapter, so what it accepts or refuses is
our parser's answer and not NewRecruit's. It loads a catalogue, entries and all, out of the
mismatched-end-tag payload; recording that would pin our leniency as NR's conformance result.

## File export & snapshot assertions  (`expectedFile`)  — issue #30
A step with `expectedFile` exports the **active file's exact serialized XML** (`ExportActiveFile`) and
compares it **byte-for-byte** (only `\r\n`→`\n` normalized on read) against expected content — inline
`content:`, or a side-file next to the spec keyed by the step `id`: `{specId}.{stepId}.{ext}` (the
**NewRecruit base**) plus optional per-engine overrides `{specId}.{stepId}.{engine}.{ext}` (`ext` ∈
`cat`/`gst`, from the root element). Both NR engines serialize through NR's own writer, so they share
the base; the BS engines get overrides only where their serialization diverges. `BSSPEC_UPDATE_SNAPSHOTS=1`
(re)writes the side-files — the only switch, honored by both `bs-spec run` and `dotnet test`; there
is no `--update-snapshots` flag. **Declared ids** make exports
reproducible: `addEntry`/`addLink` accept an optional `entryId` (the id to assign the created node),
echoed back for `${{ steps.<id>.entryId }}` references. `export/export-add-entry` pins a declared-id
selection entry's `.cat` (NR base + `battlescribe`/`battlescribe-ui` overrides); `export/openfile-inline`
loads a catalogue from inline XML via `openFile`, edits it, and asserts the merged state.

## BS Data Editor UI surface notes (from probing)
- **Category links attach to force entries only** — `actAddCategoryLink` is a no-op unless a
  ForceEntry is selected (verified in the decompiled controller). Adding a category link to any
  other parent now **throws a clear error** on both anchors (rather than silently no-op'ing /
  timing out). Spec covers it on a force entry.
- **Profiles require a profile type** — `actAddProfile`/`actAddSharedProfile` only create a profile
  when at least one profile type exists; adding a profile with no profile type now **throws** on
  both anchors. Specs that add profiles define a `profileType` in setup.
- **Id-less entries** (modifier/condition/repeat/groups) and **panel-only entries** are detected by
  diffing the parent's model child-list (the agent no longer relies on new tree ids), so creation
  works uniformly regardless of tree representation.
- **characteristicType** creation is driven through the real ProfileType edit-panel sub-controller:
  the agent selects the profile type, reaches the live `ProfileTypeEditPanelController` from the
  window controller's panel list, and invokes its `actAddCharacteristicType()` handler (the same
  path the panel's ADD button triggers) — covered on both anchors.

## Literal 100%
The full data-model entity/field surface is ✅ on both BS anchors. The single non-✅ cell is
**`Cost.hidden`**, marked ➖ (not applicable): a cost is not hidden per-instance in the Data
Editor — cost visibility is controlled by `costType.hidden` (covered). Every other entity and
field is created and asserted on both the in-process reference engine and the Data Editor UI.

## Tracked debt
- **`battlescribe-ui` (BS Data Editor agent): fully UI-driven across the BS-anchored surface** (the 10
  NR-superset specs under `specs/gamedata/nr/` skip both BS anchors). Every mutation goes through a
  real JavaFX widget (text fields, checkboxes, combos incl. the Link Type combo, cost/value/revision
  Spinners, characteristic TextAreas, the modifier value control, the catalogue open path) — reading
  state is the only non-UI code. There is **no** reflective-mutation path at all (`setFieldReflectively`
  was removed): an unresolvable field throws. The one field the Data Editor has no widget for,
  `defaultCostLimit` (its CostType panel edits only `hidden`), is **skipped** on `battlescribe-ui` and
  asserted on the other three engines via a `battlescribe-ui` `expectedState` override — never written
  behind the UI's back. `openFile` (wire: `gamedataOpenFileAction`) drives the editor's real open path
  (`dataSource.f/c(path)` + the `a(BaseRootEntry)` display) for the staged file; only the native OS
  file picker is substituted. (Requires the JavaFX JDK in `lib/liberica-jdk`, provisioned by
  `setup.ps1`, to build the agent jar.)
- **`newrecruit-ui`: all but one GameData spec run on the real NR Editor UI** (only
  `export/openfile-inline` skips — mid-spec file load via the SPA file-list is flaky). The driver
  covers every family via pure UI (context menus + submenus, the right-panel
  property table incl. contenteditable rows, cost/characteristic widgets, the query/modifier
  editors incl. category-modifier value autocompletes, link "Link Type" selects, and reference
  autocompletes). The only per-field omissions on NR are values NR derives or doesn't model
  (entry-link `collective`, category-link `primary`), each handled via a per-engine `expectedState`
  override while the spec still runs on `newrecruit-ui`.
