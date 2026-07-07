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
    /// <summary>Creates one adapter process per worker. Disposed by the runner.</summary>
    public required Func<AdapterProcess> AdapterFactory { get; init; }
}

public sealed class SpecSuiteResult
{
    public required IReadOnlyList<SpecResult> Results { get; init; }
    public required IReadOnlyList<SpecResultSummary> ReportResults { get; init; }
    public required IReadOnlyDictionary<SpecResult, SpecFile> SpecsByResult { get; init; }
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
                var spec = specsByResult.TryGetValue(r, out var s) ? s : null;
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
