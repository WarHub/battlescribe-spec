namespace BattleScribeSpec.Concurrency;

/// <summary>
/// One decision, for one engine on one machine. Every concurrency and reuse knob in the harness
/// reads from this — the CLI's worker count, the in-process pools' size, xUnit's collection
/// parallelism, and whether engines are reused across setups.
/// </summary>
/// <remarks>
/// One policy governing everything is a single point of failure, deliberately. Today a bad
/// NR_PARALLEL degrades one lane and a bad --workers default degrades another, independently and
/// inconsistently. One place to be wrong is one place to measure, fix and tune.
/// </remarks>
/// <param name="Workers">How many instances of the engine may run concurrently.</param>
/// <param name="PoolSize">Size of the in-process reuse pool. Currently mirrors <see cref="Workers"/>.</param>
/// <param name="MaxParallelThreads">Degree of parallelism to hand to the test runner (e.g. xUnit collections).</param>
/// <param name="ReuseRoster">Whether the roster engine may be reused across setups instead of cold-started each time.</param>
/// <param name="ReuseGameData">Whether the gamedata engine may be reused across setups instead of cold-started each time.</param>
public sealed record ConcurrencyPlan(
    int Workers,
    int PoolSize,
    int MaxParallelThreads,
    bool ReuseRoster,
    bool ReuseGameData);
