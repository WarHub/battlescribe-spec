namespace BattleScribeSpec;

/// <summary>
/// Engine-agnostic state records for roster conformance testing.
/// These types are used by both the spec runner and engine implementations.
/// </summary>

public record RosterState(
    string Name,
    string GameSystemId,
    IReadOnlyList<ForceState> Forces,
    IReadOnlyList<CostState> Costs,
    IReadOnlyList<string> ValidationErrors);

public record ForceState(
    string Name,
    string? CatalogueId,
    IReadOnlyList<SelectionState> Selections);

public record SelectionState(
    string Name,
    string? EntryId,
    string? Type,
    int Number,
    bool Hidden,
    IReadOnlyList<CostState> Costs,
    IReadOnlyList<SelectionState> Children);

public record CostState(
    string Name,
    string TypeId,
    double Value);
