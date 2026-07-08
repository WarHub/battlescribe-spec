using System.Diagnostics;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Covers the rewired <c>verify</c> gamedata matrix (#271 PR 2): each <c>--engines</c> entry is
/// resolved via <c>EngineConnectable.Parse</c> + <c>EngineRegistry.Resolve</c>, spawned as a child
/// adapter process, and driven entirely over the JSON-line protocol. Drives the BattleScribe
/// reference adapter as the <c>battlescribe</c> identity (<c>battlescribe=dotnet:bs-reference-adapter.dll</c>),
/// same locator as <see cref="RunProtocolTests"/>.
/// </summary>
public sealed class VerifyProtocolTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Verify_GameDataSpec_OverReferenceAdapter_PrintsMatrixAndExitsZero()
    {
        // Spawned out-of-process (like RunProtocolTests' break-and-quit case): asserting on
        // captured stdout means redirecting Console.Out, and xUnit runs test classes
        // concurrently by default, so redirecting the shared static Console in-process risks
        // cross-test interference. A separate process gives this run its own stdout pipe.
        var repoRoot = FindRepoRoot();
        var spec = Path.Combine(repoRoot, "specs", "gamedata", "entry", "add-entry-basic.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = FindReferenceAdapterDll(repoRoot);
        var cliDll = FindCliDll(repoRoot);

        var (exitCode, stdOut, stdErr) = await RunCliAsync(
            cliDll, ["verify", spec, "--engines", $"battlescribe=dotnet:{adapterDll}"]);

        Assert.True(exitCode == 0, $"exit code {exitCode}; stdout: {stdOut}; stderr: {stdErr}");
        Assert.Contains("battlescribe", stdOut);
        Assert.Contains("spec", stdOut);
        // Verify the matrix row shows a PASS cell for the spec, not just that N/A appears.
        Assert.Matches(@"add-entry-basic\s+PASS", stdOut);
    }

    /// <summary>Spawn the built bs-spec CLI out-of-process and capture output/exit code.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string cliDll, string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
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
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdOutTask, await stdErrTask);
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
