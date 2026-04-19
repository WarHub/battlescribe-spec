using System.Text.Json.Serialization;

namespace BattleScribeSpec;

/// <summary>
/// Engine-agnostic state records for roster conformance testing.
/// These types are used by both the spec runner and engine implementations.
/// They serialize directly to the JSON wire format via System.Text.Json attributes.
/// </summary>

public record ValidationErrorState(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("ownerType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerType = null,
    [property: JsonPropertyName("ownerId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerId = null,
    [property: JsonPropertyName("ownerEntryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerEntryId = null,
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntryId = null,
    [property: JsonPropertyName("constraintId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConstraintId = null);

public record RosterState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("gameSystemId")] string GameSystemId,
    [property: JsonPropertyName("forces")] IReadOnlyList<ForceState> Forces,
    [property: JsonPropertyName("costs")] IReadOnlyList<CostState> Costs,
    [property: JsonPropertyName("validationErrors")] IReadOnlyList<ValidationErrorState> ValidationErrors);

public record ForceState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("catalogueId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CatalogueId,
    [property: JsonPropertyName("selections")] IReadOnlyList<SelectionState> Selections,
    [property: JsonPropertyName("availableEntryCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AvailableEntryCount = null,
    IReadOnlyList<ForceState>? ChildForces = null,
    IReadOnlyList<ProfileState>? Profiles = null,
    IReadOnlyList<RuleState>? Rules = null,
    [property: JsonPropertyName("publicationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicationId = null,
    [property: JsonPropertyName("page"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Page = null)
{
    [JsonPropertyName("childForces"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ForceState> ChildForces { get; init; } = ChildForces ?? [];
    [JsonPropertyName("profiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProfileState> Profiles { get; init; } = Profiles ?? [];
    [JsonPropertyName("rules"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RuleState> Rules { get; init; } = Rules ?? [];
}

public record SelectionState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntryId,
    [property: JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("costs")] IReadOnlyList<CostState> Costs,
    [property: JsonPropertyName("children")] IReadOnlyList<SelectionState> Children,
    IReadOnlyList<ProfileState>? Profiles = null,
    IReadOnlyList<RuleState>? Rules = null,
    IReadOnlyList<CategoryState>? Categories = null,
    [property: JsonPropertyName("page"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Page = null,
    [property: JsonPropertyName("publicationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicationId = null,
    [property: JsonPropertyName("publicationName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicationName = null)
{
    [JsonPropertyName("profiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProfileState> Profiles { get; init; } = Profiles ?? [];
    [JsonPropertyName("rules"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RuleState> Rules { get; init; } = Rules ?? [];
    [JsonPropertyName("categories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CategoryState> Categories { get; init; } = Categories ?? [];
}

public record CostState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("typeId")] string TypeId,
    [property: JsonPropertyName("value")] double Value);

public record ProfileState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("typeId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TypeId,
    [property: JsonPropertyName("typeName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TypeName,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("characteristics")] IReadOnlyList<CharacteristicState> Characteristics,
    [property: JsonPropertyName("page"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Page = null,
    [property: JsonPropertyName("publicationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicationId = null);

public record CharacteristicState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("typeId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TypeId,
    [property: JsonPropertyName("value")] string Value);

public record RuleState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("page"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Page = null,
    [property: JsonPropertyName("publicationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicationId = null);

public record CategoryState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntryId,
    [property: JsonPropertyName("primary")] bool Primary,
    IReadOnlyList<ProfileState>? Profiles = null,
    IReadOnlyList<RuleState>? Rules = null,
    [property: JsonPropertyName("publicationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicationId = null,
    [property: JsonPropertyName("page"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Page = null)
{
    [JsonPropertyName("profiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProfileState> Profiles { get; init; } = Profiles ?? [];
    [JsonPropertyName("rules"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RuleState> Rules { get; init; } = Rules ?? [];
}
