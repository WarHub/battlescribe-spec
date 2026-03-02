using YamlDotNet.Serialization;

namespace BattleScribeSpec;

/// <summary>
/// YAML-serializable models for declarative spec test files.
/// Each spec file defines a complete test scenario: setup, actions, and expected outcomes.
/// </summary>

/// <summary>
/// Root model for a spec YAML file.
/// </summary>
public sealed class SpecFile
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "category")]
    public string Category { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    [YamlMember(Alias = "setup")]
    public SetupDef Setup { get; set; } = new();

    [YamlMember(Alias = "steps")]
    public List<StepDef> Steps { get; set; } = [];
}

/// <summary>
/// Setup section defining game system and catalogue data.
/// </summary>
public sealed class SetupDef
{
    [YamlMember(Alias = "gameSystem")]
    public GameSystemDef GameSystem { get; set; } = new();

    /// <summary>
    /// Multiple catalogues for multi-catalogue scenarios.
    /// </summary>
    [YamlMember(Alias = "catalogues")]
    public List<CatalogueDef>? Catalogues { get; set; }
}

public sealed class GameSystemDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "test-gs";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "Test Game System";

    [YamlMember(Alias = "costTypes")]
    public List<CostTypeDef>? CostTypes { get; set; }

    [YamlMember(Alias = "forceEntries")]
    public List<ForceEntryDef>? ForceEntries { get; set; }

    [YamlMember(Alias = "categoryEntries")]
    public List<CategoryEntryDef>? CategoryEntries { get; set; }

    [YamlMember(Alias = "profileTypes")]
    public List<ProfileTypeDef>? ProfileTypes { get; set; }
}

public sealed class CatalogueDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "cat-1";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "Test Catalogue";

    [YamlMember(Alias = "gameSystemId")]
    public string GameSystemId { get; set; } = "test-gs";

    [YamlMember(Alias = "selectionEntries")]
    public List<SelectionEntryDef>? SelectionEntries { get; set; }

    [YamlMember(Alias = "selectionEntryGroups")]
    public List<SelectionEntryGroupDef>? SelectionEntryGroups { get; set; }

    [YamlMember(Alias = "entryLinks")]
    public List<EntryLinkDef>? EntryLinks { get; set; }

    [YamlMember(Alias = "sharedSelectionEntries")]
    public List<SelectionEntryDef>? SharedSelectionEntries { get; set; }

    [YamlMember(Alias = "sharedSelectionEntryGroups")]
    public List<SelectionEntryGroupDef>? SharedSelectionEntryGroups { get; set; }

    [YamlMember(Alias = "sharedRules")]
    public List<RuleDef>? SharedRules { get; set; }

    [YamlMember(Alias = "sharedProfiles")]
    public List<ProfileDef>? SharedProfiles { get; set; }

    [YamlMember(Alias = "sharedInfoGroups")]
    public List<InfoGroupDef>? SharedInfoGroups { get; set; }

    [YamlMember(Alias = "infoLinks")]
    public List<InfoLinkDef>? InfoLinks { get; set; }

    [YamlMember(Alias = "catalogueLinks")]
    public List<CatalogueLinkDef>? CatalogueLinks { get; set; }

    [YamlMember(Alias = "publications")]
    public List<PublicationDef>? Publications { get; set; }
}

public sealed class CatalogueLinkDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "targetId")]
    public string TargetId { get; set; } = "";

    [YamlMember(Alias = "importRootEntries")]
    public bool ImportRootEntries { get; set; } = true;
}

public sealed class PublicationDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "shortName")]
    public string ShortName { get; set; } = "";

    [YamlMember(Alias = "publisher")]
    public string Publisher { get; set; } = "";

    [YamlMember(Alias = "publicationDate")]
    public string PublicationDate { get; set; } = "";

    [YamlMember(Alias = "publisherUrl")]
    public string PublisherUrl { get; set; } = "";
}

public sealed class CostTypeDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "defaultCostLimit")]
    public double DefaultCostLimit { get; set; } = -1.0;

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "limit")]
    public bool Limit { get; set; }
}

public sealed class ForceEntryDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "categoryLinks")]
    public List<CategoryLinkDef>? CategoryLinks { get; set; }

    [YamlMember(Alias = "forceEntries")]
    public List<ForceEntryDef>? ForceEntries { get; set; }
}

