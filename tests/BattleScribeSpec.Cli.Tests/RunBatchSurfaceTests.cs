using System.Diagnostics;
using System.Text.RegularExpressions;
using BattleScribeSpec.Engines;

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
    public async Task Workers_IsGoneAsAFlag_RejectedAsUnrecognized()
    {
        // #271 Task 5: --workers is not demoted, it is DELETED (one policy key, one flag: --policy
        // workers=N). System.CommandLine rejects the unknown option before RunCommand's own handler
        // ever runs — the exact wording is locale-dependent (System.CommandLine localizes parse
        // errors), so only the failure itself (a non-zero, non-success exit) is asserted here, not
        // the message text.
        var (exitCode, _, _) = await RunCliAsync("run", "--all", "--workers", "2");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task KeepAlive_IsGoneAsAFlag_RejectedAsUnrecognized()
    {
        // --keep-alive is sugar for "force reuse on" — one concept, expressed only as --policy
        // reuse=on now. System.CommandLine no longer recognizes --keep-alive as an option, so it
        // falls through to the positional <spec> slot, which then collides with --all
        // ("mutually exclusive") — a different-looking error than an outright parse failure, but
        // still a non-zero exit for what used to be a valid flag.
        var (exitCode, _, _) = await RunCliAsync("run", "--all", "--keep-alive");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Policy_WorkersMustBeAtLeastOne()
    {
        var (exitCode, _, stdErr) = await RunCliAsync("run", "--all", "--policy", "workers=0");
        Assert.Equal(1, exitCode);
        Assert.Contains("must be a positive integer", stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdErr);
    }

    /// <summary>
    /// <c>workers=N</c> is inapplicable to a single-spec run — one spec runs in exactly one adapter
    /// process, and the child never reads the key — so it must be REJECTED, not accepted, forwarded
    /// and dropped. A flag is accepted or rejected, never silently ignored (#305).
    /// </summary>
    /// <remarks>
    /// Falsifiable: delete <c>RunCommand.RejectInertPolicyKeys</c>'s call site and this run exits 0
    /// (the spec executes, the knob does nothing), which is exactly the pre-fix behaviour.
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Policy_Workers_IsRejectedForASingleSpecRun_NotSilentlyDropped()
    {
        var (exitCode, _, stdErr) = await RunCliAsync(
            "run", "protocol-kitchen-sink", "--engine", "battlescribe", "--policy", "workers=8");

        Assert.Equal(1, exitCode);
        Assert.Contains("workers", stdErr, StringComparison.Ordinal);
        Assert.Contains("--all", stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// ...but the reuse keys DO reach the child and change its engine's behaviour, so a single-spec
    /// run must keep accepting them. Guards against over-correcting the fix above into "no --policy
    /// in single-spec mode", which would break the warm-vs-cold poke this flag exists for.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Policy_ReuseKeys_StayLegalForASingleSpecRun()
    {
        RunCommand.RejectInertPolicyKeys("reuse=off");
        RunCommand.RejectInertPolicyKeys("reuse-roster=on,reuse-gamedata=off");
        RunCommand.RejectInertPolicyKeys(null);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Policy_UnknownKey_IsRejectedForASingleSpecRunToo()
    {
        // The single-spec key check parses with the same rules as --all, so a typo still fails here.
        var ex = Assert.Throws<CliInputException>(() => RunCommand.RejectInertPolicyKeys("bogus=1"));
        Assert.Contains("unknown key", ex.Message, StringComparison.Ordinal);
    }

    // ===== --policy: capability mismatch (reject) vs policy override (allow + warn) (#271 Task 5) =====

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyPolicyOverride_NoPolicyFlag_ReturnsSelectionUnchanged()
    {
        var selection = ResolveSelection("battlescribe");
        var warnings = new List<string>();

        var result = RunCommand.ApplyPolicyOverride(selection, policyRaw: null, warnings.Add);

        Assert.Same(selection, result);
        Assert.Empty(warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyPolicyOverride_ForcingReuseOn_AnEngineNotDeclaredReuseSafe_WarnsButAllows()
    {
        // battlescribe (non-ui) declares ReuseSafeRoster/ReuseSafeGameData = false. Forcing reuse=on
        // anyway must NOT be rejected — it is exactly the ablation `bs-spec compare` needs to prove
        // reuse-(un)safety — but it must never be silent either.
        var selection = ResolveSelection("battlescribe");
        var warnings = new List<string>();

        var result = RunCommand.ApplyPolicyOverride(selection, "reuse=on", warnings.Add);

        Assert.True(result.EffectivePlan.ReuseRoster);
        Assert.True(result.EffectivePlan.ReuseGameData);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("not declared reuse-safe", w, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyPolicyOverride_ForcingReuseOn_AnEngineDeclaredReuseSafe_DoesNotWarn()
    {
        // battlescribe-ui declares both domains reuse-safe, so the same override is unremarkable.
        var selection = ResolveSelection("battlescribe-ui");
        var warnings = new List<string>();

        var result = RunCommand.ApplyPolicyOverride(selection, "reuse=on", warnings.Add);

        Assert.True(result.EffectivePlan.ReuseRoster);
        Assert.Empty(warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyPolicyOverride_InvalidPolicyString_ThrowsCliInputException()
    {
        var selection = ResolveSelection("battlescribe");
        Assert.Throws<CliInputException>(() => RunCommand.ApplyPolicyOverride(selection, "workers=0", _ => { }));
    }

    private static EngineSelection ResolveSelection(string engineName)
    {
        var entry = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(engineName));
        return new EngineSelection(entry, EngineDomain.Roster, Headed: false, KeepAlive: false);
    }

    // ===== The policy — not a hardcoded default — picks the worker count (#271 Task 5) =====

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAll_NoPolicyFlag_PicksMoreThanOneWorker_OnAMultiCoreMachine()
    {
        // With no --workers (deleted) and no --policy override, run --all must plan more than one
        // worker for an engine whose registry MaxParallel is 0 (unlimited) on a multi-core box —
        // today's-hardcoded-1 is exactly the defect this task removes. The reference adapter run as
        // an ad-hoc dotnet: connectable gets the registry's conservative DefaultProfile
        // (MaxParallel: 0), so ConcurrencyPolicy.For scales workers with Environment.ProcessorCount.
        if (Environment.ProcessorCount < 2)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var adapterDll = FindReferenceAdapterDll(repoRoot);

        var (exitCode, _, stdErr) = await RunCliAsync(
            "run", "--all",
            "--engine", $"battlescribe=dotnet:{adapterDll}",
            "--filter", "protocol/protocol-kitchen-sink",
            "--output", "summary");

        Assert.Equal(0, exitCode);
        var match = Regex.Match(stdErr, @"Workers: (\d+)", RegexOptions.None);
        Assert.True(match.Success, $"Expected a 'Workers: N' line in stderr:\n{stdErr}");
        Assert.True(int.Parse(match.Groups[1].Value) > 1, $"Expected more than one worker, stderr:\n{stdErr}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAll_PolicyWorkers_WinsOverTheAutoPickedDefault()
    {
        // An explicit --policy override can only be conveyed to a BUILT-IN engine (a launchable
        // exec:/dotnet: adapter has no --policy channel at all, and throws rather than silently
        // drop one — see EngineHostLocator.Resolve), so this uses the "battlescribe" built-in
        // (in-process, cheap) rather than the reference adapter.
        var (exitCode, _, stdErr) = await RunCliAsync(
            "run", "--all",
            "--engine", "battlescribe",
            "--filter", "protocol/protocol-kitchen-sink",
            "--policy", "workers=1",
            "--output", "summary");

        Assert.Equal(0, exitCode);
        Assert.Contains("Workers: 1", stdErr, StringComparison.Ordinal);
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
