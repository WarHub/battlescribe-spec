# Protocol Type File Map

## Setup types (ProtocolMessages.cs → JavaModelFactory.cs)

These types define game system and catalogue data for spec setup.

| Protocol type | Factory method | Java type |
|--------------|---------------|-----------|
| ProtocolGameSystem | CreateGameSystem() | GameSystem |
| ProtocolCatalogue | CreateCatalogue() | Catalogue |
| ProtocolCostType | CreateCostType() | CostType |
| ProtocolProfileType | CreateProfileType() | ProfileType |
| ProtocolForceEntry | CreateForceEntry() | ForceEntry |
| ProtocolCategoryEntry | CreateCategoryEntry() | CategoryEntry |
| ProtocolSelectionEntry | CreateSelectionEntry() | SelectionEntry |
| ProtocolSelectionEntryGroup | CreateSelectionEntryGroup() | SelectionEntryGroup |
| ProtocolEntryLink | CreateEntryLink() | EntryLink |
| ProtocolCategoryLink | CreateCategoryLink() | CategoryLink |
| ProtocolConstraint | CreateConstraint() | Constraint |
| ProtocolModifier | CreateModifier() | Modifier |
| ProtocolModifierGroup | CreateModifierGroup() | ModifierGroup |
| ProtocolCondition | CreateCondition() | Condition |
| ProtocolConditionGroup | CreateConditionGroup() | ConditionGroup |
| ProtocolRepeat | CreateRepeat() | Repeat |
| ProtocolProfile | CreateProfile() | Profile |
| ProtocolCharacteristic | CreateCharacteristic() | Characteristic |
| ProtocolRule | CreateRule() | Rule |
| ProtocolInfoGroup | CreateInfoGroup() | InfoGroup |
| ProtocolInfoLink | CreateInfoLink() | InfoLink |
| ProtocolCatalogueLink | CreateCatalogueLink() | CatalogueLink |
| ProtocolPublication | CreatePublication() | Publication |
| ProtocolCostValue | *(inline in factory)* | Cost |

## State types (EngineTypes.cs ↔ SpecFileModels.cs ↔ SpecRunner.cs)

These types define what specs can assert on in `expectedState`.

| State record | Expected* model | SpecRunner method | Matching |
|-------------|----------------|-------------------|----------|
| RosterState | ExpectedStateDef | AssertExpectedState() | — |
| ForceState | ExpectedForceDef | *(in AssertExpectedState)* | by index |
| SelectionState | ExpectedSelectionDef | AssertSelections() | by index |
| CostState | ExpectedCostDef | *(inline)* | by typeId/name |
| ProfileState | ExpectedProfileDef | AssertProfiles() | by name/index |
| CharacteristicState | ExpectedCharacteristicDef | *(in AssertProfiles)* | by name/index |
| RuleState | ExpectedRuleDef | AssertRules() | by name/index |
| CategoryState | ExpectedCategoryDef | AssertCategories() | by name/index |
| ValidationErrorState | ErrorAssertionDef | MatchErrors() | by on/from |

## Which files to update for each change type

### Adding a field to an existing setup type (e.g., new field on SelectionEntry)

1. `ProtocolMessages.cs` — add property to Protocol* class
2. `JavaModelFactory.cs` — add parameter and setter to Create* method
3. *(only if field appears in roster state):*
   - `EngineTypes.cs` — add to State record
   - `SpecFileModels.cs` — add to Expected* class
   - `SpecRunner.cs` — add assertion

### Adding a new setup type

1. `ProtocolMessages.cs` — add new Protocol* class
2. `ProtocolMessages.cs` — add array property to parent type (e.g., ProtocolGameSystem)
3. `JavaModelFactory.cs` — add Create* method
4. `JavaModelFactory.cs` — add to parent Create* method

### Adding a field to roster state only

1. `EngineTypes.cs` — add to State record
2. `SpecFileModels.cs` — add to Expected* class
3. `SpecRunner.cs` — add assertion

### Adding a new action

1. `ProtocolMessages.cs` — add fields to ActionCommand
2. `Protocol/AdapterHandler.cs` — add case to HandleAction switch
3. `IRosterEngine.cs` — add method to interface
4. All engine implementations (Oracle, NR, JsonProtocol)
