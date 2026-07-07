using System.CommandLine;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Parse-level tests for the bs-spec command surface: the verbs exist, arguments are
/// required where expected, and unknown/invalid options are rejected at parse time
/// (rather than silently ignored as in the old single-command parser).
/// </summary>
[Trait("Category", "Unit")]
public sealed class CommandSurfaceTests
{
    private static ParseResult Parse(params string[] args) =>
        CommandFactory.CreateRootCommand().Parse(args);

    [Fact]
    public void Root_ExposesTheFourVerbs()
    {
        var names = CommandFactory.CreateRootCommand().Subcommands.Select(c => c.Name).ToHashSet();

        Assert.Contains("run", names);
        Assert.Contains("probe", names);
        Assert.Contains("export-xml", names);
        Assert.Contains("format", names);
    }

    [Theory]
    [InlineData("run", "selection/selection-page")]
    [InlineData("probe", "selection/selection-page")]
    [InlineData("export-xml", "spec", "dir")]
    [InlineData("format")]
    [InlineData("format", "specs/roster")]
    public void ValidInvocations_ParseWithoutErrors(params string[] args)
    {
        Assert.Empty(Parse(args).Errors);
    }

    [Fact]
    public void Run_RequiresASpecArgument()
    {
        Assert.NotEmpty(Parse("run").Errors);
    }

    [Fact]
    public void ExportXml_RequiresSpecAndDir()
    {
        Assert.NotEmpty(Parse("export-xml", "only-spec").Errors);
    }

    [Fact]
    public void UnknownOption_IsAParseError()
    {
        Assert.NotEmpty(Parse("run", "spec", "--bogus").Errors);
    }

    [Fact]
    public void UnknownVerb_IsAParseError()
    {
        Assert.NotEmpty(Parse("frobnicate", "spec").Errors);
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("-o", "tree")]
    [InlineData("-o", "json")]
    [InlineData("--output", "json")]
    public void Run_AcceptsOutputFormatVariants(params string[] outputArgs)
    {
        string[] args = ["run", "spec", .. outputArgs];
        Assert.Empty(Parse(args).Errors);
    }

    [Fact]
    public void Run_RejectsInvalidOutputFormat()
    {
        Assert.NotEmpty(Parse("run", "spec", "-o", "yaml").Errors);
    }

    [Fact]
    public void Run_AcceptsAnyEngineStringAtParseTime()
    {
        // --engine is a free-form string (name/connectable/engines.json entry); unknown
        // names are a runtime CliInputException from EngineOptions.Resolve, not a parse error.
        Assert.Empty(Parse("run", "spec", "--engine", "warscroll").Errors);
    }

    [Fact]
    public async Task Run_RejectsUnknownEngineNameAtRuntime()
    {
        var exitCode = await Program.RunAsync("run", "spec", "--engine", "warscroll");
        Assert.NotEqual(0, exitCode);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("run", "--help")]
    [InlineData("probe", "--help")]
    [InlineData("export-xml", "--help")]
    [InlineData("format", "--help")]
    public async Task Help_ExitsZero(params string[] args)
    {
        Assert.Equal(0, await Program.RunAsync(args));
    }
}
