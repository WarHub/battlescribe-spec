using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.GameData;

/// <summary>
/// YAML spec file model for GameData conformance tests.
/// Parallel to <see cref="Roster.SpecFile"/> for roster tests.
/// </summary>
public sealed class GameDataSpecFile : SpecFileBase
{
    /// <summary>
    /// Initial data state setup for the spec.
    /// Uses the same ProtocolGameSystem/ProtocolCatalogue types as roster setup,
    /// since for data editing the setup data IS the editable artifact.
    /// </summary>
    public required GameDataSetupDef Setup { get; set; }

    /// <summary>
    /// Ordered list of steps (actions + assertions) to execute.
    /// </summary>
    public required List<GameDataStepDef> Steps { get; set; }
}

/// <summary>
/// Setup definition for a GameData spec — defines the initial data to load.
/// Reuses ProtocolGameSystem/ProtocolCatalogue since that IS the data being edited.
/// </summary>
public sealed class GameDataSetupDef
{
    /// <summary>
    /// Game system definition for the spec. Required.
    /// </summary>
    public ProtocolGameSystem? GameSystem { get; set; }

    /// <summary>
    /// Catalogue definitions for the spec.
    /// </summary>
    public List<ProtocolCatalogue>? Catalogues { get; set; }
}

/// <summary>
/// A single step in a GameData spec — either an action or an assertion.
/// </summary>
public sealed class GameDataStepDef
{
    /// <summary>
    /// Optional step ID for referencing this step's outputs in later steps.
    /// Required only when outputs are referenced via ${{ steps.xxx.entryId }}.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The action to perform (addEntry, removeEntry, setFields, addLink, dump).
    /// </summary>
    public string? Action { get; set; }

    // ===== Action parameters =====

    /// <summary>
    /// Parent entry/catalogue ID for addEntry, addLink.
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>
    /// Entry type to create (selectionEntry, selectionEntryGroup, profile, rule, etc.)
    /// Used by addEntry.
    /// </summary>
    public string? EntryType { get; set; }

    /// <summary>
    /// Name for the entry being created. Used by addEntry.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Target entry ID for removeEntry, setFields.
    /// May contain a ${{ steps.xxx.entryId }} expression.
    /// </summary>
    public string? EntryId { get; set; }

    /// <summary>
    /// Scalar fields to set on the target entry, keyed by field name.
    /// Used by setFields. Applied before <see cref="Costs"/> and <see cref="Characteristics"/>
    /// (so e.g. a profile's typeId is set before its characteristics). Values may contain
    /// ${{ steps.xxx.entryId }} expressions.
    /// </summary>
    public Dictionary<string, string?>? Fields { get; set; }

    /// <summary>
    /// Characteristic values to set on the target entry, keyed by characteristic name or type id.
    /// Used by setFields. Not supported by the NewRecruit engines.
    /// </summary>
    public Dictionary<string, string?>? Characteristics { get; set; }

    /// <summary>
    /// Cost values to set on the target entry, keyed by cost type id.
    /// Used by setFields. Not supported by the NewRecruit engines.
    /// </summary>
    public Dictionary<string, string?>? Costs { get; set; }

    /// <summary>
    /// Link type for addLink (entryLink, infoLink, categoryLink).
    /// </summary>
    public string? LinkType { get; set; }

    /// <summary>
    /// Target ID for addLink — the entry the link will point to.
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>
    /// Expected state after this step (assertions).
    /// </summary>
    public GameDataExpectedStateDef? ExpectedState { get; set; }

    /// <summary>
    /// Assert the exact serialized content of the active file after this step (byte-exact). The
    /// expected content is either inline (<see cref="ExpectedFileDef.Content"/>) or a side-file
    /// resolved next to the spec, keyed by this step's <see cref="Id"/>.
    /// </summary>
    public ExpectedFileDef? ExpectedFile { get; set; }
}

/// <summary>
/// Expected state assertions for a GameData spec step.
/// All fields are optional — only specified fields are checked (partial matching).
/// </summary>
public sealed class GameDataExpectedStateDef
{
    /// <summary>
    /// Assert on specific catalogues by partial match.
    /// </summary>
    public List<ExpectedCatalogueDataDef>? Catalogues { get; set; }

    /// <summary>
    /// Assert on the game system state.
    /// </summary>
    public ExpectedGameSystemDataDef? GameSystem { get; set; }

    /// <summary>
    /// Expected validation errors. When set (even to an empty list), the engine's current
    /// validation errors are asserted: an empty list means "expect no errors"; a non-empty
    /// list means each expected error must be present (partial match). When null, errors are
    /// not checked.
    /// </summary>
    public List<ExpectedErrorDef>? Errors { get; set; }

    /// <summary>
    /// Per-engine overrides (same pattern as roster ExpectedStateDef).
    /// </summary>
    public Dictionary<string, GameDataExpectedStateDef>? Engines { get; set; }

    /// <summary>
    /// Returns effective state for an engine, merging engine override on top of base.
    /// </summary>
    public GameDataExpectedStateDef ForEngine(string? engineName)
    {
        if (engineName is null || Engines is null || !Engines.TryGetValue(engineName, out var over))
        {
            return this;
        }

        return new GameDataExpectedStateDef
        {
            Catalogues = over.Catalogues ?? Catalogues,
            GameSystem = over.GameSystem ?? GameSystem,
            Errors = over.Errors ?? Errors,
        };
    }
}

