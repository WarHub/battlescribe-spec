using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

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
        Assert.DoesNotContain("DIVERGENCE", combined, StringComparison.Ordinal);

        // Identical configs run the identical arm twice, so the reported speedup (B/A wall time)
        // is genuinely expected to be near 1.0 — this is what the test's name promises, so assert
        // the actual value rather than just the literal word "speedup" appearing somewhere.
        //
        // This assertion is the thing that caught a real bug: `compare` used to run arm A first
        // with no warm-up, so arm A ate the process's first-run costs (JIT, cold OS file cache,
        // first AV scan of freshly built DLLs) that arm B then got for free — an intermittent
        // ~1-in-6 failure with speedup as low as 0.29 for two IDENTICAL arms. CompareCommand now
        // runs a discarded warm-up pass (same spec set, neither config) before timing either arm,
        // which removes that systematic bias. Post-fix, 40 consecutive local runs of this exact
        // scenario landed in [0.94, 1.02]. [0.6, 1.6] keeps real headroom for slower/noisier CI
        // runners while still being far tighter than the old [0.5, 2.0] — which was only wide
        // enough to let the bug slip through, not to describe a fair instrument's real variance.
        var speedupMatch = Regex.Match(
            combined, @"speedup \(B/A\):\s*([0-9]+\.[0-9]+)x", RegexOptions.IgnoreCase);
        Assert.True(speedupMatch.Success, $"Expected a 'speedup (B/A): N.NNx' line in output:\n{combined}");
        var speedup = double.Parse(speedupMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(speedup, 0.6, 1.6);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_ExpectedFailures_ThreadsIntoBothArms_ReportsExpectedFailureNotFailed()
    {
        // #271 review IMPORTANT 1: compare never set SpecSuiteOptions.ExpectedFailuresEngine, so a
        // spec annotated `engines: { <name>: fail }` was always labeled "failed"/"passed" rather
        // than "expected-failure"/"unexpected-pass". This fixture spec is annotated fail for a
        // synthetic engine identity; --config-b force-fails it via the reference adapter's test-only
        // hook while --config-a leaves it passing, so the two arms deliberately diverge on status —
        // proving --expected-failures reached BOTH arms: arm A must read "unexpected-pass" (it
        // actually passed but was annotated to fail) and arm B must read "expected-failure" (it
        // actually failed and was annotated to fail), never the plain "passed"/"failed" that
        // omitting the flag would produce.
        var repoRoot = FindRepoRoot();
        var adapterDll = FindReferenceAdapterDll(repoRoot);
        var cliDll = FindCliDll(repoRoot);
        var fixturesDir = WriteExpectedFailureFixture();

        try
        {
            var (exitCode, stdOut, stdErr) = await RunCliAsync(
                cliDll,
                "compare",
                "--engine", $"cmp-xfail-engine=dotnet:{adapterDll}",
                "--specs", fixturesDir,
                "--expected-failures", "cmp-xfail-engine",
                "--config-a", "",
                "--config-b", "BSSPEC_TEST_FORCE_FAIL=1");

            var combined = stdOut + stdErr;
            Assert.NotEqual(0, exitCode);
            Assert.Contains("DIVERGENCE", combined, StringComparison.Ordinal);
            Assert.Contains("A=unexpected-pass", combined, StringComparison.Ordinal);
            Assert.Contains("B=expected-failure", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("B=failed", combined, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturesDir, recursive: true);
        }
    }

    /// <summary>
    /// Writes a single-spec fixture directory (category "xfail") annotated
    /// <c>engines: { cmp-xfail-engine: fail }</c>: normal setup/steps that pass on the reference
    /// adapter unless <c>BSSPEC_TEST_FORCE_FAIL</c> is set. No repo spec carries a top-level "fail"
    /// annotation (grepped for one), so a private fixture is needed to exercise the
    /// expected-failure/unexpected-pass classification without touching the shared specs tree.
    /// </summary>
    private static string WriteExpectedFailureFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bsspec-compare-xfail-" + Guid.NewGuid().ToString("N")[..8], "xfail");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "xfail-demo.yaml"), """
            id: xfail-demo
            category: xfail
            description: Fixture for bs-spec compare's --expected-failures threading test.
            engines:
              cmp-xfail-engine: fail

            setup:
              gameSystem:
                id: gs-1
                name: GS
                costTypes:
                  - id: ct-pts
                    name: pts
                forceEntries:
                  - id: fe-1
                    name: Force
                    categoryLinks:
                      - id: cl-1
                        targetId: cat-1
                        name: Cat
                categoryEntries:
                  - id: cat-1
                    name: Cat

              catalogues:
                - id: cat-file-1
                  gameSystemId: gs-1
                  selectionEntries:
                    - id: se-1
                      name: Unit
                      type: unit
                      costs:
                        - name: pts
                          typeId: ct-pts
                          value: 10
                      categoryLinks:
                        - id: cl-se-1
                          targetId: cat-1
                          name: Cat
                          primary: true

            steps:
              - action: addForce
                id: add-1
                forceEntryId: fe-1

              - expectedState:
                  forceCount: 1
            """);
        return Path.GetDirectoryName(dir)!;
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
