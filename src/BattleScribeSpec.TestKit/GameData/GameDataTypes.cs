namespace BattleScribeSpec.GameData;

/// <summary>
/// State snapshot of the game data being edited.
/// </summary>
public record GameDataState
{
    /// <summary>
    /// Game system being edited.
    /// </summary>
    public GameSystemDataState? GameSystem { get; init; }

    /// <summary>
    /// Catalogues being edited.
    /// </summary>
    public IReadOnlyList<CatalogueDataState> Catalogues { get; init; } = [];
}

/// <summary>
/// State of a game system in the data editor.
/// </summary>
public record GameSystemDataState
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}

/// <summary>
/// State of a catalogue in the data editor.
/// </summary>
public record CatalogueDataState
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<EntryDataState> Entries { get; init; } = [];
}

/// <summary>
/// State of a single entry (selection entry, entry group, etc.) in the data editor.
/// </summary>
public record EntryDataState
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
}
