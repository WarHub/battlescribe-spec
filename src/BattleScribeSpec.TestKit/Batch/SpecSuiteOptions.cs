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
    public required TimeSpan Elapsed { get; init; }
    public int Passed { get; private init; }
    public int Failed { get; private init; }
    public int ExpectedFailures { get; private init; }
    public int UnexpectedPasses { get; private init; }
    public int ExitCode => Failed > 0 ? 1 : 0;

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
            Elapsed = elapsed,
            Passed = passed,
            Failed = failed,
            ExpectedFailures = expectedFailureCount,
            UnexpectedPasses = unexpectedPassCount,
            ExpectedFailuresEngine = expectedFailuresEngine,
        };
    }
}
