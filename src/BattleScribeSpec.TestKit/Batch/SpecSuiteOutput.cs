using System.Text.Json;

namespace BattleScribeSpec.Batch;

/// <summary>
/// Formats a <see cref="SpecSuiteResult"/> in the Runner's output shapes. Every writer takes an
/// explicit <see cref="TextWriter"/> so callers choose the destination (Console.Out today).
/// </summary>
public static class SpecSuiteOutput
{
    public static void WriteSummary(SpecSuiteResult result, string? engineLabel, TextWriter output)
    {
        if (engineLabel is not null)
        {
            output.WriteLine($"Engine: {engineLabel}");
        }

        foreach (var r in result.Results)
        {
            var status = r.Passed ? "PASS" : "FAIL";
            output.WriteLine($"  [{status}] {r.Category}/{r.SpecId}");
            if (!r.Passed)
            {
                foreach (var failure in r.Failures)
                {
                    output.WriteLine($"         {failure}");
                }
            }
        }

        output.WriteLine();
        var xfailLabel = result.ExpectedFailures > 0 ? $", {result.ExpectedFailures} expected failures" : "";
        output.WriteLine($"Results: {result.Passed} passed, {result.Failed} failed{xfailLabel}, {result.Results.Count} total ({result.Elapsed.TotalSeconds:F1}s)");
    }

    public static void WriteJson(SpecSuiteResult result, string? engineLabel, TextWriter output)
    {
        var report = new JsonRunReport
        {
            Engine = engineLabel,
            Passed = result.Passed,
            Failed = result.Failed,
            Total = result.Results.Count,
            ElapsedSeconds = result.Elapsed.TotalSeconds,
            Specs = [.. result.Results.Select(r =>
            {
                SpecFileBase? spec = result.SpecsByResult.TryGetValue(r, out var s) ? s
                    : result.GameDataSpecsByResult.TryGetValue(r, out var gs) ? gs
                    : null;
                result.DurationsByResult.TryGetValue(r, out var durationMs);
                return new JsonSpecEntry
                {
                    Id = r.SpecId,
                    Category = r.Category,
                    Description = r.Description,
                    Passed = r.Passed,
                    Failures = r.Failures,
                    Tags = spec?.Tags,
                    DurationMs = durationMs,
                };
            })],
        };
        output.WriteLine(JsonSerializer.Serialize(report, SuiteJsonContext.Default.JsonRunReport));
    }

    public static void WriteGitHubActions(SpecSuiteResult result, string? engineLabel, TextWriter output)
    {
        var label = engineLabel is not null ? $" ({engineLabel})" : "";
        // Step summary as markdown table
        output.WriteLine($"## BattleScribe Spec Conformance Results{label}");
        output.WriteLine();
        var xfailLabel = result.ExpectedFailures > 0 ? $", **{result.ExpectedFailures}** expected failures" : "";
        var xpassLabel = result.UnexpectedPasses > 0 ? $", **{result.UnexpectedPasses}** unexpected passes" : "";
        output.WriteLine($"**{result.Passed}** passed, **{result.Failed}** failed{xfailLabel}{xpassLabel}, **{result.Results.Count}** total ({result.Elapsed.TotalSeconds:F1}s)");
        output.WriteLine();

        if (result.Failed > 0)
        {
            output.WriteLine("### Failed Specs");
            output.WriteLine();
            output.WriteLine("| Spec | Failures |");
            output.WriteLine("|------|----------|");
            foreach (var r in result.Results)
            {
                SpecFileBase? spec = result.SpecsByResult.TryGetValue(r, out var s) ? s
                    : result.GameDataSpecsByResult.TryGetValue(r, out var gs) ? gs
                    : null;
                var isExpectedFail = result.ExpectedFailuresEngine is not null && (spec?.IsExpectedToFail(result.ExpectedFailuresEngine) ?? false);
                var isRealFailure = !r.Passed && !isExpectedFail;
                var isUnexpectedPass = r.Passed && isExpectedFail;
                if (isRealFailure || isUnexpectedPass)
                {
                    var itemLabel = isUnexpectedPass ? " ⚠️ UNEXPECTED PASS" : "";
                    var failures = isUnexpectedPass
                        ? "Expected to fail but passed — update spec engines field"
                        : string.Join("<br>", r.Failures.Select(f => f.Replace("|", "\\|")));
                    output.WriteLine($"| {r.Category}/{r.SpecId}{itemLabel} | {failures} |");
                }
            }
        }
    }

    public static void WriteConformanceReport(SpecSuiteResult result, string path, string? engineFilter, string? assertionEngine, TextWriter console)
    {
        var results = result.ReportResults;
        var reportPassed = results.Count(r => r.Status == "passed");
        var reportFailed = results.Count(r => r.Status == "failed");
        var reportSkipped = results.Count(r => r.Status == "skipped");
        var runTotal = reportPassed + reportFailed;
        var passRate = runTotal == 0 ? 0 : (double)reportPassed / runTotal * 100.0;

        var report = new ConformanceReport(
            engineFilter ?? "all",
            DateTime.UtcNow,
            result.TotalSpecs,
            reportPassed,
            reportFailed,
            reportSkipped,
            passRate,
            [.. results],
            assertionEngine != null && assertionEngine != engineFilter ? assertionEngine : null);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(report, SuiteJsonContext.Default.ConformanceReport));

        console.WriteLine();
        console.WriteLine($"Conformance report: {path}");
        console.WriteLine($"Summary: total={report.TotalSpecs}, passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}, passRate={report.PassRate:F1}%");

        if (engineFilter is not null)
        {
            var assertionLabel = report.AssertionEngine is not null ? $", assertions={report.AssertionEngine}" : "";
            console.WriteLine($"Engine breakdown: {engineFilter} => passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}{assertionLabel}");
        }

        var failedSpecs = results.Where(r => r.Status == "failed").ToList();
        if (failedSpecs.Count > 0)
        {
            console.WriteLine("Failed specs:");
            foreach (var failedSpec in failedSpecs)
            {
                console.WriteLine($"  - {failedSpec.Category}/{failedSpec.SpecId}");
                foreach (var failure in failedSpec.Failures)
                {
                    console.WriteLine($"    {failure}");
                }
            }
        }
    }
}