/// <summary>
/// File-export assertion. The active file is exported and byte-compared to an expected file —
/// either inline <see cref="Content"/>, or a side-file resolved next to the spec by the step's id
/// (with per-engine override files over an NR base). The file type (.cat/.gst) is derived from the
/// exported XML's root element unless <see cref="FileType"/> overrides it.
/// </summary>
public sealed class ExpectedFileDef
{
    /// <summary>Inline expected XML; when set, the side-file resolver is not used for this engine.</summary>
    public string? Content { get; set; }

    /// <summary>Optional file-type override ("catalogue"/"gameSystem"); normally read from the root.</summary>
    public string? FileType { get; set; }

    /// <summary>Per-engine overrides (inline content / file type), same pattern as expectedState.</summary>
    public Dictionary<string, ExpectedFileDef>? Engines { get; set; }

    /// <summary>Returns the effective expectation for an engine, merging its override on top of base.</summary>
    public ExpectedFileDef ForEngine(string? engineName)
    {
        if (engineName is null || Engines is null || !Engines.TryGetValue(engineName, out var over))
        {
            return this;
        }

        return new ExpectedFileDef
        {
            Content = over.Content ?? Content,
            FileType = over.FileType ?? FileType,
        };
    }
}

/// <summary>
/// Expected validation error matcher. <see cref="Message"/> is matched as a
/// case-insensitive substring; the id fields, when set, must match exactly.
/// </summary>
public sealed class ExpectedErrorDef
{
    public string? Message { get; set; }
    public string? EntryId { get; set; }
    public string? ConstraintId { get; set; }
}

/// <summary>
/// Expected state of a catalogue for partial-match assertions.
/// </summary>
public sealed class ExpectedCatalogueDataDef
{
    /// <summary>
    /// Catalogue ID to match against.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Expected name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Expected root metadata fields (revision, library, authorName, etc.).</summary>
    public Dictionary<string, string?>? Fields { get; set; }

    /// <summary>
    /// Expected entries in a specific container. Key is the container name
    /// (selectionEntries, sharedSelectionEntries, rules, etc.).
    /// </summary>
    public List<ExpectedDataEntryDef>? SelectionEntries { get; set; }
    public List<ExpectedDataEntryDef>? SharedSelectionEntries { get; set; }
    public List<ExpectedDataEntryDef>? SharedSelectionEntryGroups { get; set; }
    public List<ExpectedDataEntryDef>? EntryLinks { get; set; }
    public List<ExpectedDataEntryDef>? Rules { get; set; }
    public List<ExpectedDataEntryDef>? SharedRules { get; set; }
    public List<ExpectedDataEntryDef>? SharedProfiles { get; set; }
    public List<ExpectedDataEntryDef>? ForceEntries { get; set; }
    public List<ExpectedDataEntryDef>? CategoryEntries { get; set; }
    public List<ExpectedDataEntryDef>? Publications { get; set; }
    public List<ExpectedDataEntryDef>? SharedInfoGroups { get; set; }
    public List<ExpectedDataEntryDef>? CatalogueLinks { get; set; }

    // NewRecruit additions.
    public List<ExpectedDataEntryDef>? SharedForceEntries { get; set; }
    public List<ExpectedDataEntryDef>? SharedAssociations { get; set; }

    /// <summary>
    /// Assert total entry count across all containers.
    /// </summary>
    public int? EntryCount { get; set; }

    /// <summary>
    /// Count of entries in a specific container.
    /// </summary>
    public int? SelectionEntryCount { get; set; }
    public int? SharedSelectionEntryCount { get; set; }
}

/// <summary>
/// Expected state of the game system for partial-match assertions.
/// </summary>
public sealed class ExpectedGameSystemDataDef
{
    public string? Id { get; set; }
    public string? Name { get; set; }

    /// <summary>Expected root metadata fields (revision, authorName, etc.).</summary>
    public Dictionary<string, string?>? Fields { get; set; }
    public List<ExpectedDataEntryDef>? ForceEntries { get; set; }
    public List<ExpectedDataEntryDef>? CategoryEntries { get; set; }
    public List<ExpectedDataEntryDef>? CostTypes { get; set; }
    public List<ExpectedDataEntryDef>? ProfileTypes { get; set; }
    public List<ExpectedDataEntryDef>? SelectionEntries { get; set; }
    public List<ExpectedDataEntryDef>? SharedSelectionEntries { get; set; }
    public List<ExpectedDataEntryDef>? SharedSelectionEntryGroups { get; set; }
    public List<ExpectedDataEntryDef>? SharedInfoGroups { get; set; }
}

/// <summary>
/// Expected state of a single data entry for partial-match assertions.
/// Only specified fields are checked.
/// </summary>
public sealed class ExpectedDataEntryDef
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? EntryType { get; set; }
    public bool? Hidden { get; set; }

    /// <summary>
    /// Expected children of this entry.
    /// </summary>
    public List<ExpectedDataEntryDef>? Children { get; set; }

    /// <summary>
    /// Expected child count.
    /// </summary>
    public int? ChildCount { get; set; }

    /// <summary>
    /// Expected field values (type-specific properties).
    /// </summary>
    public Dictionary<string, string?>? Fields { get; set; }
}
