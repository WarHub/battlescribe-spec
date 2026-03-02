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
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
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
        continue;

    SpecFile spec;
    try
    {
        spec = loader();
    }
    catch (Exception ex)
    {
        results.Add(new SpecResult(id, category, "Failed to load", [$"Load error: {ex.Message}"]));
        continue;
    }

    // Apply tag filter
    if (tag is not null && !(spec.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) ?? false))
        continue;

    // Apply engine filter — null engines means "all engines"
    if (engineFilter is not null && spec.Engines is not null
        && !spec.Engines.Contains(engineFilter, StringComparer.OrdinalIgnoreCase))
        continue;

    // Run spec via protocol engine
    using var engine = new JsonProtocolEngine(adapterProcess);
    var runner = new SpecRunner(engine);
    var result = runner.Run(spec);
    results.Add(result);
}

sw.Stop();

// ===== Output results =====
var passed = results.Count(r => r.Passed);
var failed = results.Count(r => !r.Passed);
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

return exitCode;

// ===== Output formatters =====

void OutputSummary(List<SpecResult> results, TimeSpan elapsed)
{
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
    Console.WriteLine($"Results: {passed} passed, {failed} failed, {results.Count} total ({elapsed.TotalSeconds:F1}s)");
}

void OutputJson(List<SpecResult> results, TimeSpan elapsed)
{
    var report = new
    {
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
    // Step summary as markdown table
    Console.WriteLine("## BattleScribe Spec Conformance Results");
    Console.WriteLine();
    Console.WriteLine($"**{passed}** passed, **{failed}** failed, **{results.Count}** total ({elapsed.TotalSeconds:F1}s)");
    Console.WriteLine();

    if (failed > 0)
    {
        Console.WriteLine("### Failed Specs");
        Console.WriteLine();
        Console.WriteLine("| Spec | Failures |");
        Console.WriteLine("|------|----------|");
        foreach (var result in results.Where(r => !r.Passed))
        {
            var failures = string.Join("<br>", result.Failures.Select(f => f.Replace("|", "\\|")));
            Console.WriteLine($"| {result.Category}/{result.SpecId} | {failures} |");
        }
    }
}

void PrintUsage()
{
    Console.Error.WriteLine("""
        bs-spec-runner — BattleScribe conformance spec test runner

        Usage: bs-spec-runner --adapter <path> [options]

        Options:
          --adapter <path>    Path to adapter executable (required)
                              Use "dotnet:path.dll" for .NET adapters
          --specs <dir>       Path to specs directory (default: embedded specs)
          --output <format>   Output format: summary (default), json, github-actions
          --filter <pattern>  Only run specs matching pattern
          --tag <tag>         Only run specs with this tag
          --engine <name>     Only run specs applicable to this engine
                              (battlescribe, newrecruit, phalanx)
          -h, --help          Show this help
        """);
}
