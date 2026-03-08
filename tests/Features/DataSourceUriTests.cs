using BattleScribeSpec;

namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public class DataSourceUriTests
{
    [Fact]
    public void Parse_GithubLatest_ParsesSuccessfully()
    {
        var result = DataSourceUri.Parse("github:BSData/wh40k-10e");

        Assert.Equal("github", result.Provider);
        Assert.Equal("BSData", result.Org);
        Assert.Equal("wh40k-10e", result.Repo);
        Assert.Null(result.Ref);
    }

    [Fact]
    public void Parse_GithubTag_ParsesRef()
    {
        var result = DataSourceUri.Parse("github:BSData/wh40k-10e@v10.14.0");

        Assert.Equal("v10.14.0", result.Ref);
    }

    [Fact]
    public void Parse_GithubBranch_ParsesRef()
    {
        var result = DataSourceUri.Parse("github:BSData/age-of-sigmar-4e@main");

        Assert.Equal("main", result.Ref);
    }

    [Fact]
    public void Parse_LocalPath_ParsesSuccessfully()
    {
        var result = DataSourceUri.Parse("local:C:/path/to/data");

        Assert.Equal("local", result.Provider);
        Assert.Equal("", result.Org);
        Assert.Equal("C:/path/to/data", result.Repo);
        Assert.Null(result.Ref);
    }

    [Fact]
    public void Parse_Empty_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => DataSourceUri.Parse(""));
    }

    [Fact]
    public void Parse_NoProvider_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => DataSourceUri.Parse(":BSData/wh40k-10e"));
    }

    [Fact]
    public void Parse_GithubNoPath_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => DataSourceUri.Parse("github:"));
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalse()
    {
        var success = DataSourceUri.TryParse("github:", out var result);

        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void CacheKey_WithRef_IncludesRef()
    {
        var result = DataSourceUri.Parse("github:BSData/wh40k-10e@v10.14.0");

        Assert.Equal("github/BSData/wh40k-10e/v10.14.0", result.CacheKey);
    }

    [Fact]
    public void CacheKey_WithoutRef_UsesLatest()
    {
        var result = DataSourceUri.Parse("github:BSData/wh40k-10e");

        Assert.Equal("github/BSData/wh40k-10e/latest", result.CacheKey);
    }

    [Fact]
    public void Parse_PreservesRaw_ForRoundTrip()
    {
        const string uri = "github:BSData/wh40k-10e@main";

        var result = DataSourceUri.Parse(uri);

        Assert.Equal(uri, result.Raw);
    }
}
