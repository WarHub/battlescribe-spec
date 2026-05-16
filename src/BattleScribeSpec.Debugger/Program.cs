using BattleScribeSpec;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Roster;

// ===== Parse arguments =====
string? specInput = null;
var engineName = "battlescribe";
var dumpAll = false;
var json = false;
var headless = true;
string? exportXmlDir = null;
string? exportRosterDir = null;
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
        case "--export-roster" when i + 1 < args.Length:
            exportRosterDir = args[++i];
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

// ===== BS UI Probe mode =====
if (engineName is "bs-ui" && dumpAll && spec.Steps.Count == 0)
{
    // Probe-only mode (no spec steps, just dump tree)
    return await RunBsUiProbe(spec, dumpAll, json);
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
    // bs-ui engine uses "battlescribe" assertion overrides since it IS the BattleScribe engine
    var assertionEngineName = engineName == "bs-ui" ? "battlescribe" : engineName;
    var runner = new RosterRunner(engine, new DataSourceResolver(), assertionEngineName);

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

        // Export roster XML on the last step (before Cleanup disconnects the agent)
        if (isLastStep && exportRosterDir is not null && engine is BsUiRosterEngine bsUiEngine)
        {
            try
            {
                Directory.CreateDirectory(exportRosterDir);
                var xml = bsUiEngine.ExportRosterXmlAsync().GetAwaiter().GetResult();
                if (xml is not null)
                {
                    var rosterFile = Path.Combine(exportRosterDir, $"{spec.Id}.ros");
                    File.WriteAllText(rosterFile, xml);
                    Console.Error.WriteLine($"Exported roster to: {rosterFile}");
                }
                else
                {
                    Console.Error.WriteLine("Warning: exportRosterXml returned null.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: roster export failed: {ex.Message}");
            }
        }
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

async Task<int> RunBsUiProbe(SpecFile spec, bool dumpAll, bool json)
{
    if (spec.Setup.DataSource is { Length: > 0 })
    {
        Console.Error.WriteLine("Error: --engine bs-ui does not support dataSource specs yet.");
        return 1;
    }

    var options = ResolveBsUiOptions();

    var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup);

    // Generate XML files
    var xmlFiles = new List<(string FileName, string Content)>
    {
        ("system.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem))
    };
    foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
    {
        xmlFiles.Add((fileName, xml));
    }

    Console.Error.WriteLine($"BS UI Probe — launching with {xmlFiles.Count} data file(s)");

    await using var probe = new BsUiProbe(options);
    await probe.LaunchAsync(xmlFiles, Console.Error);

    Console.Error.WriteLine();
    Console.Error.WriteLine("═══ Scene Graph Dump ═══");
    await probe.DumpTreeAsync(Console.Out);

    Console.Error.WriteLine();
    Console.Error.WriteLine("═══ Windows ═══");
    await probe.DumpWindowsAsync(Console.Out);

    Console.Error.WriteLine();
    Console.Error.WriteLine("BS UI probe complete. BattleScribe is running.");
    Console.Error.WriteLine("Press Enter to shut down...");
    Console.In.ReadLine();

    return 0;
}

BsUiOptions ResolveBsUiOptions()
{
    // Resolve paths from environment variables or conventional locations
    var javaPath = Environment.GetEnvironmentVariable("BS_UI_JAVA_PATH");
    var appDir = Environment.GetEnvironmentVariable("BS_UI_APP_DIR");
    var agentJar = Environment.GetEnvironmentVariable("BS_UI_AGENT_JAR");

    // Fallback: look for conventional locations relative to repo root
    var repoRoot = FindRepoRoot();

    if (javaPath is null && repoRoot is not null)
    {
        // Try platform-specific JRE paths under .testdata
        var jreDir = Path.Combine(repoRoot, ".testdata", "battlescribe-app");
        if (OperatingSystem.IsWindows())
        {
            var winJava = Path.Combine(jreDir, "jre-win", "bin", "java.exe");
            if (File.Exists(winJava))
            {
                javaPath = winJava;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var macJava = Path.Combine(jreDir, "jre-mac", "bin", "java");
            if (File.Exists(macJava))
            {
                javaPath = macJava;
            }
        }
        else
        {
            var linuxJava = Path.Combine(jreDir, "jre", "bin", "java");
            if (File.Exists(linuxJava))
            {
                javaPath = linuxJava;
            }
        }
    }

    if (appDir is null && repoRoot is not null)
    {
        var candidate = Path.Combine(repoRoot, ".testdata", "battlescribe-app");
        if (Directory.Exists(candidate))
        {
            appDir = candidate;
        }
    }

    if (agentJar is null && repoRoot is not null)
    {
        var candidate = Path.Combine(repoRoot, "src", "bs-ui-java-agent", "bs-ui-java-agent.jar");
        if (File.Exists(candidate))
        {
            agentJar = candidate;
        }
    }

    if (javaPath is null)
    {
        throw new InvalidOperationException(
            "Java path not found. Set BS_UI_JAVA_PATH env var or place JRE at .testdata/battlescribe-app/jre-{platform}/");
    }

    var rosterEditorJar = appDir is not null
        ? Path.Combine(appDir, "RosterEditor.jar")
        : throw new InvalidOperationException(
            "BS app directory not found. Set BS_UI_APP_DIR env var or place app at .testdata/battlescribe-app/");

    if (!File.Exists(rosterEditorJar))
    {
        throw new InvalidOperationException($"RosterEditor.jar not found at: {rosterEditorJar}");
    }

    if (agentJar is null || !File.Exists(agentJar))
    {
        throw new InvalidOperationException(
            "Agent JAR not found. Set BS_UI_AGENT_JAR env var or build with: pwsh -File src/bs-ui-java-agent/build.ps1");
    }

    Console.Error.WriteLine($"  Java: {javaPath}");
    Console.Error.WriteLine($"  App: {rosterEditorJar}");
    Console.Error.WriteLine($"  Agent: {agentJar}");

    return new BsUiOptions
    {
        JavaPath = javaPath,
        RosterEditorJarPath = rosterEditorJar,
        AgentJarPath = agentJar,
    };
}

static string? FindRepoRoot()
{
    var dir = Directory.GetCurrentDirectory();
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            return dir;
        }

        dir = Path.GetDirectoryName(dir);
    }

    return null;
}

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

        case "bs-ui":
            {
                var bsUiOptions = ResolveBsUiOptions();
                Console.Error.WriteLine($"BS UI mode: {bsUiOptions.RosterEditorJarPath}");
                return new BsUiRosterEngine(bsUiOptions);
            }

        default:
            throw new ArgumentException($"Unknown engine: '{name}'. Use 'bs', 'nr', or 'bs-ui'.");
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
          --engine <name> Engine to use: bs (default), nr, bs-ui
          --dump          Dump state after every step (default: after last step only)
          --json          Output state as JSON instead of pretty tree
          --no-headless   Show browser window (NR engine only)
          --export-xml <dir>  Generate BattleScribe XML files from spec setup and exit
          --export-roster <dir>  Export final roster as .ros XML (bs-ui engine only)
          --format [<dir>]    Format all *.yaml files under <dir> (default: specs/roster/)
          --check             With --format: report issues without fixing (exit 1 if any)
          -h, --help      Show this help

        Examples:
          bs-spec-debug specs/selection/selection-page.yaml
          bs-spec-debug selection/selection-page
          bs-spec-debug selection-page
          bs-spec-debug --engine nr --dump specs/protocol/protocol-kitchen-sink.yaml
          bs-spec-debug --export-xml ./output/ cost/cost-hidden-limit-validation
          bs-spec-debug --engine bs-ui selection/selection-page
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
