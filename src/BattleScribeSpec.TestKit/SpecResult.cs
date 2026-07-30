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

    /// <summary>
    /// One entry per step the spec explicitly opted this engine out of (step-level
    /// <c>skipEngines</c>). A pass with a non-empty list verified strictly less than a pass with an
    /// empty one, and nothing else in the result distinguishes them — so the harness reports the
    /// count rather than letting a spec that skipped half its assertions read as a clean run. Empty
    /// for <see cref="GameData.GameDataRunner"/> results, which have no step-level skip (gamedata
    /// opts out per spec, via <c>engines: {…: skip}</c>).
    /// <para>
    /// Advisory only: <see cref="Passed"/> and <see cref="Failures"/> are unchanged, since a skip a
    /// spec asked for is not a failure. Capability gaps the spec did <em>not</em> declare are
    /// failures and appear in <see cref="Failures"/> — never here.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SkippedSteps { get; init; } = [];
}
