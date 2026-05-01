using System.Text.Json;
using System.Text.Json.Serialization;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Protocol;

// JSON-line protocol message types for BattleScribe conformance testing.
// The runner sends commands to the adapter via stdin, and receives responses via stdout.
// Each message is a single JSON object on one line (NDJSON format).

// ===== Base types =====

/// <summary>
/// Base for all protocol messages. The "type" field discriminates message kinds.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetupCommand), "setup")]
[JsonDerivedType(typeof(SetupFromFilesCommand), "setupFromFiles")]
[JsonDerivedType(typeof(ActionCommand), "action")]
[JsonDerivedType(typeof(GetStateCommand), "getState")]
[JsonDerivedType(typeof(GetErrorsCommand), "getErrors")]
[JsonDerivedType(typeof(TeardownCommand), "teardown")]
public abstract class ProtocolCommand
{
    [JsonIgnore]
    public abstract string Type { get; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetupResult), "setupResult")]
[JsonDerivedType(typeof(ActionResult), "actionResult")]
[JsonDerivedType(typeof(StateResponse), "state")]
[JsonDerivedType(typeof(ErrorsResponse), "errors")]
[JsonDerivedType(typeof(TeardownResult), "teardownResult")]
[JsonDerivedType(typeof(ProtocolError), "error")]
public abstract class ProtocolResponse
{
    [JsonIgnore]
    public abstract string Type { get; }
}

// ===== Runner → Adapter Commands =====

/// <summary>
/// Initialize the engine with game system and catalogues data.
/// </summary>
public sealed class SetupCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "setup";

    public string Version { get; set; } = "1.0";

    public string? SpecId { get; set; }

    public ProtocolGameSystem GameSystem { get; set; } = new();

    public List<ProtocolCatalogue> Catalogues { get; set; } = [];
}

/// <summary>
/// Initialize the engine with raw data files (.gst and .cat XML).
/// Used for DataSource specs that load real-world game data.
/// </summary>
public sealed class SetupFromFilesCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "setupFromFiles";

    public string? SpecId { get; set; }

    public List<ProtocolDataFile> Files { get; set; } = [];
}

/// <summary>
/// A data file (game system .gst or catalogue .cat) with its content.
/// </summary>
public sealed class ProtocolDataFile
{
    public string FileName { get; set; } = "";

    public string Content { get; set; } = "";
}

/// <summary>
/// Execute a roster editing action.
/// All addressing is ID-based: definition references use BattleScribe IDs,
/// instance references use IDs from prior action outputs.
/// </summary>
public sealed class ActionCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "action";

    public string Action { get; set; } = "";

    public string? ForceEntryId { get; set; }

    public string? EntryId { get; set; }

    public string? CatalogueId { get; set; }

    public string? ForceId { get; set; }

    public string? SelectionId { get; set; }

    public string? CostTypeId { get; set; }

    public int? Count { get; set; }

    public decimal? Value { get; set; }

    public string? CustomName { get; set; }

    public string? CustomNotes { get; set; }

    public string? CategoryEntryId { get; set; }
}

public sealed class GetStateCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "getState";
}

public sealed class GetErrorsCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "getErrors";
}

public sealed class TeardownCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "teardown";
}

// ===== Adapter → Runner Responses =====

public sealed class SetupResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "setupResult";

    public List<string> Errors { get; set; } = [];
}

public sealed class ActionResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "actionResult";

    public bool Ok { get; set; }

    public string? Error { get; set; }

    public ActionOutputs? Outputs { get; set; }
}

public sealed class StateResponse : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "state";

    public string Name { get; set; } = "";

    public string GameSystemId { get; set; } = "";

    public string? GameSystemName { get; set; }

    public List<ForceState> Forces { get; set; } = [];

    public List<CostState> Costs { get; set; } = [];

    public List<CostState>? CostLimits { get; set; }

    public List<ValidationErrorState> ValidationErrors { get; set; } = [];
}

public sealed class ErrorsResponse : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "errors";

    public List<ValidationErrorState> Errors { get; set; } = [];
}

public sealed class TeardownResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "teardownResult";
}

/// <summary>
/// Returned when the adapter encounters an unrecoverable error.
/// </summary>
public sealed class ProtocolError : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "error";

    public string Message { get; set; } = "";
}

// ===== Protocol Setup Data (game system + catalogue) =====

public class ProtocolGameSystem
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<ProtocolCostType>? CostTypes { get; set; }

    public List<ProtocolForceEntry>? ForceEntries { get; set; }

    public List<ProtocolCategoryEntry>? CategoryEntries { get; set; }

    public List<ProtocolProfileType>? ProfileTypes { get; set; }

    public List<ProtocolPublication>? Publications { get; set; }

    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }

    public List<ProtocolEntryLink>? EntryLinks { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }

    public List<ProtocolSelectionEntry>? SharedSelectionEntries { get; set; }

    public List<ProtocolSelectionEntryGroup>? SharedSelectionEntryGroups { get; set; }

    public List<ProtocolRule>? SharedRules { get; set; }

    public List<ProtocolProfile>? SharedProfiles { get; set; }

    public List<ProtocolInfoGroup>? SharedInfoGroups { get; set; }
}

