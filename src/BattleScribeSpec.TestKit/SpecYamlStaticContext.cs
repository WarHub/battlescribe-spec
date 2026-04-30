using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using YamlDotNet.Serialization;

namespace BattleScribeSpec;

/// <summary>
/// YamlDotNet static context for AOT-compatible YAML deserialization.
/// Registers all types that participate in spec YAML deserialization.
/// The rest of this partial class is generated at build time by the YamlDotNet source generator.
/// </summary>
[YamlStaticContext]
// Roster spec file model types
[YamlSerializable(typeof(SpecFile))]
[YamlSerializable(typeof(SetupDef))]
[YamlSerializable(typeof(GameSystemDef))]
[YamlSerializable(typeof(CatalogueDef))]
[YamlSerializable(typeof(StepDef))]
[YamlSerializable(typeof(ExpectedStateDef))]
[YamlSerializable(typeof(ErrorAssertionDef))]
[YamlSerializable(typeof(ExpectedForceDef))]
[YamlSerializable(typeof(ExpectedSelectionDef))]
[YamlSerializable(typeof(ExpectedCostDef))]
[YamlSerializable(typeof(ExpectedProfileDef))]
[YamlSerializable(typeof(ExpectedCharacteristicDef))]
[YamlSerializable(typeof(ExpectedRuleDef))]
[YamlSerializable(typeof(ExpectedCategoryDef))]
[YamlSerializable(typeof(ExpectedPublicationDef))]
// GameData spec file model types
[YamlSerializable(typeof(GameDataSpecFile))]
[YamlSerializable(typeof(GameDataSetupDef))]
[YamlSerializable(typeof(GameDataStepDef))]
[YamlSerializable(typeof(GameDataExpectedStateDef))]
// Protocol setup data types (used in YAML setup section)
[YamlSerializable(typeof(ProtocolGameSystem))]
[YamlSerializable(typeof(ProtocolCatalogue))]
[YamlSerializable(typeof(ProtocolCostType))]
[YamlSerializable(typeof(ProtocolProfileType))]
[YamlSerializable(typeof(ProtocolCharacteristicType))]
[YamlSerializable(typeof(ProtocolForceEntry))]
[YamlSerializable(typeof(ProtocolCategoryEntry))]
[YamlSerializable(typeof(ProtocolSelectionEntry))]
[YamlSerializable(typeof(ProtocolSelectionEntryGroup))]
[YamlSerializable(typeof(ProtocolEntryLink))]
[YamlSerializable(typeof(ProtocolCategoryLink))]
[YamlSerializable(typeof(ProtocolCostValue))]
[YamlSerializable(typeof(ProtocolConstraint))]
[YamlSerializable(typeof(ProtocolModifier))]
[YamlSerializable(typeof(ProtocolModifierGroup))]
[YamlSerializable(typeof(ProtocolCondition))]
[YamlSerializable(typeof(ProtocolConditionGroup))]
[YamlSerializable(typeof(ProtocolRepeat))]
[YamlSerializable(typeof(ProtocolRule))]
[YamlSerializable(typeof(ProtocolProfile))]
[YamlSerializable(typeof(ProtocolCharacteristic))]
[YamlSerializable(typeof(ProtocolInfoGroup))]
[YamlSerializable(typeof(ProtocolInfoLink))]
[YamlSerializable(typeof(ProtocolCatalogueLink))]
[YamlSerializable(typeof(ProtocolPublication))]
public partial class SpecYamlStaticContext : YamlDotNet.Serialization.StaticContext;
