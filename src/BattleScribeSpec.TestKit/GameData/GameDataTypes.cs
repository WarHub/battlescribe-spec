namespace BattleScribeSpec.GameData;

/// <summary>
/// Engine-agnostic state records for GameData conformance testing.
/// Parallel to <see cref="Roster.RosterState"/> etc. for roster testing.
/// State is returned by IGameDataEngine.GetState() and asserted against in specs.
/// </summary>

/// <summary>
/// Root state snapshot of all data files being edited.
/// </summary>
public record GameDataState
{
    /// <summary>
    /// Game system being edited (null if only editing a standalone catalogue).
    /// </summary>
    public GameSystemDataState? GameSystem { get; init; }

    /// <summary>
    /// Catalogues being edited.
    /// </summary>
    public IReadOnlyList<CatalogueDataState> Catalogues { get; init; } = [];
}

/// <summary>
/// State of a game system in the data editor.
/// Explicit containers preserve the semantic grouping of entries.
/// </summary>
public record GameSystemDataState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>
    /// Root-level metadata fields (revision, authorName, authorContact, authorUrl, readme).
    /// Only non-default values need to be included.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Fields { get; init; }
    public IReadOnlyList<DataEntryState> ForceEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> CategoryEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> CostTypes { get; init; } = [];
    public IReadOnlyList<DataEntryState> ProfileTypes { get; init; } = [];
    public IReadOnlyList<DataEntryState> Publications { get; init; } = [];
    public IReadOnlyList<DataEntryState> SelectionEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> EntryLinks { get; init; } = [];
    public IReadOnlyList<DataEntryState> Rules { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedSelectionEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedSelectionEntryGroups { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedRules { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedProfiles { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedInfoGroups { get; init; } = [];

    // NewRecruit additions.
    public IReadOnlyList<DataEntryState> SharedForceEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedAssociations { get; init; } = [];
}

/// <summary>
/// State of a catalogue in the data editor.
/// Explicit containers preserve the semantic grouping of entries.
/// </summary>
public record CatalogueDataState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string GameSystemId { get; init; } = "";

    /// <summary>
    /// Root-level metadata fields (revision, authorName, authorContact, authorUrl, readme,
    /// library, gameSystemRevision).
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Fields { get; init; }
    public IReadOnlyList<DataEntryState> SelectionEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> EntryLinks { get; init; } = [];
    public IReadOnlyList<DataEntryState> Rules { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedSelectionEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedSelectionEntryGroups { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedRules { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedProfiles { get; init; } = [];
    public IReadOnlyList<DataEntryState> ForceEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> CategoryEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> Publications { get; init; } = [];
    public IReadOnlyList<DataEntryState> CostTypes { get; init; } = [];
    public IReadOnlyList<DataEntryState> ProfileTypes { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedInfoGroups { get; init; } = [];
    public IReadOnlyList<DataEntryState> CatalogueLinks { get; init; } = [];

    // NewRecruit additions.
    public IReadOnlyList<DataEntryState> SharedForceEntries { get; init; } = [];
    public IReadOnlyList<DataEntryState> SharedAssociations { get; init; } = [];
}

/// <summary>
/// State of a single entry node in the data tree.
/// Represents any entry type (selectionEntry, profile, rule, constraint, etc.)
/// with type-specific fields stored in the Fields dictionary.
/// </summary>
public record DataEntryState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>
    /// The BattleScribe entry type key (selectionEntry, selectionEntryGroup, profile, rule,
    /// constraint, modifier, entryLink, infoLink, categoryLink, forceEntry, etc.)
    /// </summary>
    public string EntryType { get; init; } = "";

    public bool Hidden { get; init; }

    /// <summary>
    /// Child entries nested inside this entry.
    /// </summary>
    public IReadOnlyList<DataEntryState> Children { get; init; } = [];

    /// <summary>
    /// Type-specific field values. Keys are field names (e.g., "type", "value", "scope",
    /// "targetId", "field"). Values are string representations.
    /// Only non-default fields need to be included.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Fields { get; init; }
}
