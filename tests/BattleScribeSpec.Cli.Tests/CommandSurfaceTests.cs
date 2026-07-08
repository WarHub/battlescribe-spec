using System.CommandLine;
using System.Diagnostics;

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
    public void Run_WithoutModeSelector_ParsesButIsARuntimeError()
    {
        // The spec argument is now optional (Arity ZeroOrOne) so that --all/--matrix can stand
        // in for it; a bare `run` is therefore NOT a parse error — the "exactly one of
        // <spec>|--all|--matrix" rule is a runtime CliInputException (see RunBatchSurfaceTests).
        Assert.Empty(Parse("run").Errors);
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
        // In-process Program.RunAsync() returning a non-zero exit code is NOT proof this was
        // rendered cleanly: System.CommandLine's own invocation pipeline catches ANY unhandled
        // exception from a command action, prints "Unhandled exception: <type>: <message>" plus
        // a full stack trace to stderr, and still completes the Task with exit code 1 — so a
        // genuine crash and a clean `Ui.Error` + `return 1` look identical from exit code alone.
        // Spawn the real CLI out-of-process and inspect stderr to tell the two apart (this is
        // the CliInputException path from EngineOptions.Resolve, thrown while constructing
        // RunCommand's RunOptions — see RunCommand.cs's SetAction try/catch).
        var (exitCode, _, stdErr) = await RunCliAsync("run", "spec", "--engine", "warscroll");

        Assert.Equal(1, exitCode);
        Assert.Contains("error:", stdErr);
        Assert.Contains("Unknown engine 'warscroll'", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
        Assert.DoesNotContain("CliInputException", stdErr);
    }

    [Fact]
    public void Verify_EnginesWithConnectableEntry_ParsesWithoutErrors()
    {
        // --engines is a free-form CSV (built-in names, exec:/dotnet: connectables, and
        // <name>=<connectable> forms all parse at the System.CommandLine layer); resolution
        // against the registry happens at runtime in ExecuteGameDataAsync, one column at a
        // time, so an unresolvable entry is a runtime Unavailable cell, not a parse error.
        string[] args =
        [
            "verify", "gamedata/entry/add-entry-basic",
            "--engines", "battlescribe,wham=dotnet:adapter.dll",
        ];
        var parse = CommandFactory.CreateRootCommand().Parse(args);

        Assert.Empty(parse.Errors);
    }

    [Fact]
    public async Task Run_GameDataAnonymousConnectable_RendersCleanly_NotAnUnhandledCrash()
    {
        // Regression test: an anonymous exec:/dotnet: connectable has no registry identity
        // (EngineSelection.EngineName is null), so a gamedata spec with an `engines:` map used
        // to NRE deep in SpecFileBase.ShouldSkip (Dictionary<string,string>.TryGetValue(null))
        // when RunCommand.RunGameDataAsync called spec.IsApplicableTo(engineName!). The fix
        // only checks applicability when an identity exists; here the downstream "unknown
        // gamedata engine" failure is expected and fine — the NRE/ArgumentNullException crash
        // is not.
        var repoRoot = FindRepoRoot();
        var spec = Path.Combine(repoRoot, "specs", "gamedata", "nr", "nr-type-def-additions.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var (exitCode, _, stdErr) = await RunCliAsync(
            "run", spec, "--engine", "exec:doesnotexist", "--gamedata");

        Assert.Equal(1, exitCode);
        Assert.Contains("error:", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
        Assert.DoesNotContain("NullReferenceException", stdErr);
        Assert.DoesNotContain("ArgumentNullException", stdErr);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static string FindCliDll()
    {
        var dll = Path.Combine(FindRepoRoot(), "artifacts", "bin", "BattleScribeSpec.Cli", "debug", "bs-spec.dll");
        Assert.True(File.Exists(dll), $"CLI not built: {dll}");
        return dll;
    }

    /// <summary>Spawn the real <c>bs-spec</c> CLI out-of-process and capture its output/exit code.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(FindCliDll());
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start bs-spec.dll.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdOutTask, await stdErrTask);
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
