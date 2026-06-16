using System.Text.Json;
using BattleScribeSpec;
using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Debugger;
using BattleScribeSpec.GameData;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

// ===== Parse arguments =====
string? specInput = null;
var engineName = "battlescribe";
string? engineType = null; // "roster" or "gamedata"; inferred from spec path if unset
var dumpAll = false;
var json = false;
var headless = true;
string? exportXmlDir = null;
string? exportRosterDir = null;
string? screenshotsDir = null;
string? reportPath = null;
var keepAlive = false;
var probeMode = false;
string? recordPath = null;
var formatMode = false;
var formatCheck = false;
string? formatDir = null;
int? stopBefore = null;

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
            {
                var engineArg = args[++i].ToLowerInvariant();
                if (engineArg.Contains('/'))
                {
                    var parts = engineArg.Split('/', 2);
                    engineType = parts[0];
                    engineName = parts[1];
                }
                else
                {
                    engineName = engineArg;
                }
            }
            break;
        case "--dump":
            dumpAll = true;
            break;
        case "--probe":
            probeMode = true;
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
        case "--screenshots" when i + 1 < args.Length:
            screenshotsDir = args[++i];
            break;
        case "--keep-alive":
            keepAlive = true;
            break;
        case "--record" when i + 1 < args.Length:
            recordPath = args[++i];
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        case "--stop-before" when i + 1 < args.Length:
            stopBefore = int.Parse(args[++i]);
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

// ===== Export XML mode (standalone — exits before any engine resolution/validation) =====
if (exportXmlDir is not null)
{
    SpecFile xmlSpec;
    try
    {
        xmlSpec = LoadSpec(specInput);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error loading spec: {ex.Message}");
        return 1;
    }

    if (xmlSpec.Setup.DataSource is { Length: > 0 })
    {
        Console.Error.WriteLine("Error: --export-xml is not supported for dataSource specs.");
        return 1;
    }

    var (xmlGameSystem, xmlCatalogues) = SpecLoader.GetSetupData(xmlSpec.Setup, xmlSpec.Id);
    var specExportDir = Path.Combine(exportXmlDir, xmlSpec.Id);
    Directory.CreateDirectory(specExportDir);

    var gstOut = Path.Combine(specExportDir, $"{SanitizeFileName(xmlGameSystem.Name)}.gst");
    File.WriteAllText(gstOut, CatXmlGenerator.GenerateGameSystemXml(xmlGameSystem));
    Console.Error.WriteLine($"Wrote {gstOut}");

    for (var catIdx = 0; catIdx < xmlCatalogues.Length; catIdx++)
    {
        var catName = SanitizeFileName(xmlCatalogues[catIdx].Name);
        // Deduplicate filename if needed (e.g. two catalogues with same sanitized name)
        var catFileName = catIdx == 0 || Enumerable.Range(0, catIdx).All(j => SanitizeFileName(xmlCatalogues[j].Name) != catName)
            ? catName
            : $"{catName}-{catIdx + 1}";
        var catOut = Path.Combine(specExportDir, $"{catFileName}.cat");
        File.WriteAllText(catOut, CatXmlGenerator.GenerateCatalogueXml(xmlGameSystem, xmlCatalogues[catIdx]));
        Console.Error.WriteLine($"Wrote {catOut}");
    }

    Console.Error.WriteLine($"Exported {1 + xmlCatalogues.Length} file(s) to {specExportDir}");
    return 0;
}

// ===== Resolve engine type (roster vs gamedata) =====
// If --engine used the <type>/<name> form, engineType is already set. Otherwise infer
// it from the resolved spec path: contains "gamedata" → gamedata, "roster" → roster,
// defaulting to roster when unknowable.
engineType ??= InferEngineType(specInput);

