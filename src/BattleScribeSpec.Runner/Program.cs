using System.Diagnostics;
using System.Text.Json;
using BattleScribeSpec;
using BattleScribeSpec.Protocol;

// ===== Parse arguments =====
var adapter = "";
string? specsDir = null;
var output = "summary";
string? filter = null;
string? tag = null;
string? engineFilter = null;
string? reportPath = null;
string? matrixDir = null;
string? expectedFailuresEngine = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--adapter" when i + 1 < args.Length:
            adapter = args[++i];
            break;
        case "--specs" when i + 1 < args.Length:
            specsDir = args[++i];
            break;
        case "--output" when i + 1 < args.Length:
            output = args[++i];
            break;
        case "--filter" when i + 1 < args.Length:
            filter = args[++i];
            break;
        case "--tag" when i + 1 < args.Length:
            tag = args[++i];
            break;
        case "--engine" when i + 1 < args.Length:
            engineFilter = args[++i];
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        case "--matrix" when i + 1 < args.Length:
            matrixDir = args[++i];
            break;
        case "--expected-failures" when i + 1 < args.Length:
            expectedFailuresEngine = args[++i];
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}

if (!string.IsNullOrEmpty(matrixDir))
{
    if (!Directory.Exists(matrixDir))
    {
        Console.Error.WriteLine($"Error: matrix directory not found: {matrixDir}");
        return 1;
    }

    var files = Directory.GetFiles(matrixDir, "*-conformance.json");
    if (files.Length == 0)
    {
        Console.Error.WriteLine($"Error: no *-conformance.json files found in {matrixDir}");
        return 1;
    }

    var reports = files.Select(CompatibilityMatrix.LoadReport).ToArray();
    Console.WriteLine(CompatibilityMatrix.GenerateMarkdown(reports));
    return 0;
}

if (string.IsNullOrEmpty(adapter))
{
    Console.Error.WriteLine("Error: --adapter is required.");
    PrintUsage();
    return 1;
}

// ===== Discover specs =====
List<(string Path, string Id, string Category)>? fileSpecs = null;
List<(string ResourceName, string Id, string Category)>? embeddedSpecs = null;

if (specsDir is not null)
{
    if (!Directory.Exists(specsDir))
    {
        Console.Error.WriteLine($"Error: specs directory not found: {specsDir}");
        return 1;
    }
    fileSpecs = SpecLoader.DiscoverSpecs(specsDir).ToList();
}
else
{
    // Try filesystem first, then embedded
    specsDir = SpecLoader.FindSpecsDirectory();
    if (specsDir is not null)
        fileSpecs = SpecLoader.DiscoverSpecs(specsDir).ToList();
    else
        embeddedSpecs = SpecLoader.DiscoverEmbeddedSpecs().ToList();
}

var totalSpecs = fileSpecs?.Count ?? embeddedSpecs?.Count ?? 0;
if (totalSpecs == 0)
{
    Console.Error.WriteLine("Error: no spec files found.");
    return 1;
}

// ===== Engine expectations from spec-level engines field =====
// The --expected-failures flag is now used only as the engine name for spec-level expectations.
// Per-spec expected failures are encoded in the YAML engines field, not in separate JSON files.

// ===== Start adapter process =====
// Support "dotnet:path.dll" syntax for .NET adapters
string adapterExe, adapterArgs;
if (adapter.StartsWith("dotnet:", StringComparison.OrdinalIgnoreCase))
{
    adapterExe = "dotnet";
    adapterArgs = adapter[7..];
}
else
{
    adapterExe = adapter;
    adapterArgs = "";
}

using var adapterProcess = AdapterProcess.Start(adapterExe, adapterArgs);

// ===== Run specs =====
var results = new List<SpecResult>();
var reportResults = new List<SpecResultSummary>();
var specsByResult = new Dictionary<SpecResult, SpecFile>();
var skipped = 0;
var sw = Stopwatch.StartNew();

IEnumerable<(string IdForLoad, string Id, string Category, Func<SpecFile> Loader)> specSources;
if (fileSpecs is not null)
{
    specSources = fileSpecs.Select(s => (s.Path, s.Id, s.Category, (Func<SpecFile>)(() => SpecLoader.Load(s.Path))));
}
else
{
    specSources = embeddedSpecs!.Select(s => (s.ResourceName, s.Id, s.Category, (Func<SpecFile>)(() => SpecLoader.LoadEmbedded(s.ResourceName))));
}

