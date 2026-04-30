namespace BattleScribeSpec.GameData;

/// <summary>
/// Runs GameData conformance specs against an <see cref="IGameDataEngine"/> implementation.
/// </summary>
public sealed class GameDataRunner
{
    private readonly IGameDataEngine _engine;
    private readonly string _engineName;

    public GameDataRunner(IGameDataEngine engine, string engineName)
    {
        _engine = engine;
        _engineName = engineName;
    }

    /// <summary>
    /// Run a GameData spec and return the result.
    /// </summary>
    public SpecResult Run(GameDataSpecFile spec)
    {
        // Stub implementation — will be expanded when GameData spec format is finalized.
        throw new NotImplementedException(
            $"GameData spec execution is not yet implemented. Spec: {spec.Id}, Engine: {_engineName}");
    }
}