// Validate engine name.
var validEngineNames = new[] { "battlescribe", "battlescribe-ui", "newrecruit", "newrecruit-ui" };
if (!validEngineNames.Contains(engineName))
{
    Console.Error.WriteLine(
        $"Unknown engine name: '{engineName}'. Valid names: {string.Join(", ", validEngineNames)}.");
    PrintUsage();
    return 1;
}

// ===== GameData engines (type=gamedata): load gamedata spec, not roster spec =====
if (engineType == "gamedata")
{
    if (probeMode && engineName == "battlescribe-ui")
    {
        return await RunBsGameDataUiProbe(specInput);
    }

    if (probeMode && engineName == "newrecruit-ui")
    {
        return await RunNrGameDataUiProbe(specInput, headless: false);
    }

    if (probeMode)
    {
        Console.Error.WriteLine($"--probe is only supported for gamedata UI engines (battlescribe-ui, newrecruit-ui).");
        return 1;
    }

    // Non-probe: run a gamedata spec with assertions against the chosen gamedata engine.
    return await RunGameDataSpec(specInput, engineName, headless, dumpAll, json);
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


// ===== BS UI Probe mode (roster) =====
if (probeMode && engineName is "battlescribe-ui")
{
    return await RunBsUiProbe(spec);
}

// ===== NR UI Probe mode (roster) =====
if (probeMode && engineName is "newrecruit-ui")
{
    return await RunNrUiProbe(spec, headless: false);
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
    // The roster UI engines reuse their non-UI counterpart's assertion overrides since the
    // UI engine drives the same underlying product (battlescribe-ui IS BattleScribe, etc.).
    var assertionEngineName = engineName switch
    {
        "battlescribe-ui" => "battlescribe",
        "newrecruit-ui" => "newrecruit",
        _ => engineName
    };
    var runner = new RosterRunner(engine, new DataSourceResolver(), assertionEngineName);

    var stepCount = spec.Steps.Count;
    var lastStepIndex = stepCount - 1;
    var timeline = reportPath is not null ? new TimelineReport(spec.Id) : null;

    runner.OnStepCompleted = (stepIndex, step, state, errors) =>
    {
        var isDumpAction = step.Action == "dump";
        var isLastStep = stepIndex == lastStepIndex;
        var shouldDump = isDumpAction || dumpAll || isLastStep;

        // Capture screenshot for screenshots dir and/or timeline report (bs-ui and nr-ui engines)
        byte[]? screenshotBytes = null;
        if ((screenshotsDir is not null || timeline is not null) && engine is BsUiRosterEngine screenshotEngine)
        {
            try
            {
                screenshotBytes = screenshotEngine.CaptureScreenshotAsync().GetAwaiter().GetResult();
                if (screenshotBytes is not null && screenshotsDir is not null)
                {
                    Directory.CreateDirectory(screenshotsDir);
                    var actionName = SanitizeFileName(step.Action ?? "assert");
                    var fileName = $"{stepIndex:D3}_{actionName}.png";
                    var filePath = Path.Combine(screenshotsDir, fileName);
                    File.WriteAllBytes(filePath, screenshotBytes);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screenshots] Step {stepIndex} capture failed: {ex.Message}");
            }
        }
        else if ((screenshotsDir is not null || timeline is not null) && engine is NrRosterUiEngine nrUiEngine)
        {
            try
            {
                screenshotBytes = nrUiEngine.CaptureScreenshotAsync().GetAwaiter().GetResult();
                if (screenshotBytes is not null && screenshotsDir is not null)
                {
                    Directory.CreateDirectory(screenshotsDir);
                    var actionName = SanitizeFileName(step.Action ?? "assert");
                    var fileName = $"{stepIndex:D3}_{actionName}.png";
                    var filePath = Path.Combine(screenshotsDir, fileName);
                    File.WriteAllBytes(filePath, screenshotBytes);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screenshots] Step {stepIndex} capture failed: {ex.Message}");
            }
        }

        timeline?.AddStep(stepIndex, step, state, errors, screenshotBytes);

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

    // Stop-before: pause execution before a specific step and drop into REPL
    if (stopBefore is not null)
    {
        runner.OnBeforeStep = (stepIndex, step) =>
        {
            if (stepIndex != stopBefore.Value)
            {
                return true;
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine($"═══ Stopped before step {stepIndex}: {DescribeStep(step)} ═══");

            if (engine is NrRosterUiEngine nrUiStopEngine)
            {
                Console.Error.WriteLine("NR UI page available. Enter JS expressions (exit/quit to continue):");
                Console.Error.Write("> ");
                while (true)
                {
                    var line = Console.In.ReadLine();
                    if (line is null or "exit" or "quit")
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        Console.Error.Write("> ");
                        continue;
                    }

                    try
                    {
                        var evalResult = nrUiStopEngine.EvaluateAsync<JsonElement>(line).GetAwaiter().GetResult();
                        Console.Out.WriteLine(evalResult.ToString());
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error: {ex.Message}");
                    }

                    Console.Error.Write("> ");
                }
            }
            else
            {
                Console.Error.WriteLine("Press Enter to continue execution, or Ctrl+C to abort...");
                Console.In.ReadLine();
            }

            return true; // continue execution after REPL
        };
    }

    Console.Error.WriteLine($"Running {stepCount} steps...");
    Console.Error.WriteLine();

    // Start action recording if requested (bs-ui engine only)
    if (recordPath is not null && engine is BsUiRosterEngine recordingEngine)
    {
        await recordingEngine.StartRecordingAsync();
        Console.Error.WriteLine("Recording UI actions...");
    }

    var result = runner.Run(spec);

    // Stop recording and save captured actions
    if (recordPath is not null && engine is BsUiRosterEngine recordStopEngine)
    {
        try
        {
            var actions = await recordStopEngine.StopRecordingAsync();
            if (actions is not null)
            {
                var jsonStr = actions.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(recordPath, jsonStr);
                Console.Error.WriteLine($"Recorded actions saved to: {recordPath}");
            }
            else
            {
                Console.Error.WriteLine("Warning: no actions recorded.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to save recorded actions: {ex.Message}");
        }
    }

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
        // Show diagnostic dump paths if any were captured during the run
        var diagDir = BsUiDiagnostics.DiagnosticsDirectory;
        if (Directory.Exists(diagDir))
        {
            var diagFiles = Directory.GetFiles(diagDir, "*.txt")
                .OrderByDescending(f => f)
                .Take(3)
                .ToArray();
            if (diagFiles.Length > 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Diagnostic dumps:");
                foreach (var f in diagFiles)
                {
                    Console.Error.WriteLine($"    {f}");
                }
            }
        }
    }

    if (timeline is not null && reportPath is not null)
    {
        timeline.Write(reportPath, result.Failures.Count == 0, result.Failures);
        Console.Error.WriteLine($"Timeline report: {reportPath}");
    }

    if (!headless && engine is NrRosterUiEngine)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("NR UI: Browser will remain open. Press Enter to close...");
        Console.In.ReadLine();
    }

    return result.Failures.Count == 0 ? 0 : 1;
}

// ===== Helpers =====

async Task<int> RunBsUiProbe(SpecFile spec)
{
    if (spec.Setup.DataSource is { Length: > 0 })
    {
        Console.Error.WriteLine("Error: --engine bs-ui does not support dataSource specs yet.");
        return 1;
    }

    var options = ResolveBsUiOptions();

    var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);

    // Generate XML files
    var xmlFiles = new List<(string FileName, string Content)>
    {
        ($"{gameSystem.Id}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem))
    };
    foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
    {
        xmlFiles.Add((fileName, xml));
    }

    Console.Error.WriteLine($"BS UI Probe — launching with {xmlFiles.Count} data file(s)");

    await using var probe = new BsUiProbe(options);
    await probe.LaunchAsync(gameSystem, catalogues, xmlFiles, Console.Error);

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

async Task<int> RunNrUiProbe(SpecFile spec, bool headless)
{
    if (spec.Setup.DataSource is { Length: > 0 })
    {
        Console.Error.WriteLine("Error: --engine nr-ui probe does not support dataSource specs yet.");
        return 1;
    }

    var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);

    Console.Error.WriteLine($"NR UI Probe — launching with {catalogues.Length + 1} data file(s)");

    await using var probe = new NrUiProbe();
    var url = Environment.GetEnvironmentVariable("NR_ENGINE_URL") ?? "https://newrecruit.eu";
    await probe.LaunchAsync(gameSystem, catalogues, url, Console.Error);

    Console.Error.WriteLine();
    Console.Error.WriteLine("NR UI probe ready. Browser is open.");
    Console.Error.WriteLine("Entering REPL — type JS expressions to evaluate, 'exit' to quit:");

    await probe.RunReplAsync(Console.In, Console.Out);

    return 0;
}

async Task<int> RunBsGameDataUiProbe(string specInput)
{
    GameDataSpecFile gameDataSpec;
    try
    {
        gameDataSpec = LoadGameDataSpec(specInput);
        Console.Error.WriteLine($"Loaded GameData spec: {gameDataSpec.Category}/{gameDataSpec.Id} — {gameDataSpec.Description}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error loading GameData spec: {ex.Message}");
        return 1;
    }

    var (gameSystem, catalogues) = SpecLoader.GetGameDataSetupData(gameDataSpec.Setup);

    // Resolve the *Data Editor* jar (DataEditor.jar) — the same artifacts the gamedata engine
    // uses. ResolveBsUiOptions() returns RosterEditor.jar, which opens the Roster Editor instead.
    var options = BsGameDataUiEngine.FindOptions()
        ?? throw new InvalidOperationException(
            "BS UI artifacts not found — run setup.ps1 (installs the Liberica JDK and builds the " +
            "agent jar), or set BS_UI_JAVA_PATH and ensure DataEditor.jar + the agent jar exist.");
    Console.Error.WriteLine($"BattleScribe Data Editor UI: {options.RosterEditorJarPath}");
    Console.Error.WriteLine($"BS GameData UI Probe — launching with {catalogues.Length + 1} data file(s)");

    await using var probe = new BsGameDataUiProbe(options);
    await probe.LaunchAsync(gameSystem, catalogues, Console.Error);

    Console.Error.WriteLine();
    Console.Error.WriteLine("BS GameData UI probe complete. BattleScribe is running.");
    Console.Error.WriteLine("Press Enter to shut down...");
    Console.In.ReadLine();

    return 0;
}

async Task<int> RunNrGameDataUiProbe(string specInput, bool headless)
{
    GameDataSpecFile gameDataSpec;
    try
    {
        gameDataSpec = LoadGameDataSpec(specInput);
        Console.Error.WriteLine($"Loaded GameData spec: {gameDataSpec.Category}/{gameDataSpec.Id} — {gameDataSpec.Description}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error loading GameData spec: {ex.Message}");
        return 1;
    }

    var (gameSystem, catalogues) = SpecLoader.GetGameDataSetupData(gameDataSpec.Setup);

    Console.Error.WriteLine($"NR Editor GameData UI Probe — launching with {catalogues.Length + 1} data file(s)");

    await using var probe = new NrGameDataUiProbe();

    var staticDir = NewRecruitGameDataEngine.FindFrozenStaticDir();
    if (staticDir is not null)
    {
        Console.Error.WriteLine($"  Using frozen NR Editor static files: {staticDir}");
        await probe.LaunchFrozenAsync(staticDir, gameSystem, catalogues, Console.Error);
    }
    else
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_EDITOR_URL")
            ?? "https://giloushaker.github.io/nr-editor";
        Console.Error.WriteLine($"  Using live NR Editor: {baseUrl}");
        await probe.LaunchAsync(gameSystem, catalogues, baseUrl, Console.Error);
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("NR Editor GameData UI probe ready. Browser is open.");
    Console.Error.WriteLine("Entering REPL — type JS expressions to evaluate, 'exit' to quit:");

    await probe.RunReplAsync(Console.In, Console.Out);

    return 0;
}

// Runs a GameData spec end-to-end against a GameData UI engine, with assertions and
// optional per-step state dumps. This is the assertion-based counterpart to the gamedata
// --probe modes (which only open the editor for interactive inspection).
async Task<int> RunGameDataSpec(string? specInput, string engine, bool headless, bool dumpAll, bool jsonDump)
{
    if (specInput is null)
    {
        Console.Error.WriteLine($"Error: a gamedata spec path/id is required for engine '{engine}'.");
        return 1;
    }

    GameDataSpecFile spec;
    try
    {
        spec = LoadGameDataSpec(specInput);
        Console.Error.WriteLine($"Loaded GameData spec: {spec.Category}/{spec.Id} — {spec.Description}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error loading GameData spec: {ex.Message}");
        return 1;
    }

    if (!spec.IsApplicableTo(engine))
    {
        Console.Error.WriteLine($"Spec '{spec.Id}' is not applicable to engine '{engine}' (skipped).");
        return 0;
    }

    Console.Error.WriteLine($"Engine: {engine}");
    IGameDataEngine gameDataEngine;
    try
    {
        switch (engine)
        {
            case "newrecruit-ui":
                {
                    var staticDir = NrGameDataUiEngine.FindFrozenStaticDir()
                        ?? throw new InvalidOperationException(
                            "NR Editor frozen static dir not found (.testdata/nr-editor) — run setup.ps1.");
                    Console.Error.WriteLine($"NR Editor GameData UI (frozen): {staticDir}");
                    gameDataEngine = await NrGameDataUiEngine.CreateFrozenAsync(staticDir, headless);
                    break;
                }

            case "battlescribe-ui":
                {
                    var options = BsGameDataUiEngine.FindOptions()
                        ?? throw new InvalidOperationException(
                            "BS UI artifacts not found — run setup.ps1 (installs the Liberica JDK and builds the " +
            "agent jar), or set BS_UI_JAVA_PATH and ensure DataEditor.jar + the agent jar exist.");
                    Console.Error.WriteLine($"BattleScribe Data Editor UI: {options.RosterEditorJarPath}");
                    gameDataEngine = new BsGameDataUiEngine(options);
                    break;
                }

            case "newrecruit":
                {
                    var staticDir = NewRecruitGameDataEngine.FindFrozenStaticDir()
                        ?? throw new InvalidOperationException(
                            "NR Editor frozen static dir not found (.testdata/nr-editor) — run setup.ps1.");
                    Console.Error.WriteLine($"NewRecruit GameData (frozen): {staticDir}");
                    gameDataEngine = await NewRecruitGameDataEngine.CreateFrozenAsync(staticDir, headless);
                    break;
                }

            case "battlescribe":
                {
                    Console.Error.WriteLine("BattleScribe GameData (in-process)");
                    gameDataEngine = new BattleScribeGameDataEngine();
                    break;
                }

            default:
                throw new ArgumentException(
                    $"Unknown gamedata engine: '{engine}'. Use 'battlescribe', 'newrecruit', 'battlescribe-ui', or 'newrecruit-ui'.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error creating engine: {ex.Message}");
        return 1;
    }

    using (gameDataEngine)
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var runner = new GameDataRunner(gameDataEngine, engine)
        {
            OnStepCompleted = (index, step, state) =>
            {
                Console.Error.WriteLine($"  step {index}: {step.Action ?? "assert"}");
                if (dumpAll || jsonDump)
                {
                    Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(state, jsonOptions));
                }
            },
        };

        var result = runner.Run(spec);

        if (result.Passed)
        {
            Console.Error.WriteLine($"PASS — {spec.Id} on {engine} ({spec.Steps.Count} step(s))");
            return 0;
        }

        Console.Error.WriteLine($"FAIL — {spec.Id} on {engine}: {result.Failures.Count} error(s):");
        foreach (var (failure, i) in result.Failures.Select((f, i) => (f, i)))
        {
            Console.Error.WriteLine($"  [{i + 1}] {failure}");
        }

        return 1;
    }
}

