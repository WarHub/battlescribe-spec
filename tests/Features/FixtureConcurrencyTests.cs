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
    /// The fixture pool cap actually binds. Pure inputs, so this is falsifiable on every machine —
    /// on the 4-vCPU CI runner the policy's own answer (2–4) is already below the cap, so asserting
    /// only against a live <c>PoolSizeFor</c> would pass there whether or not the cap existed.
    /// </summary>
    [Fact]
    public void CapPoolSize_BoundsABigBoxesPlan_AndLeavesASmallOneAlone()
    {
        // Uncapped, the policy asks this 32-core box for 32 contexts for newrecruit-ui.
        Assert.Equal(FixtureConcurrency.FixturePoolCap, FixtureConcurrency.CapPoolSize(32));

        // ...but the cap is a ceiling, never a floor: it must not inflate the CI runner's 2.
        Assert.Equal(2, FixtureConcurrency.CapPoolSize(2));
    }

    [Theory]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void PoolSizeFor_IsThePolicysAnswerBoundedByTheCap(string engineName)
    {
        var expected = FixtureConcurrency.CapPoolSize(FixtureConcurrency.Resolve(engineName).PoolSize);

        var actual = FixtureConcurrency.PoolSizeFor(engineName);

        Assert.Equal(expected, actual);
        Assert.InRange(actual, 1, FixtureConcurrency.FixturePoolCap);
    }
}
