namespace BattleScribeSpec.GameData;

/// <summary>
/// YAML spec file model for GameData conformance tests.
/// </summary>
public sealed class GameDataSpecFile : SpecFileBase
{
    /// <summary>
    /// Initial data state setup for the spec.
    /// </summary>
    public GameDataSetupDef? Setup { get; set; }

    /// <summary>
    /// Ordered list of steps (actions + assertions) to execute.
    /// </summary>
    public List<GameDataStepDef> Steps { get; set; } = [];
}

/// <summary>
/// Setup definition for a GameData spec — defines the initial data to load.
/// </summary>
public sealed class GameDataSetupDef
{
    /// <summary>
    /// Game system definition for the spec.
    /// </summary>
    public object? GameSystem { get; set; }

    /// <summary>
    /// Catalogue definitions for the spec.
    /// </summary>
    public List<object>? Catalogues { get; set; }
}

/// <summary>
/// A single step in a GameData spec — either an action or an assertion.
/// </summary>
public sealed class GameDataStepDef
{
    /// <summary>
    /// The action to perform (e.g., "addEntry", "removeEntry", "editEntry").
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// Expected state after this step (assertions).
    /// </summary>
    public GameDataExpectedStateDef? ExpectedState { get; set; }
}

/// <summary>
/// Expected state assertions for a GameData spec step.
/// </summary>
public sealed class GameDataExpectedStateDef
{
    // Placeholder — will be expanded as GameData spec format is defined.
}
