using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.Messages;

namespace BattleScribeSpec.Tests.Profiles;

/// <summary>
/// <b>What one run of the test app executed, tallied per engine lane while it runs.</b> Only a result of
/// one of a lane's own classes (<see cref="EngineLane.LaneTests"/>) counts towards that lane; every
/// result counts towards the totals.
/// </summary>
/// <remarks>
/// <para>
/// Fed by a Microsoft.Testing.Platform data consumer that <see cref="Register"/> adds next to this
/// project's self-registered extensions, so the counting happens as results arrive and nothing is left
/// to do after the run but compare a few numbers: xunit's entry point starts a watchdog when the platform
/// returns, which prints at one second and kills the process at eleven.
/// </para>
/// <para>
/// A result is attributed by its <see cref="TestMethodIdentifierProperty"/> — the class that declares the
/// test — not by its <c>Engine</c> trait. The trait is shared with classes that are not the lane
/// (<see cref="EngineLanes.NotLaneTests"/>): six <c>Engine=FrozenNrUiRoster</c> regression facts drive a
/// blank page and pass on a machine with no HAR, while the lane they share the trait with skips whole.
/// Counted by trait, that lane would look executed.
/// </para>
/// </remarks>
internal sealed class LaneTally
{
    /// <summary>Which lane each lane class belongs to, from the registry.</summary>
    private static readonly IReadOnlyDictionary<string, string> LaneOfClass =
        EngineLanes.All.SelectMany(static l => l.LaneTests.Select(t => (Type: t, l.Trait)))
            .ToDictionary(static p => p.Type, static p => p.Trait, StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LaneCount> _lanes = new(StringComparer.Ordinal);
    private int _executed;
    private int _skipped;

    /// <summary>Every result that executed (passed, failed, errored, timed out or was cancelled), lane or not.</summary>
    public int Executed
    {
        get
        {
            lock (_gate)
            {
                return _executed;
            }
        }
    }

    /// <summary>Every result that skipped, lane or not.</summary>
    public int Skipped
    {
        get
        {
            lock (_gate)
            {
                return _skipped;
            }
        }
    }

    /// <summary>Adds the consumer that feeds this tally to the test platform being built.</summary>
    public void Register(ITestApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.TestHost.AddDataConsumer(_ => new Consumer(this));
    }

    /// <summary>Counts one final result.</summary>
    /// <param name="testClass">The full name of the class that declares the test, or <see langword="null"/> when the result carried none.</param>
    /// <param name="executed"><see langword="true"/> for a result that ran, <see langword="false"/> for a skip.</param>
    /// <param name="duration">How long it took, as the framework reported it.</param>
    public void Record(string? testClass, bool executed, TimeSpan duration)
    {
        lock (_gate)
        {
            if (executed)
            {
                _executed++;
            }
            else
            {
                _skipped++;
            }

            if (testClass is not null && LaneOfClass.TryGetValue(testClass, out var lane))
            {
                var count = _lanes.GetValueOrDefault(lane) ?? new LaneCount(0, 0, TimeSpan.Zero);
                _lanes[lane] = executed
                    ? count with { Executed = count.Executed + 1, Duration = count.Duration + duration }
                    : count with { Skipped = count.Skipped + 1 };
            }
        }
    }

    /// <summary>What <paramref name="lane"/>'s own classes did in this run.</summary>
    public LaneCount Of(string lane)
    {
        lock (_gate)
        {
            return _lanes.GetValueOrDefault(lane) ?? new LaneCount(0, 0, TimeSpan.Zero);
        }
    }

    /// <summary>The platform side: every final test result, handed to <see cref="Record"/>.</summary>
    private sealed class Consumer(LaneTally tally) : IDataConsumer
    {
        public Type[] DataTypesConsumed => [typeof(TestNodeUpdateMessage)];

        public string Uid => "BattleScribeSpec.LaneTally";

        public string Version => "1.0.0";

        public string DisplayName => "Engine-lane composition";

        public string Description => "Counts the executed and skipped results of each engine lane's own classes for the test-profile host.";

        public Task<bool> IsEnabledAsync() => Task.FromResult(true);

        public Task ConsumeAsync(IDataProducer dataProducer, IData value, CancellationToken cancellationToken)
        {
            if (value is TestNodeUpdateMessage { TestNode: var node })
            {
                bool? executed = node.Properties.SingleOrDefault<TestNodeStateProperty>() switch
                {
                    null or DiscoveredTestNodeStateProperty or InProgressTestNodeStateProperty => null, // not a result
                    SkippedTestNodeStateProperty => false,
                    _ => true, // passed, failed, errored, timed out: it ran
                };
                if (executed is { } ran)
                {
                    var method = node.Properties.SingleOrDefault<TestMethodIdentifierProperty>();
                    var testClass = method is null ? null : method.Namespace.Length == 0 ? method.TypeName : $"{method.Namespace}.{method.TypeName}";
                    tally.Record(testClass, ran, node.Properties.SingleOrDefault<TimingProperty>()?.GlobalTiming.Duration ?? TimeSpan.Zero);
                }
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>What one lane's own classes did in a run.</summary>
/// <param name="Executed">Results that ran.</param>
/// <param name="Skipped">Results that skipped.</param>
/// <param name="Duration">The summed duration of the results that ran.</param>
internal sealed record LaneCount(int Executed, int Skipped, TimeSpan Duration);

/// <summary>How the composition check came out.</summary>
internal enum CompositionVerdict
{
    /// <summary>Every lane the profile claims, and may not skip, executed at least one of its own tests.</summary>
    Passed,

    /// <summary>
    /// A lane the profile claims executed none of its own tests — or the run passed and none of its results
    /// reached the tally, so no lane can be shown to have run: the run exits 8.
    /// </summary>
    Failed,

    /// <summary>The check did not apply: the run failed by itself, a <c>--filter</c> narrowed it, or the profile claims no lane.</summary>
    NotChecked,
}

/// <summary>One lane's line in a <see cref="CompositionReport"/>.</summary>
/// <param name="Lane">The lane.</param>
/// <param name="Claimed">Whether the profile claims it in this assembly.</param>
/// <param name="MaySkip">Whether the profile lets it skip whole (<see cref="TestProfile.MaySkip"/>).</param>
/// <param name="Count">What its own classes did.</param>
internal sealed record LaneLine(EngineLane Lane, bool Claimed, bool MaySkip, LaneCount Count);

/// <summary>The engine-composition check of one profiled run, and what it makes the run exit with.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="Assembly">The test assembly that ran.</param>
/// <param name="RunExitCode">What the platform returned.</param>
/// <param name="ExitCode">What the app exits with: <paramref name="RunExitCode"/>, or 8 when the check failed.</param>
/// <param name="Verdict">How the check came out.</param>
/// <param name="Reason">Why, in a sentence.</param>
/// <param name="Lanes">Every lane the profile claims here, and every other lane whose own tests ran, in registry order.</param>
/// <param name="Empty">The claimed lanes, not allowed to skip, that executed none of their own tests.</param>
/// <param name="Executed">Every result that ran.</param>
/// <param name="Skipped">Every result that skipped.</param>
/// <param name="Narrowed">Whether a caller's <c>--filter</c> narrowed the profile.</param>
internal sealed record CompositionReport(
    TestProfile Profile,
    string Assembly,
    int RunExitCode,
    int ExitCode,
    CompositionVerdict Verdict,
    string Reason,
    IReadOnlyList<LaneLine> Lanes,
    IReadOnlyList<EngineLane> Empty,
    int Executed,
    int Skipped,
    bool Narrowed)
{
    /// <summary>
    /// The <see cref="Empty"/> lanes whose <see cref="EngineLane.EmptyHint"/> is printed: every one when the check
    /// failed, and when the run failed by itself with a claimed lane empty — the platform's own exit 8 says only
    /// that nothing ran, and a failing regression fact next to a skipped lane hides why it skipped.
    /// </summary>
    /// <remarks>
    /// A hint is a guess at why a lane's tests were not there to run or all skipped, and it names setup. So a lane
    /// that executed nothing for a reason the run already shows gets none: in a narrowed run, or one that stopped
    /// before its end (exit 3 aborted, 7 crashed, 13 the failure limit), a lane none of whose tests were selected
    /// or reached is the caller's <c>--filter</c> or the stop — only a lane whose tests were there and every one
    /// skipped is hinted. A narrowed run that failed for another reason gets none at all: the caller chose which
    /// lanes to reach.
    /// </remarks>
    public IReadOnlyList<EngineLane> Hinted =>
        Verdict == CompositionVerdict.Failed || (RunExitCode != 0 && (!Narrowed || RunExitCode == LaneComposition.ZeroTests))
            ? [.. Empty.Where(l => Lanes.Single(x => x.Lane == l).Count.Skipped > 0 || (!Narrowed && RanToItsEnd))]
            : [];

    /// <summary>
    /// Whether a caller's <c>--filter</c> narrowed the profile to nothing: the platform's exit 8 with not one
    /// result, executed or skipped. The cause is the filter, not any lane's setup, and the host says so.
    /// </summary>
    public bool FilterSelectedNothing => Narrowed && RunExitCode == LaneComposition.ZeroTests && Executed == 0 && Skipped == 0;

    /// <summary>
    /// Whether the platform ran the session to its end: passed (0), a test failed (2), or nothing executed (8).
    /// Any other code stopped it early, so a lane it never reached says nothing about the lane.
    /// </summary>
    private bool RanToItsEnd => RunExitCode is 0 or 2 or LaneComposition.ZeroTests;
}

/// <summary>
/// <b>The engine-composition check: a profiled run passes only if every engine lane its profile claims
/// executed at least one of its own tests.</b> <see cref="TestHost"/> runs it after the platform returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the strict zero-tests policy cannot see.</b> Strict fails a run that executed nothing at all.
/// <c>pre-push</c> claims six lanes and runs three thousand tests; on a machine where <c>setup.ps1</c> never
/// fetched the HAR, both frozen NR roster lanes skip whole and the run is still green, because everything
/// else executed. This check holds each claimed lane to the same rule strict holds the run to, and prints
/// the lane's <see cref="EngineLane.EmptyHint"/> — the fix — with exit 8, the platform's own code for "nothing
/// executed".
/// </para>
/// <para>
/// <b>Scoped so that a red means something.</b> Claims are the profile's <see cref="Selection.Claims"/>, in the
/// assembly every lane lives in (<see cref="EngineLanes.Assembly"/>); another assembly of the profile claims
/// nothing. A <see cref="Selection.Raw"/> profile claims only what it declares, so <c>lint</c> and
/// <c>non-conformance</c> claim nothing. A lane the profile lets skip (<see cref="TestProfile.MaySkip"/>) is
/// exempt. The check does not run for an IDE's session, an unprofiled run, a run that executes nothing by
/// design (<c>--list-tests</c>, <c>--help</c>, <c>--info</c>, <c>--xunit-list</c>), or a run a caller's
/// <c>--filter</c> narrowed — one spec through one lane need not reach the others — and it leaves a run that
/// failed by itself with its own exit code, still printing the hint of a claimed lane that came up empty
/// (<see cref="CompositionReport.Hinted"/>: never for a lane the caller's filter or an aborted session simply did
/// not reach, and a narrowed run that selected nothing is told that its filter matched nothing, not sent to
/// <c>setup.ps1</c>). A passing run none of whose results reached the tally fails too, and says it is the wiring
/// that broke rather than any lane: under the strict policy a passing run executed something.
/// </para>
/// <para>
/// After the check the host appends the verdict and the per-lane table to <c>$GITHUB_STEP_SUMMARY</c>, after
/// the run's trace summary, and writes <c>&lt;telemetry artifact&gt;.composition.json</c> next to the
/// telemetry artifact <c>TelemetryAssemblyFixture</c> names (<see cref="TestProfileContext.TelemetryArtifactBase"/>),
/// where <c>TelemetryRetention</c> sweeps it with the rest of the set.
/// </para>
/// </remarks>
internal static class LaneComposition
{
    /// <summary>What each line the check prints starts with.</summary>
    public const string Prefix = "[composition]";

    /// <summary>The platform's exit code for a run that executed nothing, used when a claimed lane executed nothing.</summary>
    public const int ZeroTests = 8;

    /// <summary>The suffix of the JSON record written next to the run's telemetry artifact.</summary>
    public const string JsonSuffix = ".composition.json";

    /// <summary>The lanes <paramref name="profile"/> claims in <paramref name="assembly"/>: its claims in the lanes' assembly, none elsewhere.</summary>
    public static IReadOnlyList<string> ClaimsIn(TestProfile profile, string assembly) =>
        string.Equals(assembly, EngineLanes.Assembly, StringComparison.Ordinal) ? profile.Selection.Claims : [];

    /// <summary>Checks one profiled run that executed tests.</summary>
    public static CompositionReport Check(TestProfile profile, string assembly, LaneTally tally, bool narrowed, int runExitCode)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tally);
        var claims = ClaimsIn(profile, assembly);
        var lines = EngineLanes.All
            .Select(l => new LaneLine(l, claims.Contains(l.Trait, StringComparer.Ordinal),
                profile.MaySkip.Any(m => m.Engine == l.Trait), tally.Of(l.Trait)))
            .Where(static l => l.Claimed || l.Count.Executed + l.Count.Skipped > 0)
            .ToList();
        // A run the platform passed executed at least one test (the policy is strict), so a tally that saw no result
        // at all was never fed: no lane can be shown to have run, and blaming each lane's setup would mislead.
        var unfed = runExitCode == 0 && tally.Executed == 0;
        List<EngineLane> empty = unfed ? [] : [.. lines.Where(static l => l.Claimed && !l.MaySkip && l.Count.Executed == 0).Select(static l => l.Lane)];

        var (verdict, reason) = (runExitCode, narrowed, claims.Count, empty.Count) switch
        {
            (0, _, _, _) when unfed => (CompositionVerdict.Failed, "the run passed, and not one of its results reached the lane tally: it is not "
                + "registered with the test platform (TestHost.RunAsync registers it after the project's own extensions), or the platform "
                + "no longer publishes results to data consumers — so no lane can be shown to have run"),
            (ZeroTests, true, _, _) when tally.Executed == 0 && tally.Skipped == 0 => (CompositionVerdict.NotChecked,
                $"the caller's --filter selected none of {profile.Name}'s tests in {assembly}, so nothing ran (exit {runExitCode})"),
            (not 0, _, _, _) => (CompositionVerdict.NotChecked, $"the run failed by itself (exit {runExitCode})"),
            (_, true, _, _) => (CompositionVerdict.NotChecked, "a --filter narrowed the profile, so the run need not reach every lane it claims"),
            (_, _, 0, _) => (CompositionVerdict.NotChecked, $"{profile.Name} claims no engine lane in {assembly}"),
            (_, _, _, > 0) => (CompositionVerdict.Failed, $"{string.Join(", ", empty.Select(static l => l.Trait))} executed none of {(empty.Count == 1 ? "its" : "their")} own tests"),
            _ => (CompositionVerdict.Passed, "every lane the profile claims executed at least one of its own tests"),
        };

        return new CompositionReport(profile, assembly, runExitCode, verdict == CompositionVerdict.Failed ? ZeroTests : runExitCode,
            verdict, reason, lines, empty, tally.Executed, tally.Skipped, narrowed);
    }

    /// <summary>What the host prints on the console: the hints to stderr when there are any, one verdict line otherwise.</summary>
    public static (IReadOnlyList<string> Out, IReadOnlyList<string> Error) ConsoleLines(CompositionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var where = $"{report.Profile.Name} ({report.Assembly})";
        if (report.Verdict == CompositionVerdict.Failed && report.Empty.Count == 0)
        {
            return ([], [$"{Prefix} {where}: {report.Reason}. The run fails with exit {ZeroTests}."]);
        }

        var hinted = report.Hinted;
        if (hinted.Count > 0)
        {
            var error = new List<string>
            {
                report.Verdict == CompositionVerdict.Failed
                    ? $"{Prefix} {where}: {report.Reason} — every one skipped, or none was selected — so the run fails with exit {ZeroTests}. "
                        + "A profile's run passes only when every lane it claims executed (tests/TestProfiles/LaneComposition.cs):"
                    : $"{Prefix} {where}: the run failed (exit {report.RunExitCode}), and {hinted.Count} lane(s) it claims executed none of their own tests:",
            };
            error.AddRange(hinted.Select(l => $"  {l.Trait}: {Describe(report.Lanes.Single(x => x.Lane == l).Count)} — {l.EmptyHint}"));
            return ([], error);
        }

        // A narrowed run that selected nothing: the platform says "zero tests ran", and the cause it cannot name is
        // the caller's own filter — not any lane's setup, so no lane is hinted.
        if (report.FilterSelectedNothing)
        {
            return ([], [$"{Prefix} {where}: {report.Reason}. Check the --filter you added."]);
        }

        var lanes = report.Lanes.Where(static l => l.Claimed).Select(static l => $"{l.Lane.Trait} {l.Count.Executed}{(l.MaySkip && l.Count.Executed == 0 ? " (may skip)" : "")}");
        return ([$"{Prefix} {where}: {VerdictWord(report.Verdict)} — {report.Reason}{(report.Verdict == CompositionVerdict.Passed ? $": {string.Join(", ", lanes)}" : "")}."], []);
    }

    /// <summary>The verdict line and the per-lane table, as GitHub step-summary markdown.</summary>
    public static string StepSummary(CompositionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"### Lane composition — {report.Profile.Name} ({report.Assembly})\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"**{VerdictWord(report.Verdict)}**{(report.Verdict == CompositionVerdict.Failed ? $" (exit {ZeroTests})" : "")}: {report.Reason}. ");
        sb.Append(CultureInfo.InvariantCulture, $"{report.Executed} test(s) executed and {report.Skipped} skipped in all.\n\n");
        if (report.Lanes.Count > 0)
        {
            sb.Append("| Lane | Claimed | Executed | Skipped | Time (s) |\n|---|---|---:|---:|---:|\n");
            foreach (var line in report.Lanes)
            {
                var claimed = !line.Claimed ? "no" : line.MaySkip ? "may skip" : "yes";
                var flag = report.Empty.Contains(line.Lane) ? " **(empty)**" : "";
                sb.Append(CultureInfo.InvariantCulture,
                    $"| {line.Lane.Trait}{flag} | {claimed} | {line.Count.Executed} | {line.Count.Skipped} | {line.Count.Duration.TotalSeconds:F1} |\n");
            }

            sb.Append('\n');
        }

        foreach (var lane in report.Hinted)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- **{lane.Trait}**: {lane.EmptyHint}\n");
        }

        return sb.Append('\n').ToString();
    }

    /// <summary>The machine-readable record of the check, written next to the run's telemetry artifact.</summary>
    public static string Json(CompositionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var record = new
        {
            profile = report.Profile.Name,
            assembly = report.Assembly,
            verdict = VerdictWord(report.Verdict),
            reason = report.Reason,
            runExitCode = report.RunExitCode,
            exitCode = report.ExitCode,
            narrowed = report.Narrowed,
            executed = report.Executed,
            skipped = report.Skipped,
            lanes = report.Lanes.Select(static l => new
            {
                engine = l.Lane.Trait,
                claimed = l.Claimed,
                maySkip = l.MaySkip,
                executed = l.Count.Executed,
                skipped = l.Count.Skipped,
                durationSeconds = Math.Round(l.Count.Duration.TotalSeconds, 3),
            }),
        };
        return JsonSerializer.Serialize(record, JsonOptions) + "\n";
    }

    private static string Describe(LaneCount count) =>
        count.Skipped == 0 ? "no lane test was selected" : $"0 of its lane tests executed, {count.Skipped} skipped";

    private static string VerdictWord(CompositionVerdict verdict) => verdict switch
    {
        CompositionVerdict.Passed => "passed",
        CompositionVerdict.Failed => "failed",
        _ => "not checked",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
