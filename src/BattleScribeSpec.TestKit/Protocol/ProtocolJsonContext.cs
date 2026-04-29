using System.Text.Json;
using System.Text.Json.Serialization;

namespace BattleScribeSpec.Protocol;

/// <summary>
/// Source-generated JSON serializer context for all protocol message types.
/// Eliminates runtime reflection for serialization (~43% faster, ~15% less allocation).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
// Commands (polymorphic base + concrete types)
[JsonSerializable(typeof(ProtocolCommand))]
[JsonSerializable(typeof(SetupCommand))]
[JsonSerializable(typeof(SetupFromFilesCommand))]
[JsonSerializable(typeof(ActionCommand))]
[JsonSerializable(typeof(GetStateCommand))]
[JsonSerializable(typeof(GetErrorsCommand))]
[JsonSerializable(typeof(TeardownCommand))]
// Responses (polymorphic base + concrete types)
[JsonSerializable(typeof(ProtocolResponse))]
[JsonSerializable(typeof(SetupResult))]
[JsonSerializable(typeof(ActionResult))]
[JsonSerializable(typeof(StateResponse))]
[JsonSerializable(typeof(ErrorsResponse))]
[JsonSerializable(typeof(TeardownResult))]
[JsonSerializable(typeof(ProtocolError))]
// Setup data types
[JsonSerializable(typeof(ProtocolGameSystem))]
[JsonSerializable(typeof(ProtocolCatalogue))]
[JsonSerializable(typeof(ProtocolCostType))]
[JsonSerializable(typeof(ProtocolForceEntry))]
[JsonSerializable(typeof(ProtocolCategoryEntry))]
[JsonSerializable(typeof(ProtocolProfileType))]
[JsonSerializable(typeof(ProtocolCharacteristicType))]
[JsonSerializable(typeof(ProtocolPublication))]
[JsonSerializable(typeof(ProtocolSelectionEntry))]
[JsonSerializable(typeof(ProtocolSelectionEntryGroup))]
[JsonSerializable(typeof(ProtocolEntryLink))]
[JsonSerializable(typeof(ProtocolCatalogueLink))]
[JsonSerializable(typeof(ProtocolRule))]
[JsonSerializable(typeof(ProtocolInfoLink))]
[JsonSerializable(typeof(ProtocolInfoGroup))]
[JsonSerializable(typeof(ProtocolProfile))]
[JsonSerializable(typeof(ProtocolCharacteristic))]
[JsonSerializable(typeof(ProtocolConstraint))]
[JsonSerializable(typeof(ProtocolModifier))]
[JsonSerializable(typeof(ProtocolModifierGroup))]
[JsonSerializable(typeof(ProtocolCondition))]
[JsonSerializable(typeof(ProtocolConditionGroup))]
[JsonSerializable(typeof(ProtocolCostValue))]
[JsonSerializable(typeof(ProtocolRepeat))]
[JsonSerializable(typeof(ProtocolDataFile))]
[JsonSerializable(typeof(ProtocolCategoryLink))]
// State types (from EngineTypes.cs)
[JsonSerializable(typeof(RosterState))]
[JsonSerializable(typeof(ForceState))]
[JsonSerializable(typeof(SelectionState))]
[JsonSerializable(typeof(CostState))]
[JsonSerializable(typeof(ProfileState))]
[JsonSerializable(typeof(CharacteristicState))]
[JsonSerializable(typeof(RuleState))]
[JsonSerializable(typeof(CategoryState))]
[JsonSerializable(typeof(PublicationState))]
[JsonSerializable(typeof(ValidationErrorState))]
[JsonSerializable(typeof(ActionOutputs))]
public partial class ProtocolJsonContext : JsonSerializerContext;
