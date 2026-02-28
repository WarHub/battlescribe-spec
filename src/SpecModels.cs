namespace BattleScribeSpec;

/// <summary>
/// Pure .NET specification records used to configure oracle test scenarios.
/// These types contain NO Java type references, making them safe to use from the test project.
/// </summary>

public record ModifierSpec(
    string Type,
    string Field,
    string Value,
    ConditionSpec[]? Conditions = null);

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
    CostSpec[]? Costs = null,
    ConstraintSpec[]? Constraints = null,
    ModifierSpec[]? Modifiers = null,
    SelectionEntrySpec[]? ChildEntries = null);

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

/// <summary>
/// Complete test scenario specification.
/// </summary>
public record ScenarioSpec(
    GameSystemSpec GameSystem,
    CatalogueSpec Catalogue);
