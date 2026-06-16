using System.CommandLine;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Tests the orthogonal engine model: product × ui × domain resolve to the right concrete
/// engine name, the UI→non-UI assertion mapping, and how <see cref="EngineOptions.Resolve"/>
/// derives the domain (explicit override vs. spec-path inference).
/// </summary>
[Trait("Category", "Unit")]
public sealed class EngineSpecTests
{
    // [Theory] methods must be public, so they take bools (not the internal enums) and map.
    [Theory]
    [InlineData(false, false, "battlescribe")]
    [InlineData(false, true, "battlescribe-ui")]
    [InlineData(true, false, "newrecruit")]
    [InlineData(true, true, "newrecruit-ui")]
    public void EngineName_MapsProductAndSurface(bool newrecruit, bool ui, string expected)
    {
        var spec = new EngineSpec(Product(newrecruit), ui, EngineDomain.Roster);
        Assert.Equal(expected, spec.EngineName);
    }

    [Theory]
    [InlineData(false, "battlescribe")]
    [InlineData(true, "newrecruit")]
    public void AssertionEngineName_DropsTheUiSurface(bool newrecruit, string expected)
    {
        var spec = new EngineSpec(Product(newrecruit), Ui: true, EngineDomain.Roster);
        Assert.Equal(expected, spec.AssertionEngineName);
    }

    [Theory]
    [InlineData(false, false, "roster/battlescribe")]
    [InlineData(true, true, "gamedata/battlescribe-ui")]
    public void Display_CombinesDomainAndEngineName(bool gamedata, bool ui, string expected)
    {
        var domain = gamedata ? EngineDomain.Gamedata : EngineDomain.Roster;
        var spec = new EngineSpec(EngineProduct.Battlescribe, ui, domain);
        Assert.Equal(expected, spec.Display);
    }

    private static EngineProduct Product(bool newrecruit) =>
        newrecruit ? EngineProduct.Newrecruit : EngineProduct.Battlescribe;

    [Fact]
    public void Resolve_DefaultsToBattlescribeRoster()
    {
        var spec = Resolve("plain-spec-id");

        Assert.Equal(EngineProduct.Battlescribe, spec.Product);
        Assert.False(spec.Ui);
        Assert.Equal(EngineDomain.Roster, spec.Domain);
    }

    [Fact]
    public void Resolve_ReadsProductAndUi()
    {
        var spec = Resolve("plain-spec-id", "--engine", "newrecruit", "--ui");

        Assert.Equal(EngineProduct.Newrecruit, spec.Product);
        Assert.True(spec.Ui);
    }

    [Fact]
    public void Resolve_InfersGamedataFromSpecPath()
    {
        var spec = Resolve("specs/gamedata/entry/add-entry-basic");
        Assert.Equal(EngineDomain.Gamedata, spec.Domain);
    }

    [Fact]
    public void Resolve_RosterFlagOverridesGamedataPath()
    {
        var spec = Resolve("specs/gamedata/entry/add-entry-basic", "--roster");
        Assert.Equal(EngineDomain.Roster, spec.Domain);
    }

    [Fact]
    public void Resolve_GamedataFlagForcesGamedata()
    {
        var spec = Resolve("plain-roster-spec", "--gamedata");
        Assert.Equal(EngineDomain.Gamedata, spec.Domain);
    }

    [Fact]
    public void Resolve_RejectsConflictingDomainFlags()
    {
        var options = new EngineOptions();
        var result = ParseWith(options, "spec", "--gamedata", "--roster");

        Assert.Throws<CliInputException>(() => options.Resolve(result, "spec"));
    }

    private static EngineSpec Resolve(string specInput, params string[] extraArgs)
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
