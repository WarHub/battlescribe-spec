namespace BattleScribeSpec.Roster;

/// <summary>
/// Engine-agnostic state records for roster conformance testing.
/// These types are used by both the spec runner and engine implementations.
/// They serialize to JSON via ProtocolJsonContext (camelCase naming, null-omission by default).
/// </summary>

/// <remarks>
/// <para>
/// <b>Two attributions, named apart.</b> <see cref="RaisedOnType"/> and <see cref="RaisedOnId"/> are
/// the RUNTIME NODE the engine raised the error on — the element the error was read off, written
/// once at capture and never rewritten afterwards. <see cref="OwnerType"/> and
/// <see cref="OwnerEntryId"/> are the NORMALIZED attribution: where the spec corpus reports the
/// error, which <c>BattleScribeErrorPlacement</c> may move off the raising node onto the selection
/// responsible. A collective over-limit violation is raised by a category and attributed to a
/// selection, so the two disagree by design and neither is a substitute for the other.
/// </para>
/// <para>
/// The distinction used to live in one field: <c>OwnerId</c> held the raising node's id while
/// <c>OwnerType</c> held the post-placement type, and placement nulled the id whenever it moved an
/// error rather than let the record name one node by type and a different one by id. Nulling threw
/// away the only thing that identifies a node — <see cref="OwnerEntryId"/> is a CATALOGUE entry id,
/// and three selections of one entry share it (issue #421).
/// </para>
/// <para>
/// <see cref="RaisedOnId"/> is a runtime node id and is never link-reduced: the
/// <c>ReduceToTargetEntry</c> rule (#400) applies to entry ids, and applying it here would corrupt
/// an id that has no link-composite form.
/// </para>
/// <para>
/// <see cref="ConstraintType"/> ("min"/"max") and <see cref="ConstraintField"/>
/// ("selections"/"forces"/a cost-type id) are read from the live constraint at capture and let
/// <c>BattleScribeErrorPlacement</c> decide where an error belongs from structural facts instead of
/// the message prose. Both are null for the id-less paths (roster cost-limit bypass) and the
/// reserved pseudo-constraints ("hidden"/"collective").
/// </para>
/// </remarks>
public record ValidationErrorState(
    string Message,
    string? OwnerType = null,
    string? OwnerEntryId = null,
    string? EntryId = null,
    string? ConstraintId = null,
    string? ConstraintType = null,
    string? ConstraintField = null,
    string? RaisedOnType = null,
    string? RaisedOnId = null);

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
    decimal Value,
    bool Hidden = false);

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

/// <remarks>
/// <para>
/// <b>One record, two different things.</b> A FORCE's categories
/// (<c>force.getCategories()</c>) are roster NODES: the engine mints one per category link when it
/// creates the force, gives it a runtime identity, and raises collective over-limit violations on
/// it. A SELECTION's categories (<c>selection.getSelectionCategories()</c>) are the category TAGS
/// that selection counts against — what it is, not where it lives. Both map here, so
/// <see cref="Id"/> is the node's id for the first and is often absent for the second.
/// </para>
/// <para>
/// <see cref="Id"/> is a RUNTIME node id, not <see cref="EntryId"/>: a force with two links to one
/// category entry has two nodes sharing an entry id, and an error is raised on one of them. On
/// BattleScribe it is <c>Category.getId()</c> for both kinds. On NewRecruit a force category is a
/// full instance node and its identity is <c>uid</c> — <c>id</c>/<c>getId()</c> there return the
/// CATALOGUE entry id, which looks plausible and is wrong — while a selection category is a plain
/// object literal with no node identity at all, so <see cref="Id"/> is null for it. That asymmetry
/// is the engines', not ours; it is reported rather than papered over, and no spec assertion reads
/// this field, so it cannot fail a spec on the engine that has less to say (issue #420).
/// </para>
/// </remarks>
public record CategoryState(
    string? Id,
    string Name,
    string? EntryId,
    bool Primary,
    IReadOnlyList<ProfileState>? Profiles = null,
    IReadOnlyList<RuleState>? Rules = null,
    string? PublicationId = null,
    string? Page = null,
    string? CustomName = null,
    string? CustomNotes = null);

public record PublicationState(
    string Id,
    string Name);
