using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.GameData;

/// <summary>
/// Structured outputs from mutating GameData actions.
/// Contains IDs of created elements so that later steps can reference them.
/// </summary>
public sealed class GameDataActionOutputs
{
    /// <summary>
    /// ID of the created entry (returned by addEntry, addLink).
    /// </summary>
    public string? EntryId { get; set; }
}

/// <summary>
/// Abstraction for a BattleScribe-compatible data editing engine.
/// Implementations allow editing game system and catalogue data files
/// (entries, profiles, rules, modifiers, constraints, etc.)
/// <para>
/// Parallel to <see cref="Roster.IRosterEngine"/> but for data editing.
/// Roster specs test how engines handle roster creation from fixed data;
/// GameData specs test how engines handle data file mutations.
/// </para>
/// </summary>
public interface IGameDataEngine : IDisposable
{
    /// <summary>
    /// Optional per-test context (e.g., for logging or debug identification).
    /// </summary>
    void SetTestContext(string specId) { }

    /// <summary>
    /// Initialize the engine with game system and catalogue data as the initial state.
    /// This data IS the editable artifact (unlike roster specs where it's fixed input).
    /// Returns initialization errors (empty list = success).
    /// </summary>
    IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues);

    /// <summary>
    /// Clean up any resources used by the engine instance.
    /// </summary>
    void Cleanup() { }

    // ===== Structural mutations =====

    /// <summary>
    /// Add a new entry to a parent container in the data tree.
    /// The parentId references a catalogue, game system, or existing entry.
    /// </summary>
    /// <param name="parentId">ID of the parent (catalogue/entry) to add into.</param>
    /// <param name="entryType">Type of entry to create (selectionEntry, selectionEntryGroup, profile, rule, etc.).</param>
    /// <param name="name">Optional name for the new entry.</param>
    /// <returns>Outputs containing the ID of the created entry.</returns>
    GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null);

    /// <summary>
    /// Remove an entry from the data tree by its ID.
    /// </summary>
    void RemoveEntry(string entryId);

    /// <summary>
    /// Move an entry to a new parent in the data tree.
    /// </summary>
    /// <param name="entryId">ID of the entry to move.</param>
    /// <param name="newParentId">ID of the new parent to move into.</param>
    /// <param name="index">Optional position index within the new parent.</param>
    void MoveEntry(string entryId, string newParentId, int? index = null);

    // ===== Property mutations =====

    /// <summary>
    /// Set a field value on an existing entry.
    /// Field names correspond to BattleScribe data model properties (name, hidden, type, etc.).
    /// </summary>
    /// <param name="entryId">ID of the entry to modify.</param>
    /// <param name="field">Field name to set.</param>
    /// <param name="value">New value (null to clear).</param>
    void SetField(string entryId, string field, string? value);

    // ===== Link management =====

    /// <summary>
    /// Add a link entry pointing to a shared/target entry.
    /// </summary>
    /// <param name="parentId">ID of the parent to add the link into.</param>
    /// <param name="linkType">Type of link (entryLink, infoLink, categoryLink).</param>
    /// <param name="targetId">ID of the target entry the link points to.</param>
    /// <returns>Outputs containing the ID of the created link.</returns>
    GameDataActionOutputs AddLink(string parentId, string linkType, string targetId);

    // ===== State queries =====

    /// <summary>
    /// Get the current state of the data being edited as an immutable snapshot.
    /// </summary>
    GameDataState GetState();

    /// <summary>
    /// Get all current validation errors.
    /// </summary>
    IReadOnlyList<Roster.ValidationErrorState> GetValidationErrors() => [];
}
