using System.Diagnostics;
using BattleScribeSpec.Batch;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Scenario;

namespace BattleScribeSpec.Tests.Features;

public sealed class SpecSuiteRunnerTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FilteredSuite_RunsAgainstReferenceAdapter()
    {
        var dll = AdapterTestHost.ReferenceAdapterDll;

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
        var dll = AdapterTestHost.ReferenceAdapterDll;

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
        var dll = AdapterTestHost.ReferenceAdapterDll;

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
        var dll = AdapterTestHost.ReferenceAdapterDll;

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
        var dll = AdapterTestHost.ReferenceAdapterDll;
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
            scenarioSpecsByResult: new Dictionary<SpecResult, ScenarioSpecFile>(),
            durationsByResult: new Dictionary<SpecResult, double>(),
            totalSpecs: 1, selected: 1, elapsed: TimeSpan.Zero, expectedFailuresEngine: "xfail-engine");

        Assert.Equal(0, result.Passed + result.Failed);
        Assert.Equal(1, result.ExpectedFailures);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.NothingExecutedMessage);
    }

    private const string ScenarioFilter = "handoff/handoff-saved-edit-reaches-new-roster";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScenarioDomain_RunsOverTheSameAdapterPool()
    {
        var dll = AdapterTestHost.ReferenceAdapterDll;

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["roster", "gamedata", "scenario"],
            FilterPatterns = [ScenarioFilter],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, result.ExitCode);
        var scenario = Assert.Single(result.ReportResults, r => r.Status != "skipped");
        Assert.Equal(("scenario", "handoff", "handoff-saved-edit-reaches-new-roster", "passed"),
            (scenario.Domain, scenario.Category, scenario.SpecId, scenario.Status));
        Assert.Single(result.ScenarioSpecsByResult);
    }

    /// <summary>
    /// Parallel workers hand each scenario one pooled process for both of its editors, alongside roster and
    /// gamedata specs in the same pool.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AllThreeDomains_ParallelWorkers_RunEveryDomain()
    {
        var dll = AdapterTestHost.ReferenceAdapterDll;

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["roster", "gamedata", "scenario"],
            Workers = 2,
            FilterPatterns = ["protocol/protocol-kitchen-sink", "entry/add-entry-basic", ScenarioFilter],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Domain == "roster" && r.SpecId == "protocol-kitchen-sink" && r.Status == "passed");
        Assert.Contains(result.ReportResults, r => r.Domain == "gamedata" && r.SpecId == "add-entry-basic" && r.Status == "passed");
        Assert.Contains(result.ReportResults, r => r.Domain == "scenario" && r.SpecId == "handoff-saved-edit-reaches-new-roster" && r.Status == "passed");
    }

    /// <summary>
    /// <b>A scenario on an adapter that serves only the roster domain is skipped with the reason, never run.</b>
    /// It drives a data editor too, and an adapter that has none would fail it halfway with an engine error —
    /// or worse, a gate that checked only "roster" would let it through.
    /// </summary>
    /// <remarks>Falsifiable: gate scenarios on "roster" alone and this executes the spec (and fails it).</remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScenarioOnARosterOnlyAdapter_IsSkippedAtTheDescribeGate()
    {
        var dll = AdapterTestHost.ReferenceAdapterDll;
        var rosterOnly = new Dictionary<string, string> { ["BSSPEC_TEST_ROSTER_ONLY"] = "1" };

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["scenario"],
            FilterPatterns = [ScenarioFilter],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll, rosterOnly),
        });

        Assert.Equal(8, result.ExitCode);
        Assert.Empty(result.Results);
        Assert.Equal(1, result.Selected);
        var skipped = Assert.Single(result.ReportResults, r => r.SpecId == "handoff-saved-edit-reaches-new-roster");
        Assert.Equal(("scenario", "skipped"), (skipped.Domain, skipped.Status));
        Assert.Equal([SpecSuiteRunner.ScenarioUnsupportedReason], skipped.Failures);
        Assert.Contains("engine does not support both roster and gamedata", SpecSuiteRunner.ScenarioUnsupportedReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Under an explicit specs root, the other domains' subtrees are not roster specs.</b> Roster discovery
    /// used to take every yaml under the explicit directory, so <c>SpecsDirectory = specs/</c> found
    /// <c>specs/gamedata/entry/add-entry-basic.yaml</c> as the roster spec <c>entry/add-entry-basic</c> and ran
    /// a gamedata file as the wrong model. Asked for the roster domain alone, that id must not exist at all,
    /// and the roster specs found are exactly <c>specs/roster</c>'s.
    /// </summary>
    /// <remarks>Falsifiable: discover roster over the whole explicit directory again and this selects one spec and fails it.</remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExplicitSpecsRoot_RosterDomain_DoesNotSweepInOtherDomainsFiles()
    {
        var dll = AdapterTestHost.ReferenceAdapterDll;
        var specsRoot = Path.Combine(TestPaths.Root, "specs");

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            SpecsDirectory = specsRoot,
            Domains = ["roster"],
            FilterPatterns = ["entry/add-entry-basic"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(SpecSuiteResult.NothingExecutedExitCode, result.ExitCode);
        Assert.Equal(0, result.Selected);
        Assert.Equal(Directory.EnumerateFiles(Path.Combine(specsRoot, "roster"), "*.yaml", SearchOption.AllDirectories).Count(), result.TotalSpecs);
        Assert.All(result.ReportResults, r => Assert.Equal("roster", r.Domain));
    }

    /// <summary>
    /// The same explicit root still serves every domain from its own subtree — a scenario selected under
    /// <c>SpecsDirectory = specs/</c> runs — and a roster tree passed directly (<c>specs/roster</c>) is still the
    /// whole roster tree. It has a category named <c>roster</c>, which is why "a <c>roster</c> subdirectory
    /// means a specs root" would have narrowed it to that one category.
    /// </summary>
    /// <remarks>
    /// Falsifiable: read a <c>roster</c> subdirectory as the roster tree and the second run finds only
    /// <c>specs/roster/roster</c>, so the kitchen-sink is not there to pass.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExplicitSpecsRoot_ServesEachDomain_AndARosterTreeStaysARosterTree()
    {
        var dll = AdapterTestHost.ReferenceAdapterDll;
        var specsRoot = Path.Combine(TestPaths.Root, "specs");

        var fromRoot = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            SpecsDirectory = specsRoot,
            Domains = ["roster", "gamedata", "scenario"],
            FilterPatterns = [ScenarioFilter, "protocol/protocol-kitchen-sink"],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, fromRoot.ExitCode);
        Assert.Contains(fromRoot.ReportResults, r => r.Domain == "scenario" && r.SpecId == "handoff-saved-edit-reaches-new-roster" && r.Status == "passed");
        Assert.Contains(fromRoot.ReportResults, r => r.Domain == "roster" && r.SpecId == "protocol-kitchen-sink" && r.Status == "passed");

        var rosterTree = Path.Combine(specsRoot, "roster");
        var fromRosterTree = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            SpecsDirectory = rosterTree,
            Domains = ["roster", "gamedata", "scenario"],
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, fromRosterTree.ExitCode);
        Assert.Contains(fromRosterTree.ReportResults, r => r.Domain == "roster" && r.SpecId == "protocol-kitchen-sink" && r.Status == "passed");
        Assert.DoesNotContain(fromRosterTree.ReportResults, r => r.Domain is "gamedata" or "scenario");
        Assert.Equal(
            Directory.EnumerateFiles(rosterTree, "*.yaml", SearchOption.AllDirectories).Count(),
            fromRosterTree.TotalSpecs);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LegacyDefaultDomains_ExcludeGameData()
    {
        var dll = AdapterTestHost.ReferenceAdapterDll;

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            // Domains left at its default (roster-only) — the Runner shell's exact current behavior.
            FilterPatterns = ["entry/add-entry-basic"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.DoesNotContain(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic");
    }
}
