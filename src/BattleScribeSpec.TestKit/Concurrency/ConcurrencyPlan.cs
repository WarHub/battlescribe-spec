namespace BattleScribeSpec.Concurrency;

/// <summary>
/// One decision, for one engine on one machine: how many instances may run at once, and whether
/// they are reused across setups. Produced by <see cref="ConcurrencyPolicy.For"/>, and the only
/// input the CLI, the engine host and the test fixtures take for those questions.
/// </summary>
/// <remarks>
/// <para>
/// One policy governing everything is a single point of failure, deliberately. Before it, a bad
/// <c>NR_PARALLEL</c> degraded one lane and a bad <c>--workers</c> default degraded another,
/// independently and inconsistently. One place to be wrong is one place to measure, fix and tune.
/// </para>
/// <para>
/// <b>What this plan does NOT govern — xUnit's own thread count.</b> The test runner's
/// <c>maxParallelThreads</c> lives in <c>tests/xunit.runner.json</c> and
/// <c>tests/BattleScribeSpec.Cli.Tests/xunit.runner.json</c>, because xUnit reads that JSON
/// <em>before any of this code runs</em> and cannot call a C# function for it (the same is true of
/// the VSTest RunSettings alternative — see <see cref="ConcurrencyPolicy"/>'s remarks). This record
/// therefore carries no field for it: a field claiming to control a quantity it cannot reach is
/// worse than no field, and the previous <c>MaxParallelThreads</c> member had zero consumers while
/// its doc comment claimed it bounded the test runner. The JSON value stands on its own justification
/// (<c>ConcurrencyConfigurationDriftTests</c>), not on a number borrowed from this plan.
/// </para>
/// <para>
/// <b>What is bounded and what is not (tracked in issue #314):</b> this plan bounds each individual
/// pool's size (<see cref="PoolSize"/>). It does <b>not</b> bound the product across simultaneously
/// live xUnit collection fixtures. A collection fixture lives for the whole collection, not for one
/// thread-slot, so two independent collections (e.g. <c>FrozenNrRosterFixture</c> and
/// <c>FrozenNrGameDataUiFixture</c>) can each be fully alive at once, each holding a pool — total
/// live browser contexts can reach the sum across collections, not any single pool's cap. Nor can
/// xUnit's thread count bound it: the real parallelism inside a conformance test is a
/// <c>Parallel.ForEachAsync(MaxDegreeOfParallelism = pool.Size)</c> <em>within one <c>[Fact]</c></em>,
/// which <c>maxParallelThreads</c> does not constrain at all. <c>FixtureConcurrency.FixturePoolCap</c>
/// is the interim defensive guard on the test path; a shared budget the pools draw from is the real
/// fix, and is #314's business, not this record's.
/// </para>
/// </remarks>
/// <param name="Workers">How many instances of the engine may run concurrently (CLI: adapter processes).</param>
/// <param name="PoolSize">
/// Size of the in-process reuse pool (test fixtures: browser contexts). Mirrors <see cref="Workers"/>,
/// but note it is a <em>different unit</em> — a context shares its browser and Node driver with its
/// siblings, whereas a worker is a whole process family. The measured constants behind
/// <see cref="Workers"/> were fitted on the CLI path; see the remarks and #314.
/// </param>
/// <param name="ReuseRoster">Whether the roster engine may be reused across setups instead of cold-started each time.</param>
/// <param name="ReuseGameData">Whether the gamedata engine may be reused across setups instead of cold-started each time.</param>
public sealed record ConcurrencyPlan(
    int Workers,
    int PoolSize,
    bool ReuseRoster,
    bool ReuseGameData);