public class ProtocolCatalogue
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string GameSystemId { get; set; } = "";

    public bool Library { get; set; }

    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }

    public List<ProtocolEntryLink>? EntryLinks { get; set; }

    public List<ProtocolSelectionEntry>? SharedSelectionEntries { get; set; }

    public List<ProtocolSelectionEntryGroup>? SharedSelectionEntryGroups { get; set; }

    public List<ProtocolRule>? SharedRules { get; set; }

    public List<ProtocolProfile>? SharedProfiles { get; set; }

    public List<ProtocolInfoGroup>? SharedInfoGroups { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }

    public List<ProtocolCatalogueLink>? CatalogueLinks { get; set; }

    public List<ProtocolPublication>? Publications { get; set; }

    public List<ProtocolCostType>? CostTypes { get; set; }

    public List<ProtocolProfileType>? ProfileTypes { get; set; }

    public List<ProtocolCategoryEntry>? CategoryEntries { get; set; }

    public List<ProtocolForceEntry>? ForceEntries { get; set; }
}

public sealed class ProtocolCostType
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public decimal? DefaultCostLimit { get; set; }

    public bool Hidden { get; set; }

    public bool Limit { get; set; }
}

public sealed class ProtocolProfileType
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<ProtocolCharacteristicType>? CharacteristicTypes { get; set; }
}

public sealed class ProtocolCharacteristicType
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
}

