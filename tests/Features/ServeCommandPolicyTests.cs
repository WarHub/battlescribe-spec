using BattleScribeSpec.Concurrency;
using BattleScribeSpec.EngineHost;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The parent decides, the child is told: <c>ServeCommand.BuildOptions</c> takes the reuse
/// decision as a <see cref="ConcurrencyPlan"/> parameter instead of string-matching its own engine
/// name and reading <c>BSSPEC_DISABLE_WARM_REUSE</c>. <c>HostEngineFactory</c>'s <c>KeepAlive</c>
/// must agree with the plan it is handed, not contradict it (the bug this task fixes: it used to
/// force <c>KeepAlive</c> on for battlescribe-ui roster regardless of what <c>ServeCommand</c>
/// decided).
/// </summary>
[Trait("Category", "Unit")]
public sealed class ServeCommandPolicyTests
{
    [Fact]
    public void BuildOptions_ReuseOn_SetsBothAcrossSetupsFlagsFromThePlan()
    {
        var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, MaxParallelThreads: 1, ReuseRoster: true, ReuseGameData: true);

        var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

        Assert.True(options.ReuseRosterEngineAcrossSetups);
        Assert.True(options.ReuseGameDataEngineAcrossSetups);
    }

    [Fact]
    public void BuildOptions_ReuseOff_SetsBothAcrossSetupsFlagsFromThePlan()
    {
        var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, MaxParallelThreads: 1, ReuseRoster: false, ReuseGameData: false);

        var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

        Assert.False(options.ReuseRosterEngineAcrossSetups);
        Assert.False(options.ReuseGameDataEngineAcrossSetups);
    }

    [Fact]
    public void BuildOptions_ReuseDomainsAreIndependent_NotAllOrNothing()
    {
        // Regression guard for the string-match era, where reuse was an engine-name-wide on/off.
        // The plan can (and does, e.g. a future engine) disagree per domain.
        var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, MaxParallelThreads: 1, ReuseRoster: true, ReuseGameData: false);

        var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

        Assert.True(options.ReuseRosterEngineAcrossSetups);
        Assert.False(options.ReuseGameDataEngineAcrossSetups);
    }

    [Fact]
    public void BuildOptions_MaxParallel_ComesFromTheEnginesOwnProfile_NotThePlansWorkers()
    {
        // MaxParallel is an engine CEILING (EngineRegistry.Builtins), not this run's chosen worker
        // count — a --policy workers=N override must not leak into the capability the client uses
        // to decide how many adapter processes IT may spawn.
        var plan = new ConcurrencyPlan(Workers: 7, PoolSize: 7, MaxParallelThreads: 7, ReuseRoster: true, ReuseGameData: true);

        var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

        Assert.Equal(1, options.Capabilities.MaxParallel); // battlescribe-ui's declared ceiling
    }

    [Fact]
    public void BuildOptions_DoesNotReadTheAblationEnvironmentVariable()
    {
        // BSSPEC_DISABLE_WARM_REUSE is retired: the plan is the only input now. Prove it by setting
        // the old ablation var to "1" (which used to force everything cold) alongside a plan that
        // explicitly asks for reuse, and asserting the plan wins.
        var prior = Environment.GetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE");
        try
        {
            Environment.SetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE", "1");
            var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, MaxParallelThreads: 1, ReuseRoster: true, ReuseGameData: true);

            var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

            Assert.True(options.ReuseRosterEngineAcrossSetups);
            Assert.True(options.ReuseGameDataEngineAcrossSetups);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE", prior);
        }
    }

    [Fact]
    public async Task CreateRosterEngineAsync_BattlescribeUi_KeepAliveMatchesTheReuseDecision_True()
    {
        var engine = await HostEngineFactory.CreateRosterEngineAsync("battlescribe-ui", headless: true, reuseRoster: true);
        using (engine as IDisposable)
        {
            Assert.IsType<BattleScribeSpec.BsRosterUiDriver.BsUiRosterEngine>(engine);
            Assert.True(((BattleScribeSpec.BsRosterUiDriver.BsUiRosterEngine)engine).KeepAlive);
        }
    }

    [Fact]
    public async Task CreateRosterEngineAsync_BattlescribeUi_KeepAliveMatchesTheReuseDecision_False()
    {
        var prior = Environment.GetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE");
        try
        {
            // Old ablation var says "force reuse off"; the new code must ignore it entirely and
            // key off the explicit parameter alone. Set it to something that would have forced
            // KeepAlive ON under the old `keepAlive || !reuseDisabled` logic (i.e. leave it unset,
            // which used to mean reuseDisabled=false => KeepAlive=true) to prove the parameter,
            // not the environment, governs.
            Environment.SetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE", null);

            var engine = await HostEngineFactory.CreateRosterEngineAsync("battlescribe-ui", headless: true, reuseRoster: false);
            using (engine as IDisposable)
            {
                Assert.False(((BattleScribeSpec.BsRosterUiDriver.BsUiRosterEngine)engine).KeepAlive);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE", prior);
        }
    }

    [Fact]
    public async Task CreateGameDataEngineAsync_BattlescribeUi_KeepAliveMatchesTheReuseDecision()
    {
        var engineOn = await HostEngineFactory.CreateGameDataEngineAsync("battlescribe-ui", headless: true, reuseGameData: true);
        using (engineOn as IDisposable)
        {
            Assert.True(((BattleScribeSpec.BsGameDataUiDriver.BsGameDataUiEngine)engineOn).KeepAlive);
        }

        var engineOff = await HostEngineFactory.CreateGameDataEngineAsync("battlescribe-ui", headless: true, reuseGameData: false);
        using (engineOff as IDisposable)
        {
            Assert.False(((BattleScribeSpec.BsGameDataUiDriver.BsGameDataUiEngine)engineOff).KeepAlive);
        }
    }

}
