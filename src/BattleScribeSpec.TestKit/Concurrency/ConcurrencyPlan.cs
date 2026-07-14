namespace BattleScribeSpec.Concurrency;

/// <summary>
/// One decision, for one engine on one machine: how many instances may run at once, and whether
/// they are reused across setups. Produced by <see cref="ConcurrencyPolicy.For"/>, and the only
/// input the CLI, the engine host and the test fixtures take for those questions.
/// </summary>
/// <remarks>
/// <para>
/// <b>TWO AXES. THEY ARE NOT THE SAME NUMBER, AND NOTHING READS BOTH.</b>
/// </para>
/// <list type="table">
/// <listheader><term/><description><b>Workers</b> (process axis) vs <b>PoolSize</b> (context axis)</description></listheader>
/// <item><term>Unit</term><description>
/// <see cref="Workers"/> = a whole adapter <em>process family</em> (adapter + its own Node driver +
/// its own browser tree, ≈1.2–1.4 GiB each). <see cref="PoolSize"/> = a browser <em>context</em>
/// inside ONE shared browser behind ONE shared Node driver (≈225 MiB each).
/// </description></item>
/// <item><term>Who reads it</term><description>
/// <see cref="Workers"/>: the CLI batch path only (<c>bs-spec run --all</c> → <c>SpecSuiteRunner</c>
/// → <c>EngineHostLocator</c>, which puts <c>workers=</c> on the wire). <see cref="PoolSize"/>: the
/// xUnit path only (<c>dotnet test</c> → <c>FixtureConcurrency.PoolSizeFor</c> → the fixture pools'
/// <c>MaxDegreeOfParallelism</c>) — <b>which is what every NewRecruit CI conformance lane runs</b>.
/// Neither path reads the other's field; <see cref="PoolSize"/> is not even on the protocol wire.
/// </description></item>
/// <item><term>How it is sized</term><description>
/// <see cref="Workers"/> scales with <c>CpuCount</c> (<c>ceil(cpuCount × k)</c>, memory-bounded).
/// <see cref="PoolSize"/> <b>does not scale with anything about the CPU</b> — it is an absolute
/// per-engine measured constant, memory-bounded. Contention on the single shared Playwright driver
/// binds it, and an 8× CPU cut moves it by 0.5%.
/// </description></item>
/// </list>
/// <para>
/// <b>These two fields were once one number</b> (<c>PoolSize: workers</c>), which is how a constant
/// fitted by sweeping worker <em>processes</em> ended up sizing browser <em>contexts</em>: CI's pools
/// silently became 2 and 4 where the measured optima are 4 and 16, a 2× regression on
/// <c>nr-editor-ui-frozen</c>. If a change ever makes these two equal for <c>newrecruit-ui</c>, it is
/// a bug, and <c>ConcurrencyPolicyTests</c> says so out loud.
/// </para>
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
/// pool's size (<see cref="PoolSize"/>), by the engine's measured constant and by memory. It does
/// <b>not</b> bound the product across simultaneously live xUnit collection fixtures. A collection
/// fixture lives for the whole collection, not for one thread-slot, so two independent collections
/// (e.g. <c>FrozenNrRosterFixture</c> and <c>FrozenNrGameDataUiFixture</c>) can each be fully alive
/// at once, each holding a pool — total live browser contexts can reach the sum across collections,
/// not any single pool's size. Nor can xUnit's thread count bound it: the real parallelism inside a
/// conformance test is a <c>Parallel.ForEachAsync(MaxDegreeOfParallelism = pool.Size)</c>
/// <em>within one <c>[Fact]</c></em>, which <c>maxParallelThreads</c> does not constrain at all.
/// A shared budget the pools draw from is the real fix and remains #314's business; the interim
/// blanket cap that used to sit in <c>FixtureConcurrency</c> is gone, because it capped the measured
/// optimum (16 → 8, costing that lane 31%) while claiming to be free.
/// </para>
/// </remarks>
/// <param name="Workers">
/// <b>PROCESS AXIS.</b> How many adapter <em>processes</em> the CLI batch path may run concurrently.
/// Scales with <c>CpuCount</c> (<c>ceil(cpuCount × OversubscriptionFactor)</c>), bounded by memory
/// (<c>MemPerInstanceBytes</c>) and by <c>MaxParallel</c>. <b>Read only by the CLI path</b>
/// (<c>SpecSuiteRunner</c>/<c>EngineHostLocator</c>, which sends it as <c>workers=</c>); read nowhere
/// in <c>tests/Infrastructure/</c>.
/// </param>
/// <param name="PoolSize">
/// <b>CONTEXT AXIS.</b> How many browser <em>contexts</em> one in-process fixture pool holds. An
/// engine's absolute measured constant (<c>EngineProfile.ContextPoolSize</c>), bounded by memory
/// (<c>MemPerContextBytes</c>) and by <c>MaxParallel</c> — and <b>deliberately not a function of
/// <c>CpuCount</c></b>, because the sweep found the same optimum on a 32-core box and a 4-CPU
/// container. <b>Read only by the xUnit path</b> (<c>FixtureConcurrency.PoolSizeFor</c>); it is not
/// on the protocol wire and the CLI has no pool at all. It is <em>not</em> a rescaled
/// <see cref="Workers"/>: for <c>newrecruit-ui</c> on a 4-vCPU runner the two are 4 and 16.
/// </param>
/// <param name="ReuseRoster">Whether the roster engine may be reused across setups instead of cold-started each time.</param>
/// <param name="ReuseGameData">Whether the gamedata engine may be reused across setups instead of cold-started each time.</param>
public sealed record ConcurrencyPlan(
    int Workers,
    int PoolSize,
    bool ReuseRoster,
    bool ReuseGameData);
