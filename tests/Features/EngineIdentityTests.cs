using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The one engine-expectation rule (#573): every per-engine lookup reads the engine's own entry, else its
/// family's. <see cref="EngineIdentity"/> holds the rule; <see cref="SpecFileBase.ExpectationFor"/> is the
/// spec-level <c>engines:</c> reader every runner calls.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EngineIdentityTests
{
    [Theory]
    [InlineData("battlescribe", "battlescribe")]
    [InlineData("battlescribe-ui", "battlescribe")]
    [InlineData("newrecruit", "newrecruit")]
    [InlineData("newrecruit-ui", "newrecruit")]
    [InlineData("my-adapter", "my-adapter")]
    // A bare suffix is a name, not a driver of an engine called "".
    [InlineData("-ui", "-ui")]
    public void FamilyOf_DropsATrailingUiSuffix(string name, string family)
    {
        Assert.Equal(family, EngineIdentity.FamilyOf(name));
        Assert.Equal(new EngineIdentity(name, family), EngineIdentity.Of(name));
    }

    [Fact]
    public void Names_AreTheEnginesOwnThenItsFamilys()
    {
        Assert.Equal(["battlescribe-ui", "battlescribe"], EngineIdentity.Of("battlescribe-ui").Names);
        Assert.Equal(["battlescribe"], EngineIdentity.Of("battlescribe").Names);
    }

    [Theory]
    // The driver's own entry wins over its family's.
    [InlineData("battlescribe-ui", "battlescribe:skip,battlescribe-ui:pass", EngineExpectation.Pass)]
    [InlineData("battlescribe-ui", "battlescribe:pass,battlescribe-ui:fail", EngineExpectation.Fail)]
    // No entry of its own: the family's applies — the case every runner but two used to get wrong.
    [InlineData("battlescribe-ui", "battlescribe:skip", EngineExpectation.Skip)]
    [InlineData("newrecruit-ui", "newrecruit:fail", EngineExpectation.Fail)]
    // A driver's entry is the driver's alone.
    [InlineData("battlescribe", "battlescribe-ui:skip", EngineExpectation.Pass)]
    // Another family's entry never applies.
    [InlineData("newrecruit-ui", "battlescribe:skip", EngineExpectation.Pass)]
    // Values compare case-insensitively, as the runners always did; the lint holds the corpus to lowercase.
    [InlineData("newrecruit", "newrecruit:SKIP", EngineExpectation.Skip)]
    // No map, or nothing for this engine: expected to pass.
    [InlineData("battlescribe-ui", "", EngineExpectation.Pass)]
    public void ExpectationFor_IsTheEnginesOwnEntry_ElseItsFamilys(string engine, string entries, EngineExpectation expected)
    {
        var spec = SpecWith(entries);
        Assert.Equal(expected, spec.ExpectationFor(EngineIdentity.Of(engine)));
        Assert.Equal(expected != EngineExpectation.Skip, spec.IsApplicableTo(EngineIdentity.Of(engine)));
        Assert.Equal(expected == EngineExpectation.Fail, spec.IsExpectedToFail(EngineIdentity.Of(engine)));
    }

    [Theory]
    [InlineData("battlescribe-ui", "battlescribe:skip", "battlescribe")]
    [InlineData("battlescribe-ui", "battlescribe:skip,battlescribe-ui:pass", "battlescribe-ui")]
    public void DeclaringKey_NamesTheEntryThatDecided(string engine, string entries, string key)
        => Assert.Equal(key, SpecWith(entries).DeclaringKey(EngineIdentity.Of(engine)));

    /// <summary>A stated family replaces the derived one (<c>--assertion-engine</c>); the engine's own entry still wins.</summary>
    [Fact]
    public void AStatedFamily_IsInheritedInsteadOfTheDerivedOne()
    {
        var engine = new EngineIdentity("my-adapter", "newrecruit");
        Assert.Equal(EngineExpectation.Skip, SpecWith("newrecruit:skip").ExpectationFor(engine));
        Assert.Equal(EngineExpectation.Pass, SpecWith("newrecruit:skip,my-adapter:pass").ExpectationFor(engine));
    }

    private static SpecFile SpecWith(string entries) => new()
    {
        Id = "identity",
        Category = "identity",
        Description = "engine expectation resolution",
        Setup = new SetupDef(),
        Steps = [],
        Engines = entries.Length == 0
            ? null
            : entries.Split(',').Select(e => e.Split(':')).ToDictionary(kv => kv[0], kv => kv[1]),
    };
}