foreach (var (_, id, category, loader) in specSources)
{
    var specName = $"{category}/{id}";

    // Apply filter
    if (filter is not null && !specName.Contains(filter, StringComparison.OrdinalIgnoreCase))
    {
        skipped++;
        reportResults.Add(new SpecResultSummary(id, category, "", "skipped", [$"Skipped by filter '{filter}'"]));
        continue;
    }

    SpecFile spec;
    try
    {
        spec = loader();
    }
    catch (Exception ex)
    {
        var failures = new List<string> { $"Load error: {ex.Message}" };
        results.Add(new SpecResult(id, category, "Failed to load", failures));
        reportResults.Add(new SpecResultSummary(id, category, "Failed to load", "failed", failures));
        continue;
    }

    // Apply tag filter
    if (tag is not null && !(spec.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) ?? false))
    {
        skipped++;
        reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped", [$"Skipped by tag '{tag}'"]));
        continue;
    }

    // Apply engine filter — null engines means "all engines"
    if (engineFilter is not null && !spec.IsApplicableTo(engineFilter))
    {
        skipped++;
        reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped", [$"Skipped by engine filter '{engineFilter}'"]));
        continue;
    }

    // Run spec via protocol engine — use longer timeout for DataSource specs
    var timeout = spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
    using var engine = new JsonProtocolEngine(adapterProcess, timeout);
    var runner = new SpecRunner(engine, new DataSourceResolver());
    var result = runner.Run(spec);
    results.Add(result);
    specsByResult[result] = spec;

    // Determine status considering engine expectations
    var status = result.Passed ? "passed" : "failed";
    if (expectedFailuresEngine is not null)
    {
        var isExpectedFail = spec.IsExpectedToFail(expectedFailuresEngine);
        if (!result.Passed && isExpectedFail)
            status = "expected-failure";
        else if (result.Passed && isExpectedFail)
            status = "unexpected-pass";
    }

    reportResults.Add(new SpecResultSummary(
        result.SpecId,
        result.Category,
        result.Description,
        status,
        [.. result.Failures]));
}

sw.Stop();

// ===== Output results =====
var passed = results.Count(r => r.Passed);
int failed;
int expectedFailureCount = 0;
int unexpectedPassCount = 0;
if (expectedFailuresEngine is not null)
{
    failed = 0;
    foreach (var r in results)
    {
        var spec = specsByResult.TryGetValue(r, out var s) ? s : null;
        var isExpectedFail = spec?.IsExpectedToFail(expectedFailuresEngine) ?? false;
        if (!r.Passed && !isExpectedFail)
            failed++;
        if (!r.Passed && isExpectedFail)
            expectedFailureCount++;
        if (r.Passed && isExpectedFail)
            unexpectedPassCount++;
    }
    failed += unexpectedPassCount; // Unexpected passes count as failures
}
else
{
    failed = results.Count(r => !r.Passed);
}
var exitCode = failed > 0 ? 1 : 0;

switch (output)
{
    case "json":
        OutputJson(results, sw.Elapsed);
        break;
    case "github-actions":
        OutputGitHubActions(results, sw.Elapsed);
        break;
    default:
        OutputSummary(results, sw.Elapsed);
        break;
}

if (reportPath is not null)
    OutputConformanceReport(reportPath, reportResults);

return exitCode;

// ===== Output formatters =====

void OutputSummary(List<SpecResult> results, TimeSpan elapsed)
{
    if (engineFilter is not null)
        Console.WriteLine($"Engine: {engineFilter}");

    foreach (var result in results)
    {
        var status = result.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"  [{status}] {result.Category}/{result.SpecId}");
        if (!result.Passed)
        {
            foreach (var failure in result.Failures)
                Console.WriteLine($"         {failure}");
        }
    }
    Console.WriteLine();
    var xfailLabel = expectedFailureCount > 0 ? $", {expectedFailureCount} expected failures" : "";
    Console.WriteLine($"Results: {passed} passed, {failed} failed{xfailLabel}, {results.Count} total ({elapsed.TotalSeconds:F1}s)");
}

