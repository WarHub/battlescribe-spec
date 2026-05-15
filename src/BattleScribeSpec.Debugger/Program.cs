using BattleScribeSpec;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

// ===== Parse arguments =====
string? specInput = null;
var engineName = "battlescribe";
var dumpAll = false;
var json = false;
var headless = true;
string? exportXmlDir = null;
var formatMode = false;
var formatCheck = false;
string? formatDir = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--format":
            formatMode = true;
            // Optional next arg: directory (if it doesn't start with '-')
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                formatDir = args[++i];
            }
            break;
        case "--check":
            formatCheck = true;
            break;
        case "--engine" when i + 1 < args.Length:
            engineName = args[++i].ToLowerInvariant() switch
            {
                "bs" => "battlescribe",
                "nr" => "newrecruit",
                "nr-ui" => "nr-ui",
                var name => name
            };
            break;
        case "--dump":
            dumpAll = true;
            break;
        case "--json":
            json = true;
            break;
        case "--no-headless":
            headless = false;
            break;
        case "--export-xml" when i + 1 < args.Length:
            exportXmlDir = args[++i];
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        case "-":
            specInput = "-";
            break;
        default:
            if (args[i].StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintUsage();
                return 1;
            }
            specInput = args[i];
            break;
    }
}

// ===== Format mode (exits before engine loading) =====
if (formatMode)
{
    var targetDir = formatDir
        ?? SpecLoader.FindRosterSpecsDirectory()
        ?? throw new InvalidOperationException("Could not locate specs/roster directory. Pass a directory as argument.");

    if (formatCheck)
    {
        Console.Error.WriteLine($"Checking formatting in: {targetDir}");
        var issues = SpecFormatter.FormatDirectory(targetDir, checkOnly: true, log: Console.Error);
        if (issues > 0)
        {
            Console.Error.WriteLine($"\n{issues} file(s) need formatting. Run format-specs.ps1 to fix.");
            return 1;
        }
        Console.Error.WriteLine($"All files are correctly formatted.");
        return 0;
    }
    else
    {
        Console.Error.WriteLine($"Formatting specs in: {targetDir}");
        var fixed_ = SpecFormatter.FormatDirectory(targetDir, checkOnly: false, log: Console.Error);
        if (fixed_ > 0)
        {
            Console.Error.WriteLine($"\nFixed {fixed_} file(s).");
        }
        else
        {
            Console.Error.WriteLine("All files are already correctly formatted.");
        }
        return 0;
    }
}

if (specInput is null)
{
    // Check if stdin has data
    if (Console.IsInputRedirected)
    {
        specInput = "-";
    }
    else
    {
        Console.Error.WriteLine("Error: no spec provided. Pass a file path, spec ID, or pipe YAML via stdin.");
        PrintUsage();
        return 1;
    }
}

// ===== Load spec =====
SpecFile spec;
try
{
    spec = LoadSpec(specInput);
    Console.Error.WriteLine($"Loaded spec: {spec.Category}/{spec.Id} — {spec.Description}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error loading spec: {ex.Message}");
    return 1;
}

// ===== Export XML mode =====
if (exportXmlDir is not null)
{
    if (spec.Setup.DataSource is { Length: > 0 })
    {
        Console.Error.WriteLine("Error: --export-xml is not supported for dataSource specs.");
        return 1;
    }

    var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup);
    Directory.CreateDirectory(exportXmlDir);

    var gstFile = Path.Combine(exportXmlDir, "system.gst");
    File.WriteAllText(gstFile, CatXmlGenerator.GenerateGameSystemXml(gameSystem));
    Console.Error.WriteLine($"Wrote {gstFile}");

    foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
    {
        var catFile = Path.Combine(exportXmlDir, fileName);
        File.WriteAllText(catFile, xml);
        Console.Error.WriteLine($"Wrote {catFile}");
    }

    Console.Error.WriteLine($"Exported {1 + catalogues.Length} file(s) to {exportXmlDir}");
    return 0;
}

// ===== Create engine =====
Console.Error.WriteLine($"Engine: {engineName}");
IRosterEngine engine;
try
{
    engine = await CreateEngine(engineName, headless);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error creating engine: {ex.Message}");
    return 1;
}

using (engine)
{
    var dumpOptions = new DumpOptions(Json: json);
    var runner = new RosterRunner(engine, new DataSourceResolver(), engineName);

    var stepCount = spec.Steps.Count;
    var lastStepIndex = stepCount - 1;

    runner.OnStepCompleted = (stepIndex, step, state, errors) =>
    {
        var isDumpAction = step.Action == "dump";
        var isLastStep = stepIndex == lastStepIndex;
        var shouldDump = isDumpAction || dumpAll || isLastStep;

        if (!shouldDump)
        {
            return;
        }

        Console.Out.Flush();
        Console.Error.WriteLine();
        Console.Error.WriteLine($"═══ Step {stepIndex}: {DescribeStep(step)} ═══");
        Console.Error.Flush();

        StateDumper.Dump(state, errors, Console.Out, dumpOptions);
        Console.Out.Flush();
    };

    Console.Error.WriteLine($"Running {stepCount} steps...");
    Console.Error.WriteLine();

    var result = runner.Run(spec);

    Console.Error.WriteLine();
    if (result.Failures.Count == 0)
    {
        Console.Error.WriteLine("✓ PASS — all assertions passed");
    }
    else
    {
        Console.Error.WriteLine($"✗ FAIL — {result.Failures.Count} failure(s):");
        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine($"  {failure}");
        }
    }

    return result.Failures.Count == 0 ? 0 : 1;
}

// ===== Helpers =====

