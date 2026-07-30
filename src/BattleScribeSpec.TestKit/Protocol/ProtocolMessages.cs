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
[JsonDerivedType(typeof(DescribeCommand), "describe")]
[JsonDerivedType(typeof(ScreenshotCommand), "screenshot")]
[JsonDerivedType(typeof(ExportRosterXmlCommand), "exportRosterXml")]
[JsonDerivedType(typeof(RecordStartCommand), "recordStart")]
[JsonDerivedType(typeof(RecordStopCommand), "recordStop")]
[JsonDerivedType(typeof(GameDataSetupCommand), "gamedataSetup")]
[JsonDerivedType(typeof(GameDataActionCommand), "gamedataAction")]
[JsonDerivedType(typeof(GameDataGetStateCommand), "gamedataGetState")]
[JsonDerivedType(typeof(GameDataGetErrorsCommand), "gamedataGetErrors")]
public abstract class ProtocolCommand
{
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>
    /// Optional correlation id (protocol v1.1+), wire name <c>corrId</c>. Clients SHOULD send it;
    /// adapters (via <see cref="AdapterHandler"/>) echo it verbatim on the response so a
    /// client-side timeout can discard a late response instead of desyncing the stream. Omitted
    /// from the wire when null — a response with no corrId falls back to strict positional
    /// ordering (legacy adapters). Named <c>corrId</c> rather than <c>id</c> because
    /// <see cref="GameDataActionCommand.Id"/> already uses the bare "id" wire field for a
    /// domain concept (declared entry id / openFile target).
    /// </summary>
    [JsonPropertyName("corrId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CorrId { get; set; }

    /// <summary>
    /// Optional W3C trace-context header (protocol v1.1+), wire name <c>traceparent</c>.
    /// Clients SHOULD send it so the adapter can parent its spans under the client's spec span,
    /// producing one distributed trace across the runner and the engine process.
    /// </summary>
    /// <remarks>
    /// Per-request rather than per-process on purpose: one adapter process serves many specs, so
    /// a process-level parent would collapse every spec into a single trace. Adapters that ignore
    /// this field remain fully conformant — same optional contract as <see cref="CorrId"/>.
    /// </remarks>
    [JsonPropertyName("traceparent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Traceparent { get; set; }

    /// <summary>
    /// Optional W3C <c>tracestate</c>, the companion of <see cref="Traceparent"/>.
    /// </summary>
    /// <remarks>
    /// W3C requires a vendor that receives <c>tracestate</c> to forward it on outgoing requests.
    /// Without it, a third-party adapter sitting behind a vendor backend loses its vendor context —
    /// which is precisely the cross-language case this field exists to serve. Together the two
    /// fields form a W3C trace-context carrier, so an adapter in any language can feed them
    /// straight into its stock propagator.
    /// </remarks>
    [JsonPropertyName("tracestate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tracestate { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetupResult), "setupResult")]
[JsonDerivedType(typeof(ActionResult), "actionResult")]
[JsonDerivedType(typeof(StateResponse), "state")]
[JsonDerivedType(typeof(ErrorsResponse), "errors")]
[JsonDerivedType(typeof(TeardownResult), "teardownResult")]
[JsonDerivedType(typeof(ProtocolError), "error")]
[JsonDerivedType(typeof(DescribeResult), "describeResult")]
[JsonDerivedType(typeof(ScreenshotResult), "screenshotResult")]
[JsonDerivedType(typeof(RosterXmlResult), "rosterXmlResult")]
[JsonDerivedType(typeof(RecordResult), "recordResult")]
[JsonDerivedType(typeof(GameDataActionResult), "gamedataActionResult")]
[JsonDerivedType(typeof(GameDataStateResponse), "gamedataState")]
public abstract class ProtocolResponse
{
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>
    /// Echo of the originating command's <see cref="ProtocolCommand.CorrId"/> (protocol v1.1+),
    /// wire name <c>corrId</c>, omitted when the command had none. Named <c>corrId</c> rather
    /// than <c>id</c> because <see cref="GameDataActionResult.Id"/> already uses the bare "id"
    /// wire field for a domain concept (loaded file root id). See
    /// <see cref="ProtocolCommand.CorrId"/>.
    /// </summary>
    [JsonPropertyName("corrId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CorrId { get; set; }
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

    /// <summary>loadRoster: the BattleScribe <c>.ros</c> XML payload.</summary>
    public string? Xml { get; set; }
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

/// <summary>
/// Protocol v1.1: capability handshake. Sent once after process start; the adapter answers
/// with its identity, supported domains, and optional capabilities. Legacy v1.0 adapters
/// answer with an error — callers treat that as roster-only with no optional capabilities.
/// </summary>
public sealed class DescribeCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "describe";
}

/// <summary>Protocol v1.1 (optional): capture the engine UI as a PNG.</summary>
public sealed class ScreenshotCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "screenshot";
}

/// <summary>Protocol v1.1 (optional): export the current roster as .ros XML.</summary>
public sealed class ExportRosterXmlCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "exportRosterXml";
}

/// <summary>Protocol v1.1 (optional): start recording UI actions.</summary>
public sealed class RecordStartCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "recordStart";
}

/// <summary>Protocol v1.1 (optional): stop recording and return the recorded actions.</summary>
public sealed class RecordStopCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "recordStop";
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

/// <summary>Protocol v1.1: response to <see cref="DescribeCommand"/>.</summary>
public sealed class DescribeResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "describeResult";

    /// <summary>Engine identity (e.g. "battlescribe"); keys spec applicability and report labels.</summary>
    public string Name { get; set; } = "";

    /// <summary>Engine/adapter version, free-form.</summary>
    public string? Version { get; set; }

    public string ProtocolVersion { get; set; } = "1.1";

    /// <summary>Supported spec domains: "roster" and/or "gamedata".</summary>
    public List<string> Domains { get; set; } = ["roster"];

    public AdapterCapabilities Capabilities { get; set; } = new();
}

/// <summary>Optional protocol v1.1 capabilities advertised by <see cref="DescribeResult"/>.</summary>
public sealed class AdapterCapabilities
{
    public bool Screenshot { get; set; }

    public bool Record { get; set; }

    /// <summary>Supports <c>exportRosterXml</c>.</summary>
    public bool RosterXml { get; set; }

    /// <summary>Max concurrent instances the engine tolerates; 0 = unlimited.</summary>
    public int MaxParallel { get; set; }
}

public sealed class ScreenshotResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "screenshotResult";

    public string PngBase64 { get; set; } = "";
}

public sealed class RosterXmlResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "rosterXmlResult";

    public string Xml { get; set; } = "";
}

public sealed class RecordResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "recordResult";

    /// <summary>Recorded actions as a JSON array string; null when nothing was recorded.</summary>
    public string? ActionsJson { get; set; }
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

    /// <summary>NewRecruit addition: shared force-entry collection.</summary>
    public List<ProtocolForceEntry>? SharedForceEntries { get; set; }

    /// <summary>NewRecruit addition: shared association collection.</summary>
    public List<ProtocolAssociation>? SharedAssociations { get; set; }
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

    /// <summary>NewRecruit addition.</summary>
    public string? Kind { get; set; }

    public List<ProtocolCharacteristicType>? CharacteristicTypes { get; set; }

    /// <summary>NewRecruit addition: export-only attribute types (parallel to characteristic types).</summary>
    public List<ProtocolAttributeType>? AttributeTypes { get; set; }
}

public sealed class ProtocolCharacteristicType
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>NewRecruit addition.</summary>
    public string? Kind { get; set; }

    /// <summary>NewRecruit addition.</summary>
    public string? DefaultValue { get; set; }
}

/// <summary>NewRecruit addition: export-only attribute type on a profile type.</summary>
public sealed class ProtocolAttributeType
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

