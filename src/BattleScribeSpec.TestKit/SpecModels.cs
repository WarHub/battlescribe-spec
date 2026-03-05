namespace BattleScribeSpec;

/// <summary>
/// Pure .NET specification records used to configure oracle test scenarios.
/// These types contain NO Java type references, making them safe to use from the test project.
/// </summary>

public record ModifierSpec(
    string Type,
    string Field,
    string Value,
    ConditionSpec[]? Conditions = null,
    ConditionGroupSpec[]? ConditionGroups = null,
    RepeatSpec[]? Repeats = null);

public record ConditionSpec(
    string Type,
    double Value,
    string Field,
    string Scope,
    string ChildId = "",
    bool PercentValue = false,
    bool Shared = false,
    bool IncludeChildSelections = false,
    bool IncludeChildForces = false);

public record ConstraintSpec(
    string Id,
    string Type,
    double Value,
    string Field,
    string Scope,
    bool Shared = false,
    bool IncludeChildSelections = false,
    bool IncludeChildForces = false,
    bool PercentValue = false);

public record CostSpec(
    string Name,
    string TypeId,
    double Value);

public record CostTypeSpec(
    string Id,
    string Name,
    double DefaultCostLimit = -1.0,
    bool Hidden = false,
    bool Limit = false);

public record SelectionEntrySpec(
    string Id,
    string Name,
    string Type = "unit",
    bool Hidden = false,
    CostSpec[]? Costs = null,
    ConstraintSpec[]? Constraints = null,
    ModifierSpec[]? Modifiers = null,
    ModifierGroupSpec[]? ModifierGroups = null,
    SelectionEntrySpec[]? ChildEntries = null,
    SelectionEntryGroupSpec[]? SelectionEntryGroups = null,
    CategoryLinkSpec[]? CategoryLinks = null,
    bool Collective = false,
    RuleSpec[]? Rules = null,
    ProfileSpec[]? Profiles = null,
    InfoGroupSpec[]? InfoGroups = null,
    string Page = "",
    EntryLinkSpec[]? EntryLinks = null,
    InfoLinkSpec[]? InfoLinks = null,
    bool Import = true);

public record ForceEntrySpec(
    string Id,
    string Name,
    CategoryLinkSpec[]? CategoryLinks = null,
    ForceEntrySpec[]? ForceEntries = null);

public record GameSystemSpec(
    string Id = "test-gs",
    string Name = "Test Game System",
    ForceEntrySpec[]? ForceEntries = null,
    CostTypeSpec[]? CostTypes = null,
    CategoryEntrySpec[]? CategoryEntries = null,
    ProfileTypeSpec[]? ProfileTypes = null);

public record CategoryEntrySpec(
    string Id,
    string Name);

public record SelectionEntryGroupSpec(
    string Id,
    string Name,
    bool Hidden = false,
    string DefaultSelectionEntryId = "",
    ConstraintSpec[]? Constraints = null,
    ModifierSpec[]? Modifiers = null,
    SelectionEntrySpec[]? SelectionEntries = null,
    bool Import = true);

public record CatalogueSpec(
    string Id = "cat-1",
    string Name = "Cat",
    string GameSystemId = "test-gs",
    SelectionEntrySpec[]? SelectionEntries = null,
    SelectionEntryGroupSpec[]? SelectionEntryGroups = null,
    EntryLinkSpec[]? EntryLinks = null,
    SelectionEntrySpec[]? SharedSelectionEntries = null,
    SelectionEntryGroupSpec[]? SharedSelectionEntryGroups = null,
    RuleSpec[]? SharedRules = null,
    ProfileSpec[]? SharedProfiles = null,
    InfoGroupSpec[]? SharedInfoGroups = null,
    InfoLinkSpec[]? InfoLinks = null,
    CatalogueLinkSpec[]? CatalogueLinks = null,
    PublicationSpec[]? Publications = null);

public record CatalogueLinkSpec(
    string Id,
    string Name,
    string TargetId,
    bool ImportRootEntries = true);

public record PublicationSpec(
    string Id,
    string Name,
    string ShortName = "",
    string Publisher = "",
    string PublicationDate = "",
    string PublisherUrl = "");

public record ConditionGroupSpec(
    string Type,
    ConditionSpec[]? Conditions = null,
    ConditionGroupSpec[]? ConditionGroups = null);

public record RepeatSpec(
    double Value = 1,
    int Repeats = 1,
    string Field = "selections",
    string Scope = "self",
    string ChildId = "",
    bool RoundUp = false,
    bool Shared = false,
    bool IncludeChildSelections = false,
    bool IncludeChildForces = false,
    bool PercentValue = false);

public record ModifierGroupSpec(
    ConditionSpec[]? Conditions = null,
    ConditionGroupSpec[]? ConditionGroups = null,
    RepeatSpec[]? Repeats = null,
    ModifierSpec[]? Modifiers = null,
    ModifierGroupSpec[]? ModifierGroups = null);

public record CategoryLinkSpec(
    string Id,
    string TargetId,
    string Name,
    bool Primary = false);

public record RuleSpec(
    string Id,
    string Name,
    string Description = "",
    bool Hidden = false,
    string Page = "",
    ModifierSpec[]? Modifiers = null);

public record ProfileSpec(
    string Id,
    string Name,
    string TypeId = "",
    string TypeName = "",
    bool Hidden = false,
    CharacteristicSpec[]? Characteristics = null,
    ModifierSpec[]? Modifiers = null,
    string Page = "");

public record CharacteristicSpec(
    string Name,
    string TypeId,
    string Value = "");

public record InfoGroupSpec(
    string Id,
    string Name,
    bool Hidden = false,
    ProfileSpec[]? Profiles = null,
    RuleSpec[]? Rules = null,
    ModifierSpec[]? Modifiers = null,
    InfoLinkSpec[]? InfoLinks = null);

public record EntryLinkSpec(
    string Id,
    string Name,
    string TargetId,
    string Type = "selectionEntry",
    bool Hidden = false,
    CostSpec[]? Costs = null,
    ConstraintSpec[]? Constraints = null,
    ModifierSpec[]? Modifiers = null,
    CategoryLinkSpec[]? CategoryLinks = null,
    bool Import = true);

public record InfoLinkSpec(
    string Id,
    string Name,
    string TargetId,
    string Type = "profile",
    bool Hidden = false,
    ModifierSpec[]? Modifiers = null);

/// <summary>
/// Complete test scenario specification.
/// Supports multiple catalogues — each force can be from a different catalogue.
/// </summary>
public record ScenarioSpec(
    GameSystemSpec GameSystem,
    CatalogueSpec[] Catalogues);

public record ProfileTypeSpec(
    string Id,
    string Name,
    CharacteristicTypeSpec[]? CharacteristicTypes = null);

public record CharacteristicTypeSpec(
    string Id,
    string Name);
