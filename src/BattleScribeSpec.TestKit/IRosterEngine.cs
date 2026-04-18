using BattleScribeSpec.Protocol;

namespace BattleScribeSpec;

/// <summary>
/// Abstraction for a BattleScribe-compatible roster editing engine.
/// Any engine conforming to BattleScribe v2.03 behavior should implement this interface.
/// The spec runner executes declarative YAML tests against implementations of this interface.
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
    /// Add a force to the roster using a force entry by index.
    /// Index refers to the order in <see cref="ProtocolGameSystem.ForceEntries"/>.
    /// CatalogueIndex specifies which catalogue's entries to use (default 0).
    /// </summary>
    void AddForce(int forceEntryIndex, int catalogueIndex = 0);

    /// <summary>
    /// Remove a force from the roster by its index.
    /// </summary>
    void RemoveForce(int forceIndex);

    /// <summary>
    /// Select (add) an entry in the specified force, creating a new selection.
    /// Entry index refers to order in <see cref="ProtocolCatalogue.SelectionEntries"/>.
    /// </summary>
    void SelectEntry(int forceIndex, int entryIndex);

    /// <summary>
    /// Select a child entry under an existing selection.
    /// </summary>
    void SelectChildEntry(int forceIndex, int selectionIndex, int childEntryIndex);

    /// <summary>
    /// Deselect (remove) a selection by its index within the force.
    /// </summary>
    void DeselectSelection(int forceIndex, int selectionIndex);

    /// <summary>
    /// Set the number of instances for a selection entry.
    /// </summary>
    void SetSelectionCount(int forceIndex, int entryIndex, int count);

    /// <summary>
    /// Duplicate a selection within a force.
    /// </summary>
    void DuplicateSelection(int forceIndex, int selectionIndex);

    /// <summary>
    /// Set cost limit for a cost type by its ID.
    /// </summary>
    void SetCostLimit(string costTypeId, double value);

    /// <summary>
    /// Get the current roster state as an immutable snapshot.
    /// </summary>
    RosterState GetRosterState();

    /// <summary>
    /// Get all current validation errors with structured entry links.
    /// </summary>
    IReadOnlyList<ValidationErrorState> GetValidationErrors();

    // ===== Lifecycle =====

    /// <summary>
    /// Clean up engine state after a spec run completes.
    /// Called by the SpecRunner in a finally block after each spec, including
    /// when <see cref="Setup(ProtocolGameSystem, ProtocolCatalogue[])"/> fails,
    /// throws, or the spec aborts before setup fully completes.
    /// Implementations must therefore be safe to call when the engine is only
    /// partially initialized and should be written to be idempotent/best-effort.
    /// Engines that maintain state across Setup() calls (e.g. browser-based)
    /// should override this to release resources like rosters.
    /// Cleanup should not throw; implementations should swallow or internally
    /// handle cleanup failures where possible.
    /// </summary>
    void Cleanup() { }

    // ===== DataSource support (file-based setup + name-based actions) =====

    /// <summary>
    /// Configure the engine with raw BattleScribe XML files (e.g. from a DataSource).
    /// Returns initialization errors (empty list = success).
    /// </summary>
    IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
        => throw new NotSupportedException("This engine does not support file-based setup.");

    /// <summary>
    /// Add a force by name (for DataSource specs where index-based resolution isn't available).
    /// catalogueName identifies which faction/catalogue to use.
    /// </summary>
    void AddForceByName(string forceName, string? catalogueName = null, int catalogueIndex = 0)
        => throw new NotSupportedException("This engine does not support name-based force addition.");

    /// <summary>
    /// Select an entry by name within the specified force.
    /// </summary>
    void SelectEntryByName(int forceIndex, string entryName)
        => throw new NotSupportedException("This engine does not support name-based entry selection.");

    /// <summary>
    /// Select a child entry by name under an existing selection.
    /// </summary>
    void SelectChildEntryByName(int forceIndex, int selectionIndex, string childEntryName)
        => throw new NotSupportedException("This engine does not support name-based child entry selection.");
}
