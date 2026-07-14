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
        var before = FixtureConcurrency.Resolve(engineName, LoadTarget.Local);

        var previous = Environment.GetEnvironmentVariable("NR_PARALLEL");
        Environment.SetEnvironmentVariable("NR_PARALLEL", "999999");
        try
        {
            var after = FixtureConcurrency.Resolve(engineName, LoadTarget.Local);

            Assert.Equal(before, after);
            Assert.NotEqual(999999, after.PoolSize);
            Assert.NotEqual(999999, FixtureConcurrency.PoolSizeFor(engineName, LoadTarget.Local));
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
            var withVariableUnset = FixtureConcurrency.Resolve("battlescribe-ui", LoadTarget.Local).ReuseGameData;

            Environment.SetEnvironmentVariable("BS_UI_KEEP_ALIVE", "true");
            var withVariableSet = FixtureConcurrency.Resolve("battlescribe-ui", LoadTarget.Local).ReuseGameData;

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
        var plan = FixtureConcurrency.Resolve(engineName, LoadTarget.Local);

        Assert.Equal(plan.PoolSize, FixtureConcurrency.PoolSizeFor(engineName, LoadTarget.Local));
    }

    /// <summary>
    /// <b>The live lane's pool is the third-party load limit — 2 — and the frozen lane's is the engine's
    /// measured optimum — 4. Same engine, same machine, different number.</b> This is the fixture-level
    /// statement of the defect: <c>LiveNrRosterFixture</c> and <c>FrozenNrRosterFixture</c> both resolve
    /// <c>"newrecruit"</c>, and the only thing that distinguishes them is the
    /// <see cref="LoadTarget"/> each declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Falsifiable, and aimed at the regression that actually shipped:</b> raise
    /// <c>ConcurrencyPolicy.ThirdPartyLiveLoadLimit</c> to match the frozen pool — the "why is this 2
    /// when that one is 4?" tidy-up — and the first assertion goes red. Delete the clamp and the live
    /// lane silently becomes 4 again (which is what commit <c>edf3b4a</c> did) and both the first and
    /// the last assertion go red. Note this runs on the <em>real</em> machine, via the same code path
    /// the fixture uses, so it also fails if <c>FixtureConcurrency</c> stops forwarding the load target.
    /// </para>
    /// <para>
    /// It asserts the live pool is <em>strictly smaller</em> rather than merely "at most" the frozen
    /// one: an "at most" test would pass in the world where both are 4, which is the world we are in
    /// today and the one this whole change exists to leave.
    /// </para>
    /// </remarks>
    [Fact]
    public void PoolSizeFor_TheLiveLane_IsTheThirdPartyLoadLimit_NotTheFrozenPool()
    {
        var live = FixtureConcurrency.PoolSizeFor("newrecruit", LoadTarget.ThirdPartyLive);
        var frozen = FixtureConcurrency.PoolSizeFor("newrecruit", LoadTarget.Local);

        Assert.Equal(2, live);
        Assert.Equal(4, frozen);
        Assert.True(live < frozen,
            $"the live lane ({live}) must not track the frozen lane's measured pool ({frozen}) — " +
            "they are the same engine, and only one of them costs a third party anything");
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
            Assert.Equal(measuredOptimum, ConcurrencyPolicy.For(machine, profile, LoadTarget.Local).PoolSize);
        }
    }
}
