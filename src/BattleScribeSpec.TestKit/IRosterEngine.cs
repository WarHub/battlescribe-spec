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
    IReadOnlyList<string> Setup(GameSystemSpec gameSystem, CatalogueSpec[] catalogues);

    /// <summary>
    /// Add a force to the roster using a force entry by index.
    /// Index refers to the order in <see cref="GameSystemSpec.ForceEntries"/>.
    /// CatalogueIndex specifies which catalogue's entries to use (default 0).
    /// </summary>
    void AddForce(int forceEntryIndex, int catalogueIndex = 0);

    /// <summary>
    /// Remove a force from the roster by its index.
    /// </summary>
    void RemoveForce(int forceIndex);

    /// <summary>
    /// Select (add) an entry in the specified force, creating a new selection.
    /// Entry index refers to order in <see cref="CatalogueSpec.SelectionEntries"/>.
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
}
