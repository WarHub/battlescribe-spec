# Protocol Types Reference

All setup data types from `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`.

## ProtocolGameSystem

| Field | Type | Notes |
|-------|------|-------|
| id | string | required |
| name | string | required |
| costTypes | CostType[] | |
| profileTypes | ProfileType[] | |
| publications | Publication[] | |
| forceEntries | ForceEntry[] | |
| categoryEntries | CategoryEntry[] | |
| selectionEntries | SelectionEntry[] | |
| entryLinks | EntryLink[] | |
| rules | Rule[] | |
| infoLinks | InfoLink[] | |
| sharedSelectionEntries | SelectionEntry[] | |
| sharedSelectionEntryGroups | SelectionEntryGroup[] | |
| sharedRules | Rule[] | |
| sharedProfiles | Profile[] | |
| sharedInfoGroups | InfoGroup[] | |

## ProtocolCatalogue

| Field | Type | Notes |
|-------|------|-------|
| id | string | required |
| name | string | required |
| gameSystemId | string | required |
| selectionEntries | SelectionEntry[] | |
| selectionEntryGroups | SelectionEntryGroup[] | |
| entryLinks | EntryLink[] | |
| sharedSelectionEntries | SelectionEntry[] | |
| sharedSelectionEntryGroups | SelectionEntryGroup[] | |
| sharedRules | Rule[] | |
| sharedProfiles | Profile[] | |
| sharedInfoGroups | InfoGroup[] | |
| rules | Rule[] | |
| infoLinks | InfoLink[] | |
| catalogueLinks | CatalogueLink[] | |
| publications | Publication[] | |
| costTypes | CostType[] | |
| profileTypes | ProfileType[] | |
| categoryEntries | CategoryEntry[] | |
| forceEntries | ForceEntry[] | |

## ProtocolCostType

id, name, defaultCostLimit (double, -1=none), hidden (bool), limit (bool)

## ProtocolProfileType

id, name, characteristicTypes: [{id, name}]

## ProtocolPublication

id, name, shortName, publisher, publicationDate, publisherUrl

## ProtocolForceEntry

id, name, hidden, page, publicationId, constraints[], modifiers[], modifierGroups[],
categoryLinks[], forceEntries[], profiles[], rules[], infoGroups[], infoLinks[]

## ProtocolCategoryEntry

id, name, hidden, page, publicationId, constraints[], modifiers[], modifierGroups[],
profiles[], rules[], infoGroups[], infoLinks[]

## ProtocolSelectionEntry

id, name, type (unit/model/upgrade), hidden, import, collective, page, publicationId,
costs[], constraints[], modifiers[], modifierGroups[], selectionEntries[],
selectionEntryGroups[], entryLinks[], categoryLinks[], rules[], profiles[],
infoGroups[], infoLinks[]

## ProtocolSelectionEntryGroup

id, name, hidden, collective, import, defaultSelectionEntryId, constraints[], modifiers[],
modifierGroups[], selectionEntries[], selectionEntryGroups[], entryLinks[],
categoryLinks[], costs[], profiles[], rules[], infoGroups[], infoLinks[], page, publicationId

## ProtocolEntryLink

id, name, targetId, type (selectionEntry/selectionEntryGroup), hidden, collective, import,
costs[], constraints[], modifiers[], modifierGroups[], categoryLinks[], selectionEntries[],
selectionEntryGroups[], entryLinks[], profiles[], rules[], infoGroups[], infoLinks[],
publicationId, page

## ProtocolCategoryLink

id, targetId, name, primary (bool), hidden, page, publicationId, constraints[], modifiers[],
modifierGroups[], profiles[], rules[], infoGroups[], infoLinks[]

## ProtocolCostValue

name, typeId, value (double)

## ProtocolConstraint

id, type (min/max), value (double), field (selections/forces), scope (parent/roster/...),
shared, includeChildSelections, includeChildForces, percentValue

## ProtocolModifier

type (set/increment/decrement/append), field, value, conditions[], conditionGroups[], repeats[]

## ProtocolCondition

type (atLeast/atMost/equalTo/greaterThan/lessThan/instanceOf), value, field, scope, childId,
shared, includeChildSelections, includeChildForces, percentValue

## ProtocolConditionGroup

type (and/or), conditions[], conditionGroups[]

## ProtocolRepeat

value, repeats, field, scope, childId, roundUp, shared, includeChildSelections,
includeChildForces, percentValue

## ProtocolProfile

id, name, typeId, typeName, hidden, page, publicationId, characteristics[], modifiers[],
modifierGroups[]

## ProtocolCharacteristic

name, typeId, value

## ProtocolRule

id, name, description, hidden, page, publicationId, modifiers[], modifierGroups[]

## ProtocolInfoGroup

id, name, hidden, publicationId, page, profiles[], rules[], modifiers[], modifierGroups[],
infoLinks[], infoGroups[]

## ProtocolInfoLink

id, name, targetId, type (rule/profile/infoGroup), hidden, publicationId, page, modifiers[],
modifierGroups[]

## ProtocolCatalogueLink

id, name, targetId, importRootEntries (bool)

---

## State Records (EngineTypes.cs)

These are the assertion targets — what you check in `expectedState`.

**SelectionState**: name, entryId, type, number, hidden, costs[], children[], profiles[],
rules[], categories[], page, publicationId, publicationName

**ForceState**: name, catalogueId, selections[], childForces[] (recursive ForceState),
availableEntryCount, profiles[], rules[], publicationId, page

**CostState**: name, typeId, value

**ProfileState**: name, typeId, typeName, hidden, characteristics[], page, publicationId

**CharacteristicState**: name, typeId, value

**RuleState**: name, description, hidden, page, publicationId

**CategoryState**: name, entryId, primary, profiles[], rules[], publicationId, page