public sealed class ProtocolForceEntry
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Hidden { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public List<ProtocolConstraint>? Constraints { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    public List<ProtocolCategoryLink>? CategoryLinks { get; set; }

    public List<ProtocolForceEntry>? ForceEntries { get; set; }

    public List<ProtocolProfile>? Profiles { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolInfoGroup>? InfoGroups { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }
}

public sealed class ProtocolCategoryEntry
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Hidden { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public List<ProtocolConstraint>? Constraints { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    public List<ProtocolProfile>? Profiles { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolInfoGroup>? InfoGroups { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }
}

public sealed class ProtocolSelectionEntry
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Type { get; set; } = "unit";

    public bool Hidden { get; set; }

    public bool Import { get; set; } = true;

    public bool Collective { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public List<ProtocolCostValue>? Costs { get; set; }

    public List<ProtocolConstraint>? Constraints { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }

    public List<ProtocolSelectionEntryGroup>? SelectionEntryGroups { get; set; }

    public List<ProtocolEntryLink>? EntryLinks { get; set; }

    public List<ProtocolCategoryLink>? CategoryLinks { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolProfile>? Profiles { get; set; }

    public List<ProtocolInfoGroup>? InfoGroups { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }
}

public sealed class ProtocolSelectionEntryGroup
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Hidden { get; set; }

    public bool Collective { get; set; }

    public bool Import { get; set; } = true;

    public string? DefaultSelectionEntryId { get; set; }

    public List<ProtocolConstraint>? Constraints { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }

    public List<ProtocolSelectionEntryGroup>? SelectionEntryGroups { get; set; }

    public List<ProtocolEntryLink>? EntryLinks { get; set; }

    public List<ProtocolCategoryLink>? CategoryLinks { get; set; }

    public List<ProtocolCostValue>? Costs { get; set; }

    public List<ProtocolProfile>? Profiles { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolInfoGroup>? InfoGroups { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }
}

public sealed class ProtocolEntryLink
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string Type { get; set; } = "selectionEntry";

    public bool Hidden { get; set; }

    public bool Collective { get; set; }

    public bool Import { get; set; } = true;

    public List<ProtocolCostValue>? Costs { get; set; }

    public List<ProtocolConstraint>? Constraints { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    public List<ProtocolCategoryLink>? CategoryLinks { get; set; }

    public List<ProtocolSelectionEntry>? SelectionEntries { get; set; }

    public List<ProtocolSelectionEntryGroup>? SelectionEntryGroups { get; set; }

    public List<ProtocolEntryLink>? EntryLinks { get; set; }

    public List<ProtocolProfile>? Profiles { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolInfoGroup>? InfoGroups { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }

    public string? PublicationId { get; set; }

    public string? Page { get; set; }
}

public sealed class ProtocolCategoryLink
{
    public string Id { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Primary { get; set; }

    public bool Hidden { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public List<ProtocolConstraint>? Constraints { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    public List<ProtocolProfile>? Profiles { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolInfoGroup>? InfoGroups { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }
}

public sealed class ProtocolCostValue
{
    public string Name { get; set; } = "";

    public string TypeId { get; set; } = "";

    public decimal Value { get; set; }
}

public sealed class ProtocolConstraint
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public decimal Value { get; set; }

    public string Field { get; set; } = "selections";

    public string Scope { get; set; } = "parent";

    public bool Shared { get; set; }

    public bool IncludeChildSelections { get; set; }

    public bool IncludeChildForces { get; set; }

    public bool PercentValue { get; set; }
}

public sealed class ProtocolModifier
{
    public string Type { get; set; } = "";

    public string Field { get; set; } = "";

    public string Value { get; set; } = "";

    public List<ProtocolCondition>? Conditions { get; set; }

    public List<ProtocolConditionGroup>? ConditionGroups { get; set; }

    public List<ProtocolRepeat>? Repeats { get; set; }
}

public sealed class ProtocolModifierGroup
{
    public List<ProtocolCondition>? Conditions { get; set; }

    public List<ProtocolConditionGroup>? ConditionGroups { get; set; }

    public List<ProtocolRepeat>? Repeats { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }
}

public sealed class ProtocolCondition
{
    public string Type { get; set; } = "";

    public decimal Value { get; set; }

    public string Field { get; set; } = "selections";

    public string Scope { get; set; } = "self";

    public string ChildId { get; set; } = "";

    public bool Shared { get; set; }

    public bool IncludeChildSelections { get; set; }

    public bool IncludeChildForces { get; set; }

    public bool PercentValue { get; set; }
}

public sealed class ProtocolConditionGroup
{
    public string Type { get; set; } = "and";

    public List<ProtocolCondition>? Conditions { get; set; }

    public List<ProtocolConditionGroup>? ConditionGroups { get; set; }
}

public sealed class ProtocolRepeat
{
    public decimal Value { get; set; } = 1;

    public int Repeats { get; set; } = 1;

    public string Field { get; set; } = "selections";

    public string Scope { get; set; } = "self";

    public string ChildId { get; set; } = "";

    public bool RoundUp { get; set; }

    public bool Shared { get; set; }

    public bool IncludeChildSelections { get; set; }

    public bool IncludeChildForces { get; set; }

    public bool PercentValue { get; set; }
}

public sealed class ProtocolRule
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public bool Hidden { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }
}

public sealed class ProtocolProfile
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string TypeId { get; set; } = "";

    public string TypeName { get; set; } = "";

    public bool Hidden { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public List<ProtocolCharacteristic>? Characteristics { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }
}

public sealed class ProtocolCharacteristic
{
    public string Name { get; set; } = "";

    public string TypeId { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed class ProtocolInfoGroup
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Hidden { get; set; }

    public string? PublicationId { get; set; }

    public string? Page { get; set; }

    public List<ProtocolProfile>? Profiles { get; set; }

    public List<ProtocolRule>? Rules { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }

    public List<ProtocolInfoLink>? InfoLinks { get; set; }

    public List<ProtocolInfoGroup>? InfoGroups { get; set; }
}

public sealed class ProtocolInfoLink
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string Type { get; set; } = "";

    public bool Hidden { get; set; }

    public string? PublicationId { get; set; }

    public string? Page { get; set; }

    public List<ProtocolModifier>? Modifiers { get; set; }

    public List<ProtocolModifierGroup>? ModifierGroups { get; set; }
}

public sealed class ProtocolCatalogueLink
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string? Type { get; set; }

    public bool ImportRootEntries { get; set; } = true;
}

public sealed class ProtocolPublication
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? ShortName { get; set; }

    public string? Publisher { get; set; }

    public string? PublicationDate { get; set; }

    public string? PublisherUrl { get; set; }
}

// ===== Serialization helpers =====

/// <summary>
/// Shared JSON serialization for the protocol.
/// Uses source-generated <see cref="ProtocolJsonContext"/> for reflection-free serialization
/// with STJ polymorphic type discriminators for command/response routing.
/// </summary>
public static class ProtocolSerializer
{
    /// <summary>
    /// JSON serializer options matching the source-generated context.
    /// Prefer using <see cref="ProtocolJsonContext.Default"/> directly for type-safe serialization.
    /// </summary>
    public static JsonSerializerOptions Options => ProtocolJsonContext.Default.Options;

    public static string SerializeCommand(ProtocolCommand command)
        => JsonSerializer.Serialize(command, ProtocolJsonContext.Default.ProtocolCommand);

    public static string SerializeResponse(ProtocolResponse response)
        => JsonSerializer.Serialize(response, ProtocolJsonContext.Default.ProtocolResponse);

    public static ProtocolResponse? DeserializeResponse(string json)
        => JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ProtocolResponse);

    public static ProtocolCommand? DeserializeCommand(string json)
        => JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ProtocolCommand);
}
