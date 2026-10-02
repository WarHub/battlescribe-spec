namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Covers the rewired <c>run</c> roster path (#271 PR 2): the CLI spawns the engine as a
/// child adapter process and drives it entirely over the JSON-line protocol via
/// <c>JsonProtocolEngine</c>, with artifact options gated by the describe handshake.
/// The end-to-end case drives the BattleScribe reference adapter as the <c>battlescribe</c>
/// identity (<c>battlescribe=dotnet:bs-reference-adapter.dll</c>).
/// </summary>
public sealed class RunProtocolTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_RosterSpec_OverReferenceAdapter_Passes()
    {
        var spec = CliProcess.SpecPath("roster", "protocol", "protocol-kitchen-sink.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = CliProcess.ReferenceAdapterDll;

        var exitCode = await Program.RunAsync(
            "run", spec, "--engine", $"battlescribe=dotnet:{adapterDll}");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_BreakAndQuit_AbortsWithExitCode130_NotPass()
    {
        // Pins the `quit` contract at the REPL prompt: the run must render as an abort
        // (exit 130, "aborted at step N") rather than falling through to the runner's normal
        // (empty-failures) result and rendering "PASS — all assertions passed" + exit 0.
        // Spawned out-of-process (through CliProcess, like every such test here) rather than via
        // in-process Program.RunAsync + Console.SetIn: Console.In/Out/Error are global mutable
        // state, and xUnit runs test classes concurrently by default, so redirecting them
        // in-process risks cross-test interference. Out-of-process gives each run its own
        // stdin/stdout/stderr pipes.
        var spec = CliProcess.SpecPath("roster", "protocol", "protocol-kitchen-sink.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = CliProcess.ReferenceAdapterDll;

        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            ["run", spec, "--engine", $"battlescribe=dotnet:{adapterDll}", "--break", "1"],
            stdin: "quit\n");

        Assert.Equal(130, exitCode);
        Assert.Contains("aborted at step 1", stdOut + stdErr);
        Assert.DoesNotContain("PASS — all assertions passed", stdOut + stdErr);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAll_OverReferenceAdapter_FilteredToKitchenSink_Passes()
    {
        // Batch mode over the reference adapter, narrowed to a single roster spec. Domains default
        // to both, but the --filter excludes every gamedata spec, so only the roster kitchen-sink
        // runs — exit 0 and a "Results:" summary line on stdout.
        var adapterDll = CliProcess.ReferenceAdapterDll;

        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            [
                "run", "--all",
                "--engine", $"battlescribe=dotnet:{adapterDll}",
                "--filter", "protocol/protocol-kitchen-sink",
                "--output", "summary",
            ],
            stdin: "");

        Assert.Equal(0, exitCode);
        Assert.Contains("Results:", stdOut + stdErr);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_GameDataSpec_OverReferenceAdapter_Passes()
    {
        var spec = CliProcess.SpecPath("gamedata", "entry", "add-entry-basic.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = CliProcess.ReferenceAdapterDll;

        var exitCode = await Program.RunAsync(
            "run", spec, "--engine", $"battlescribe=dotnet:{adapterDll}", "--gamedata");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Run_BreakOnGamedataSpec_ParsesWithoutErrors()
    {
        // --break stays an engine-agnostic run option; on a gamedata target the run path
        // warns-and-ignores it (unchanged by the roster protocol rewire), so the invocation
        // must still parse cleanly rather than becoming a parse error.
        string[] args = ["run", "gamedata/entry/add-entry-basic", "--engine", "battlescribe", "--break", "2"];
        var parse = CommandFactory.CreateRootCommand().Parse(args);

        Assert.Empty(parse.Errors);
    }
}
