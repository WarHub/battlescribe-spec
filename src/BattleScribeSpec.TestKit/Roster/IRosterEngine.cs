using System.Text.Json.Serialization;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Roster;

/// <summary>
/// Structured outputs from mutating roster actions.
/// Contains IDs of created elements so that later steps can reference them.
/// </summary>
public sealed class ActionOutputs
{
    /// <summary>
    /// ID of the created force (returned by addForce, addChildForce, duplicateForce).
    /// </summary>
    [JsonPropertyName("forceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForceId { get; set; }

    /// <summary>
    /// ID of the primary created selection (returned by selectEntry, selectChildEntry, duplicateSelection).
    /// </summary>
    [JsonPropertyName("selectionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionId { get; set; }

    /// <summary>
    /// Map of entryId → selectionId for auto-selected child selections.
    /// Populated when selectEntry/selectChildEntry creates children via defaults.
    /// </summary>
    [JsonPropertyName("selections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Selections { get; set; }
}

/// <summary>
/// Abstraction for a BattleScribe-compatible roster editing engine.
/// Any engine conforming to BattleScribe v2.03 behavior should implement this interface.
/// The spec runner executes declarative YAML tests against implementations of this interface.
/// <para>
/// All addressing is ID-based. Definition references (forceEntryId, entryId) use
/// BattleScribe data model IDs. Instance references (forceId, selectionId) use
/// IDs returned as outputs from previous mutating actions.
/// </para>
/// </summary>
public interface IRosterEngine : IDisposable
{
    /// <summary>
    /// Configure the engine with game system and catalogue data.
    /// Must be called before any roster operations.
    /// Returns initialization errors (empty list = success).
    /// </summary>
    IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues);

    /// <summary>
    /// Add a top-level force to the roster by force entry ID.
    /// </summary>
    ActionOutputs AddForce(string forceEntryId, string catalogueId);

    /// <summary>
    /// Add a child force under an existing parent force.
    /// </summary>
    ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId);

    /// <summary>
    /// Remove a force from the roster by its instance ID.
    /// </summary>
    void RemoveForce(string forceId);

    /// <summary>
    /// Select (add) an entry in the specified force, creating a new selection.
    /// </summary>
    ActionOutputs SelectEntry(string forceId, string entryId);

    /// <summary>
    /// Select a child entry under an existing selection.
    /// </summary>
    ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId);

    /// <summary>
    /// Deselect (remove) a selection by its instance ID.
    /// </summary>
    void DeselectSelection(string forceId, string selectionId);

    /// <summary>
    /// Set the number of instances for a selection.
    /// </summary>
    void SetSelectionCount(string forceId, string selectionId, int count);

    /// <summary>
    /// Duplicate a selection within a force.
    /// </summary>
    ActionOutputs DuplicateSelection(string forceId, string selectionId);

    /// <summary>
    /// Duplicate a force, creating a deep copy with all selections.
    /// Returns the ID of the newly created force.
    /// </summary>
    ActionOutputs DuplicateForce(string forceId);

    /// <summary>
    /// Set cost limit for a cost type by its ID.
    /// </summary>
    void SetCostLimit(string costTypeId, decimal value);

    /// <summary>
    /// Set custom name and/or notes on a force, selection, or category.
    /// Targeting: if categoryEntryId → category; else if selectionId → selection; else → force.
    /// NR does not support category customization (silently ignored).
    /// </summary>
    void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
        => throw new NotSupportedException("This engine does not support setCustomization.");

    /// <summary>
    /// Get the current roster state as an immutable snapshot.
    /// </summary>
    RosterState GetRosterState();

    /// <summary>
    /// Get all current validation errors with structured entry links.
    /// </summary>
    IReadOnlyList<ValidationErrorState> GetValidationErrors();

    /// <summary>
    /// Serialize the current roster to its BattleScribe <c>.ros</c> XML form for byte-compare
    /// (<c>expectedFile</c>) assertions. Engines that cannot export a roster leave this unsupported,
    /// and the file assertion is skipped for them. The root <c>roster</c> element identifies the file
    /// type to the snapshot resolver.
    /// </summary>
    string ExportRosterXml()
        => throw new NotSupportedException("This engine does not support roster XML export.");

    // ===== Persistence =====

    /// <summary>
    /// Load a roster from its BattleScribe <c>.ros</c> XML form, replacing the engine's current roster
    /// wholesale, and re-link it against the game system and catalogues supplied to <see cref="Setup"/>.
    /// The loaded roster must come back live: subsequent actions and <see cref="GetRosterState"/> operate
    /// on it exactly as they would on a roster the engine built itself. Backs the <c>loadRoster</c> action.
    /// Engines that cannot load a roster throw, and specs opt them out explicitly via
    /// <c>skipEngines</c> / <c>engines:</c> — a spec must never pass by silently skipping the load.
    /// <para>
    /// No id is returned. Unlike <see cref="GameData.IGameDataEngine.LoadFile"/> — where loaded files
    /// coexist and the returned root id is the handle used to address them — a roster is a singleton
    /// that is replaced, never added to; nothing addresses it by id, and <see cref="RosterState"/>
    /// does not even expose one.
    /// </para>
    /// </summary>
    /// <param name="xml">The <c>.ros</c> XML to load. The root element is <c>roster</c>.</param>
    void LoadRoster(string xml)
        => throw new NotSupportedException("This engine does not support roster load.");

    /// <summary>
    /// Serialize the current roster to its on-disk <c>.ros</c> form and load it straight back, replacing
    /// the in-memory roster with what a fresh load of the saved file produces. Mirrors
    /// <see cref="GameData.IGameDataEngine.Reload"/>: round-trip specs place a repeated
    /// <c>expectedState</c> after a reload to assert that save + load preserved semantics.
    /// Engines that cannot persist and reload a roster throw; specs opt them out explicitly.
    /// </summary>
    void ReloadRoster()
        => throw new NotSupportedException("This engine does not support roster reload.");

    // ===== Lifecycle =====

    /// <summary>
    /// Provide the spec ID for the upcoming test run.
    /// Called by <see cref="RosterRunner"/> before <see cref="Setup"/> or <see cref="SetupFromFiles"/>.
    /// Engines may use this to label the roster (e.g. for non-headless debugging).
    /// </summary>
    void SetTestContext(string specId) { }

    /// <summary>
    /// Clean up engine state after a spec run completes.
    /// Implementations must be safe to call when partially initialized.
    /// </summary>
    void Cleanup() { }

    // ===== DataSource support =====

    /// <summary>
    /// Configure the engine with raw BattleScribe XML files (e.g. from a DataSource).
    /// Returns initialization errors (empty list = success).
    /// </summary>
    IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
        => throw new NotSupportedException("This engine does not support file-based setup.");
}
