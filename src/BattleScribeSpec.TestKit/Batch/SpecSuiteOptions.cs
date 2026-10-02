using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Batch;

public sealed class SpecSuiteOptions
{
    /// <summary>Specs directory; null → SpecLoader.FindRosterSpecsDirectory() then embedded fallback.</summary>
    public string? SpecsDirectory { get; init; }
    public IReadOnlyList<string>? FilterPatterns { get; init; }
    public TagFilter? TagFilter { get; init; }
    public string? EngineFilter { get; init; }
    public string? ExpectedFailuresEngine { get; init; }
    public string? AssertionEngine { get; init; }
    public int Workers { get; init; } = 1;
    /// <summary>
    /// Creates one adapter process per worker; the argument is the zero-based worker index.
    /// Disposed by the runner. The index lets callers give each child a distinct identity —
    /// a per-worker diagnostics directory, a worker tag on its telemetry.
    /// </summary>
    public required Func<int, AdapterProcess> AdapterFactory { get; init; }

    /// <summary>
    /// Spec domains to discover and run. Defaults to roster-only so existing callers (the
    /// `bs-spec run` CLI command) keep their exact current behavior without passing this at all.
    /// Include <c>"gamedata"</c> to additionally discover and run GameData specs over the same
    /// adapter pool. Domain discovery rule when <see cref="SpecsDirectory"/> is set explicitly:
    /// see <see cref="SpecSuiteRunner"/>'s remarks.
    /// </summary>
    public IReadOnlyList<string> Domains { get; init; } = ["roster"];

    /// <summary>
    /// Caps the number of adapter-process deaths tolerated across the whole run (every worker
    /// shares one budget) before <see cref="SpecSuiteRunner"/>'s recovery policy — retry the dying
    /// spec once on a fresh replacement process — stops replacing dead processes and fails the
    /// remainder outright with a message naming the cap. Default 3: an engine that dies
    /// deterministically must not respawn forever, but a couple of independent crashes across a
    /// long batch (hundreds of specs under warm-reuse) are worth rescuing — 3 clears genuine flakes
    /// with room to spare while still catching a systemic crash quickly (the incident that motivated
    /// this policy was a SINGLE crash cascading into 98 of 102 specs failing).
    /// </summary>
    public int MaxAdapterDeaths { get; init; } = 3;
}

public sealed class SpecSuiteResult
{
    public required IReadOnlyList<SpecResult> Results { get; init; }
    public required IReadOnlyList<SpecResultSummary> ReportResults { get; init; }
    public required IReadOnlyDictionary<SpecResult, SpecFile> SpecsByResult { get; init; }

    /// <summary>
    /// GameData spec results, parallel to <see cref="SpecsByResult"/>. Kept as a separate map
    /// (rather than widening <see cref="SpecsByResult"/>'s value type) so the existing public
    /// shape stays backward compatible — <see cref="GameDataSpecFile"/> and <see cref="SpecFile"/>
    /// are different types with no shared non-abstract base exposing the roster-specific shape.
    /// Empty when the suite's <see cref="SpecSuiteOptions.Domains"/> didn't include "gamedata".
    /// </summary>
    public required IReadOnlyDictionary<SpecResult, GameDataSpecFile> GameDataSpecsByResult { get; init; }

    /// <summary>
    /// Wall-clock duration (milliseconds) of each executed spec, keyed the same way as
    /// <see cref="SpecsByResult"/> and <see cref="GameDataSpecsByResult"/>. Kept as a side map
    /// (rather than widening <see cref="SpecResult"/> itself) for the same reason those two are
    /// separate: <see cref="SpecResult"/> is a public record shape callers already depend on.
    /// Absent (no entry) for specs never executed — skipped or failed to load.
    /// </summary>
    public required IReadOnlyDictionary<SpecResult, double> DurationsByResult { get; init; }

    public required int TotalSpecs { get; init; }

    /// <summary>
    /// Specs that survived every selection filter (<c>--filter</c>, <c>--tags</c>, engine
    /// applicability) and were therefore meant to run. It can exceed what executed: a gamedata spec
    /// on an adapter that does not serve that domain is selected and then skipped.
    /// </summary>
    public int Selected { get; private init; }

    public required TimeSpan Elapsed { get; init; }
    public int Passed { get; private init; }
    public int Failed { get; private init; }
    public int ExpectedFailures { get; private init; }
    public int UnexpectedPasses { get; private init; }

    /// <summary>
    /// The exit code a batch run reports when nothing executed — the same 8 Microsoft.Testing.Platform
    /// uses for "zero tests ran", so an empty lane reads the same whichever harness ran it.
    /// </summary>
    public const int NothingExecutedExitCode = 8;

