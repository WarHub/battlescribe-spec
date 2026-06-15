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

// ===== GameData UI Probe modes (load gamedata spec, not roster spec) =====
if (probeMode && engineName is "battlescribe-ui")
{
    return await RunBsGameDataUiProbe(specInput);
}

if (probeMode && engineName is "nr-editor-ui")
{
    return await RunNrGameDataUiProbe(specInput, headless: false);
}

// ===== GameData spec run modes (non-probe): run a gamedata spec with assertions =====
if (engineName is "battlescribe-ui" or "nr-editor-ui")
{
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
if (probeMode && engineName is "bs-ui")
{
    return await RunBsUiProbe(spec);
}

// ===== NR UI Probe mode =====
if (probeMode && engineName is "nr-ui")
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
    // bs-ui engine uses "battlescribe" assertion overrides since it IS the BattleScribe engine
    // nr-ui engine uses "newrecruit" assertion overrides since it IS the NR engine
    var assertionEngineName = engineName switch
    {
        "bs-ui" => "battlescribe",
        "nr-ui" => "newrecruit",
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

    var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup);

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

    var options = ResolveBsUiOptions();
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
        if (engine == "nr-editor-ui")
        {
            var staticDir = NrGameDataUiEngine.FindFrozenStaticDir()
                ?? throw new InvalidOperationException(
                    "NR Editor frozen static dir not found (.testdata/nr-editor) — run setup.ps1.");
            Console.Error.WriteLine($"NR Editor GameData UI (frozen): {staticDir}");
            gameDataEngine = await NrGameDataUiEngine.CreateFrozenAsync(staticDir, headless);
        }
        else // battlescribe-ui
        {
            var options = BsGameDataUiEngine.FindOptions()
                ?? throw new InvalidOperationException(
                    "BS UI artifacts not found — set BS_UI_JAVA_PATH and ensure DataEditor.jar + the agent jar exist.");
            Console.Error.WriteLine($"BattleScribe Data Editor UI: {options.RosterEditorJarPath}");
            gameDataEngine = new BsGameDataUiEngine(options);
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
    var javaPath = Environment.GetEnvironmentVariable("BS_UI_JAVA_PATH");
    var appDir = Environment.GetEnvironmentVariable("BS_UI_APP_DIR");
    var agentJar = Environment.GetEnvironmentVariable("BS_UI_AGENT_JAR");

    // Fallback: look for conventional locations relative to repo root
    var repoRoot = FindRepoRoot();

    if (javaPath is null && repoRoot is not null)
    {
        // Try platform-specific JRE paths under lib/battlescribe
        var jreDir = Path.Combine(repoRoot, "lib", "battlescribe");
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
            "Java path not found. Set BS_UI_JAVA_PATH env var or place JRE at lib/battlescribe/jre-{platform}/");
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
                return new BsUiRosterEngine(bsUiOptions) { KeepAlive = keepAlive };
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
            throw new ArgumentException($"Unknown engine: '{name}'. Use 'bs', 'nr', 'bs-ui', or 'nr-ui'.");
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
          --engine <name> Engine to use: bs (default), nr, bs-ui, nr-ui, battlescribe-ui, nr-editor-ui
                          (battlescribe-ui / nr-editor-ui run GameData specs with assertions)
          --dump          Dump state after every step (default: after last step only)
          --probe         Run probe mode (bs-ui, nr-ui, battlescribe-ui, nr-editor-ui engines)
          --json          Output state as JSON instead of pretty tree
          --no-headless   Show browser window (NR engine only)
          --export-xml <dir>  Generate BattleScribe XML files from spec setup and exit
          --export-roster <dir>  Export final roster as .ros XML (bs-ui engine only)
          --screenshots <dir>  Capture screenshot at each step (bs-ui engine only)
          --report <file>  Generate HTML timeline report (bs-ui engine only)
          --record <file>  Record UI actions to JSON file (bs-ui engine only)
          --keep-alive     Keep BattleScribe app running between runs (bs-ui only)
          --format [<dir>]    Format all *.yaml files under <dir> (default: specs/roster/)
          --check             With --format: report issues without fixing (exit 1 if any)
          -h, --help      Show this help

        Examples:
          bs-spec-debug specs/selection/selection-page.yaml
          bs-spec-debug selection/selection-page
          bs-spec-debug selection-page
          bs-spec-debug --engine nr --dump specs/protocol/protocol-kitchen-sink.yaml
          bs-spec-debug --export-xml ./output/ cost/cost-hidden-limit-validation
          bs-spec-debug --engine bs-ui --probe selection/selection-page
          bs-spec-debug --engine bs-ui selection/selection-page
          bs-spec-debug --engine battlescribe-ui --probe gamedata/basic/entry-add
          bs-spec-debug --engine nr-editor-ui --probe gamedata/basic/entry-add
          bs-spec-debug --engine nr-editor-ui gamedata/entry/add-entry-nested
          bs-spec-debug --engine battlescribe-ui --dump gamedata/entry/move-entry
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
