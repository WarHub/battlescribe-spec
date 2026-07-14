using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
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
        var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, ReuseRoster: true, ReuseGameData: true);

        var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

        Assert.True(options.ReuseRosterEngineAcrossSetups);
        Assert.True(options.ReuseGameDataEngineAcrossSetups);
    }

    [Fact]
    public void BuildOptions_ReuseOff_SetsBothAcrossSetupsFlagsFromThePlan()
    {
        var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, ReuseRoster: false, ReuseGameData: false);

        var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

        Assert.False(options.ReuseRosterEngineAcrossSetups);
        Assert.False(options.ReuseGameDataEngineAcrossSetups);
    }

    [Fact]
    public void BuildOptions_ReuseDomainsAreIndependent_NotAllOrNothing()
    {
        // Regression guard for the string-match era, where reuse was an engine-name-wide on/off.
        // The plan can (and does, e.g. a future engine) disagree per domain.
        var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, ReuseRoster: true, ReuseGameData: false);

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
        var plan = new ConcurrencyPlan(Workers: 7, PoolSize: 7, ReuseRoster: true, ReuseGameData: true);

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
            var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, ReuseRoster: true, ReuseGameData: true);

            var options = ServeCommand.BuildOptions("battlescribe-ui", headless: true, plan);

            Assert.True(options.ReuseRosterEngineAcrossSetups);
            Assert.True(options.ReuseGameDataEngineAcrossSetups);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE", prior);
        }
    }

    /// <summary>
    /// Artifact-free BS-UI paths. Construction never launches the JVM (that happens on the first
    /// <c>SetupAsync</c>), so these paths are never dereferenced here — the same trick
    /// <c>BsUiSetupFailureTeardownTests</c> uses. This is what lets the KeepAlive-follows-the-plan
    /// gate below run in EVERY CI job, including the ones that do not build the Java agent jar
    /// (<c>checks</c>, <c>thorough-conformance</c>: <c>setup.ps1</c> skips the jar when CI=true).
    /// Going through <c>HostEngineFactory.Create*EngineAsync</c> instead would throw "Agent JAR not
    /// found" there — a gate that cannot fail in CI is not a gate.
    /// </summary>
    private static BsUiOptions UnusedOptions() => new()
    {
        JavaPath = "unused-java-bsspec-test.exe",
        RosterEditorJarPath = "unused-roster-editor.jar",
        AgentJarPath = "unused-agent.jar",
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateBsUiRosterEngine_KeepAliveMatchesTheReuseDecision(bool reuseRoster)
    {
        // KeepAlive must be the plan's reuse decision, verbatim. The bug this guards: the factory
        // used to force KeepAlive ON for battlescribe-ui roster regardless of what the caller
        // decided (`keepAlive || !reuseDisabled`), so the child overrode the parent.
        var engine = HostEngineFactory.CreateBsUiRosterEngine(UnusedOptions(), reuseRoster);

        using (engine as IDisposable)
        {
            Assert.Equal(reuseRoster, Assert.IsType<BsUiRosterEngine>(engine).KeepAlive);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateBsUiGameDataEngine_KeepAliveMatchesTheReuseDecision(bool reuseGameData)
    {
        var engine = HostEngineFactory.CreateBsUiGameDataEngine(UnusedOptions(), reuseGameData);

        using (engine as IDisposable)
        {
            Assert.Equal(reuseGameData, Assert.IsType<BsGameDataUiEngine>(engine).KeepAlive);
        }
    }

    [Fact]
    public void CreateBsUiEngines_DoNotReadTheAblationEnvironmentVariable()
    {
        // The reuse decision arrives as a parameter and nothing else. Set the retired ablation var
        // to the value that used to force reuse OFF, ask for reuse ON, and prove the parameter wins.
        var prior = Environment.GetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE");
        try
        {
            Environment.SetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE", "1");

            var roster = HostEngineFactory.CreateBsUiRosterEngine(UnusedOptions(), reuseRoster: true);
            using (roster as IDisposable)
            {
                Assert.True(Assert.IsType<BsUiRosterEngine>(roster).KeepAlive);
            }

            var gameData = HostEngineFactory.CreateBsUiGameDataEngine(UnusedOptions(), reuseGameData: true);
            using (gameData as IDisposable)
            {
                Assert.True(Assert.IsType<BsGameDataUiEngine>(gameData).KeepAlive);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE", prior);
        }
    }
}
