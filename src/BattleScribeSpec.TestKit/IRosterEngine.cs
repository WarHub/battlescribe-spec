using BattleScribeSpec.Protocol;

namespace BattleScribeSpec;

/// <summary>
/// Abstraction for a BattleScribe-compatible roster editing engine.
/// Any engine conforming to BattleScribe v2.03 behavior should implement this interface.
/// The spec runner executes declarative YAML tests against implementations of this interface.
/// <para>
/// All force-targeting methods use <c>int[] forcePath</c> to address forces at any depth.
/// For <c>AddForce</c>, forcePath identifies the parent (empty = top-level).
/// For all other actions, forcePath identifies the target force.
/// </para>
/// <para>
/// Selection-targeting methods additionally use <c>int[] selectionPath</c> to address
/// selections at any depth within the target force.
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
    /// Add a force to the roster.
    /// <paramref name="forcePath"/> identifies the parent force: empty array adds a top-level force,
    /// <c>[0]</c> adds a child under force 0, <c>[0, 1]</c> adds under force 0's child 1, etc.
    /// <paramref name="forceEntryIndex"/> indexes force entries at the target level.
    /// </summary>
    void AddForce(int[] forcePath, int forceEntryIndex, int catalogueIndex = 0);

    /// <summary>
    /// Remove a force from the roster.
    /// <paramref name="forcePath"/> identifies the target force to remove:
    /// <c>[0]</c> removes top-level force 0, <c>[0, 1]</c> removes child 1 of force 0, etc.
    /// </summary>
    void RemoveForce(int[] forcePath);

    /// <summary>
    /// Select (add) an entry in the specified force, creating a new selection.
    /// <paramref name="forcePath"/> identifies the target force.
    /// </summary>
    void SelectEntry(int[] forcePath, int entryIndex);

    /// <summary>
    /// Select a child entry under an existing selection.
    /// <paramref name="forcePath"/> identifies the target force.
    /// <paramref name="selectionPath"/> identifies the parent selection within the force.
    /// </summary>
    void SelectChildEntry(int[] forcePath, int[] selectionPath, int childEntryIndex);

    /// <summary>
    /// Deselect (remove) a selection.
    /// <paramref name="forcePath"/> identifies the target force.
    /// <paramref name="selectionPath"/> identifies the selection to remove.
    /// </summary>
    void DeselectSelection(int[] forcePath, int[] selectionPath);

    /// <summary>
    /// Set the number of instances for a selection entry.
    /// <paramref name="forcePath"/> identifies the target force.
    /// </summary>
    void SetSelectionCount(int[] forcePath, int entryIndex, int count);

    /// <summary>
    /// Duplicate a selection within a force.
    /// <paramref name="forcePath"/> identifies the target force.
    /// <paramref name="selectionPath"/> identifies the selection to duplicate.
    /// </summary>
    void DuplicateSelection(int[] forcePath, int[] selectionPath);

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
    /// Provide the spec ID for the upcoming test run.
    /// Called by <see cref="SpecRunner"/> before <see cref="Setup"/> or <see cref="SetupFromFiles"/>.
    /// Engines may use this to label the roster (e.g. for non-headless debugging).
    /// </summary>
    void SetTestContext(string specId) { }

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
    /// <paramref name="forcePath"/> identifies the parent force (empty = top-level).
    /// </summary>
    void AddForceByName(int[] forcePath, string forceName, string? catalogueName = null, int catalogueIndex = 0)
        => throw new NotSupportedException("This engine does not support name-based force addition.");

    /// <summary>
    /// Select an entry by name within the specified force.
    /// <paramref name="forcePath"/> identifies the target force.
    /// </summary>
    void SelectEntryByName(int[] forcePath, string entryName)
        => throw new NotSupportedException("This engine does not support name-based entry selection.");

    /// <summary>
    /// Select a child entry by name under an existing selection.
    /// <paramref name="forcePath"/> identifies the target force.
    /// <paramref name="selectionPath"/> identifies the parent selection.
    /// </summary>
    void SelectChildEntryByName(int[] forcePath, int[] selectionPath, string childEntryName)
        => throw new NotSupportedException("This engine does not support name-based child entry selection.");
}
