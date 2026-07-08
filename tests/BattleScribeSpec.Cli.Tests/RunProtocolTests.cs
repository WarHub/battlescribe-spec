using System.Diagnostics;

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
        var repoRoot = FindRepoRoot();
        var spec = Path.Combine(repoRoot, "specs", "roster", "protocol", "protocol-kitchen-sink.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = FindReferenceAdapterDll(repoRoot);

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
        // Spawned out-of-process (like CommandSurfaceTests' RunCliAsync) rather than via
        // in-process Program.RunAsync + Console.SetIn: Console.In/Out/Error are global mutable
        // state, and xUnit runs test classes concurrently by default, so redirecting them
        // in-process risks cross-test interference. Out-of-process gives each run its own
        // stdin/stdout/stderr pipes.
        var repoRoot = FindRepoRoot();
        var spec = Path.Combine(repoRoot, "specs", "roster", "protocol", "protocol-kitchen-sink.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = FindReferenceAdapterDll(repoRoot);
        var cliDll = FindCliDll(repoRoot);

        var (exitCode, stdOut, stdErr) = await RunCliWithStdinAsync(
            cliDll,
            ["run", spec, "--engine", $"battlescribe=dotnet:{adapterDll}", "--break", "1"],
            stdin: "quit\n");

        Assert.Equal(130, exitCode);
        Assert.Contains("aborted at step 1", stdOut + stdErr);
        Assert.DoesNotContain("PASS — all assertions passed", stdOut + stdErr);
    }

    /// <summary>Spawn the built bs-spec CLI out-of-process, feed it <paramref name="stdin"/>, and capture output/exit code.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliWithStdinAsync(
        string cliDll, string[] args, string stdin)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start bs-spec.dll.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_GameDataSpec_OverReferenceAdapter_Passes()
    {
        var repoRoot = FindRepoRoot();
        var spec = Path.Combine(repoRoot, "specs", "gamedata", "entry", "add-entry-basic.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = FindReferenceAdapterDll(repoRoot);

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

    private static string FindReferenceAdapterDll(string repoRoot)
    {
        // The reference adapter builds under the same pivot (debug/release) as this test; try
        // that pivot first, then fall back to debug (the pivot CI builds tests under).
        var pivot = ExtractPivot(AppContext.BaseDirectory);
        foreach (var candidatePivot in new[] { pivot, "debug" }.Where(p => p is not null).Distinct())
        {
            var dll = Path.Combine(repoRoot, "artifacts", "bin",
                "BattleScribeSpec.ReferenceAdapter", candidatePivot!, "bs-reference-adapter.dll");
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        var expected = Path.Combine(repoRoot, "artifacts", "bin",
            "BattleScribeSpec.ReferenceAdapter", pivot ?? "debug", "bs-reference-adapter.dll");
        Assert.Fail($"Reference adapter not built: {expected}");
        return expected;
    }

    private static string FindCliDll(string repoRoot)
    {
        // Same pivot fallback as FindReferenceAdapterDll: try this test assembly's own
        // debug/release pivot first, then fall back to debug (the pivot CI builds tests under).
        var pivot = ExtractPivot(AppContext.BaseDirectory);
        foreach (var candidatePivot in new[] { pivot, "debug" }.Where(p => p is not null).Distinct())
        {
            var dll = Path.Combine(repoRoot, "artifacts", "bin", "BattleScribeSpec.Cli", candidatePivot!, "bs-spec.dll");
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        var expected = Path.Combine(repoRoot, "artifacts", "bin", "BattleScribeSpec.Cli", pivot ?? "debug", "bs-spec.dll");
        Assert.Fail($"CLI not built: {expected}");
        return expected;
    }

    private static string? ExtractPivot(string baseDirectory)
    {
        var segments = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var binIndex = Array.FindLastIndex(segments, s => s.Equals("bin", StringComparison.OrdinalIgnoreCase));
        return binIndex >= 0 && binIndex + 2 < segments.Length ? segments[binIndex + 2] : null;
    }
}
