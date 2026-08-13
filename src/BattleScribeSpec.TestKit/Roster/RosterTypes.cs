namespace BattleScribeSpec.Roster;

/// <summary>
/// Engine-agnostic state records for roster conformance testing.
/// These types are used by both the spec runner and engine implementations.
/// They serialize to JSON via ProtocolJsonContext (camelCase naming, null-omission by default).
/// </summary>

/// <remarks>
/// <para>
/// <b>One attribution, the engine's own.</b> <see cref="RaisedOnType"/> and <see cref="RaisedOnId"/>
/// are the RUNTIME NODE the engine raised the error on — the element the error was read off, written
/// once at capture and never rewritten. <see cref="RaisedOnEntryId"/> is that same node's CATALOGUE
/// entry id, which is a different fact: three selections of one entry share it, so it names a set
/// and <see cref="RaisedOnId"/> names a member. It is kept because no other field carries it and it
/// is what makes a failure line readable — a bare runtime GUID says nothing about what was selected.
/// </para>
/// <para>
/// The record used to carry a second, NORMALIZED attribution (<c>OwnerType</c>/<c>OwnerEntryId</c>)
/// that a shared placement pass moved off the raising node onto "the selection responsible". Measured
/// across the corpus, that pass rewrote BattleScribe's answer into NewRecruit's on 24 of the 38
/// assertions both lanes evaluate; the divergence it hid is now recorded per engine by the specs
/// themselves (#426). <c>OwnerType</c> was, on every honest path, the same value as
/// <see cref="RaisedOnType"/>.
/// </para>
/// <para>
/// <see cref="RaisedOnEntryId"/> is shipped as the engine reports it, link route and all: a node
/// reached through an entry link reports <c>linkId::…::targetId</c>, and reducing that to the target
/// was a normalization for a matcher that no longer reads the field.
/// </para>
/// <para>
/// <see cref="ConstraintType"/> ("min"/"max") and <see cref="ConstraintField"/>
/// ("selections"/"forces"/a cost-type id) are read from the live constraint at capture, so a
/// consumer can tell an over-limit violation from an unmet minimum without parsing message prose.
/// Both are null for the id-less paths (roster cost-limit bypass) and the reserved pseudo-constraints
/// ("hidden"/"collective").
/// </para>
/// </remarks>
public record ValidationErrorState(
    string Message,
    string? EntryId = null,
    string? ConstraintId = null,
    string? ConstraintType = null,
    string? ConstraintField = null,
    string? RaisedOnType = null,
    string? RaisedOnId = null,
    string? RaisedOnEntryId = null);

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
