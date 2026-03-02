using System.Text.Json;
using System.Text.Json.Serialization;

namespace BattleScribeSpec.Protocol;

/// <summary>
/// JSON-line protocol message types for BattleScribe conformance testing.
/// The runner sends commands to the adapter via stdin, and receives responses via stdout.
/// Each message is a single JSON object on one line (NDJSON format).
/// </summary>

// ===== Base types =====

/// <summary>
/// Base for all protocol messages. The "type" field discriminates message kinds.
/// </summary>
public abstract class ProtocolCommand
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public abstract class ProtocolResponse
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

// ===== Runner → Adapter Commands =====

/// <summary>
/// Initialize the engine with game system and catalogues data.
/// </summary>
public sealed class SetupCommand : ProtocolCommand
{
    [JsonPropertyName("type")]
    public override string Type => "setup";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("gameSystem")]
    public ProtocolGameSystem GameSystem { get; set; } = new();

    [JsonPropertyName("catalogues")]
    public List<ProtocolCatalogue> Catalogues { get; set; } = [];
}

/// <summary>
/// Execute a roster editing action.
/// </summary>
public sealed class ActionCommand : ProtocolCommand
{
    [JsonPropertyName("type")]
    public override string Type => "action";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("forceEntryIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ForceEntryIndex { get; set; }

    [JsonPropertyName("forceIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ForceIndex { get; set; }

    [JsonPropertyName("entryIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EntryIndex { get; set; }

    [JsonPropertyName("selectionIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SelectionIndex { get; set; }

    [JsonPropertyName("childEntryIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChildEntryIndex { get; set; }

    [JsonPropertyName("catalogueIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CatalogueIndex { get; set; }

    [JsonPropertyName("costTypeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CostTypeId { get; set; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Value { get; set; }
}

public sealed class GetStateCommand : ProtocolCommand
{
    [JsonPropertyName("type")]
    public override string Type => "getState";
}

public sealed class GetErrorsCommand : ProtocolCommand
{
    [JsonPropertyName("type")]
    public override string Type => "getErrors";
}

public sealed class TeardownCommand : ProtocolCommand
{
    [JsonPropertyName("type")]
    public override string Type => "teardown";
}

// ===== Adapter → Runner Responses =====

public sealed class SetupResult : ProtocolResponse
{
    [JsonPropertyName("type")]
    public override string Type => "setupResult";

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];
}

public sealed class ActionResult : ProtocolResponse
{
    [JsonPropertyName("type")]
    public override string Type => "actionResult";

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public sealed class StateResponse : ProtocolResponse
{
    [JsonPropertyName("type")]
    public override string Type => "state";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("gameSystemId")]
    public string GameSystemId { get; set; } = "";

    [JsonPropertyName("forces")]
    public List<ProtocolForce> Forces { get; set; } = [];

    [JsonPropertyName("costs")]
    public List<ProtocolCost> Costs { get; set; } = [];

    [JsonPropertyName("validationErrors")]
    public List<string> ValidationErrors { get; set; } = [];
}

public sealed class ErrorsResponse : ProtocolResponse
{
    [JsonPropertyName("type")]
    public override string Type => "errors";

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];
}

public sealed class TeardownResult : ProtocolResponse
{
    [JsonPropertyName("type")]
    public override string Type => "teardownResult";
}

/// <summary>
/// Returned when the adapter encounters an unrecoverable error.
/// </summary>
public sealed class ProtocolError : ProtocolResponse
{
    [JsonPropertyName("type")]
    public override string Type => "error";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

// ===== Protocol Data Types (state representation) =====

public sealed class ProtocolForce
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("catalogueId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogueId { get; set; }

    [JsonPropertyName("availableEntryCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AvailableEntryCount { get; set; }

    [JsonPropertyName("selections")]
    public List<ProtocolSelection> Selections { get; set; } = [];
}

public sealed class ProtocolSelection
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("entryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryId { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("costs")]
    public List<ProtocolCost> Costs { get; set; } = [];

    [JsonPropertyName("children")]
    public List<ProtocolSelection> Children { get; set; } = [];

    [JsonPropertyName("profiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionProfile>? Profiles { get; set; }

    [JsonPropertyName("rules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionRule>? Rules { get; set; }

    [JsonPropertyName("categories")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionCategory>? Categories { get; set; }

    [JsonPropertyName("page")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Page { get; set; }
}

public sealed class ProtocolSelectionProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("typeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeId { get; set; }

    [JsonPropertyName("typeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("characteristics")]
    public List<ProtocolCharacteristic> Characteristics { get; set; } = [];
}

public sealed class ProtocolSelectionRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }
}

public sealed class ProtocolSelectionCategory
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("entryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryId { get; set; }

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}

public sealed class ProtocolCost
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("typeId")]
    public string TypeId { get; set; } = "";

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

// ===== Protocol Setup Data (game system + catalogue) =====

public sealed class ProtocolGameSystem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("costTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCostType>? CostTypes { get; set; }

    [JsonPropertyName("forceEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolForceEntry>? ForceEntries { get; set; }

    [JsonPropertyName("categoryEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCategoryEntry>? CategoryEntries { get; set; }

    [JsonPropertyName("profileTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolProfileType>? ProfileTypes { get; set; }
}

public sealed class ProtocolCatalogue
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("gameSystemId")]
    public string GameSystemId { get; set; } = "";

    [JsonPropertyName("selectionEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }

    [JsonPropertyName("selectionEntryGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionEntryGroup>? SelectionEntryGroups { get; set; }

    [JsonPropertyName("entryLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolEntryLink>? EntryLinks { get; set; }

    [JsonPropertyName("sharedSelectionEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionEntry>? SharedSelectionEntries { get; set; }

    [JsonPropertyName("sharedSelectionEntryGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionEntryGroup>? SharedSelectionEntryGroups { get; set; }

    [JsonPropertyName("sharedRules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolRule>? SharedRules { get; set; }

    [JsonPropertyName("sharedProfiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolProfile>? SharedProfiles { get; set; }

    [JsonPropertyName("sharedInfoGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolInfoGroup>? SharedInfoGroups { get; set; }

    [JsonPropertyName("infoLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolInfoLink>? InfoLinks { get; set; }

    [JsonPropertyName("catalogueLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCatalogueLink>? CatalogueLinks { get; set; }

    [JsonPropertyName("publications")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolPublication>? Publications { get; set; }
}

public sealed class ProtocolCostType
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("defaultCostLimit")]
    public double DefaultCostLimit { get; set; } = -1.0;

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("limit")]
    public bool Limit { get; set; }
}

public sealed class ProtocolProfileType
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("characteristicTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCharacteristicType>? CharacteristicTypes { get; set; }
}

public sealed class ProtocolCharacteristicType
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class ProtocolForceEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("categoryLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCategoryLink>? CategoryLinks { get; set; }

    [JsonPropertyName("forceEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolForceEntry>? ForceEntries { get; set; }
}

public sealed class ProtocolCategoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class ProtocolSelectionEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "unit";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("import")]
    public bool Import { get; set; } = true;

    [JsonPropertyName("collective")]
    public bool Collective { get; set; }

    [JsonPropertyName("page")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Page { get; set; }

    [JsonPropertyName("costs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCostValue>? Costs { get; set; }

    [JsonPropertyName("constraints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolConstraint>? Constraints { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }

    [JsonPropertyName("modifierGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    [JsonPropertyName("selectionEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }

    [JsonPropertyName("selectionEntryGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionEntryGroup>? SelectionEntryGroups { get; set; }

    [JsonPropertyName("entryLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolEntryLink>? EntryLinks { get; set; }

    [JsonPropertyName("categoryLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCategoryLink>? CategoryLinks { get; set; }

    [JsonPropertyName("rules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolRule>? Rules { get; set; }

    [JsonPropertyName("profiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolProfile>? Profiles { get; set; }

    [JsonPropertyName("infoGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolInfoGroup>? InfoGroups { get; set; }

    [JsonPropertyName("infoLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolInfoLink>? InfoLinks { get; set; }
}

public sealed class ProtocolSelectionEntryGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("import")]
    public bool Import { get; set; } = true;

    [JsonPropertyName("defaultSelectionEntryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultSelectionEntryId { get; set; }

    [JsonPropertyName("constraints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolConstraint>? Constraints { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }

    [JsonPropertyName("selectionEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }
}

public sealed class ProtocolEntryLink
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "selectionEntry";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("import")]
    public bool Import { get; set; } = true;

    [JsonPropertyName("costs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCostValue>? Costs { get; set; }

    [JsonPropertyName("constraints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolConstraint>? Constraints { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }

    [JsonPropertyName("categoryLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCategoryLink>? CategoryLinks { get; set; }
}

public sealed class ProtocolCategoryLink
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}

public sealed class ProtocolCostValue
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("typeId")]
    public string TypeId { get; set; } = "";

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

public sealed class ProtocolConstraint
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("field")]
    public string Field { get; set; } = "selections";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "parent";

    [JsonPropertyName("shared")]
    public bool Shared { get; set; }

    [JsonPropertyName("includeChildSelections")]
    public bool IncludeChildSelections { get; set; }

    [JsonPropertyName("includeChildForces")]
    public bool IncludeChildForces { get; set; }

    [JsonPropertyName("percentValue")]
    public bool PercentValue { get; set; }
}

public sealed class ProtocolModifier
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("conditions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCondition>? Conditions { get; set; }

    [JsonPropertyName("conditionGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolConditionGroup>? ConditionGroups { get; set; }

    [JsonPropertyName("repeats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolRepeat>? Repeats { get; set; }
}

public sealed class ProtocolModifierGroup
{
    [JsonPropertyName("conditions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCondition>? Conditions { get; set; }

    [JsonPropertyName("conditionGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolConditionGroup>? ConditionGroups { get; set; }

    [JsonPropertyName("repeats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolRepeat>? Repeats { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }

    [JsonPropertyName("modifierGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }
}

public sealed class ProtocolCondition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("field")]
    public string Field { get; set; } = "selections";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "self";

    [JsonPropertyName("childId")]
    public string ChildId { get; set; } = "";

    [JsonPropertyName("shared")]
    public bool Shared { get; set; }

    [JsonPropertyName("includeChildSelections")]
    public bool IncludeChildSelections { get; set; }

    [JsonPropertyName("includeChildForces")]
    public bool IncludeChildForces { get; set; }

    [JsonPropertyName("percentValue")]
    public bool PercentValue { get; set; }
}

public sealed class ProtocolConditionGroup
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "and";

    [JsonPropertyName("conditions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCondition>? Conditions { get; set; }

    [JsonPropertyName("conditionGroups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolConditionGroup>? ConditionGroups { get; set; }
}

public sealed class ProtocolRepeat
{
    [JsonPropertyName("value")]
    public double Value { get; set; } = 1;

    [JsonPropertyName("repeats")]
    public int Repeats { get; set; } = 1;

    [JsonPropertyName("field")]
    public string Field { get; set; } = "selections";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "self";

    [JsonPropertyName("childId")]
    public string ChildId { get; set; } = "";

    [JsonPropertyName("roundUp")]
    public bool RoundUp { get; set; }

    [JsonPropertyName("shared")]
    public bool Shared { get; set; }

    [JsonPropertyName("includeChildSelections")]
    public bool IncludeChildSelections { get; set; }

    [JsonPropertyName("includeChildForces")]
    public bool IncludeChildForces { get; set; }

    [JsonPropertyName("percentValue")]
    public bool PercentValue { get; set; }
}

public sealed class ProtocolRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("page")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Page { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }
}

public sealed class ProtocolProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("typeId")]
    public string TypeId { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("characteristics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolCharacteristic>? Characteristics { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }
}

public sealed class ProtocolCharacteristic
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("typeId")]
    public string TypeId { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public sealed class ProtocolInfoGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("profiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolProfile>? Profiles { get; set; }

    [JsonPropertyName("rules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolRule>? Rules { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }

    [JsonPropertyName("infoLinks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolInfoLink>? InfoLinks { get; set; }
}

public sealed class ProtocolInfoLink
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProtocolModifier>? Modifiers { get; set; }
}

public sealed class ProtocolCatalogueLink
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("importRootEntries")]
    public bool ImportRootEntries { get; set; } = true;
}

public sealed class ProtocolPublication
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("shortName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShortName { get; set; }

    [JsonPropertyName("publisher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Publisher { get; set; }

    [JsonPropertyName("publicationDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicationDate { get; set; }

    [JsonPropertyName("publisherUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublisherUrl { get; set; }
}

// ===== Serialization helpers =====

/// <summary>
/// Shared JSON serialization options for the protocol.
/// Uses manual type dispatch since the "type" discriminator is a semantic property.
/// </summary>
public static class ProtocolSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string SerializeCommand(ProtocolCommand command) =>
        JsonSerializer.Serialize(command, command.GetType(), Options);

    public static string SerializeResponse(ProtocolResponse response) =>
        JsonSerializer.Serialize(response, response.GetType(), Options);

    public static ProtocolResponse? DeserializeResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        return type switch
        {
            "setupResult" => JsonSerializer.Deserialize<SetupResult>(json, Options),
            "actionResult" => JsonSerializer.Deserialize<ActionResult>(json, Options),
            "state" => JsonSerializer.Deserialize<StateResponse>(json, Options),
            "errors" => JsonSerializer.Deserialize<ErrorsResponse>(json, Options),
            "teardownResult" => JsonSerializer.Deserialize<TeardownResult>(json, Options),
            "error" => JsonSerializer.Deserialize<ProtocolError>(json, Options),
            _ => throw new JsonException($"Unknown response type: {type}"),
        };
    }

    public static ProtocolCommand? DeserializeCommand(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        return type switch
        {
            "setup" => JsonSerializer.Deserialize<SetupCommand>(json, Options),
            "action" => JsonSerializer.Deserialize<ActionCommand>(json, Options),
            "getState" => JsonSerializer.Deserialize<GetStateCommand>(json, Options),
            "getErrors" => JsonSerializer.Deserialize<GetErrorsCommand>(json, Options),
            "teardown" => JsonSerializer.Deserialize<TeardownCommand>(json, Options),
            _ => throw new JsonException($"Unknown command type: {type}"),
        };
    }
}
