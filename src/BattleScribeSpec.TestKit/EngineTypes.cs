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
    IReadOnlyList<SelectionState> Children,
    IReadOnlyList<ProfileState> Profiles = default!,
    IReadOnlyList<RuleState> Rules = default!,
    IReadOnlyList<CategoryState> Categories = default!,
    string? Page = null)
{
    public IReadOnlyList<ProfileState> Profiles { get; init; } = Profiles ?? [];
    public IReadOnlyList<RuleState> Rules { get; init; } = Rules ?? [];
    public IReadOnlyList<CategoryState> Categories { get; init; } = Categories ?? [];
}

public record CostState(
    string Name,
    string TypeId,
    double Value);

public record ProfileState(
    string Name,
    string? TypeId,
    string? TypeName,
    bool Hidden,
    IReadOnlyList<CharacteristicState> Characteristics);

public record CharacteristicState(
    string Name,
    string? TypeId,
    string Value);

public record RuleState(
    string Name,
    string Description,
    bool Hidden);

public record CategoryState(
    string Name,
    string? EntryId,
    bool Primary);
