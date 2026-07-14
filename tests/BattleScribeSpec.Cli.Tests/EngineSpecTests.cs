using System.CommandLine;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Tests the string-based engine surface: <see cref="EngineOptions.Resolve"/> parses
/// <c>--engine</c> as a name/connectable/engines.json entry via <see cref="EngineConnectable"/>
/// and <see cref="EngineRegistry"/>, the <c>--ui</c> sugar, the UI→non-UI assertion mapping,
/// and how the domain is derived (explicit override vs. spec-path inference).
/// </summary>
[Trait("Category", "Unit")]
public sealed class EngineSpecTests
{
    [Theory]
    [InlineData("battlescribe")]
    [InlineData("battlescribe-ui")]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void Resolve_ResolvesEachBuiltinName(string name)
    {
        var selection = Resolve("plain-spec-id", "--engine", name);
        Assert.Equal(name, selection.EngineName);
    }

    [Fact]
    public void Resolve_UiSugar_AppendsUiToAPlainName()
    {
        var selection = Resolve("plain-spec-id", "--engine", "newrecruit", "--ui");
        Assert.Equal("newrecruit-ui", selection.EngineName);
    }

    [Fact]
    public void Resolve_UiSugar_IsIdempotentOnAnAlreadyUiName()
    {
        var selection = Resolve("plain-spec-id", "--engine", "newrecruit-ui", "--ui");
        Assert.Equal("newrecruit-ui", selection.EngineName);
    }

    [Fact]
    public void Resolve_NameEqualsDotnetLaunch_CarriesIdentityAndLaunch()
    {
        var selection = Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll");

        Assert.Equal("wham", selection.EngineName);
        Assert.Equal("dotnet", selection.Entry.Executable);
        Assert.Equal("adapter.dll", selection.Entry.Arguments);
        Assert.False(selection.Entry.Builtin);
    }

    [Fact]
    public void Resolve_UnknownEngineName_ThrowsCliInputException()
    {
        Assert.Throws<CliInputException>(() => Resolve("plain-spec-id", "--engine", "warscroll"));
    }

    [Fact]
    public void Resolve_UiCombinedWithExecConnectable_ThrowsCliInputException()
    {
        Assert.Throws<CliInputException>(() => Resolve("plain-spec-id", "--engine", "exec:node adapter.js", "--ui"));
    }

    [Theory]
    [InlineData("battlescribe", "battlescribe")]
    [InlineData("battlescribe-ui", "battlescribe")]
    [InlineData("newrecruit", "newrecruit")]
    [InlineData("newrecruit-ui", "newrecruit")]
    public void AssertionEngineName_DropsTheUiSuffix(string engineName, string expected)
    {
        var selection = Resolve("plain-spec-id", "--engine", engineName);
        Assert.Equal(expected, selection.AssertionEngineName);
    }

    [Theory]
    [InlineData(false, false, "roster/battlescribe")]
    [InlineData(true, true, "gamedata/battlescribe-ui")]
    public void Display_CombinesDomainAndEngineName(bool gamedata, bool ui, string expected)
    {
        string[] domainArgs = gamedata ? ["--gamedata"] : [];
        string[] uiArgs = ui ? ["--ui"] : [];
        string[] args = ["--engine", "battlescribe", .. domainArgs, .. uiArgs];

        var selection = Resolve("plain-spec-id", args);
        Assert.Equal(expected, selection.Display);
    }

    [Fact]
    public void Resolve_DefaultsToBattlescribeRoster()
    {
        var selection = Resolve("plain-spec-id");

        Assert.Equal("battlescribe", selection.EngineName);
        Assert.Equal(EngineDomain.Roster, selection.Domain);
    }

    [Fact]
    public void Resolve_InfersGamedataFromSpecPath()
    {
        var selection = Resolve("specs/gamedata/entry/add-entry-basic");
        Assert.Equal(EngineDomain.Gamedata, selection.Domain);
    }

    [Fact]
    public void Resolve_RosterFlagOverridesGamedataPath()
    {
        var selection = Resolve("specs/gamedata/entry/add-entry-basic", "--roster");
        Assert.Equal(EngineDomain.Roster, selection.Domain);
    }

    [Fact]
    public void Resolve_GamedataFlagForcesGamedata()
    {
        var selection = Resolve("plain-roster-spec", "--gamedata");
        Assert.Equal(EngineDomain.Gamedata, selection.Domain);
    }

    [Fact]
    public void Resolve_RejectsConflictingDomainFlags()
    {
        var options = new EngineOptions();
        var result = ParseWith(options, "spec", "--gamedata", "--roster");

        Assert.Throws<CliInputException>(() => options.Resolve(result, "spec"));
    }

    // ---- The parent decides; the child is told. ----
    // These pin the property that distinguishes "parent decides" from "both guess and hopefully
    // agree": the composed child command line must CARRY the decision. A child that computed its
    // own plan would still work on this machine and would still be wrong — it is a separate process
    // that may see a different machine (container CPU limits, cgroup quotas) and drift silently.

