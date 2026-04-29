using System.Diagnostics;
using System.Text.Json;
using BattleScribeSpec;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Runner;

// ===== Parse arguments =====
var adapter = "";
string? specsDir = null;
var output = "summary";
string? filter = null;
string? tagsExpr = null;
string? engineFilter = null;
string? reportPath = null;
string? matrixDir = null;
string? expectedFailuresEngine = null;
string? assertionEngine = null;
var workers = 1;

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
        case "--tags" when i + 1 < args.Length:
            tagsExpr = args[++i];
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
        case "--assertion-engine" when i + 1 < args.Length:
            assertionEngine = args[++i];
            break;
        case "--workers" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out workers) || workers < 1)
            {
                Console.Error.WriteLine("Error: --workers must be a positive integer.");
                return 1;
            }
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

// Parse --tags into a TagFilter
var tagFilter = TagFilter.Parse(tagsExpr);

// Parse --filter into patterns (comma-separated, OR logic); treat empty as no filter
var filterPatterns = filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    is { Length: > 0 } patterns ? patterns : null;

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

using var adapterProcess = workers <= 1 ? AdapterProcess.Start(adapterExe, adapterArgs) : null;

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

// Pre-filter specs (filtering doesn't need the adapter)
var filteredSpecs = new List<(string Id, string Category, SpecFile Spec)>();
foreach (var (_, id, category, loader) in specSources)
{
    var specName = $"{category}/{id}";

    if (filterPatterns is not null && !filterPatterns.Any(p => specName.Contains(p, StringComparison.OrdinalIgnoreCase)))
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

    if (tagFilter is not null && !tagFilter.Matches(spec.Tags))
    {
        skipped++;
        reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
            [$"Skipped by tag filter '{tagFilter}'"], spec.Tags));
        continue;
    }

    if (engineFilter is not null && !spec.IsApplicableTo(engineFilter))
    {
        skipped++;
        reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
            [$"Skipped by engine filter '{engineFilter}'"], spec.Tags));
        continue;
    }

    filteredSpecs.Add((id, category, spec));
}

if (workers > 1)
{
    // Parallel execution with N adapter processes
    Console.Error.WriteLine($"Running {filteredSpecs.Count} specs with {workers} workers...");

    var adapterProcesses = new List<AdapterProcess>();
    for (int w = 0; w < workers; w++)
        adapterProcesses.Add(AdapterProcess.Start(adapterExe, adapterArgs));

    // Channel-based process pool
    var processPool = System.Threading.Channels.Channel.CreateBounded<AdapterProcess>(workers);
    foreach (var proc in adapterProcesses)
        processPool.Writer.TryWrite(proc);

    var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<(SpecResult Result, SpecFile Spec, string Status)>();

    await Parallel.ForEachAsync(
        filteredSpecs,
        new ParallelOptions { MaxDegreeOfParallelism = workers },
        async (item, ct) =>
        {
            var (id, category, spec) = item;
            var proc = await processPool.Reader.ReadAsync(ct);
            try
            {
                var timeout = spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
                using var engine = new JsonProtocolEngine(proc, timeout);
                var runner = new SpecRunner(engine, new DataSourceResolver(), assertionEngine ?? engineFilter);
                var result = runner.Run(spec);

                var status = result.Passed ? "passed" : "failed";
                if (expectedFailuresEngine is not null)
                {
                    var isExpectedFail = spec.IsExpectedToFail(expectedFailuresEngine);
                    if (!result.Passed && isExpectedFail) status = "expected-failure";
                    else if (result.Passed && isExpectedFail) status = "unexpected-pass";
                }

                concurrentResults.Add((result, spec, status));
            }
            finally
            {
                processPool.Writer.TryWrite(proc);
            }
        });

    // Collect results in order
    foreach (var (result, spec, status) in concurrentResults)
    {
        results.Add(result);
        specsByResult[result] = spec;
        reportResults.Add(new SpecResultSummary(result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags));
    }

    // Dispose adapter processes
    foreach (var proc in adapterProcesses)
        proc.Dispose();
}
else
{
    // Sequential execution with single adapter process
    foreach (var (id, category, spec) in filteredSpecs)
    {
        var timeout = spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
        using var engine = new JsonProtocolEngine(adapterProcess!, timeout);
        var runner = new SpecRunner(engine, new DataSourceResolver(), assertionEngine ?? engineFilter);
        var result = runner.Run(spec);
        results.Add(result);
        specsByResult[result] = spec;

        var status = result.Passed ? "passed" : "failed";
        if (expectedFailuresEngine is not null)
        {
            var isExpectedFail = spec.IsExpectedToFail(expectedFailuresEngine);
            if (!result.Passed && isExpectedFail) status = "expected-failure";
            else if (result.Passed && isExpectedFail) status = "unexpected-pass";
        }

        reportResults.Add(new SpecResultSummary(result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags));
    }
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
    var report = new JsonRunReport
    {
        Engine = engineFilter,
        Passed = passed,
        Failed = failed,
        Total = results.Count,
        ElapsedSeconds = elapsed.TotalSeconds,
        Specs = results.Select(r =>
        {
            var spec = specsByResult.TryGetValue(r, out var s) ? s : null;
            return new JsonSpecEntry
            {
                Id = r.SpecId,
                Category = r.Category,
                Description = r.Description,
                Passed = r.Passed,
                Failures = r.Failures,
                Tags = spec?.Tags,
            };
        }).ToList(),
    };
    Console.WriteLine(JsonSerializer.Serialize(report, RunnerJsonContext.Default.JsonRunReport));
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
          --filter <pattern>  Only run specs matching pattern (comma-separated, OR logic)
                              Examples: "kitchen-sink", "protocol/,category/"
          --tags <expr>       Tag filter expression (comma-separated, +/- prefix)
                              Include: "cost,constraint" (OR — matches any)
                              Exclude: "-undefined-behavior"
                              Combined: "cost,constraint,-undefined-behavior"
          --engine <name>     Only run specs applicable to this engine
                              (battlescribe, newrecruit, phalanx, wham)
          --report <path>     Write conformance report JSON to file
          --expected-failures <engine>
                              Engine name for spec-level expected failures (from engines YAML field)
                              Expected failures don't count toward exit code; unexpected passes do
          --assertion-engine <engine>
                              Engine name for step-level assertion overrides (defaults to --engine)
                              Use when the adapter engine differs from the spec engine filter
          --workers <N>       Run specs in parallel with N adapter processes (default: 1)
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
        results,
        assertionEngine != null && assertionEngine != engineFilter ? assertionEngine : null);

    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    File.WriteAllText(path, JsonSerializer.Serialize(report, RunnerJsonContext.Default.ConformanceReport));

    Console.WriteLine();
    Console.WriteLine($"Conformance report: {path}");
    Console.WriteLine($"Summary: total={report.TotalSpecs}, passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}, passRate={report.PassRate:F1}%");

    if (engineFilter is not null)
    {
        var assertionLabel = report.AssertionEngine is not null ? $", assertions={report.AssertionEngine}" : "";
        Console.WriteLine($"Engine breakdown: {engineFilter} => passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}{assertionLabel}");
    }

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
