using System.Text.Json;
using System.Text.RegularExpressions;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// <c>bs-spec run</c>, <c>run --all</c> and <c>compare</c> over scenario specs (<c>specs/scenario/</c>,
/// <c>docs/adr/002-scenario-specs.md</c>): one adapter process serving a data editor and a roster editor at
/// once. Out of process through <see cref="CliProcess"/>, like every test here that reads what the CLI
/// printed.
/// </summary>
public sealed class RunScenarioTests
{
    private static string ScenarioPath =>
        CliProcess.SpecPath("scenario", SpecResolutionTests.ScenarioCategory, SpecResolutionTests.ScenarioId + ".yaml");

    private const string ScenarioName = SpecResolutionTests.ScenarioCategory + "/" + SpecResolutionTests.ScenarioId;

    /// <summary>The reference adapter as the <c>battlescribe</c> identity, serving both domains (or only roster).</summary>
    private static string ReferenceAdapter => $"battlescribe=dotnet:{CliProcess.ReferenceAdapterDll}";

    private static readonly Dictionary<string, string> RosterOnly = new() { ["BSSPEC_TEST_ROSTER_ONLY"] = "1" };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_ScenarioSpec_OnBattlescribe_Passes()
    {
        Assert.True(File.Exists(ScenarioPath), $"Spec not found: {ScenarioPath}");

        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync("run", ScenarioPath, "--engine", "battlescribe");

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 0, $"exit code {exitCode}; output:\n{combined}");
        Assert.Contains($"Loaded scenario spec: {ScenarioName}", combined, StringComparison.Ordinal);
        Assert.Contains("PASS", combined, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--all-steps</c> dumps after every block step of both kinds, under the one global step index, and
    /// says which editor each one came from.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_ScenarioSpec_AllSteps_DumpsBothKindsOfBlock()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "run", SpecResolutionTests.ScenarioId, "--engine", "battlescribe", "--all-steps", "--json");

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 0, $"exit code {exitCode}; output:\n{combined}");
        Assert.Contains("Step 0: data setFields", combined, StringComparison.Ordinal);
        Assert.Contains("Step 3: roster addForce", combined, StringComparison.Ordinal);
    }

    /// <summary>
    /// The roster-only artifact flags follow one roster editor through a spec, and a scenario has two
    /// editors. Refused, before any engine is started — not accepted and left to write nothing.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("--save-roster", "out")]
    [InlineData("--screenshots", "shots")]
    [InlineData("--timeline", "t.html")]
    [InlineData("--record", "r.json")]
    [InlineData("--break", "1")]
    public async Task Run_ScenarioSpec_RefusesRosterOnlyArtifactFlags(string flag, string value)
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "run", ScenarioPath, "--engine", "battlescribe", flag, value);

        Assert.Equal(1, exitCode);
        Assert.Contains($"{flag} is not supported for scenario specs", stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Engine:", stdOut + stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdErr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Run_ScenarioWithRosterFlag_IsRefused()
    {
        var (exitCode, _, stdErr) = await CliProcess.RunAsync("run", ScenarioPath, "--scenario", "--roster");

        Assert.Equal(1, exitCode);
        Assert.Contains("--roster and --scenario are mutually exclusive", stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// An adapter that serves only the roster domain cannot run a scenario. A single-spec run says so and
    /// skips — never a pass, never an engine error halfway through the spec.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_ScenarioSpec_OnARosterOnlyAdapter_IsSkippedWithTheReason()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            ["run", ScenarioPath, "--engine", ReferenceAdapter], stdin: "", environment: RosterOnly);

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 0, $"exit code {exitCode}; output:\n{combined}");

        // The warning is wrapped at the console width; the words are what is asserted, not where it broke.
        var unwrapped = Regex.Replace(combined, @"\s+", " ");
        Assert.Contains("does not support both roster and gamedata", unwrapped, StringComparison.Ordinal);
        Assert.Contains("it does not advertise gamedata (skipped)", unwrapped, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAll_Scenario_OnBattlescribe_RunsTheScenarios()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "run", "--all", "--scenario", "--engine", "battlescribe", "--policy", "workers=1", "--output", "summary");

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 0, $"exit code {exitCode}; output:\n{combined}");
        Assert.Contains("Domains: scenario", stdErr, StringComparison.Ordinal);
        Assert.Contains($"[PASS] {ScenarioName}", stdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("[FAIL]", stdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>run --all</c> with no domain flag covers scenarios too. Falsifiable: leave the default domain list
    /// at roster and gamedata, and the filter below selects nothing (exit 8).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAll_NoDomainFlag_IncludesScenarios()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "run", "--all", "--engine", ReferenceAdapter, "--filter", ScenarioName);

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 0, $"exit code {exitCode}; output:\n{combined}");
        Assert.Contains("Domains: roster, gamedata, scenario", stdErr, StringComparison.Ordinal);
        Assert.Contains($"[PASS] {ScenarioName}", stdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A roster-only adapter skips every scenario with the reason</b>, and a run whose only selected spec
    /// was skipped executed nothing — exit 8, as for any empty run.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAll_Scenario_OnARosterOnlyAdapter_SkipsEveryScenarioWithTheReason()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"bsspec-scenario-skip-{Guid.NewGuid():N}.json");
        try
        {
            var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
                [
                    "run", "--all", "--scenario",
                    "--engine", ReferenceAdapter,
                    "--report", reportPath,
                ],
                stdin: "",
                environment: RosterOnly);

            var combined = stdOut + stdErr;
            Assert.True(exitCode == 8, $"exit code {exitCode}; output:\n{combined}");
            Assert.Contains("every selected spec was skipped", CliProcess.LastLine(stdErr), StringComparison.Ordinal);

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            var entries = report.RootElement.GetProperty("Results").EnumerateArray().ToList();
            Assert.NotEmpty(entries);
            Assert.All(entries, entry =>
            {
                Assert.Equal("scenario", entry.GetProperty("Domain").GetString());
                Assert.Equal("skipped", entry.GetProperty("Status").GetString());
                Assert.Contains(
                    entry.GetProperty("Failures").EnumerateArray(),
                    f => f.GetString()!.Contains("engine does not support both roster and gamedata", StringComparison.Ordinal));
            });
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_Scenario_IdenticalArms_VerdictsIdentical()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "compare",
            "--engine", ReferenceAdapter,
            "--scenario",
            "--filter", ScenarioName,
            "--config-a", "",
            "--config-b", "");

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 0, $"exit code {exitCode}; output:\n{combined}");
        Assert.Contains("Domains: scenario", combined, StringComparison.Ordinal);
        Assert.Contains("Verdicts identical", combined, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b><c>compare</c> keys verdicts by domain as well as <c>category/id</c>.</b> The spec trees share
    /// category names, so a roster and a scenario spec of one <c>category/id</c> are two specs — and keyed by
    /// <c>category/id</c> they were one key, which threw building the map.
    /// </summary>
    /// <remarks>Falsifiable: key <c>ToStatusMap</c> by <c>category/id</c> again and this throws.</remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void CompareStatusMap_SameCategoryAndIdInTwoDomains_AreTwoSpecs()
    {
        var suite = new SpecSuiteResult
        {
            Results = [],
            ReportResults =
            [
                new SpecResultSummary("shared-id", "cost", "", "passed", [], Domain: "roster"),
                new SpecResultSummary("shared-id", "cost", "", "failed", [], Domain: "scenario"),
            ],
            SpecsByResult = new Dictionary<SpecResult, SpecFile>(),
            GameDataSpecsByResult = new Dictionary<SpecResult, GameData.GameDataSpecFile>(),
            ScenarioSpecsByResult = new Dictionary<SpecResult, Scenario.ScenarioSpecFile>(),
            DurationsByResult = new Dictionary<SpecResult, double>(),
            TotalSpecs = 2,
            Elapsed = TimeSpan.Zero,
        };

        var statuses = CompareCommand.ToStatusMap(suite);

        Assert.Equal("passed", statuses["roster/cost/shared-id"]);
        Assert.Equal("failed", statuses["scenario/cost/shared-id"]);
    }
}
