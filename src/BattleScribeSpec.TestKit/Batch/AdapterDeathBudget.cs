namespace BattleScribeSpec.Batch;

/// <summary>
/// Tracks adapter-process deaths across one entire <see cref="SpecSuiteRunner.RunAsync"/> call,
/// shared by every worker (sequential runs have exactly one worker; parallel runs share this one
/// instance across all of them via a closure) so the recovery cap in
/// <see cref="SpecSuiteOptions.MaxAdapterDeaths"/> is enforced per RUN, not per worker — a
/// deterministically-crashing engine binary is a property of the engine, not of which worker slot
/// happened to draw it, so the budget must not let N workers each independently retry the same
/// systemic failure N times over.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="Increment"/> uses <see cref="Interlocked.Increment(ref int)"/> so
/// concurrent parallel workers cannot race past the cap.
/// </remarks>
internal sealed class AdapterDeathBudget(int maxDeaths)
{
    private int _count;

    /// <summary>The configured cap (see <see cref="SpecSuiteOptions.MaxAdapterDeaths"/>).</summary>
    public int MaxDeaths { get; } = maxDeaths;

    /// <summary>Records one more death and returns the new running total.</summary>
    public int Increment() => Interlocked.Increment(ref _count);

    /// <summary>Deaths recorded so far.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>True once more deaths have occurred than <see cref="MaxDeaths"/> tolerates.</summary>
    public bool IsExceeded => Count > MaxDeaths;
}