    /// <summary>
    /// 1 if any spec failed (load errors and unexpected passes included); otherwise
    /// <see cref="NothingExecutedExitCode"/> if no spec executed; otherwise 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A run that checks nothing does not pass.</b> This used to be <c>Failed &gt; 0 ? 1 : 0</c>,
    /// and the only emptiness check — "no spec files found" — runs before any filter. So a
    /// <c>--filter</c> with a typo, a <c>--specs</c> pointing at the wrong tree or a domain the adapter
    /// does not serve printed <c>Results: 0 passed, 0 failed, 0 total</c> and exited 0, which a CI step
    /// reads exactly as it reads a green suite.
    /// </para>
    /// <para>
    /// <b>"Executed" is <see cref="Results"/>, not <c>Passed + Failed</c>.</b> Under
    /// <c>--expected-failures</c> an expected failure is counted in neither, so a run whose every
    /// spec failed as annotated has <c>Passed + Failed == 0</c> and still executed every one of them.
    /// <see cref="Results"/> holds one entry per spec that ran (and per load error, which is a failure
    /// and decided above); skips never enter it.
    /// </para>
    /// </remarks>
    public int ExitCode => Failed > 0 ? 1 : Results.Count == 0 ? NothingExecutedExitCode : 0;

    /// <summary>
    /// The one-line explanation of <see cref="NothingExecutedExitCode"/>, or null when specs executed.
    /// It starts from the number every reader needs first — selected versus executed — and then says
    /// which knob to look at.
    /// </summary>
    public string? NothingExecutedMessage => Failed > 0 || Results.Count > 0
        ? null
        : Selected == 0
            ? $"selected 0 of {TotalSpecs} specs, executed 0: nothing matched the selection " +
              "(--filter, --tags, --specs, the domain flags, and which specs apply to this engine). " +
              $"A batch run that checks nothing fails (exit {NothingExecutedExitCode})."
            : $"selected {Selected} of {TotalSpecs} specs, executed 0: every selected spec was skipped " +
              $"(the skip reasons are in the report). A batch run that checks nothing fails (exit {NothingExecutedExitCode}).";

    /// <summary>Engine name used for spec-level expected-failure classification (null when unused).</summary>
    internal string? ExpectedFailuresEngine { get; private init; }

    /// <summary>
    /// Computes the passed/failed/expected-failure/unexpected-pass counts once, running the same
    /// logic the Runner used inline (former Program.cs lines 325–356).
    /// </summary>
    internal static SpecSuiteResult Create(
        IReadOnlyList<SpecResult> results,
        IReadOnlyList<SpecResultSummary> reportResults,
        IReadOnlyDictionary<SpecResult, SpecFile> specsByResult,
        IReadOnlyDictionary<SpecResult, GameDataSpecFile> gameDataSpecsByResult,
        IReadOnlyDictionary<SpecResult, double> durationsByResult,
        int totalSpecs,
        int selected,
        TimeSpan elapsed,
        string? expectedFailuresEngine)
    {
        var passed = results.Count(r => r.Passed);
        int failed;
        var expectedFailureCount = 0;
        var unexpectedPassCount = 0;
        if (expectedFailuresEngine is not null)
        {
            failed = 0;
            foreach (var r in results)
            {
                SpecFileBase? spec = specsByResult.TryGetValue(r, out var s) ? s
                    : gameDataSpecsByResult.TryGetValue(r, out var gs) ? gs
                    : null;
                var isExpectedFail = spec?.IsExpectedToFail(expectedFailuresEngine) ?? false;
                if (!r.Passed && !isExpectedFail)
                {
                    failed++;
                }

                if (!r.Passed && isExpectedFail)
                {
                    expectedFailureCount++;
                }

                if (r.Passed && isExpectedFail)
                {
                    unexpectedPassCount++;
                }
            }

            failed += unexpectedPassCount; // Unexpected passes count as failures
        }
        else
        {
            failed = results.Count(r => !r.Passed);
        }

        return new SpecSuiteResult
        {
            Results = results,
            ReportResults = reportResults,
            SpecsByResult = specsByResult,
            GameDataSpecsByResult = gameDataSpecsByResult,
            DurationsByResult = durationsByResult,
            TotalSpecs = totalSpecs,
            Selected = selected,
            Elapsed = elapsed,
            Passed = passed,
            Failed = failed,
            ExpectedFailures = expectedFailureCount,
            UnexpectedPasses = unexpectedPassCount,
            ExpectedFailuresEngine = expectedFailuresEngine,
        };
    }
}