    /// <summary>NewRecruit addition: associations relating this entry to query-resolved selections.</summary>
    public List<ProtocolAssociation>? Associations { get; set; }
}

/// <summary>
/// NewRecruit addition: an association relating a selection to a min/max number of other
/// selections resolved by a query (scope/field/childId). Not in original BattleScribe v2.03.
/// </summary>
public sealed class ProtocolAssociation
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public int Min { get; set; }

    public int Max { get; set; }

    public string? Scope { get; set; }

    public string? ChildId { get; set; }

    public string? Field { get; set; }
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

    /// <summary>NewRecruit addition.</summary>
    public bool Negative { get; set; }

    /// <summary>NewRecruit addition.</summary>
    public bool Automatic { get; set; }

    /// <summary>NewRecruit addition: custom constraint-violation message.</summary>
    public string? Message { get; set; }
}

public sealed class ProtocolModifier
{
    public string Type { get; set; } = "";

    public string Field { get; set; } = "";

    public string Value { get; set; } = "";

    public List<ProtocolCondition>? Conditions { get; set; }

    public List<ProtocolConditionGroup>? ConditionGroups { get; set; }

    public List<ProtocolRepeat>? Repeats { get; set; }

    /// <summary>NewRecruit addition: a modifier's local condition groups.</summary>
    public List<ProtocolLocalConditionGroup>? LocalConditionGroups { get; set; }
}

