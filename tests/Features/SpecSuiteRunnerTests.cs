using System.Diagnostics;
using BattleScribeSpec.Batch;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

public sealed class SpecSuiteRunnerTests
{
    private static string FindAdapterDll()
    {
        // Tests run from artifacts/bin/BattleScribeSpec.Tests/<pivot>/ — walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        var dll = Path.Combine(dir.FullName, "artifacts", "bin",
            "BattleScribeSpec.ReferenceAdapter", "debug", "bs-reference-adapter.dll");
        Assert.True(File.Exists(dll), $"Reference adapter not built: {dll}");
        return dll;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FilteredSuite_RunsAgainstReferenceAdapter()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            EngineFilter = "battlescribe",
            ExpectedFailuresEngine = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.True(result.TotalSpecs > 0);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Status == "passed");
    }

    [Fact]
    public async Task MissingSpecsDirectory_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            SpecsDirectory = Path.Combine(Path.GetTempPath(), "does-not-exist-bsspec"),
            AdapterFactory = _ => throw new UnreachableException(),
        }));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GameDataDomain_RunsOverTheSameAdapterPool()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["roster", "gamedata"],
            FilterPatterns = ["entry/add-entry-basic"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic" && r.Status == "passed");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MixedDomains_ParallelWorkers_RunBothDomains()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["roster", "gamedata"],
            Workers = 2,
            FilterPatterns = ["protocol/protocol-kitchen-sink", "entry/add-entry-basic"],
            EngineFilter = "battlescribe",
            ExpectedFailuresEngine = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Category == "protocol" && r.SpecId == "protocol-kitchen-sink" && r.Status == "passed");
        Assert.Contains(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic" && r.Status == "passed");
    }

    /// <summary>
    /// <b>A batch run that executes nothing exits 8, not 0.</b> The emptiness check that existed —
    /// "no spec files found" — runs before any filter, so a filter matching nothing used to finish
    /// with <c>0 passed, 0 failed</c> and exit 0, indistinguishable from a green suite.
    /// </summary>
    /// <remarks>Falsifiable: put <c>ExitCode</c> back to <c>Failed &gt; 0 ? 1 : 0</c> and this reads 0.</remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FilterSelectingNothing_ExitsEight_AndSaysSo()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["no-such-spec"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(SpecSuiteResult.NothingExecutedExitCode, result.ExitCode);
        Assert.Equal(8, result.ExitCode);
        Assert.Equal(0, result.Selected);
        Assert.Empty(result.Results);
        Assert.StartsWith($"selected 0 of {result.TotalSpecs} specs, executed 0", result.NothingExecutedMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Selected, then skipped at the describe gate, is as empty as matching nothing</b> — and the
    /// message says which of the two happened. The adapter here serves the roster domain alone
    /// (the reference adapter's <c>BSSPEC_TEST_ROSTER_ONLY</c> hook), so the one gamedata spec the
    /// filter selects is skipped without running, and the run exits 8.
    /// </summary>
    /// <remarks>
    /// End to end through <see cref="SpecSuiteRunner.RunAsync"/>, because what matters is where the
    /// runner counts <see cref="SpecSuiteResult.Selected"/>: before the gate. Falsifiable: count it
    /// after the gate (which empties the gamedata list) and the message reads "selected 0 … nothing
    /// matched the selection", sending the reader to their filter instead of to the adapter.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SelectedThenSkippedAtTheDescribeGate_ExitsEight_AndSaysTheyWereSkipped()
    {
        var dll = FindAdapterDll();
        var rosterOnly = new Dictionary<string, string> { ["BSSPEC_TEST_ROSTER_ONLY"] = "1" };

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["gamedata"],
            FilterPatterns = ["entry/add-entry-basic"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll, rosterOnly),
        });

        Assert.Equal(8, result.ExitCode);
        Assert.Empty(result.Results);
        Assert.Equal(1, result.Selected);
        var skipped = Assert.Single(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic");
        Assert.Equal("skipped", skipped.Status);
        Assert.Contains(skipped.Failures, f => f.Contains("does not support gamedata", StringComparison.Ordinal));
        Assert.StartsWith(
            $"selected 1 of {result.TotalSpecs} specs, executed 0: every selected spec was skipped",
            result.NothingExecutedMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Executed" is the result list, not <c>Passed + Failed</c>: an expected failure under
    /// <c>--expected-failures</c> is counted in neither, so a run whose every spec failed exactly as
    /// annotated has <c>Passed + Failed == 0</c> — and executed. It must stay a pass.
    /// </summary>
    /// <remarks>Falsifiable: define "nothing executed" as <c>Passed + Failed == 0</c> and this reads 8.</remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void EveryExecutedSpecAnExpectedFailure_StillExitsZero()
    {
        var spec = new SpecFile
        {
            Id = "xfail-demo",
            Category = "xfail",
            Description = "",
            Engines = new() { ["xfail-engine"] = "fail" },
            Setup = new SetupDef(),
            Steps = [],
        };
        var failed = new SpecResult("xfail-demo", "xfail", "", ["fails, as annotated"]);

        var result = SpecSuiteResult.Create(
            results: [failed], reportResults: [], specsByResult: new Dictionary<SpecResult, SpecFile> { [failed] = spec },
            gameDataSpecsByResult: new Dictionary<SpecResult, GameDataSpecFile>(),
            durationsByResult: new Dictionary<SpecResult, double>(),
            totalSpecs: 1, selected: 1, elapsed: TimeSpan.Zero, expectedFailuresEngine: "xfail-engine");

        Assert.Equal(0, result.Passed + result.Failed);
        Assert.Equal(1, result.ExpectedFailures);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.NothingExecutedMessage);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LegacyDefaultDomains_ExcludeGameData()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            // Domains left at its default (roster-only) — the Runner shell's exact current behavior.
            FilterPatterns = ["entry/add-entry-basic"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.DoesNotContain(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic");
    }
}
