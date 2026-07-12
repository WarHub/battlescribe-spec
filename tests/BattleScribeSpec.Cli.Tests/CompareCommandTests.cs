using System.Diagnostics;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Covers <c>bs-spec compare</c> (#271 Task 12): the verdict-equality rail a future auto-tuner
/// must pass. The critical case is RED-on-divergence — two configurations that produce different
/// per-spec verdicts must fail the comparison with a non-zero exit code, never just a slower/faster
/// number. The reference adapter's <c>BSSPEC_TEST_FORCE_FAIL</c> hook (see
/// <c>src/BattleScribeSpec.ReferenceAdapter/ForceFailEngines.cs</c>) is what lets a test make one
/// arm diverge deterministically without touching any real engine.
/// </summary>
public sealed class CompareCommandTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_ExitsNonZero_WhenAVerdictDiverges()
    {
        // A configuration change that alters conformance results is not an optimization, it is a
        // regression — and this assertion is the only thing in the harness that catches it.
        var repoRoot = FindRepoRoot();
        var adapterDll = FindReferenceAdapterDll(repoRoot);
        var cliDll = FindCliDll(repoRoot);

        var (exitCode, stdOut, stdErr) = await RunCliAsync(
            cliDll,
            "compare",
            "--engine", $"battlescribe=dotnet:{adapterDll}",
            "--filter", "protocol/protocol-kitchen-sink",
            "--config-a", "",
            "--config-b", "BSSPEC_TEST_FORCE_FAIL=1");

        Assert.NotEqual(0, exitCode);
        var combined = stdOut + stdErr;
        Assert.Contains("DIVERGENCE", combined, StringComparison.Ordinal);
        Assert.Contains("A=passed", combined, StringComparison.Ordinal);
        Assert.Contains("B=failed", combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_IdenticalConfigs_ExitsZero_AndReportsSpeedupNearOne()
    {
        var repoRoot = FindRepoRoot();
        var adapterDll = FindReferenceAdapterDll(repoRoot);
        var cliDll = FindCliDll(repoRoot);

        var (exitCode, stdOut, stdErr) = await RunCliAsync(
            cliDll,
            "compare",
            "--engine", $"battlescribe=dotnet:{adapterDll}",
            "--filter", "protocol/protocol-kitchen-sink",
            "--config-a", "",
            "--config-b", "");

        var combined = stdOut + stdErr;
        Assert.Equal(0, exitCode);
        Assert.Contains("Verdicts identical", combined, StringComparison.Ordinal);
        Assert.Contains("speedup", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DIVERGENCE", combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Compare_RejectsMalformedConfig()
    {
        var (exitCode, _, stdErr) = await RunCliAsync(
            FindCliDll(FindRepoRoot()),
            "compare", "--engine", "battlescribe",
            "--config-a", "NOT_KEY_VALUE",
            "--config-b", "");

        Assert.Equal(1, exitCode);
        Assert.Contains("--config-a", stdErr, StringComparison.Ordinal);
        Assert.Contains("KEY=VALUE", stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdErr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Compare_Workers_MustBeAtLeastOne()
    {
        var (exitCode, _, stdErr) = await RunCliAsync(
            FindCliDll(FindRepoRoot()),
            "compare", "--engine", "battlescribe",
            "--workers", "0",
            "--config-a", "",
            "--config-b", "");

        Assert.Equal(1, exitCode);
        Assert.Contains("--workers must be at least 1", stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdErr, StringComparison.Ordinal);
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

    /// <summary>Spawn the built bs-spec CLI out-of-process and capture its output/exit code.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string cliDll, params string[] args)
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
}
