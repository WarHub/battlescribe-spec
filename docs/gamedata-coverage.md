# GameData spec coverage matrix

Tracks progress toward **100% coverage of the BattleScribe GameData model surface** — every
data-model entity type and every settable field — verified against the two BattleScribe anchor
engines (`battlescribe` in-process reference + `battlescribe-ui` Data Editor). NewRecruit
(`newrecruit`, `newrecruit-ui`) is not a gate; specs add `engines: { newrecruit: skip }` where NR
diverges.

The authoritative surface is the decompiled model under
`../battlescribe-decompiled/BattleScribeEngine/sources/net/battlescribe/model/data/` (51 classes).

## Legend
- ✅ covered by a spec, passing on both BS anchors
- 🟦 enabled in the in-process reference engine + spec authored; **BS Data Editor UI pending W3**
- ⬜ not yet covered
- ➖ not applicable to this entity

## Engine action/field support (harness status)

| Capability | battlescribe (in-proc) | battlescribe-ui | newrecruit (frozen) | newrecruit-ui |
|---|---|---|---|---|
| addEntry: core (se, seg, rule, profile, entryLink, forceEntry, categoryEntry) | ✅ | ✅ | ✅ | ✅ |
| addEntry: constraint, modifier, modifierGroup, condition, conditionGroup, repeat | ✅ (W2) | wired (verify W3) | partial | partial |
| addEntry: infoGroup, infoLink, categoryLink, catalogueLink | ✅ (W2) | wired (verify W3) | ? | ? |
| addEntry: costType, profileType, characteristicType, publication | ✅ (W2) | wired (verify W3) | ? | ? |
| addEntry: shared* root variants | ✅ (W2) | wired (verify W3) | ? | ? |
| setField: scalar fields (generic reflective/UI) | ✅ | ✅ | ✅ | ✅ |
| setCost (cost values by type) | ✅ (W2) | **pending W3** | ❌ skip | ❌ skip |
| setCharacteristic (characteristic values) | ✅ (W2) | **pending W3** | ❌ skip | ❌ skip |
| state: full query/modifier/cost/characteristic field serialization | ✅ (W2) | **pending W3** | partial | partial |

## Entity × field coverage

Each row = an entity type. Cells mark whether a spec exercises **creation** and **each settable
field** on both BS anchors.

### SelectionEntry  (`selection`/`entry`)
create ✅ · name ✅ · hidden ✅ · type ✅ · collective ✅ · import ✅ · page ✅ · publicationId ✅ · comment ⬜
costs 🟦 (`cost/cost-set-values`) · constraints 🟦 (`constraint/constraint-create-and-fields`) · modifiers ⬜ · profiles 🟦 (`profile/…`) · rules ⬜ · categoryLinks ⬜ · infoGroups ⬜ · infoLinks ⬜

### SelectionEntryGroup
create ✅ · name ✅ · hidden ✅ · collective ✅ · import ✅ · defaultSelectionEntryId ✅ · page ✅ · publicationId ✅ · comment ⬜

### EntryLink
create ✅ · targetId ✅ · type ⬜ · collective ⬜ · import ⬜ · hidden ⬜

### ForceEntry
create ✅ · name ✅ · hidden ✅ · page ✅ · publicationId ✅ · comment ⬜ · nested forceEntries ⬜ · categoryLinks ⬜ · constraints ⬜

### CategoryEntry
create ✅ · name ✅ · hidden ✅ · page ✅ · publicationId ✅ · comment ⬜ · constraints ⬜ · modifiers ⬜

### CategoryLink
create 🟦 · name ⬜ · hidden ⬜ · targetId 🟦 (addLink) · primary ⬜

### Cost / CostType
Cost: value-by-type 🟦 (`cost/cost-set-values`) · hidden ⬜
CostType: create 🟦 · name ⬜ · defaultCostLimit ⬜ · hidden ⬜

### Profile / Characteristic
Profile: create 🟦 · name ✅ · typeId 🟦 · typeName ⬜ · hidden ⬜ · page ⬜ · publicationId ⬜
Characteristic: value-by-name 🟦 (`profile/profile-create-with-characteristics`)

### ProfileType / CharacteristicType
ProfileType: create 🟦 · name ⬜ · characteristicTypes ⬜
CharacteristicType: create 🟦 · name ⬜

### Rule
create ✅ · name ✅ · hidden ⬜ · page ⬜ · publicationId ⬜ · description ⬜ · modifiers ⬜

### Constraint
create 🟦 · type (min/max) 🟦 · value 🟦 · field 🟦 · scope 🟦 · childId ⬜ · shared ⬜ · percentValue ⬜ · includeChildSelections ⬜ · includeChildForces ⬜

### Modifier / ModifierGroup
Modifier: create 🟦 · type ⬜ · field ⬜ · value ⬜ · conditions ⬜ · repeats ⬜
ModifierGroup: create 🟦 · modifiers ⬜ · conditions ⬜

### Condition / ConditionGroup
Condition: create 🟦 · type (8 variants) ⬜ · value ⬜ · field ⬜ · scope ⬜ · childId ⬜ · shared ⬜ · percentValue ⬜
ConditionGroup: create 🟦 · type (and/or) ⬜

### Repeat
create 🟦 · repeats ⬜ · roundUp ⬜ · value ⬜ · field ⬜ · scope ⬜ · childId ⬜

### InfoLink / InfoGroup
InfoLink: create 🟦 · targetId ⬜ · type (profile/rule/infoGroup) ⬜ · name ⬜ · hidden ⬜
InfoGroup: create 🟦 · name ⬜ · hidden ⬜ · profiles ⬜ · rules ⬜ · infoLinks ⬜

### Publication
create 🟦 · name ⬜ · shortName ⬜ · publisher ⬜ · publicationDate ⬜ · publisherUrl ⬜

### GameSystem / Catalogue (root)
name ✅ · revision ⬜ · battleScribeVersion ⬜ · authorName/Contact/Url ⬜ · readme ⬜ · (catalogue) gameSystemId ✅ · library ⬜

## Tracked debt
- **W3 (BS Data Editor UI agent)** must add `setCost`/`setCharacteristic` dispatch and expand
  `buildEntryJson` to emit the full field set, then the 🟦 cells become ✅ and any temporary
  `battlescribe-ui: skip` on new specs is removed. Requires a JavaFX JDK (`lib/liberica-jdk`,
  provisioned by `setup.ps1`) to build/verify the agent jar.
- **NR engines** lack `setCost`/`setCharacteristic`; cost/characteristic specs carry
  `newrecruit`/`newrecruit-ui` skips until/unless NR support is added.