public sealed class CategoryEntryDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }
}

public sealed class SelectionEntryDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "unit";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "collective")]
    public bool Collective { get; set; }

    [YamlMember(Alias = "costs")]
    public List<CostDef>? Costs { get; set; }

    [YamlMember(Alias = "constraints")]
    public List<ConstraintDef>? Constraints { get; set; }

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }

    [YamlMember(Alias = "modifierGroups")]
    public List<ModifierGroupDef>? ModifierGroups { get; set; }

    [YamlMember(Alias = "selectionEntries")]
    public List<SelectionEntryDef>? SelectionEntries { get; set; }

    [YamlMember(Alias = "selectionEntryGroups")]
    public List<SelectionEntryGroupDef>? SelectionEntryGroups { get; set; }

    [YamlMember(Alias = "entryLinks")]
    public List<EntryLinkDef>? EntryLinks { get; set; }

    [YamlMember(Alias = "categoryLinks")]
    public List<CategoryLinkDef>? CategoryLinks { get; set; }

    [YamlMember(Alias = "rules")]
    public List<RuleDef>? Rules { get; set; }

    [YamlMember(Alias = "profiles")]
    public List<ProfileDef>? Profiles { get; set; }

    [YamlMember(Alias = "infoGroups")]
    public List<InfoGroupDef>? InfoGroups { get; set; }

    [YamlMember(Alias = "page")]
    public string Page { get; set; } = "";

    [YamlMember(Alias = "infoLinks")]
    public List<InfoLinkDef>? InfoLinks { get; set; }

    [YamlMember(Alias = "import")]
    public bool Import { get; set; } = true;
}

public sealed class SelectionEntryGroupDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "defaultSelectionEntryId")]
    public string DefaultSelectionEntryId { get; set; } = "";

    [YamlMember(Alias = "constraints")]
    public List<ConstraintDef>? Constraints { get; set; }

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }

    [YamlMember(Alias = "selectionEntries")]
    public List<SelectionEntryDef>? SelectionEntries { get; set; }

    [YamlMember(Alias = "import")]
    public bool Import { get; set; } = true;
}

public sealed class CostDef
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "typeId")]
    public string TypeId { get; set; } = "";

    [YamlMember(Alias = "value")]
    public double Value { get; set; }
}

public sealed class ConstraintDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "";

    [YamlMember(Alias = "value")]
    public double Value { get; set; }

    [YamlMember(Alias = "field")]
    public string Field { get; set; } = "selections";

    [YamlMember(Alias = "scope")]
    public string Scope { get; set; } = "parent";

    [YamlMember(Alias = "shared")]
    public bool Shared { get; set; }

    [YamlMember(Alias = "includeChildSelections")]
    public bool IncludeChildSelections { get; set; }

    [YamlMember(Alias = "includeChildForces")]
    public bool IncludeChildForces { get; set; }

    [YamlMember(Alias = "percentValue")]
    public bool PercentValue { get; set; }
}

public sealed class ModifierDef
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "";

    [YamlMember(Alias = "field")]
    public string Field { get; set; } = "";

    [YamlMember(Alias = "value")]
    public string Value { get; set; } = "";

    [YamlMember(Alias = "conditions")]
    public List<ConditionDef>? Conditions { get; set; }

    [YamlMember(Alias = "conditionGroups")]
    public List<ConditionGroupDef>? ConditionGroups { get; set; }

    [YamlMember(Alias = "repeats")]
    public List<RepeatDef>? Repeats { get; set; }
}

public sealed class ModifierGroupDef
{
    [YamlMember(Alias = "conditions")]
    public List<ConditionDef>? Conditions { get; set; }

    [YamlMember(Alias = "conditionGroups")]
    public List<ConditionGroupDef>? ConditionGroups { get; set; }

    [YamlMember(Alias = "repeats")]
    public List<RepeatDef>? Repeats { get; set; }

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }

    [YamlMember(Alias = "modifierGroups")]
    public List<ModifierGroupDef>? ModifierGroups { get; set; }
}

