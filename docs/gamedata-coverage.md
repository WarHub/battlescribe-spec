# GameData spec coverage matrix

Tracks progress toward **100% coverage of the BattleScribe GameData model surface** — every
data-model entity type and every settable field — verified against the two BattleScribe anchor
engines (`battlescribe` in-process reference + `battlescribe-ui` Data Editor). NewRecruit
(`newrecruit`, `newrecruit-ui`) is not a gate; specs add `engines: { newrecruit-ui: skip }` where
NR's editor UI diverges.

## NewRecruit engine status

- **`newrecruit` (frozen HAR replay + live NR Editor): all 80 GameData specs pass.** The adapter
  parses the spec's generated XML into an editable in-memory model and exercises the full action
  surface — `addEntry`/`addLink`/`setFields` (incl. `costs` via `cost:<typeId>` and
  `characteristics` via `char:<name>` composite fields), root metadata fields, shared-root and
  type-def containers, catalogue links across multiple catalogues, and link-target validation
  (`EntryLink/CatalogueLink must have a target that exists`). Child ordering matches the BS
  reference engine's fixed container order.
- **`newrecruit-ui` (real NR Editor, Playwright): pure-UI driven, no store writes.** All data
  mutations go through rendered widgets (context menus + submenus, property tables, selects,
  checkboxes, contenteditable fields, autocompletes) — the Pinia store is only ever read.
  **77 of 80 GameData specs are driven via the real NR Editor UI** (the rest are genuine UI
  divergences, below). Covered families: every basic entry/group/force/category spec; constraints;
  root fields; publication; costs; profiles + characteristics; **the full query-editor tier**
  (modifier types, conditions incl. condition groups, repeats, modifier groups, modifier-on-rule);
  info groups; type defs (incl. nested characteristicType); shared-root entries; entry-link fields;
  catalogue links (incl. the dangling-target re-point via the raw "Target ID" input); and
  broken-link validation.
  - **Genuine NR-UI divergences** (the 3 remaining `newrecruit-ui: skip`; asserted on the BS
    anchors, and where a partial spec is still driven, omitted for NR via a per-engine
    `expectedState` override):
    - `gameSystem.battleScribeVersion` has no editor widget (driven for the rest of
      `root-fields-gamesystem`); an entry link's `collective` checkbox is disabled, NR derives it
      (driven for the rest of `link-fields`).
    - `link-types`: NR's entry-link Target list won't surface a shared `selectionEntryGroup`.
    - `links-create-and-fields`: a force-entry categoryLink is added via a direct "Category" item
      that auto-targets a category and exposes no targetId/primary/name/hidden panel (the
      entryLink/infoLink surfaces are covered by `link-fields`/`selection-entry-containers`/`info-group`).
    - `modifier-list-types`: the list/category modifier types are gated behind a category-target
      field the type-only spec doesn't establish.
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
| addEntry: infoGroup, infoLink, categoryLink, catalogueLink | ✅ (W2) | ✅ (W3) | ✅ | infoGroup/infoLink/catalogueLink ✅ · categoryLink skip |
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

### EntryLink  (`links/links-create-and-fields`, `links/link-fields`)
create ✅ · targetId ✅ · type ✅ · collective ✅ · import ✅ · hidden ✅

### ForceEntry  (`force/force-create-and-nest`, `links/…`)
create ✅ · name ✅ · hidden ✅ · page ✅ · publicationId ✅ · comment ✅ (`comment/comment-fields`) · nested forceEntries ✅ · categoryLinks ✅ · constraints ✅

### CategoryEntry  (`category/category-entry-with-constraint`)
create ✅ · name ✅ · hidden ✅ · page ✅ · publicationId ✅ · comment ✅ · constraints ✅ · modifiers ✅

### CategoryLink  (`links/links-create-and-fields` — attaches to force entries)
create ✅ · name ✅ · hidden ✅ · targetId ✅ · primary ✅

### Cost / CostType  (`cost/…`, `type-def/…`)
Cost: value-by-type ✅ (`cost/cost-set-values`) · hidden ➖ (not editable per-cost; hide via `costType.hidden`)
CostType: create ✅ · name ✅ · defaultCostLimit ✅ · hidden ✅

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
name ✅ · revision ✅ · battleScribeVersion ✅ · authorName ✅ · authorContact ✅ · authorUrl ✅ · readme ✅ · (catalogue) gameSystemId ✅ · library ✅

> Root metadata is asserted via a generic `fields:` map on `gameSystem:` / catalogue entries
> (added to the state records + runner in this work).

## Validation errors  (`validation/…`)
Specs can assert the editor's validation error list via an `errors:` key on `expectedState`
(empty list = expect no errors; entries match error messages as case-insensitive substrings).
- `validation/no-errors-clean-state` — a valid system/catalogue reports no errors.
- `validation/error-broken-entry-link` — a dangling entry-link target is flagged
  ("EntryLink must have a target that exists").
- `links/catalogue-link` — a valid cross-catalogue link reports no errors, while a dangling
  catalogue-link target is flagged ("CatalogueLink must have a target that exists").

Both BS anchors surface validation. The **battlescribe-ui** engine reads the Data Editor's
live error list. The **in-process reference engine** now constructs the same BattleScribe
data manager directly (`engine.a.d` for a catalogue, `engine.a.e` for a game system; both
built with the DESKTOP platform constant, a no-op logger, and a perf tracker) and calls its
`a(true)` validation method — so `errors:` assertions run on **both** anchors (no per-engine
override needed). Construction is defensive: if the obfuscated classes ever drift, validation
degrades to an empty list rather than throwing.

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
- **W3 (BS Data Editor UI agent): done.** `setCost`/`setCharacteristic` dispatch + the expanded
  `buildEntryJson` field serialization are implemented and verified; the three sample specs pass
  on `battlescribe-ui`. (Requires the JavaFX JDK in `lib/liberica-jdk`, provisioned by `setup.ps1`,
  to build the agent jar.)
- **`newrecruit-ui` remaining clusters** still skipped pending bespoke widget drivers: the
  modifier/condition/repeat query editors, info groups, type defs, the remaining link specs
  (`links-create-and-fields`, `link-types`, `catalogue-link`), and shared-root entries. The driver
  now covers root fields, publication, costs, characteristics, profiles, and entry-link fields via
  pure UI (context menus + submenus, the right-panel property table incl. contenteditable rows,
  cost/characteristic widgets, and reference autocompletes).
