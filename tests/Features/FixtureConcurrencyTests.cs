using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Guards the deletion of the fixtures' environment-variable knobs: every xUnit fixture's pool size
/// and reuse decision must come from <see cref="FixtureConcurrency"/> (backed by
/// <c>ConcurrencyPolicy</c>) and be completely deaf to <c>NR_PARALLEL</c> and <c>BS_UI_KEEP_ALIVE</c>.
/// Regression protection against a future contributor re-adding either "just to unblock debugging."
/// </summary>
[Trait("Category", "Unit")]
public sealed class FixtureConcurrencyTests
{
    [Theory]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void Resolve_IgnoresNrParallel_ThePolicyIsTheOnlySource(string engineName)
    {
        var before = FixtureConcurrency.Resolve(engineName);

        var previous = Environment.GetEnvironmentVariable("NR_PARALLEL");
        Environment.SetEnvironmentVariable("NR_PARALLEL", "999999");
        try
        {
            var after = FixtureConcurrency.Resolve(engineName);

            Assert.Equal(before, after);
            Assert.NotEqual(999999, after.PoolSize);
            Assert.NotEqual(999999, FixtureConcurrency.PoolSizeFor(engineName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NR_PARALLEL", previous);
        }
    }

    /// <summary>
    /// The BS-UI gamedata fixture's warm/cold decision is the policy's, not an environment
    /// variable's. Fails if <c>BS_UI_KEEP_ALIVE</c> is ever reintroduced as an input: the variable is
    /// set to the value that used to mean "cold" (unset) and then to "true", and the answer must not
    /// move either way.
    /// </summary>
    [Fact]
    public void Resolve_BattlescribeUiGameDataReuse_IgnoresBsUiKeepAlive()
    {
        var previous = Environment.GetEnvironmentVariable("BS_UI_KEEP_ALIVE");
        try
        {
            Environment.SetEnvironmentVariable("BS_UI_KEEP_ALIVE", null);
            var withVariableUnset = FixtureConcurrency.Resolve("battlescribe-ui").ReuseGameData;

            Environment.SetEnvironmentVariable("BS_UI_KEEP_ALIVE", "true");
            var withVariableSet = FixtureConcurrency.Resolve("battlescribe-ui").ReuseGameData;

            Assert.Equal(withVariableSet, withVariableUnset);

            // And the policy's actual answer for this engine: battlescribe-ui is ColdStartCost
            // .Expensive (a JVM + JavaFX launch per spec) and declares ReuseSafeGameData, so reuse
            // is ON — the answer the unset variable used to contradict, silently, on every local run.
            Assert.True(withVariableUnset);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BS_UI_KEEP_ALIVE", previous);
        }
    }

    /// <summary>
    /// The fixtures apply <b>no bound of their own</b> on top of the policy. A blanket
    /// <c>FixturePoolCap = 8</c> used to sit here, and it was quietly capping <c>newrecruit-ui</c>'s
    /// measured optimum of 16 — costing that lane 31% while its docstring claimed the cap could only
    /// "over-provision, not OOM" (true of memory; false of time). The policy's per-context memory
    /// bound does the real work now.
    /// </summary>
    /// <remarks>
    /// Falsifiable, and it is the test that fails if anyone re-adds a magic ceiling: on any machine
    /// with enough memory for the declared pool — which includes the 4-vCPU / 16 GiB CI runner, where
    /// the policy asks for 16 — reintroducing a cap below 16 makes <c>PoolSizeFor</c> disagree with
    /// the plan. It cannot be satisfied by a cap that "happens not to bind here", because it compares
    /// against the plan itself rather than a literal.
    /// </remarks>
    [Theory]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void PoolSizeFor_IsThePolicysAnswer_UnmodifiedByAnyFixtureLevelCap(string engineName)
    {
        var plan = FixtureConcurrency.Resolve(engineName);

        Assert.Equal(plan.PoolSize, FixtureConcurrency.PoolSizeFor(engineName));
    }

    /// <summary>
    /// And the pool the fixtures will actually build is the engine's measured optimum, not the
    /// worker count that used to be mirrored into it. Asserted on explicit machine profiles so it is
    /// falsifiable on whatever box the suite happens to run on (the CI runner is 4-vCPU; the dev box
    /// is 32-core; the old mirror gave those two boxes different pools, which is the bug).
    /// </summary>
    private static readonly MachineProfile[] Machines =
    [
        new MachineProfile(CpuCount: 4, AvailableMemoryBytes: 16L << 30),   // CI runner
        new MachineProfile(CpuCount: 32, AvailableMemoryBytes: 96L << 30),  // dev box
    ];

    [Theory]
    [InlineData("newrecruit", 4)]
    [InlineData("newrecruit-ui", 16)]
    public void PoolSizeFor_DeliversTheMeasuredOptimum_OnEveryMachineWithMemoryForIt(
        string engineName, int measuredOptimum)
    {
        var profile = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(engineName)).Profile;

        foreach (var machine in Machines)
        {
            Assert.Equal(measuredOptimum, ConcurrencyPolicy.For(machine, profile).PoolSize);
        }
    }
}
