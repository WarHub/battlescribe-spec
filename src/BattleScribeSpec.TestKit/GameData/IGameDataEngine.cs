namespace BattleScribeSpec.GameData;

/// <summary>
/// Abstraction for a BattleScribe-compatible data editing engine.
/// Implementations allow editing game system and catalogue data files
/// (entries, profiles, rules, modifiers, constraints, etc.)
/// </summary>
public interface IGameDataEngine : IDisposable
{
    /// <summary>
    /// Optional per-test context (e.g., for logging or debug identification).
    /// </summary>
    void SetTestContext(string specId) { }

    /// <summary>
    /// Initialize the engine with game system and catalogue data.
    /// Returns initialization errors (empty list = success).
    /// </summary>
    IReadOnlyList<string> Initialize(GameDataSetupResult setup);

    /// <summary>
    /// Get the current state of the data being edited.
    /// </summary>
    GameDataState GetState();

    /// <summary>
    /// Clean up any resources used by the engine instance.
    /// </summary>
    void Cleanup() { }
}

/// <summary>
/// Result of setting up game data for editing, containing resolved identifiers.
/// </summary>
public record GameDataSetupResult(string GameSystemId, IReadOnlyList<string> CatalogueIds);