public sealed class ConditionDef
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "";

    [YamlMember(Alias = "value")]
    public double Value { get; set; }

    [YamlMember(Alias = "field")]
    public string Field { get; set; } = "selections";

    [YamlMember(Alias = "scope")]
    public string Scope { get; set; } = "self";

    [YamlMember(Alias = "childId")]
    public string ChildId { get; set; } = "";

    [YamlMember(Alias = "shared")]
    public bool Shared { get; set; }

    [YamlMember(Alias = "includeChildSelections")]
    public bool IncludeChildSelections { get; set; }

    [YamlMember(Alias = "includeChildForces")]
    public bool IncludeChildForces { get; set; }

    [YamlMember(Alias = "percentValue")]
    public bool PercentValue { get; set; }
}

public sealed class ConditionGroupDef
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "and";

    [YamlMember(Alias = "conditions")]
    public List<ConditionDef>? Conditions { get; set; }

    [YamlMember(Alias = "conditionGroups")]
    public List<ConditionGroupDef>? ConditionGroups { get; set; }
}

public sealed class RepeatDef
{
    [YamlMember(Alias = "value")]
    public double Value { get; set; } = 1;

    [YamlMember(Alias = "repeats")]
    public int Repeats { get; set; } = 1;

    [YamlMember(Alias = "field")]
    public string Field { get; set; } = "selections";

    [YamlMember(Alias = "scope")]
    public string Scope { get; set; } = "self";

    [YamlMember(Alias = "childId")]
    public string ChildId { get; set; } = "";

    [YamlMember(Alias = "roundUp")]
    public bool RoundUp { get; set; }

    [YamlMember(Alias = "shared")]
    public bool Shared { get; set; }

    [YamlMember(Alias = "includeChildSelections")]
    public bool IncludeChildSelections { get; set; }

    [YamlMember(Alias = "includeChildForces")]
    public bool IncludeChildForces { get; set; }

    [YamlMember(Alias = "percentValue")]
    public bool PercentValue { get; set; }
}

public sealed class CategoryLinkDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "targetId")]
    public string TargetId { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "primary")]
    public bool Primary { get; set; }
}

public sealed class RuleDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "page")]
    public string Page { get; set; } = "";

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }
}

public sealed class ProfileDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "typeId")]
    public string TypeId { get; set; } = "";

    [YamlMember(Alias = "typeName")]
    public string TypeName { get; set; } = "";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "characteristics")]
    public List<CharacteristicDef>? Characteristics { get; set; }

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }
}

public sealed class CharacteristicDef
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "typeId")]
    public string TypeId { get; set; } = "";

    [YamlMember(Alias = "value")]
    public string Value { get; set; } = "";
}

public sealed class InfoGroupDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "profiles")]
    public List<ProfileDef>? Profiles { get; set; }

    [YamlMember(Alias = "rules")]
    public List<RuleDef>? Rules { get; set; }

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }

    [YamlMember(Alias = "infoLinks")]
    public List<InfoLinkDef>? InfoLinks { get; set; }
}

public sealed class EntryLinkDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "targetId")]
    public string TargetId { get; set; } = "";

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "selectionEntry";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "costs")]
    public List<CostDef>? Costs { get; set; }

    [YamlMember(Alias = "constraints")]
    public List<ConstraintDef>? Constraints { get; set; }

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }

    [YamlMember(Alias = "categoryLinks")]
    public List<CategoryLinkDef>? CategoryLinks { get; set; }

    [YamlMember(Alias = "import")]
    public bool Import { get; set; } = true;
}

public sealed class InfoLinkDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "targetId")]
    public string TargetId { get; set; } = "";

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "profile";

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    [YamlMember(Alias = "modifiers")]
    public List<ModifierDef>? Modifiers { get; set; }
}

// ===== Step definitions =====

/// <summary>
/// A single step in a spec test — either an action or an assertion.
/// </summary>
public sealed class StepDef
{
    [YamlMember(Alias = "action")]
    public string? Action { get; set; }

    [YamlMember(Alias = "forceEntryIndex")]
    public int? ForceEntryIndex { get; set; }

    [YamlMember(Alias = "forceIndex")]
    public int? ForceIndex { get; set; }

    [YamlMember(Alias = "entryIndex")]
    public int? EntryIndex { get; set; }

    [YamlMember(Alias = "selectionIndex")]
    public int? SelectionIndex { get; set; }

    [YamlMember(Alias = "childEntryIndex")]
    public int? ChildEntryIndex { get; set; }

    [YamlMember(Alias = "catalogueIndex")]
    public int? CatalogueIndex { get; set; }

