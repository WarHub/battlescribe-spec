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
    /// Every selection node the step minted, grouped by the catalogue entry each came from and
    /// listed in roster order. Populated when addForce/selectEntry/selectChildEntry create
    /// children via defaults.
    /// <para>
    /// A list per key, not a node per key (#428). One step routinely mints two selections of one
    /// entry — a <c>min: 2</c> auto-add is the everyday case — and a
    /// <c>Dictionary&lt;string, string&gt;</c> silently kept whichever the collector visited last,
    /// so the other node existed in the roster and could not be named by anything. That is the
    /// entry-addressed proxy #419 removed from <c>on:</c>, one level down.
    /// </para>
    /// <para>
    /// Order is roster order — the order the engine reports the nodes in — so index 0 is the first
    /// node of that entry and <c>${{ steps.X.selections.se-a }}</c> means it. See
    /// <see cref="ExpressionResolver"/> for the <c>[n]</c> sibling syntax.
    /// </para>
    /// </summary>
    [JsonPropertyName("selections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Selections { get; set; }

    /// <summary>
    /// Every category node the created force owns, grouped by the category entry each came from
    /// and listed in force order (returned by addForce, addChildForce, duplicateForce).
    /// <para>
    /// A map rather than a scalar because nothing creates a category: a force mints all of its own
    /// at once, from its force entry's category links, so there is no action to hang a
    /// <c>categoryId</c> output on. Keyed by catalogue entry id, which is what a spec can write
    /// down — <c>${{ steps.add-patrol.categories.cat-troops }}</c> — where the node id it resolves
    /// to is minted per run.
    /// </para>
    /// <para>
    /// A list per key for the same reason <see cref="Selections"/> carries one: a force entry that
    /// links one category entry twice owns two category nodes, and the old shape kept one of them
    /// and dropped the other without saying so. Both maps now answer the same question the same
    /// way, so a spec learns one rule.
    /// </para>
    /// <para>
    /// <c>duplicateForce</c> returns its OWN categories, not the source force's: duplicating a
    /// force mints fresh category nodes (measured on NewRecruit), so returning the source's would
    /// hand a spec ids belonging to a force it did not just create.
    /// </para>
    /// </summary>
    [JsonPropertyName("categories")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Categories { get; set; }
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
    /// (<c>expectedFile</c>) assertions. The root <c>roster</c> element identifies the file type to
    /// the snapshot resolver.
    /// <para>
    /// Engines that cannot export a roster leave this unsupported and the assertion <b>fails</b> for
    /// them — it used to be skipped silently, which passed the step while comparing nothing. Specs opt
    /// an engine out explicitly via <c>skipEngines</c> / <c>engines:</c>, the same rule
    /// <see cref="LoadRoster"/> and <see cref="ReloadRoster"/> follow. So an engine that <em>can</em>
    /// export must implement this member rather than exposing an export some other way: leaving the
    /// default in place is a claim, and the stack now believes it.
    /// </para>
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
    /// <para>
    /// "Re-link against the setup data" is what the contract asks for and not, in every engine, how
    /// the engine gets there: NewRecruit resolves the payload's own <c>gameSystemId</c> through its
    /// library and selects that system before building anything. With one system loaded the two are
    /// the same thing; with a dangling reference they are not, and the difference is a conformance
    /// finding the specs record per engine rather than something an adapter should paper over. See
    /// <c>docs/nr-behavioral-differences.md</c>, "Roster Load".
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
