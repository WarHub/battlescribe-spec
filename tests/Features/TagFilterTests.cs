namespace BattleScribeSpec.Tests;

public sealed class TagFilterTests
{
    [Fact]
    public void Parse_NullExpression_ReturnsNull()
    {
        Assert.Null(TagFilter.Parse(null));
    }

    [Fact]
    public void Parse_EmptyExpression_ReturnsNull()
    {
        Assert.Null(TagFilter.Parse(""));
        Assert.Null(TagFilter.Parse("   "));
    }

    [Fact]
    public void Parse_SingleIncludeTag()
    {
        var filter = TagFilter.Parse("cost")!;
        Assert.Single(filter.IncludeTags, "cost");
        Assert.Empty(filter.ExcludeTags);
    }

    [Fact]
    public void Parse_PlusPrefixIsInclude()
    {
        var filter = TagFilter.Parse("+cost")!;
        Assert.Single(filter.IncludeTags, "cost");
        Assert.Empty(filter.ExcludeTags);
    }

    [Fact]
    public void Parse_MinusPrefixIsExclude()
    {
        var filter = TagFilter.Parse("-undefined-behavior")!;
        Assert.Empty(filter.IncludeTags);
        Assert.Single(filter.ExcludeTags, "undefined-behavior");
    }

    [Fact]
    public void Parse_CommaSeparated_MultipleIncludes()
    {
        var filter = TagFilter.Parse("cost,constraint")!;
        Assert.Equal(["cost", "constraint"], filter.IncludeTags);
        Assert.Empty(filter.ExcludeTags);
    }

    [Fact]
    public void Parse_MixedIncludeExclude()
    {
        var filter = TagFilter.Parse("cost,constraint,-undefined-behavior")!;
        Assert.Equal(["cost", "constraint"], filter.IncludeTags);
        Assert.Single(filter.ExcludeTags, "undefined-behavior");
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var filter = TagFilter.Parse(" cost , -constraint ")!;
        Assert.Single(filter.IncludeTags, "cost");
        Assert.Single(filter.ExcludeTags, "constraint");
    }

    [Fact]
    public void Parse_SkipsEmptyTokens()
    {
        var filter = TagFilter.Parse("cost,,constraint")!;
        Assert.Equal(["cost", "constraint"], filter.IncludeTags);
    }

    [Fact]
    public void Parse_BareMinus_Ignored()
    {
        var filter = TagFilter.Parse("-");
        Assert.Null(filter);
    }

    // --- Matches ---

    [Fact]
    public void Matches_IncludeOnly_MatchesIfAny()
    {
        var filter = TagFilter.Parse("cost,constraint")!;

        Assert.True(filter.Matches(["cost", "auto-select"]));
        Assert.True(filter.Matches(["constraint"]));
        Assert.False(filter.Matches(["auto-select"]));
        Assert.False(filter.Matches([]));
        Assert.False(filter.Matches(null));
    }

    [Fact]
    public void Matches_ExcludeOnly_MatchesUnlessExcluded()
    {
        var filter = TagFilter.Parse("-undefined-behavior")!;

        Assert.True(filter.Matches(["cost"]));
        Assert.True(filter.Matches([]));
        Assert.True(filter.Matches(null));
        Assert.False(filter.Matches(["undefined-behavior"]));
        Assert.False(filter.Matches(["cost", "undefined-behavior"]));
    }

    [Fact]
    public void Matches_IncludeAndExclude_ExcludeOverrides()
    {
        var filter = TagFilter.Parse("cost,-undefined-behavior")!;

        Assert.True(filter.Matches(["cost"]));
        Assert.False(filter.Matches(["cost", "undefined-behavior"]));
        Assert.False(filter.Matches(["auto-select"]));
        Assert.False(filter.Matches(["undefined-behavior"]));
    }

    [Fact]
    public void Matches_CaseInsensitive()
    {
        var filter = TagFilter.Parse("Cost,-Undefined-Behavior")!;

        Assert.True(filter.Matches(["cost", "auto-select"]));
        Assert.False(filter.Matches(["cost", "undefined-behavior"]));
    }

    // --- ToString ---

    [Fact]
    public void ToString_RoundTrips()
    {
        var filter = TagFilter.Parse("cost,constraint,-undefined-behavior")!;
        Assert.Equal("cost,constraint,-undefined-behavior", filter.ToString());
    }
}