void OutputJson(List<SpecResult> results, TimeSpan elapsed)
{
    var report = new
    {
        engine = engineFilter,
        passed,
        failed,
        total = results.Count,
        elapsedSeconds = elapsed.TotalSeconds,
        specs = results.Select(r => new
        {
            id = r.SpecId,
            category = r.Category,
            description = r.Description,
            passed = r.Passed,
            failures = r.Failures,
        }),
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

void OutputGitHubActions(List<SpecResult> results, TimeSpan elapsed)
{
    var engineLabel = engineFilter is not null ? $" ({engineFilter})" : "";
    // Step summary as markdown table
    Console.WriteLine($"## BattleScribe Spec Conformance Results{engineLabel}");
    Console.WriteLine();
    var xfailLabel = expectedFailureCount > 0 ? $", **{expectedFailureCount}** expected failures" : "";
    var xpassLabel = unexpectedPassCount > 0 ? $", **{unexpectedPassCount}** unexpected passes" : "";
    Console.WriteLine($"**{passed}** passed, **{failed}** failed{xfailLabel}{xpassLabel}, **{results.Count}** total ({elapsed.TotalSeconds:F1}s)");
    Console.WriteLine();

    if (failed > 0)
    {
        Console.WriteLine("### Failed Specs");
        Console.WriteLine();
        Console.WriteLine("| Spec | Failures |");
        Console.WriteLine("|------|----------|");
        foreach (var result in results)
        {
            var spec = specsByResult.TryGetValue(result, out var s) ? s : null;
            var isExpectedFail = expectedFailuresEngine is not null && (spec?.IsExpectedToFail(expectedFailuresEngine) ?? false);
            var isRealFailure = !result.Passed && !isExpectedFail;
            var isUnexpectedPass = result.Passed && isExpectedFail;
            if (isRealFailure || isUnexpectedPass)
            {
                var label = isUnexpectedPass ? " ⚠️ UNEXPECTED PASS" : "";
                var failures = isUnexpectedPass
                    ? "Expected to fail but passed — update spec engines field"
                    : string.Join("<br>", result.Failures.Select(f => f.Replace("|", "\\|")));
                Console.WriteLine($"| {result.Category}/{result.SpecId}{label} | {failures} |");
            }
        }
    }
}

void PrintUsage()
{
    Console.Error.WriteLine("""
        bs-spec-runner — BattleScribe conformance spec test runner

        Usage: bs-spec-runner --adapter <path> [options]
               bs-spec-runner --matrix <dir>

        Options:
          --adapter <path>    Path to adapter executable (required)
                              Use "dotnet:path.dll" for .NET adapters
          --matrix <dir>      Read *-conformance.json files and output markdown matrix
          --specs <dir>       Path to specs directory (default: embedded specs)
          --output <format>   Output format: summary (default), json, github-actions
          --filter <pattern>  Only run specs matching pattern
          --tag <tag>         Only run specs with this tag
          --engine <name>     Only run specs applicable to this engine
                              (battlescribe, newrecruit, phalanx)
          --report <path>     Write conformance report JSON to file
          --expected-failures <engine>
                              Engine name for spec-level expected failures (from engines YAML field)
                              Expected failures don't count toward exit code; unexpected passes do
          -h, --help          Show this help
        """);
}

void OutputConformanceReport(string path, List<SpecResultSummary> results)
{
    var reportPassed = results.Count(r => r.Status == "passed");
    var reportFailed = results.Count(r => r.Status == "failed");
    var reportSkipped = results.Count(r => r.Status == "skipped");
    var runTotal = reportPassed + reportFailed;
    var passRate = runTotal == 0 ? 0 : (double)reportPassed / runTotal * 100.0;

    var report = new ConformanceReport(
        engineFilter ?? "all",
        DateTime.UtcNow,
        totalSpecs,
        reportPassed,
        reportFailed,
        reportSkipped,
        passRate,
        results);

    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine();
    Console.WriteLine($"Conformance report: {path}");
    Console.WriteLine($"Summary: total={report.TotalSpecs}, passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}, passRate={report.PassRate:F1}%");

    if (engineFilter is not null)
        Console.WriteLine($"Engine breakdown: {engineFilter} => passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}");

    var failedSpecs = results.Where(r => r.Status == "failed").ToList();
    if (failedSpecs.Count > 0)
    {
        Console.WriteLine("Failed specs:");
        foreach (var failedSpec in failedSpecs)
        {
            Console.WriteLine($"  - {failedSpec.Category}/{failedSpec.SpecId}");
            foreach (var failure in failedSpec.Failures)
                Console.WriteLine($"    {failure}");
        }
    }
}