    [Theory]
    [InlineData("battlescribe-ui")]
    [InlineData("battlescribe")]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void ResolveLaunch_AlwaysTellsTheChildThePolicy_EvenWithNoUserOverride(string engineName)
    {
        var selection = Resolve("plain-spec-id", "--engine", engineName);

        var launch = selection.ResolveLaunch();

        // Not "only when the user asked for an override" — EVERY spawn carries the plan.
        Assert.Contains("--policy ", launch.Arguments, StringComparison.Ordinal);

        // And it carries THIS plan's values, not a re-derivation the child could disagree with.
        var plan = selection.EffectivePlan;
        Assert.Contains($"workers={plan.Workers}", launch.Arguments, StringComparison.Ordinal);
        Assert.Contains($"reuse-roster={(plan.ReuseRoster ? "on" : "off")}", launch.Arguments, StringComparison.Ordinal);
        Assert.Contains($"reuse-gamedata={(plan.ReuseGameData ? "on" : "off")}", launch.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectivePlan_ComesFromTheConcurrencyPolicy_ForThisMachineAndEngine()
    {
        var selection = Resolve("plain-spec-id", "--engine", "battlescribe-ui");

        var expected = ConcurrencyPolicy.For(MachineProfile.Current(), selection.Entry.Profile);

        Assert.Equal(expected, selection.EffectivePlan);

        // battlescribe-ui is the engine whose reuse was measured verdict-neutral AND faster in both
        // domains; the plan the child is told must actually say so, or the refactor turned reuse off.
        Assert.True(selection.EffectivePlan.ReuseRoster);
        Assert.True(selection.EffectivePlan.ReuseGameData);
    }

    [Fact]
    public void EffectivePlan_KeepAlive_ForcesReuseOn_AndIsFoldedIntoThePolicySentToTheChild()
    {
        // --keep-alive is sugar for "force reuse on". It must be folded into the ONE decision the
        // child receives, not survive as a separate flag the child has to reconcile.
        var selection = Resolve("plain-spec-id", "--engine", "newrecruit-ui") with { KeepAlive = true };

        Assert.True(selection.EffectivePlan.ReuseRoster);
        Assert.True(selection.EffectivePlan.ReuseGameData);
        Assert.Contains("reuse-roster=on,reuse-gamedata=on", selection.ResolveLaunch().Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveLaunch_LaunchableAdapter_GetsNoPolicy_AndIsNotGivenAFabricatedOne()
    {
        // exec:/dotnet: adapters have no --policy channel (#305 is the sibling gap for
        // --headed/--keep-alive). They must not be handed a policy flag they'd choke on...
        var selection = Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll");

        var launch = selection.ResolveLaunch();

        Assert.DoesNotContain("--policy", launch.Arguments, StringComparison.Ordinal);
        Assert.Equal("adapter.dll", launch.Arguments);
    }

    [Fact]
    public void ResolveLaunch_LaunchableAdapter_WithAnExplicitOverride_ThrowsRatherThanSilentlyDropIt()
    {
        // ...but an override the USER explicitly asked for must never be silently ignored.
        var selection = Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll") with
        {
            PlanOverride = new ConcurrencyPlan(2, 2, ReuseRoster: true, ReuseGameData: true),
        };

        Assert.Throws<InvalidOperationException>(selection.ResolveLaunch);
    }

    // ---- --headed: a flag is accepted or rejected, never silently dropped (#305, #271 Task 5) ----

    [Fact]
    public void Resolve_HeadedAgainstLaunchableAdapter_ThrowsNamingNoChannel()
    {
        // EngineHostLocator.Resolve has no channel to convey --headed to an exec:/dotnet: adapter
        // and used to just drop it on the floor. Reject at the CLI layer instead, before any
        // process is spawned.
        var ex = Assert.Throws<CliInputException>(
            () => Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll", "--headed"));

        Assert.Contains("no channel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_HeadedAgainstNonUiBuiltin_ThrowsNamingNoUi()
    {
        // "battlescribe" (no -ui suffix) has no window to show; --headed against it is a mistake,
        // not a no-op.
        var ex = Assert.Throws<CliInputException>(
            () => Resolve("plain-spec-id", "--engine", "battlescribe", "--headed"));

        Assert.Contains("no UI", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_HeadedAgainstUiBuiltin_Succeeds()
    {
        var selection = Resolve("plain-spec-id", "--engine", "battlescribe-ui", "--headed");
        Assert.True(selection.Headed);
    }

    [Fact]
    public void Resolve_HeadedWithUiSugar_Succeeds()
    {
        // --ui appends "-ui" to a plain name BEFORE the headed capability check runs, so this must
        // not throw even though the user typed the non-ui name.
        var selection = Resolve("plain-spec-id", "--engine", "newrecruit", "--ui", "--headed");
        Assert.True(selection.Headed);
    }

    private static EngineSelection Resolve(string specInput, params string[] extraArgs)
    {
        var options = new EngineOptions();
        string[] args = [specInput, .. extraArgs];
        return options.Resolve(ParseWith(options, args), specInput);
    }

    private static ParseResult ParseWith(EngineOptions options, params string[] args)
    {
        var command = new Command("probe-engine-options");
        command.Arguments.Add(new Argument<string>("spec"));
        options.AddTo(command);
        return command.Parse(args);
    }
}
