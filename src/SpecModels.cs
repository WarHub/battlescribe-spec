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
    bool PercentValue = false);

public record ConstraintSpec(
    string Id,
    string Type,
    double Value,
    string Field,
    string Scope);

public record CostSpec(
    string Name,
    string TypeId,
    double Value);

public record CostTypeSpec(
    string Id,
    string Name,
    double DefaultCostLimit = -1.0);

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
    CategoryLinkSpec[]? CategoryLinks = null);

public record ForceEntrySpec(
    string Id,
    string Name);

public record GameSystemSpec(
    string Id = "test-gs",
    string Name = "Test Game System",
    ForceEntrySpec[]? ForceEntries = null,
    CostTypeSpec[]? CostTypes = null);

public record CatalogueSpec(
    string Id = "cat-1",
    string Name = "Cat",
    string GameSystemId = "test-gs",
    SelectionEntrySpec[]? SelectionEntries = null);

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
    ModifierSpec[]? Modifiers = null);

public record CategoryLinkSpec(
    string Id,
    string TargetId,
    string Name,
    bool Primary = false);

/// <summary>
/// Complete test scenario specification.
/// </summary>
public record ScenarioSpec(
    GameSystemSpec GameSystem,
    CatalogueSpec Catalogue);
