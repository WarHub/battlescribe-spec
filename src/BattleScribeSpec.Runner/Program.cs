using BattleScribeSpec;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Protocol;

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

try
{
    var result = await SpecSuiteRunner.RunAsync(
        new SpecSuiteOptions
        {
            SpecsDirectory = specsDir,
            FilterPatterns = filterPatterns,
            TagFilter = tagFilter,
            EngineFilter = engineFilter,
            ExpectedFailuresEngine = expectedFailuresEngine,
            AssertionEngine = assertionEngine,
            Workers = workers,
            AdapterFactory = () => AdapterProcess.Start(adapterExe, adapterArgs),
        },
        progressWriter: Console.Error);

    switch (output)
    {
        case "json":
            SpecSuiteOutput.WriteJson(result, engineFilter, Console.Out);
            break;
        case "github-actions":
            SpecSuiteOutput.WriteGitHubActions(result, engineFilter, Console.Out);
            break;
        default:
            SpecSuiteOutput.WriteSummary(result, engineFilter, Console.Out);
            break;
    }

    if (reportPath is not null)
    {
        SpecSuiteOutput.WriteConformanceReport(result, reportPath, engineFilter, assertionEngine, Console.Out);
    }

    return result.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static void PrintUsage()
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
