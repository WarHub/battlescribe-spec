using System.CommandLine;
using System.Text.Json;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec run &lt;spec&gt;</c> — execute a spec end-to-end against an engine and report
/// pass/fail. Roster and gamedata domains share one pass/fail renderer and one dump path;
/// artifact options are accepted for every engine and skipped (with a uniform warning) when
/// the engine can't honor them.
/// </summary>
internal static class RunCommand
{
    private sealed record RunOptions(
        string Spec,
        EngineSelection Engine,
        OutputFormat Format,
        bool Headed,
        bool AllSteps,
        string? ScreenshotsDir,
        string? TimelinePath,
        string? RecordPath,
        string? SaveRosterDir,
        bool KeepAlive,
        int? BreakAt)
    {
        public bool Headless => !Headed;
    }

    public static Command Create()
    {
        var spec = new Argument<string>("spec")
        {
            Description = "Spec file path, spec ID (e.g. \"selection/selection-page\"), or \"-\" for stdin.",
        };
        var engineOptions = new EngineOptions();
        var output = new Option<OutputFormat>("--output", "-o")
        {
            Description = "State dump format.",
            DefaultValueFactory = _ => OutputFormat.Tree,
        };
        var json = new Option<bool>("--json") { Description = "Shortcut for --output json." };
        var allSteps = new Option<bool>("--all-steps")
        {
            Description = "Dump state after every step (default: after the last step only).",
        };
        var screenshots = new Option<string?>("--screenshots")
        {
            Description = "Capture a screenshot after each step into <dir> (UI engines).",
        };
        var timeline = new Option<string?>("--timeline")
        {
            Description = "Write an HTML timeline report to <file> (screenshots embedded for UI engines).",
        };
        var record = new Option<string?>("--record")
        {
            Description = "Record UI actions to <file> (battlescribe-ui).",
        };
        var saveRoster = new Option<string?>("--save-roster")
        {
            Description = "Save the final roster as .ros XML into <dir> (battlescribe-ui).",
        };
        var keepAlive = new Option<bool>("--keep-alive")
        {
            Description = "Keep the BattleScribe app running between runs (battlescribe-ui).",
        };
        var breakAt = new Option<int?>("--break")
        {
            Description = "Pause before step <n> and drop into a REPL / inspection prompt.",
        };

        var command = new Command("run", "Execute a spec end-to-end against an engine and report pass/fail.");
        command.Arguments.Add(spec);
        engineOptions.AddTo(command);
        foreach (var option in new Option[] { output, json, allSteps, screenshots, timeline, record, saveRoster, keepAlive, breakAt })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, _) =>
        {
            var specInput = parseResult.GetValue(spec)!;
            var keepAliveValue = parseResult.GetValue(keepAlive);
            try
            {
                var options = new RunOptions(
                    Spec: specInput,
                    Engine: engineOptions.Resolve(parseResult, specInput) with { KeepAlive = keepAliveValue },
                    Format: parseResult.GetValue(json) ? OutputFormat.Json : parseResult.GetValue(output),
                    Headed: parseResult.GetValue(engineOptions.Headed),
                    AllSteps: parseResult.GetValue(allSteps),
                    ScreenshotsDir: parseResult.GetValue(screenshots),
                    TimelinePath: parseResult.GetValue(timeline),
                    RecordPath: parseResult.GetValue(record),
                    SaveRosterDir: parseResult.GetValue(saveRoster),
                    KeepAlive: parseResult.GetValue(keepAlive),
                    BreakAt: parseResult.GetValue(breakAt));
                return ExecuteAsync(options);
            }
            catch (CliInputException ex)
            {
                Ui.Error(ex.Message);
                return Task.FromResult(1);
            }
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(RunOptions options)
    {
        try
        {
            return options.Engine.Domain == EngineDomain.Gamedata
                ? await RunGameDataAsync(options)
                : await RunRosterAsync(options);
        }
        catch (CliInputException ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunRosterAsync(RunOptions options)
    {
        SpecFile spec;
        try
        {
            spec = SpecLoading.LoadSpec(options.Spec);
            Ui.Info($"Loaded spec: {spec.Category}/{spec.Id} — {spec.Description}");
        }
        catch (Exception ex)
        {
            Ui.Error($"Error loading spec: {ex.Message}");
            return 1;
        }

        Ui.Info($"Engine: {options.Engine.EngineName ?? options.Engine.Display}");

        // Spawn the engine as a child adapter process (bs-engine-host for built-ins, or any
        // exec:/dotnet: connectable) and drive it entirely over the JSON-line protocol. The
        // describe handshake tells us which optional capabilities the adapter honors.
        AdapterProcess process;
        DescribeResult described;
        try
        {
            process = options.Engine.StartProcess();
            described = await AdapterDescriber.DescribeAsync(process);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error starting engine: {ex.Message}");
            return 1;
        }

        using (process)
        {
            var engine = new JsonProtocolEngine(process,
                spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : null);

            // Accept every artifact option for every engine; warn once and disable the ones
            // the described adapter can't honor (uniform handling, no silent no-ops).
            var screenshotsDir = Gate(options.ScreenshotsDir, described.Capabilities.Screenshot, options.Engine, "--screenshots");
            var recordPath = Gate(options.RecordPath, described.Capabilities.Record, options.Engine, "--record");
            var saveRosterDir = Gate(options.SaveRosterDir, described.Capabilities.RosterXml, options.Engine, "--save-roster");
            var timeline = options.TimelinePath is not null ? new TimelineReport(spec.Id) : null;

            var dumpOptions = new DumpOptions(Json: options.Format == OutputFormat.Json);
            var runner = new RosterRunner(engine, new DataSourceResolver(), options.Engine.AssertionEngineName);
            var lastStepIndex = spec.Steps.Count - 1;

            runner.OnStepCompleted = (stepIndex, step, state, errors) =>
            {
                byte[]? screenshot = null;
                if (screenshotsDir is not null || timeline is not null)
                {
                    try
                    {
                        // Returns the PNG bytes, or throws NotSupportedException when the
                        // adapter can't screenshot — caught below and treated like the old
                        // null path (this step simply gets no image).
                        screenshot = engine.CaptureScreenshot();
                        if (screenshot is not null && screenshotsDir is not null)
                        {
                            Directory.CreateDirectory(screenshotsDir);
                            var fileName = $"{stepIndex:D3}_{StepFormatter.SanitizeFileName(step.Action ?? "assert")}.png";
                            File.WriteAllBytes(Path.Combine(screenshotsDir, fileName), screenshot);
                        }
                    }
                    catch (Exception ex)
                    {
                        Ui.Warn($"step {stepIndex} screenshot capture failed: {ex.Message}");
                    }
                }

                timeline?.AddStep(stepIndex, step, state, errors, screenshot);

                var shouldDump = step.Action == "dump" || options.AllSteps || stepIndex == lastStepIndex;
                if (!shouldDump)
                {
                    return;
                }

                Console.Out.Flush();
                Ui.Blank();
                Ui.Rule($"Step {stepIndex}: {StepFormatter.DescribeStep(step)}");
                StateDumper.Dump(state, errors, Console.Out, dumpOptions);
                Console.Out.Flush();

                if (stepIndex == lastStepIndex && saveRosterDir is not null)
                {
                    try
                    {
                        Directory.CreateDirectory(saveRosterDir);
                        // saveRosterDir is already capability-gated, so ExportRosterXml can't
                        // hit NotSupported here; the try/catch stays for adapter/IO faults.
                        var xml = engine.ExportRosterXml();
                        if (xml is not null)
                        {
                            var rosterFile = Path.Combine(saveRosterDir, $"{spec.Id}.ros");
                            File.WriteAllText(rosterFile, xml);
                            Ui.Info($"Saved roster to: {rosterFile}");
                        }
                        else
                        {
                            Ui.Warn("exportRosterXml returned null.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Ui.Warn($"roster export failed: {ex.Message}");
                    }
                }
            };

            if (options.BreakAt is { } breakStep)
            {
                runner.OnBeforeStep = (stepIndex, step) =>
                {
                    if (stepIndex == breakStep)
                    {
                        // Returns false when the user types `quit`, which aborts the run.
                        return ProtocolBreakRepl.Run(process, stepIndex, StepFormatter.DescribeStep(step));
                    }

                    return true;
                };
            }

            Ui.Info($"Running {spec.Steps.Count} steps...");
            Ui.Blank();

            if (recordPath is not null)
            {
                // recordPath is capability-gated (Record), so this can't hit NotSupported.
                engine.StartRecording();
                Ui.Info("Recording UI actions...");
            }

            var result = runner.Run(spec);

            if (recordPath is not null)
            {
                try
                {
                    var actions = engine.StopRecording();
                    if (actions is not null)
                    {
                        // actions is already a JSON string; the CLI cannot pretty-print it
                        // reflection-free (AOT), so it is written verbatim — the on-disk form
                        // is the adapter's own (typically compact) JSON, not indented.
                        File.WriteAllText(recordPath, actions);
                        Ui.Info($"Recorded actions saved to: {recordPath}");
                    }
                    else
                    {
                        Ui.Warn("no actions recorded.");
                    }
                }
                catch (Exception ex)
                {
                    Ui.Warn($"failed to save recorded actions: {ex.Message}");
                }
            }

            Ui.Blank();
            if (result.Failures.Count == 0)
            {
                Ui.Pass("PASS — all assertions passed");
            }
            else
            {
                Ui.Fail($"FAIL — {result.Failures.Count} failure(s):");
                foreach (var failure in result.Failures)
                {
                    Ui.FailItem(failure);
                }

                ReportDiagnosticDumps();
            }

            if (timeline is not null && options.TimelinePath is not null)
            {
                timeline.Write(options.TimelinePath, result.Failures.Count == 0, result.Failures);
                Ui.Info($"Timeline report: {options.TimelinePath}");
            }

            if (options.Headed && options.Engine.EngineName == "newrecruit-ui")
            {
                Ui.Blank();
                Ui.Info("NR UI: Browser will remain open. Press Enter to close...");
                Console.In.ReadLine();
            }

            return result.Failures.Count == 0 ? 0 : 1;
        }
    }

    private static async Task<int> RunGameDataAsync(RunOptions options)
    {
        GameDataSpecFile spec;
        try
        {
            spec = SpecLoading.LoadGameDataSpec(options.Spec);
            Ui.Info($"Loaded GameData spec: {spec.Category}/{spec.Id} — {spec.Description}");
        }
        catch (Exception ex)
        {
            Ui.Error($"Error loading GameData spec: {ex.Message}");
            return 1;
        }

        // Only named registry entries carry an identity for spec applicability; anonymous
        // exec:/dotnet: connectables (no `name=` prefix) have no identity to check against
        // the spec's `engines:` map, so skip the check rather than NRE/throw looking one up.
        if (options.Engine.EngineName is { } identityName && !spec.IsApplicableTo(identityName))
        {
            Ui.Warn($"Spec '{spec.Id}' is not applicable to engine '{identityName}' (skipped).");
            return 0;
        }

        WarnUnsupportedForGameData(options);

        var engineLabel = options.Engine.EngineName ?? options.Engine.Display;
        Ui.Info($"Engine: {engineLabel}");
        IGameDataEngine engine;
        try
        {
            // CreateGameDataEngineAsync requires a non-null name but already tolerates unknown
            // names cleanly (throws ArgumentException, caught below); "" surfaces that same
            // clean error for anonymous (null-identity) connectables instead of NREing.
            engine = await EngineFactory.CreateGameDataEngineAsync(options.Engine.EngineName ?? "", options.Headless);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error creating engine: {ex.Message}");
            return 1;
        }

        using (engine)
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            var lastStepIndex = spec.Steps.Count - 1;
            // GameDataRunner's engineName parameter is nullable and used only for tiered
            // snapshot lookup (falls back to the base tier when unset), so pass the identity
            // through as-is rather than coercing to "".
            var runner = new GameDataRunner(engine, options.Engine.EngineName)
            {
                OnStepCompleted = (index, step, state) =>
                {
                    Ui.Info($"  step {index}: {step.Action ?? "assert"}");
                    if (!options.AllSteps && index != lastStepIndex)
                    {
                        return;
                    }

                    Ui.Blank();
                    Ui.Rule($"Step {index}: {step.Action ?? "assert"}");
                    Console.Out.WriteLine(JsonSerializer.Serialize(state, jsonOptions));
                    Console.Out.Flush();
                },
            };

            var result = runner.Run(spec);

            Ui.Blank();
            if (result.Passed)
            {
                Ui.Pass($"PASS — {spec.Id} on {engineLabel} ({spec.Steps.Count} step(s))");
                return 0;
            }

            Ui.Fail($"FAIL — {spec.Id} on {engineLabel}: {result.Failures.Count} error(s):");
            foreach (var (failure, i) in result.Failures.Select((f, i) => (f, i)))
            {
                Ui.FailItem($"[{i + 1}] {failure}");
            }

            return 1;
        }
    }

    /// <summary>Disable an artifact option the engine can't support, warning once.</summary>
    private static string? Gate(string? value, bool supported, EngineSelection engine, string flag)
    {
        if (value is null || supported)
        {
            return value;
        }

        Ui.Warn($"engine '{engine.EngineName}' does not support {flag}; skipping.");
        return null;
    }

    private static void WarnUnsupportedForGameData(RunOptions options)
    {
        (bool set, string flag)[] artifacts =
        [
            (options.ScreenshotsDir is not null, "--screenshots"),
            (options.TimelinePath is not null, "--timeline"),
            (options.RecordPath is not null, "--record"),
            (options.SaveRosterDir is not null, "--save-roster"),
            (options.KeepAlive, "--keep-alive"),
            (options.BreakAt is not null, "--break"),
        ];

        foreach (var (set, flag) in artifacts)
        {
            if (set)
            {
                Ui.Warn($"{flag} is not supported for gamedata runs; ignoring.");
            }
        }
    }

    private static void ReportDiagnosticDumps()
    {
        // Path convention mirrors BsRosterUiDriver.BsUiDiagnostics.DiagnosticsDirectory
        // (the driver writes dumps here). Inlined so the CLI never references the driver type.
        var diagDir = Environment.GetEnvironmentVariable("BS_UI_DIAGNOSTICS_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "bs-ui-diagnostics");
        if (!Directory.Exists(diagDir))
        {
            return;
        }

        var diagFiles = Directory.GetFiles(diagDir, "*.txt")
            .OrderByDescending(f => f)
            .Take(3)
            .ToArray();
        if (diagFiles.Length == 0)
        {
            return;
        }

        Ui.Blank();
        Ui.Info("Diagnostic dumps:");
        foreach (var file in diagFiles)
        {
            Ui.Info($"  {file}");
        }
    }
}