BsUiOptions ResolveBsUiOptions()
{
    // Resolve paths from environment variables or conventional locations
    var appDir = Environment.GetEnvironmentVariable("BS_UI_APP_DIR");
    var agentJar = Environment.GetEnvironmentVariable("BS_UI_AGENT_JAR");

    // Fallback: look for conventional locations relative to repo root
    var repoRoot = FindRepoRoot();

    // BS_UI_JAVA_PATH → repo-local Liberica JDK → bundled platform JRE. See BsUiPaths.
    var javaPath = repoRoot is not null
        ? BsUiPaths.ResolveJavaPath(repoRoot)
        : Environment.GetEnvironmentVariable("BS_UI_JAVA_PATH");

    if (appDir is null && repoRoot is not null)
    {
        var candidate = Path.Combine(repoRoot, "lib", "battlescribe");
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
            "Java runtime not found. Run setup.ps1 to install the repo-local Liberica JDK " +
            "(lib/liberica-jdk), or set BS_UI_JAVA_PATH to a JavaFX-capable java.");
    }

    var rosterEditorJar = appDir is not null
        ? Path.Combine(appDir, "RosterEditor.jar")
        : throw new InvalidOperationException(
            "BS app directory not found. Set BS_UI_APP_DIR env var or place app at lib/battlescribe/");

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