SpecFile LoadSpec(string input)
{
    if (input == "-")
    {
        // Read from stdin
        var yaml = Console.In.ReadToEnd();
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, yaml);
        try
        {
            return SpecLoader.Load(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // Try as file path first
    if (File.Exists(input))
    {
        return SpecLoader.Load(input);
    }

    // Try as spec ID: look in specs/ directory
    var specsDir = SpecLoader.FindSpecsDirectory();
    if (specsDir is not null)
    {
        // Try exact path: specs/{input}.yaml
        var candidate = Path.Combine(specsDir, input + ".yaml");
        if (File.Exists(candidate))
        {
            return SpecLoader.Load(candidate);
        }

        // Try with category: specs/{category}/{id}.yaml
        foreach (var file in Directory.EnumerateFiles(specsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var relative = Path.GetRelativePath(specsDir, file).Replace('\\', '/');
            if (name == input || relative == input || relative == input + ".yaml")
            {
                return SpecLoader.Load(file);
            }
        }
    }

    throw new FileNotFoundException($"Spec not found: '{input}'. Provide a file path, category/id, or id.");
}

async Task<IRosterEngine> CreateEngine(string name, bool headless)
{
    switch (name)
    {
        case "bs" or "battlescribe":
            return new BattleScribeRosterEngine();

        case "nr" or "newrecruit":
            {
                var url = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
                NewRecruitRosterEngine nrEngine;
                if (url is { Length: > 0 })
                {
                    Console.Error.WriteLine($"NR live mode: {url}");
                    nrEngine = await NewRecruitRosterEngine.CreateAsync(url, headless);
                }
                else
                {
                    var harFile = HarRecorder.FindFrozenHarFile() ?? throw new InvalidOperationException(
                            "NR engine requires NR_ENGINE_URL env var (live mode) or .testdata/newrecruit-har/newrecruit.har (frozen mode).");

                    Console.Error.WriteLine($"NR frozen mode: {harFile}");
                    nrEngine = await NewRecruitRosterEngine.CreateFrozenAsync(harFile, headless: headless);
                }
                return nrEngine;
            }

        case "nr-ui":
            {
                var url = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
                NrRosterUiEngine uiEngine;
                if (url is { Length: > 0 })
                {
                    Console.Error.WriteLine($"NR UI live mode: {url}");
                    uiEngine = await NrRosterUiEngine.CreateAsync(url, headless);
                }
                else
                {
                    var harFile = HarRecorder.FindFrozenHarFile() ?? throw new InvalidOperationException(
                            "NR UI engine requires NR_ENGINE_URL env var (live mode) or .testdata/newrecruit-har/newrecruit.har (frozen mode).");

                    Console.Error.WriteLine($"NR UI frozen mode: {harFile}");
                    uiEngine = await NrRosterUiEngine.CreateFrozenAsync(harFile, headless: headless);
                }
                return uiEngine;
            }

        default:
            throw new ArgumentException($"Unknown engine: '{name}'. Use 'bs', 'nr', or 'nr-ui'.");
    }
}

static string DescribeStep(StepDef step)
{
    if (step.Action is { } action)
    {
        var parts = new List<string> { action };
        if (step.Id is { Length: > 0 } sid)
        {
            parts.Add($"id={sid}");
        }

        if (step.ForceEntryId is { } feid)
        {
            parts.Add($"forceEntryId={feid}");
        }

        if (step.EntryId is { } eid)
        {
            parts.Add($"entryId={eid}");
        }

        if (step.CatalogueId is { } catid)
        {
            parts.Add($"catalogueId={catid}");
        }

        if (step.ForceId is { } fid)
        {
            parts.Add($"forceId={fid}");
        }

        if (step.SelectionId is { } selid)
        {
            parts.Add($"selectionId={selid}");
        }

        if (step.Count is { } cnt)
        {
            parts.Add($"count={cnt}");
        }

        if (step.CostTypeId is { } ctid)
        {
            parts.Add($"costTypeId={ctid}");
        }

        if (step.Value is { } val)
        {
            parts.Add($"value={val}");
        }

        return string.Join(" ", parts);
    }

    if (step.ExpectedState is not null)
    {
        return "expectedState (assertion)";
    }

    return "(unknown)";
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage: bs-spec-debug [options] <spec>
               bs-spec-debug --format [--check] [<dir>]

        Arguments:
          <spec>          Spec file path, spec ID (e.g. "selection/selection-page"),
                          or "-" for stdin

        Options:
          --engine <name> Engine to use: bs (default), nr, nr-ui
          --dump          Dump state after every step (default: after last step only)
          --json          Output state as JSON instead of pretty tree
          --no-headless   Show browser window (NR engine only)
          --export-xml <dir>  Generate BattleScribe XML files from spec setup and exit
          --format [<dir>]    Format all *.yaml files under <dir> (default: specs/roster/)
          --check             With --format: report issues without fixing (exit 1 if any)
          -h, --help      Show this help

        Examples:
          bs-spec-debug specs/selection/selection-page.yaml
          bs-spec-debug selection/selection-page
          bs-spec-debug selection-page
          bs-spec-debug --engine nr --dump specs/protocol/protocol-kitchen-sink.yaml
          bs-spec-debug --export-xml ./output/ cost/cost-hidden-limit-validation
          cat spec.yaml | bs-spec-debug -
          bs-spec-debug --format
          bs-spec-debug --format --check specs/roster/
        """);
}

/// <summary>Expose the compiler-generated Program class for test invocation.</summary>
public partial class Program
{
    /// <summary>Entry point for test invocation. Forwards to the compiler-generated &lt;Main&gt;$.</summary>
    public static Task<int> RunAsync(params string[] args) =>
        typeof(Program)
            .GetMethod("<Main>$", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [args]) as Task<int>
        ?? Task.FromResult(1);
}
