# GameData spec coverage matrix

Tracks progress toward **100% coverage of the BattleScribe GameData model surface** — every
data-model entity type and every settable field — verified against the two BattleScribe anchor
engines (`battlescribe` in-process reference + `battlescribe-ui` Data Editor). NewRecruit
(`newrecruit`, `newrecruit-ui`) is not a gate, but **all 86 GameData specs run on the real NR
Editor** — no spec carries `engines: { newrecruit-ui: skip }`. Every mutation is driven via the
real NR Editor UI; the validation specs (see "Validation / data-integrity errors" below) stage data
and read the editor's error list. Where a single field is a genuine NR limitation (it derives or
doesn't model the value), or an error class NR's reference validator doesn't model, the spec still
runs on `newrecruit-ui` and overrides just that field / error via a per-engine `expectedState`.

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

- **`newrecruit` (frozen HAR replay + live NR Editor): all 86 GameData specs pass.** The adapter
  parses the spec's generated XML into an editable in-memory model and exercises the full action
  surface — `addEntry`/`addLink`/`setFields` (incl. `costs` via `cost:<typeId>` and
  `characteristics` via `char:<name>` composite fields), root metadata fields, shared-root and
  type-def containers, catalogue links across multiple catalogues, and link-target validation
  (`EntryLink/CatalogueLink must have a target that exists`). Child ordering matches the BS
  reference engine's fixed container order.
- **`newrecruit-ui` (real NR Editor, Playwright): pure-UI driven, no store writes.** All data
  mutations go through rendered widgets (context menus + submenus, property tables, selects,
  checkboxes, contenteditable fields, autocompletes) — the Pinia store is only ever read.
  **All 86 GameData specs run on the real NR Editor UI.** Covered families: every basic
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
- **`openCatalogue` spec action**: multi-catalogue specs declare the active file with
  `action: openCatalogue` so the NR Editor UI edits the intended catalogue (no-op on engines that
  read all files at once).

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

**Not yet covered (needs new infra, deferred):** malformed / schema-invalid `.gst`/`.cat` *load*
(#31 / #268). GameData specs declare setup as a structured game-system + catalogue model, not raw
file content; asserting load-time XML/schema failures would require a raw-file setup mode
(a `setupFromFiles` path for gamedata) plumbed through every engine — out of scope for the
spec-only validation matrix here.

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

**Engine coverage.** Round-trip is verified on all three XML-based engines; the store-direct
**`newrecruit`** engine is an in-memory structural mirror with no on-disk form, so round-trip specs
`skip` it.
- **`battlescribe`** (reference) round-trips each model object through BattleScribe's own DataUtils
  serializer in memory (`a(GameSystem/Catalogue, OutputStream)` → `e`/`f(InputStream)`).
- **`battlescribe-ui`** (real Data Editor) serializes the open document's live model back to its
  file via the same DataUtils serializer (Java-agent `gamedataSaveAndReloadAction`) and re-opens it
  through the editor's real open path.
- **`newrecruit-ui`** (real NR Editor) exports the loaded files as XML (the editor's own
  serialization via `ExportLoadedFilesJsonAsync`), then feeds them back through the file-input
  pipeline so NR's real `BSXmlToJson` parse runs, and reopens the active file.

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
- **`battlescribe-ui` (BS Data Editor agent): fully UI-driven, 86/86.** Every mutation goes through a
  real JavaFX widget (text fields, checkboxes, combos incl. the Link Type combo, cost/value/revision
  Spinners, characteristic TextAreas, the modifier value control, the catalogue open path) — reading
  state is the only non-UI code. There is **no** reflective-mutation path at all (`setFieldReflectively`
  was removed): an unresolvable field throws. The one field the Data Editor has no widget for,
  `defaultCostLimit` (its CostType panel edits only `hidden`), is **skipped** on `battlescribe-ui` and
  asserted on the other three engines via a `battlescribe-ui` `expectedState` override — never written
  behind the UI's back. `openCatalogue` drives the editor's real open path
  (`dataSource.f/c(path)` + the `a(BaseRootEntry)` display) for the staged file; only the native OS
  file picker is substituted. (Requires the JavaFX JDK in `lib/liberica-jdk`, provisioned by
  `setup.ps1`, to build the agent jar.)
- **`newrecruit-ui`: all 86 GameData specs run on the real NR Editor UI** — no spec is
  skipped. The driver covers every family via pure UI (context menus + submenus, the right-panel
  property table incl. contenteditable rows, cost/characteristic widgets, the query/modifier
  editors incl. category-modifier value autocompletes, link "Link Type" selects, and reference
  autocompletes). The only per-field omissions on NR are values NR derives or doesn't model
  (entry-link `collective`, category-link `primary`), each handled via a per-engine `expectedState`
  override while the spec still runs on `newrecruit-ui`.
