using System.Text.Json;
using BattleScribeSpec.Tests.Profiles;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The engine-composition check (<see cref="LaneComposition"/>), driven through <see cref="TestHost"/>:</b> a
/// profiled run whose claimed lane executed none of its own tests exits 8, naming the lane and its fix; a run
/// the check does not apply to keeps its exit code; and what the host writes afterwards says the same.
/// </summary>
/// <remarks>
/// <para>
/// Each case hands <see cref="TestHost.RunAsync(string[], string, Func{string[], LaneTally, Task{int}}, HostIo)"/>
/// a platform stand-in that feeds the <see cref="LaneTally"/> it is given, as the real data consumer does while
/// a session runs.
/// </para>
/// <para>
/// Mutation-checked when written: the empty-lane rule deleted from <see cref="LaneComposition.Check"/> (the
/// failing cases go green, so they go red here); results counted by <c>Engine</c> trait instead of by lane
/// class (<see cref="NonLaneResults_DoNotCountForTheLane"/>); the narrowed exemption deleted and the list-mode
/// exemption deleted (<see cref="NoFalseRed"/>); <c>MaySkip</c> ignored (the <c>core</c> row of
/// <see cref="NoFalseRed"/>); every empty lane hinted again whatever the run reached, and the narrowed run that
/// selected nothing no longer named as such (<see cref="ALaneTheRunNeverReached_GetsNoSetupHint"/>).
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class LaneCompositionTests
{
    private const string StepSummary = "/ci/step-summary.md";
    private const string ArtifactBase = "/telemetry/xunit-run";

    /// <summary>
    /// A platform stand-in for <paramref name="profile"/>: one result per lane it claims — executed, or a skip for
    /// each lane in <paramref name="empty"/> — then <paramref name="exit"/>.
    /// </summary>
    private static Func<string[], LaneTally, Task<int>> Platform(string profile, int exit = 0, params string[] empty) => (_, tally) =>
    {
        foreach (var lane in TestProfiles.Find(profile)!.Selection.Claims.Select(EngineLanes.Find).OfType<EngineLane>())
        {
            tally.Record(lane.LaneTests[0], executed: !empty.Contains(lane.Trait), TimeSpan.FromSeconds(2));
        }

        tally.Record("BattleScribeSpec.Tests.SpecLintTests", executed: true, TimeSpan.FromMilliseconds(5));
        return Task.FromResult(exit);
    };

    private static TestHostTests.RecordingIo World()
    {
        var world = new TestHostTests.RecordingIo { ArtifactBase = ArtifactBase };
        world.Environment["GITHUB_STEP_SUMMARY"] = StepSummary;
        return world;
    }

    private static string Written(TestHostTests.RecordingIo world, string path) =>
        Assert.Single(world.Files, f => f.Path == path).Text;

    /// <summary><b>A profiled run whose every claimed lane executed passes</b>, saying so, with its table and its record.</summary>
    [Fact]
    public async Task ClaimedLanesThatAllExecuted_Pass()
    {
        var world = World();
        Assert.Equal(0, await TestHost.RunAsync(["--test-profile", "pre-push"], TestProfiles.Tests, Platform("pre-push"), world.Io));

        Assert.Contains($"{LaneComposition.Prefix} pre-push ({TestProfiles.Tests}): passed — every lane the profile claims executed at least one "
            + "of its own tests: BsRoster 1, BsGameData 1, FrozenNrRoster 1, FrozenNrGameData 1, FrozenNrUiRoster 1, FrozenNrGameDataUi 1.",
            world.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal("", world.Error.ToString());

        var summary = Written(world, StepSummary);
        Assert.StartsWith($"### Lane composition — pre-push ({TestProfiles.Tests})\n\n**passed**:", summary, StringComparison.Ordinal);
        Assert.Contains("| FrozenNrUiRoster | yes | 1 | 0 | 2.0 |", summary, StringComparison.Ordinal);
        Assert.True(world.Files.Single(f => f.Path == StepSummary).Append);

        using var record = JsonDocument.Parse(Written(world, ArtifactBase + LaneComposition.JsonSuffix));
        Assert.Equal("passed", record.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(6, record.RootElement.GetProperty("lanes").GetArrayLength());
        Assert.Equal(7, record.RootElement.GetProperty("executed").GetInt32());
    }

    /// <summary>
    /// <b>A claimed lane that executed none of its own tests fails the run with 8</b>, naming each empty lane and
    /// its fix — the machine where <c>setup.ps1</c> never fetched the HAR, both frozen NR roster lanes skipping
    /// while three thousand other tests pass.
    /// </summary>
    [Fact]
    public async Task AClaimedLaneThatExecutedNothing_FailsTheRunWith8()
    {
        var world = World();
        Assert.Equal(LaneComposition.ZeroTests,
            await TestHost.RunAsync(["--test-profile", "pre-push"], TestProfiles.Tests, Platform("pre-push", 0, "FrozenNrRoster", "FrozenNrUiRoster"), world.Io));

        var error = world.Error.ToString();
        Assert.Contains($"{LaneComposition.Prefix} pre-push ({TestProfiles.Tests}): FrozenNrRoster, FrozenNrUiRoster executed none of their own tests", error, StringComparison.Ordinal);
        Assert.Contains($"  FrozenNrRoster: 0 of its lane tests executed, 1 skipped — {EngineLanes.Find("FrozenNrRoster")!.EmptyHint}", error, StringComparison.Ordinal);
        Assert.Contains($"  FrozenNrUiRoster: 0 of its lane tests executed, 1 skipped — {EngineLanes.Find("FrozenNrUiRoster")!.EmptyHint}", error, StringComparison.Ordinal);
        Assert.Contains("run ./setup.ps1", error, StringComparison.Ordinal);
        Assert.DoesNotContain("BsRoster:", error, StringComparison.Ordinal);

        var summary = Written(world, StepSummary);
        Assert.Contains("**failed** (exit 8)", summary, StringComparison.Ordinal);
        Assert.Contains("| FrozenNrRoster **(empty)** | yes | 0 | 1 |", summary, StringComparison.Ordinal);
        Assert.Contains("- **FrozenNrUiRoster**: run ./setup.ps1", summary, StringComparison.Ordinal);

        using var record = JsonDocument.Parse(Written(world, ArtifactBase + LaneComposition.JsonSuffix));
        Assert.Equal("failed", record.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(0, record.RootElement.GetProperty("runExitCode").GetInt32());
        Assert.Equal(8, record.RootElement.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// <b>A passing run whose results never reached the tally fails with 8, naming the wiring</b> — not each lane's
    /// setup: under the strict policy a passing run executed something, so a tally that saw nothing was never fed.
    /// That is what a host that stopped registering it would look like, for every profile, <c>lint</c> included.
    /// </summary>
    [Theory]
    [InlineData("pre-push")]
    [InlineData("lint")]
    public async Task APassingRunTheTallyNeverSaw_FailsWith8(string profile)
    {
        var world = World();
        Assert.Equal(LaneComposition.ZeroTests,
            await TestHost.RunAsync(["--test-profile", profile], TestProfiles.Tests, static (_, _) => Task.FromResult(0), world.Io));

        var error = world.Error.ToString();
        Assert.Contains($"{LaneComposition.Prefix} {profile} ({TestProfiles.Tests}): the run passed, and not one of its results reached the lane tally", error, StringComparison.Ordinal);
        Assert.Contains("The run fails with exit 8.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("setup.ps1", error, StringComparison.Ordinal);
        Assert.Contains("**failed** (exit 8)", Written(world, StepSummary), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Only a lane's own classes count for it</b>: the <c>Engine=FrozenNrUiRoster</c> regression facts drive a blank
    /// page and pass with no HAR, and must not make the lane they share a trait with look executed.
    /// </summary>
    [Fact]
    public async Task NonLaneResults_DoNotCountForTheLane()
    {
        var world = World();
        var exit = await TestHost.RunAsync(["--test-profile", "nr-ui-frozen"], TestProfiles.Tests, (_, tally) =>
        {
            foreach (var (type, _) in EngineLanes.NotLaneTests.Where(static n => n.Type.Contains(".NrUi", StringComparison.Ordinal)))
            {
                tally.Record(type, executed: true, TimeSpan.FromSeconds(1));
            }

            tally.Record("BattleScribeSpec.Tests.FrozenNrUiRosterConformanceTests", executed: false, TimeSpan.Zero);
            return Task.FromResult(0);
        }, world.Io);

        Assert.Equal(LaneComposition.ZeroTests, exit);
        Assert.Contains("FrozenNrUiRoster: 0 of its lane tests executed, 1 skipped", world.Error.ToString(), StringComparison.Ordinal);

        var tally = new LaneTally();
        tally.Record("BattleScribeSpec.Tests.Regression.NrUiActionFailureMessageRegressionTests", executed: true, TimeSpan.FromSeconds(1));
        Assert.Equal(new LaneCount(0, 0, TimeSpan.Zero), tally.Of("FrozenNrUiRoster"));
        Assert.Equal(1, tally.Executed);
    }

    /// <summary>
    /// <b>No false reds</b>: the check passes a run it does not apply to through with the run's own exit code — a
    /// listing, help or information; a run a caller's <c>--filter</c> narrowed; a profile that claims no lane; a
    /// lane the profile lets skip; another assembly of a profile; an unprofiled run; an IDE's session.
    /// </summary>
    [Theory]
    [InlineData("--test-profile pre-push --list-tests", TestProfiles.Tests, "pre-push", false)]
    [InlineData("--test-profile pre-push --info", TestProfiles.Tests, "pre-push", false)]
    [InlineData("--test-profile pre-push --xunit-list traits", TestProfiles.Tests, "pre-push", false)]
    [InlineData("--test-profile pre-push --filter Engine!=FrozenNrUiRoster", TestProfiles.Tests, "pre-push", true)]
    [InlineData("--test-profile non-conformance", TestProfiles.Tests, "non-conformance", true)]
    [InlineData("--test-profile lint", TestProfiles.Tests, "lint", true)]
    [InlineData("--test-profile core", TestProfiles.Tests, "core", true)]
    [InlineData("--test-profile pre-push", TestProfiles.Cli, "pre-push", true)]
    [InlineData("--filter Engine=FrozenNrUiRoster", TestProfiles.Tests, null, false)]
    [InlineData("--server --client-port 1", TestProfiles.Tests, null, false)]
    public async Task NoFalseRed(string commandLine, string assembly, string? feeds, bool reports)
    {
        var world = World();
        var platform = feeds is null ? UnprofiledPlatform : Platform(feeds, 0, "FrozenNrUiRoster", "BsRosterUi");

        Assert.Equal(0, await TestHost.RunAsync(commandLine.Split(' '), assembly, platform, world.Io));
        Assert.Equal("", world.Error.ToString());
        Assert.Equal(reports, world.Files.Count > 0);
        if (reports)
        {
            using var record = JsonDocument.Parse(Written(world, ArtifactBase + LaneComposition.JsonSuffix));
            Assert.Equal(feeds == "core" ? "passed" : "not checked", record.RootElement.GetProperty("verdict").GetString());
        }

        static Task<int> UnprofiledPlatform(string[] args, LaneTally tally)
        {
            tally.Record("BattleScribeSpec.Tests.FrozenNrUiRosterConformanceTests", executed: false, TimeSpan.Zero);
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// <b>A run that failed by itself keeps its exit code</b>, and when a lane it claims is empty the hints are
    /// printed anyway — the platform's own exit 8 says only that nothing ran, and a failing test beside a skipped
    /// lane hides why the lane skipped. A narrowed run that failed for another reason gets no hints.
    /// </summary>
    [Theory]
    [InlineData("--test-profile nr-ui-frozen", 2, true)]
    [InlineData("--test-profile nr-ui-frozen", 8, true)]
    [InlineData("--test-profile nr-ui-frozen --filter DisplayName~AllSpecs", 8, true)]
    [InlineData("--test-profile nr-ui-frozen --filter DisplayName~AllSpecs", 2, false)]
    public async Task ARunThatFailedByItself_KeepsItsExitCode(string commandLine, int exit, bool hints)
    {
        var world = World();
        Assert.Equal(exit, await TestHost.RunAsync(commandLine.Split(' '), TestProfiles.Tests, Platform("nr-ui-frozen", exit, "FrozenNrUiRoster"), world.Io));
        Assert.Equal(hints, world.Error.ToString().Contains($"FrozenNrUiRoster: 0 of its lane tests executed, 1 skipped — {EngineLanes.Find("FrozenNrUiRoster")!.EmptyHint}", StringComparison.Ordinal));
        Assert.Contains($"the run failed by itself (exit {exit})", Written(world, StepSummary), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A lane the run never reached gets no setup hint</b> when the run already says why: in a narrowed run the
    /// caller's filter chose its tests away, and an aborted session (exit 3: <c>--timeout</c>, Ctrl+C) stopped before
    /// reaching them. Only a lane whose tests were there and every one skipped is hinted — and a filter that selected
    /// nothing at all is named as the cause, instead of six lanes' worth of "run ./setup.ps1" for a typo.
    /// </summary>
    [Theory]
    [InlineData("--test-profile pre-push --filter DisplayName~zzz-no-such-spec", 8, false)]
    [InlineData("--test-profile pre-push --filter DisplayName~protocol-duplicate-force", 8, true)]
    [InlineData("--test-profile pre-push", 3, true)]
    public async Task ALaneTheRunNeverReached_GetsNoSetupHint(string commandLine, int exit, bool frozenRosterSkipped)
    {
        var world = World();
        Assert.Equal(exit, await TestHost.RunAsync(commandLine.Split(' '), TestProfiles.Tests, (_, tally) =>
        {
            if (frozenRosterSkipped)
            {
                tally.Record("BattleScribeSpec.Tests.FrozenNrRosterConformanceTests", executed: false, TimeSpan.Zero);
            }

            return Task.FromResult(exit);
        }, world.Io));

        var error = world.Error.ToString();
        foreach (var unreached in new[] { "BsRoster", "BsGameData", "FrozenNrGameData", "FrozenNrUiRoster", "FrozenNrGameDataUi" })
        {
            Assert.DoesNotContain($"  {unreached}:", error, StringComparison.Ordinal);
        }

        if (frozenRosterSkipped)
        {
            Assert.Contains($"the run failed (exit {exit}), and 1 lane(s) it claims executed none of their own tests:", error, StringComparison.Ordinal);
            Assert.Contains($"  FrozenNrRoster: 0 of its lane tests executed, 1 skipped — {EngineLanes.Find("FrozenNrRoster")!.EmptyHint}", error, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(
                $"{LaneComposition.Prefix} pre-push ({TestProfiles.Tests}): the caller's --filter selected none of pre-push's tests in "
                + $"{TestProfiles.Tests}, so nothing ran (exit 8). Check the --filter you added.",
                Assert.Single(error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)));
            Assert.DoesNotContain("setup.ps1", Written(world, StepSummary), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>A summary or record that cannot be written is a warning, never a changed verdict</b> — and with no
    /// <c>$GITHUB_STEP_SUMMARY</c> and no telemetry artifact, nothing is written at all.
    /// </summary>
    [Fact]
    public async Task ReportingFailures_NeverChangeTheVerdict()
    {
        var world = World();
        var io = world.Io with { AppendText = static (_, _) => throw new IOException("disk full") };
        Assert.Equal(0, await TestHost.RunAsync(["--test-profile", "bs"], TestProfiles.Tests, Platform("bs"), io));
        Assert.Contains($"{LaneComposition.Prefix} could not write $GITHUB_STEP_SUMMARY ({StepSummary}): disk full", world.Error.ToString(), StringComparison.Ordinal);

        var quiet = new TestHostTests.RecordingIo();
        Assert.Equal(0, await TestHost.RunAsync(["--test-profile", "bs"], TestProfiles.Tests, Platform("bs"), quiet.Io));
        Assert.Empty(quiet.Files);
        Assert.Contains($"{LaneComposition.Prefix} bs ({TestProfiles.Tests}): passed", quiet.Out.ToString(), StringComparison.Ordinal);
    }
}
