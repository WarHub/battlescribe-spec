namespace BattleScribeSpec;

/// <summary>
/// Result of running a single spec test.
/// </summary>
public sealed record SpecResult(
    string SpecId,
    string Category,
    string Description,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}