/// <summary>
/// NewRecruit addition: a modifier's local condition group — a query (field/scope/childId/value)
/// plus a condition <see cref="Type"/> and a repeat count.
/// </summary>
public sealed class ProtocolLocalConditionGroup
{
    public string Type { get; set; } = "atLeast";

    public decimal Value { get; set; }

    public string Field { get; set; } = "selections";

    public string Scope { get; set; } = "parent";

    public string? ChildId { get; set; }

    public bool IncludeChildSelections { get; set; }

    public bool IncludeChildForces { get; set; }

    public int Repeats { get; set; }
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
    public decimal Value { get; set; } = 1m;

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

// ===== GameData protocol (v1.1) =====

/// <summary>
/// Protocol v1.1: initialize a gamedata (data-file editing) engine. The payload shapes
/// match roster <see cref="SetupCommand"/>, but the data IS the editable artifact.
/// </summary>
public sealed class GameDataSetupCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataSetup";

    public string? SpecId { get; set; }

    public ProtocolGameSystem GameSystem { get; set; } = new();

    public List<ProtocolCatalogue> Catalogues { get; set; } = [];
}

/// <summary>
/// Protocol v1.1: execute a data-editing action. Modeled 1:1 on the IGameDataEngine
/// operation table in docs/adapter-protocol.md.
/// </summary>
public sealed class GameDataActionCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataAction";

    /// <summary>openFile|addEntry|addLink|removeEntry|setField|setCost|setCharacteristic|reload|exportFile|loadFile.</summary>
    public string Action { get; set; } = "";

    /// <summary>openFile target id, or the declared id for addEntry/addLink.</summary>
    public string? Id { get; set; }

    public string? ParentId { get; set; }

    public string? EntryType { get; set; }

    public string? Name { get; set; }

    public string? EntryId { get; set; }

    public string? Field { get; set; }

    public string? Value { get; set; }

    public string? TargetId { get; set; }

    public string? LinkType { get; set; }

    public string? CostTypeId { get; set; }

    public string? NameOrTypeId { get; set; }

    /// <summary>loadFile: the BattleScribe XML payload.</summary>
    public string? Xml { get; set; }
}

public sealed class GameDataGetStateCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataGetState";
}

public sealed class GameDataGetErrorsCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataGetErrors";
}

public sealed class GameDataActionResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "gamedataActionResult";

    public bool Ok { get; set; }

    public string? Error { get; set; }

    /// <summary>Created entry/link id (addEntry, addLink).</summary>
    public string? EntryId { get; set; }

    /// <summary>Exported XML (exportFile).</summary>
    public string? Xml { get; set; }

    /// <summary>Loaded file root id (loadFile).</summary>
    public string? Id { get; set; }
}

public sealed class GameDataStateResponse : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "gamedataState";

    public GameData.GameDataState State { get; set; } = new();
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