// Infer the engine type ("gamedata" or "roster") from the resolved spec path.
// Rules: a path/id containing "gamedata" → gamedata, "roster" → roster; default roster.
string InferEngineType(string? input)
{
    if (input is null or "-")
    {
        return "roster";
    }

    // Resolve to an absolute path when the input is (or can be located as) a real file,
    // so inference works even for bare spec IDs.
    var resolved = input;
    if (!File.Exists(input))
    {
        var gameDataDir = SpecLoader.FindGameDataSpecsDirectory();
        if (gameDataDir is not null)
        {
            foreach (var file in Directory.EnumerateFiles(gameDataDir, "*.yaml", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var relative = Path.GetRelativePath(gameDataDir, file).Replace('\\', '/');
                if (name == input || relative == input || relative == input + ".yaml")
                {
                    return "gamedata";
                }
            }
        }
    }
    else
    {
        resolved = Path.GetFullPath(input);
    }

    var normalized = resolved.Replace('\\', '/').ToLowerInvariant();
    if (normalized.Contains("gamedata"))
    {
        return "gamedata";
    }

    if (normalized.Contains("roster"))
    {
        return "roster";
    }

    return "roster";
}

static string? FindRepoRoot()
{
    // Anchor on the solution file rather than .git: in a git worktree, .git is a file
    // (gitdir pointer), so Directory.Exists(".git") would miss it. Walk up from both the
    // current directory and the assembly location so it works regardless of cwd.
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = start;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }
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

GameDataSpecFile LoadGameDataSpec(string input)
{
    if (input == "-")
    {
        var yaml = Console.In.ReadToEnd();
        return SpecLoader.LoadGameDataFromYaml(yaml, defaultId: "stdin");
    }

    if (File.Exists(input))
    {
        return SpecLoader.LoadGameData(input);
    }

    // Try as spec ID in specs/gamedata/
    var specsDir = SpecLoader.FindGameDataSpecsDirectory();
    if (specsDir is not null)
    {
        var candidate = Path.Combine(specsDir, input + ".yaml");
        if (File.Exists(candidate))
        {
            return SpecLoader.LoadGameData(candidate);
        }

        foreach (var file in Directory.EnumerateFiles(specsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var relative = Path.GetRelativePath(specsDir, file).Replace('\\', '/');
            if (name == input || relative == input || relative == input + ".yaml")
            {
                return SpecLoader.LoadGameData(file);
            }
        }
    }

    throw new FileNotFoundException($"GameData spec not found: '{input}'. Provide a file path, category/id, or id.");
}

async Task<IRosterEngine> CreateEngine(string name, bool headless)
{
    switch (name)
    {
        case "battlescribe":
            return new BattleScribeRosterEngine();

        case "newrecruit":
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

        case "battlescribe-ui":
            {
                var bsUiOptions = ResolveBsUiOptions();
                Console.Error.WriteLine($"BS UI mode: {bsUiOptions.RosterEditorJarPath}");
                return new BsUiRosterEngine(bsUiOptions) { KeepAlive = keepAlive };
            }

        case "newrecruit-ui":
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
            throw new ArgumentException(
                $"Unknown roster engine: '{name}'. Use 'battlescribe', 'newrecruit', 'battlescribe-ui', or 'newrecruit-ui'.");
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

static string SanitizeFileName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    return new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
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
          --engine <type>/<name>
                          Engine to use. <type> ∈ {roster, gamedata} (optional — inferred
                          from the spec path when omitted: specs/gamedata/... → gamedata,
                          specs/roster/... → roster, default roster).
                          <name> ∈ {battlescribe, battlescribe-ui, newrecruit, newrecruit-ui}.
                          Default: roster/battlescribe.
          --dump          Dump state after every step (default: after last step only)
          --probe         Run probe mode (the -ui engines of either type:
                          roster/battlescribe-ui, roster/newrecruit-ui,
                          gamedata/battlescribe-ui, gamedata/newrecruit-ui)
          --json          Output state as JSON instead of pretty tree
          --no-headless   Show browser window (NR engine only)
          --export-xml <dir>  Generate BattleScribe XML files from spec setup and exit
          --export-roster <dir>  Export final roster as .ros XML (roster/battlescribe-ui only)
          --screenshots <dir>  Capture screenshot at each step (roster -ui engines only)
          --report <file>  Generate HTML timeline report (roster/battlescribe-ui only)
          --record <file>  Record UI actions to JSON file (roster/battlescribe-ui only)
          --keep-alive     Keep BattleScribe app running between runs (roster/battlescribe-ui only)
          --format [<dir>]    Format all *.yaml files under <dir> (default: specs/roster/)
          --check             With --format: report issues without fixing (exit 1 if any)
          -h, --help      Show this help

        Examples:
          bs-spec-debug specs/selection/selection-page.yaml
          bs-spec-debug selection/selection-page
          bs-spec-debug selection-page
          bs-spec-debug --engine roster/newrecruit --dump specs/protocol/protocol-kitchen-sink.yaml
          bs-spec-debug --export-xml ./output/ cost/cost-hidden-limit-validation
          bs-spec-debug --engine roster/battlescribe-ui --probe selection/selection-page
          bs-spec-debug --engine roster/battlescribe-ui selection/selection-page
          bs-spec-debug --engine gamedata/battlescribe-ui --probe gamedata/basic/entry-add
          bs-spec-debug --engine gamedata/newrecruit-ui --probe gamedata/basic/entry-add
          bs-spec-debug --engine gamedata/newrecruit-ui specs/gamedata/entry/add-entry-basic.yaml
          bs-spec-debug --engine gamedata/newrecruit specs/gamedata/entry/se-create-in-gamesystem.yaml
          bs-spec-debug --engine gamedata/battlescribe --dump specs/gamedata/entry/move-entry.yaml
          bs-spec-debug --engine newrecruit-ui specs/gamedata/entry/add-entry-basic.yaml  # type inferred
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
