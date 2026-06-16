namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Tests spec discovery: engine-domain inference from a spec path/id, and that the
/// loader resolves all advertised id forms (bare id and category/id) anchored at
/// the roster specs directory.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecResolutionTests
{
    [Theory]
    [InlineData("specs/gamedata/entry/add-entry-basic.yaml", "gamedata")]
    [InlineData("specs/roster/selection/selection-page.yaml", "roster")]
    [InlineData("plain-id-with-no-hint", "roster")]
    [InlineData("-", "roster")]
    public void InferEngineType_UsesPathHints(string input, string expected)
    {
        Assert.Equal(expected, SpecLoading.InferEngineType(input));
    }

    [Fact]
    public void InferEngineType_DetectsGamedataByBareId()
    {
        // A bare gamedata spec id (no path hint) is resolved against specs/gamedata.
        Assert.Equal("gamedata", SpecLoading.InferEngineType("add-entry-basic"));
    }

    [Theory]
    [InlineData("cost-hidden-limit-validation")]            // bare id
    [InlineData("cost/cost-hidden-limit-validation")]       // category/id
    public void LoadSpec_ResolvesAdvertisedIdForms(string specId)
    {
        var spec = SpecLoading.LoadSpec(specId);
        Assert.Equal("cost-hidden-limit-validation", spec.Id);
    }

    [Fact]
    public void LoadSpec_ThrowsForUnknownSpec()
    {
        Assert.Throws<FileNotFoundException>(() => SpecLoading.LoadSpec("no-such-spec-anywhere"));
    }
}
