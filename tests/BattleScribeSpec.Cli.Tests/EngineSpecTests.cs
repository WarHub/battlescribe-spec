using System.CommandLine;
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
