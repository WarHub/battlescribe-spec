namespace BattleScribeSpec.Roster;

/// <summary>
/// Engine-agnostic state records for roster conformance testing.
/// These types are used by both the spec runner and engine implementations.
/// They serialize to JSON via ProtocolJsonContext (camelCase naming, null-omission by default).
/// </summary>

public record ValidationErrorState(
    string Message,
    string? OwnerType = null,
    string? OwnerId = null,
    string? OwnerEntryId = null,
    string? EntryId = null,
    string? ConstraintId = null);

public record RosterState(
    string Name,
    string GameSystemId,
    IReadOnlyList<ForceState> Forces,
    IReadOnlyList<CostState> Costs,
    IReadOnlyList<ValidationErrorState> ValidationErrors,
    IReadOnlyList<CostState>? CostLimits = null,
    string? GameSystemName = null);

public record ForceState(
    string? Id,
    string Name,
    string? CatalogueId,
    IReadOnlyList<SelectionState> Selections,
    int? AvailableEntryCount = null,
    IReadOnlyList<ForceState>? ChildForces = null,
    IReadOnlyList<ProfileState>? Profiles = null,
    IReadOnlyList<RuleState>? Rules = null,
    bool Hidden = false,
    string? PublicationId = null,
    string? Page = null,
    string? EntryId = null,
    IReadOnlyList<CategoryState>? Categories = null,
    IReadOnlyList<PublicationState>? Publications = null,
    string? CatalogueName = null,
    string? CustomName = null,
    string? CustomNotes = null);

public record SelectionState(
    string? Id,
    string Name,
    string? EntryId,
    string? Type,
    int Number,
    bool Hidden,
    IReadOnlyList<CostState> Costs,
    IReadOnlyList<SelectionState> Children,
    IReadOnlyList<ProfileState>? Profiles = null,
    IReadOnlyList<RuleState>? Rules = null,
    IReadOnlyList<CategoryState>? Categories = null,
    string? Page = null,
    string? PublicationId = null,
    string? PublicationName = null,
    string? EntryGroupId = null,
    string? CustomName = null,
    string? CustomNotes = null);

public record CostState(
    string Name,
    string TypeId,
    decimal Value);

public record ProfileState(
    string Name,
    string? TypeId,
    string? TypeName,
    bool Hidden,
    IReadOnlyList<CharacteristicState> Characteristics,
    string? Page = null,
    string? PublicationId = null);

public record CharacteristicState(
    string Name,
    string? TypeId,
    string Value);

public record RuleState(
    string Name,
    string Description,
    bool Hidden,
    string? Page = null,
    string? PublicationId = null);

public record CategoryState(
    string Name,
    string? EntryId,
    bool Primary,
    IReadOnlyList<ProfileState>? Profiles = null,
    IReadOnlyList<RuleState>? Rules = null,
    string? PublicationId = null,
    string? Page = null);

public record PublicationState(
    string Id,
    string Name);
