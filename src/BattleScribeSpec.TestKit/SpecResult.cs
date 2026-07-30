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

    /// <summary>
    /// Set when the runner catches an unexpected exception from the engine/harness
    /// (as opposed to a genuine assertion failure). Format: "{ExceptionType}: {Message}".
    /// Additive/back-compatible: <see cref="Failures"/> and <see cref="Passed"/> are unchanged
    /// when this is set, so existing consumers keep working; new consumers (e.g. muster) can
    /// use this to distinguish a harness crash from a real spec failure.
    /// </summary>
    public string? HarnessError { get; init; }
}
