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

    /// <summary>
    /// Select the active file (catalogue or game system) for subsequent UI-driven edits.
    /// Specs with more than one catalogue declare which one is "open" before editing it.
    /// Engines that read all files at once (or edit a single artifact) may treat this as a no-op.
    /// </summary>
    void OpenFile(string id) { }

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

    // ===== Property mutations =====

    /// <summary>
    /// Set a field value on an existing entry.
    /// Field names correspond to BattleScribe data model properties (name, hidden, type, etc.).
    /// </summary>
    /// <param name="entryId">ID of the entry to modify.</param>
    /// <param name="field">Field name to set.</param>
    /// <param name="value">New value (null to clear).</param>
    void SetField(string entryId, string field, string? value);

    /// <summary>
    /// Set a cost value on an entry, keyed by cost type. Creates the cost if absent.
    /// </summary>
    /// <param name="entryId">ID of the entry (selection entry, group, link…) to modify.</param>
    /// <param name="costTypeId">ID of the cost type the value applies to.</param>
    /// <param name="value">New numeric cost value (as a string).</param>
    void SetCost(string entryId, string costTypeId, string? value) =>
        throw new NotSupportedException("SetCost is not supported by this engine.");

    /// <summary>
    /// Set a characteristic value on a profile entry, keyed by characteristic name or type ID.
    /// Creates the characteristic if absent.
    /// </summary>
    /// <param name="entryId">ID of the profile entry to modify.</param>
    /// <param name="nameOrTypeId">Characteristic name or characteristic-type ID.</param>
    /// <param name="value">New characteristic value.</param>
    void SetCharacteristic(string entryId, string nameOrTypeId, string? value) =>
        throw new NotSupportedException("SetCharacteristic is not supported by this engine.");

    // ===== Link management =====

    /// <summary>
    /// Add a link entry pointing to a shared/target entry.
    /// </summary>
    /// <param name="parentId">ID of the parent to add the link into.</param>
    /// <param name="linkType">Type of link (entryLink, infoLink, categoryLink).</param>
    /// <param name="targetId">ID of the target entry the link points to.</param>
    /// <returns>Outputs containing the ID of the created link.</returns>
    GameDataActionOutputs AddLink(string parentId, string linkType, string targetId);

    // ===== Persistence =====

    /// <summary>
    /// Serialize the current edited state to its on-disk form (.cat/.gst) and reload it,
    /// replacing the in-memory model with what a fresh load of the saved files produces.
    /// Used by round-trip specs: a repeated <c>expectedState</c> placed after a reload verifies
    /// that save + reload preserved semantics. Engines that cannot persist throw.
    /// </summary>
    void Reload() =>
        throw new NotSupportedException("Reload is not supported by this engine.");

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
