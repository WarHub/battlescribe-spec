using System.Diagnostics;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Surface tests for <c>bs-spec run</c>'s batch modes (#271 PR 2): the mode selectors
/// (<c>&lt;spec&gt;</c> / <c>--all</c> / <c>--matrix</c>) are mutually exclusive, the modal
/// <c>--output</c> is validated per mode, and the worker-clamp logic is a pure function.
///
/// Validation is a runtime <see cref="CliInputException"/> rendered via <c>Ui.Error</c> to
/// stderr (not a parse error), so — like <c>CommandSurfaceTests</c> — these assert the rendered
/// message out-of-process: System.CommandLine's invocation pipeline would otherwise make a clean
/// <c>Ui.Error</c> + <c>return 1</c> indistinguishable from an unhandled crash by exit code alone.
/// </summary>
public sealed class RunBatchSurfaceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SpecAndAll_AreMutuallyExclusive()
    {
        var (exitCode, _, stdErr) = await RunCliAsync("run", "some-spec", "--all");
        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AllAndMatrix_AreMutuallyExclusive()
    {
        var (exitCode, _, stdErr) = await RunCliAsync("run", "--all", "--matrix", "some-dir");
        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NoModeSelector_IsRejected()
    {
        var (exitCode, _, stdErr) = await RunCliAsync("run");
        Assert.Equal(1, exitCode);
        Assert.Contains("exactly one", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SingleSpec_RejectsBatchOutputFormat()
    {
        // "summary" is a valid --output token overall (passes the parse-time union check) but is
        // batch-only, so a single-spec run must reject it at runtime.
        var (exitCode, _, stdErr) = await RunCliAsync("run", "some-spec", "--output", "summary");
        Assert.Equal(1, exitCode);
        Assert.Contains("single-spec", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task All_RejectsSingleSpecOutputFormat()
    {
        var (exitCode, _, stdErr) = await RunCliAsync("run", "--all", "--output", "tree");
        Assert.Equal(1, exitCode);
        Assert.Contains("not valid for --all", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Json_IsRejectedUnderAll()
    {
        var (exitCode, _, stdErr) = await RunCliAsync("run", "--all", "--json");
        Assert.Equal(1, exitCode);
        Assert.Contains("--json is only valid for a single-spec run", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Workers_MustBeAtLeastOne()
    {
        var (exitCode, _, stdErr) = await RunCliAsync("run", "--all", "--workers", "0");
        Assert.Equal(1, exitCode);
        Assert.Contains("--workers must be at least 1", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    // ===== ClampWorkers (pure function) =====

    [Fact]
    [Trait("Category", "Unit")]
    public void ClampWorkers_ClampsToRegistryMax()
    {
        var warnings = new List<string>();
        var effective = RunBatch.ClampWorkers(8, registryMax: 2, describedMax: 0, warnings.Add);
        Assert.Equal(2, effective);
        Assert.Single(warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClampWorkers_ClampsToDescribedMax()
    {
        var warnings = new List<string>();
        var effective = RunBatch.ClampWorkers(8, registryMax: 0, describedMax: 4, warnings.Add);
        Assert.Equal(4, effective);
        Assert.Single(warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClampWorkers_AppliesBothCeilings_DescribedWins()
    {
        var warnings = new List<string>();
        var effective = RunBatch.ClampWorkers(8, registryMax: 6, describedMax: 2, warnings.Add);
        Assert.Equal(2, effective);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClampWorkers_ZeroMaxMeansUnlimited_NoClamp()
    {
        var warnings = new List<string>();
        var effective = RunBatch.ClampWorkers(4, registryMax: 0, describedMax: 0, warnings.Add);
        Assert.Equal(4, effective);
        Assert.Empty(warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClampWorkers_UnderCeilings_NoClamp()
    {
        var warnings = new List<string>();
        var effective = RunBatch.ClampWorkers(2, registryMax: 8, describedMax: 8, warnings.Add);
        Assert.Equal(2, effective);
        Assert.Empty(warnings);
    }

    // ===== --matrix =====

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Matrix_RendersMarkdownFromConformanceReports()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs-spec-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var json = """
                {
                  "Engine": "battlescribe",
                  "GeneratedAt": "2026-07-07T00:00:00Z",
                  "TotalSpecs": 1,
                  "Passed": 1,
                  "Failed": 0,
                  "Skipped": 0,
                  "PassRate": 100.0,
                  "Results": [
                    { "SpecId": "kitchen-sink", "Category": "protocol", "Description": "", "Status": "passed", "Failures": [] }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(dir, "battlescribe-conformance.json"), json);

            var (exitCode, stdOut, _) = await RunCliAsync("run", "--matrix", dir);

            Assert.Equal(0, exitCode);
            Assert.Contains("Engine Compatibility Matrix", stdOut);
            Assert.Contains("battlescribe", stdOut);
            Assert.Contains("protocol", stdOut);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Matrix_MissingDirectory_IsRejected()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bs-spec-missing-" + Guid.NewGuid().ToString("N"));
        var (exitCode, _, stdErr) = await RunCliAsync("run", "--matrix", missing);
        Assert.Equal(1, exitCode);
        Assert.Contains("matrix directory not found", stdErr);
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
        // Try this test assembly's own debug/release pivot first, then fall back to debug.
        var repoRoot = FindRepoRoot();
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
}