    [YamlMember(Alias = "costTypeId")]
    public string? CostTypeId { get; set; }

    [YamlMember(Alias = "count")]
    public int? Count { get; set; }

    [YamlMember(Alias = "value")]
    public double? Value { get; set; }

    // ===== Assertion fields =====

    [YamlMember(Alias = "assert")]
    public string? Assert { get; set; }

    [YamlMember(Alias = "expected")]
    public object? Expected { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "expectedState")]
    public ExpectedStateDef? ExpectedState { get; set; }
}

/// <summary>
/// Expected roster state for assertion.
/// All fields are optional — only specified fields are checked.
/// </summary>
public sealed class ExpectedStateDef
{
    [YamlMember(Alias = "forceCount")]
    public int? ForceCount { get; set; }

    [YamlMember(Alias = "selectionCount")]
    public int? SelectionCount { get; set; }

    [YamlMember(Alias = "validationErrorCount")]
    public int? ValidationErrorCount { get; set; }

    [YamlMember(Alias = "hasValidationErrors")]
    public bool? HasValidationErrors { get; set; }

    [YamlMember(Alias = "forces")]
    public List<ExpectedForceDef>? Forces { get; set; }

    [YamlMember(Alias = "costs")]
    public List<ExpectedCostDef>? Costs { get; set; }

    [YamlMember(Alias = "validationErrors")]
    public List<ExpectedValidationErrorDef>? ValidationErrors { get; set; }
}

/// <summary>
/// Expected structured validation error for assertion.
/// All fields are optional — only specified fields are checked.
/// </summary>
public sealed class ExpectedValidationErrorDef
{
    [YamlMember(Alias = "message")]
    public string? Message { get; set; }

    [YamlMember(Alias = "ownerType")]
    public string? OwnerType { get; set; }

    [YamlMember(Alias = "ownerEntryId")]
    public string? OwnerEntryId { get; set; }

    [YamlMember(Alias = "entryId")]
    public string? EntryId { get; set; }

    [YamlMember(Alias = "constraintId")]
    public string? ConstraintId { get; set; }
}

public sealed class ExpectedForceDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "selectionCount")]
    public int? SelectionCount { get; set; }

    [YamlMember(Alias = "availableEntryCount")]
    public int? AvailableEntryCount { get; set; }

    [YamlMember(Alias = "selections")]
    public List<ExpectedSelectionDef>? Selections { get; set; }
}

public sealed class ExpectedSelectionDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "number")]
    public int? Number { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool? Hidden { get; set; }

    [YamlMember(Alias = "costs")]
    public List<ExpectedCostDef>? Costs { get; set; }

    [YamlMember(Alias = "children")]
    public List<ExpectedSelectionDef>? Children { get; set; }

    [YamlMember(Alias = "profiles")]
    public List<ExpectedProfileDef>? Profiles { get; set; }

    [YamlMember(Alias = "rules")]
    public List<ExpectedRuleDef>? Rules { get; set; }

    [YamlMember(Alias = "categories")]
    public List<ExpectedCategoryDef>? Categories { get; set; }

    [YamlMember(Alias = "page")]
    public string? Page { get; set; }
}

public sealed class ExpectedCostDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "typeId")]
    public string? TypeId { get; set; }

    [YamlMember(Alias = "value")]
    public double? Value { get; set; }
}

public sealed class ExpectedProfileDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "typeId")]
    public string? TypeId { get; set; }

    [YamlMember(Alias = "typeName")]
    public string? TypeName { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool? Hidden { get; set; }

    [YamlMember(Alias = "characteristics")]
    public List<ExpectedCharacteristicDef>? Characteristics { get; set; }
}

public sealed class ExpectedCharacteristicDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "typeId")]
    public string? TypeId { get; set; }

    [YamlMember(Alias = "value")]
    public string? Value { get; set; }
}

public sealed class ExpectedRuleDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool? Hidden { get; set; }
}

public sealed class ExpectedCategoryDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "entryId")]
    public string? EntryId { get; set; }

    [YamlMember(Alias = "primary")]
    public bool? Primary { get; set; }
}

public sealed class ProfileTypeDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "characteristicTypes")]
    public List<CharacteristicTypeDef>? CharacteristicTypes { get; set; }
}

public sealed class CharacteristicTypeDef
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";
}
