using System.CommandLine;
using System.Text.Json;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.GameData;
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
        EngineSpec Engine,
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
            var options = new RunOptions(
                Spec: specInput,
                Engine: engineOptions.Resolve(parseResult, specInput),
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

        Ui.Info($"Engine: {options.Engine.EngineName}");
        IRosterEngine engine;
        try
        {
            engine = await EngineFactory.CreateRosterEngineAsync(options.Engine.EngineName, options.Headless, options.KeepAlive);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error creating engine: {ex.Message}");
            return 1;
        }

        using (engine)
        {
            // Accept every artifact option for every engine; warn once and disable the ones
            // the chosen engine can't honor (uniform handling, no silent no-ops).
            var screenshotsDir = Gate(options.ScreenshotsDir, EngineCapabilities.SupportsScreenshots(engine), options.Engine, "--screenshots");
            var recordPath = Gate(options.RecordPath, EngineCapabilities.SupportsRecording(engine), options.Engine, "--record");
            var saveRosterDir = Gate(options.SaveRosterDir, EngineCapabilities.SupportsRosterXmlExport(engine), options.Engine, "--save-roster");
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
                        screenshot = EngineCapabilities.CaptureScreenshotAsync(engine).GetAwaiter().GetResult();
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

                if (stepIndex == lastStepIndex && saveRosterDir is not null && engine is BsUiRosterEngine bsUi)
                {
                    try
                    {
                        Directory.CreateDirectory(saveRosterDir);
                        var xml = bsUi.ExportRosterXmlAsync().GetAwaiter().GetResult();
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
                        BreakRepl.Run(engine, stepIndex, StepFormatter.DescribeStep(step));
                    }

                    return true;
                };
            }

            Ui.Info($"Running {spec.Steps.Count} steps...");
            Ui.Blank();

            if (recordPath is not null && engine is BsUiRosterEngine recordingEngine)
            {
                await recordingEngine.StartRecordingAsync();
                Ui.Info("Recording UI actions...");
            }

            var result = runner.Run(spec);

            if (recordPath is not null && engine is BsUiRosterEngine recordStopEngine)
            {
                try
                {
                    var actions = await recordStopEngine.StopRecordingAsync();
                    if (actions is not null)
                    {
                        File.WriteAllText(recordPath, actions.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

            if (options.Headed && engine is NrRosterUiDriver.NrRosterUiEngine)
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

        var engineName = options.Engine.EngineName;
        if (!spec.IsApplicableTo(engineName))
        {
            Ui.Warn($"Spec '{spec.Id}' is not applicable to engine '{engineName}' (skipped).");
            return 0;
        }

        WarnUnsupportedForGameData(options);

        Ui.Info($"Engine: {engineName}");
        IGameDataEngine engine;
        try
        {
            engine = await EngineFactory.CreateGameDataEngineAsync(engineName, options.Headless);
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
            var runner = new GameDataRunner(engine, engineName)
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
                Ui.Pass($"PASS — {spec.Id} on {engineName} ({spec.Steps.Count} step(s))");
                return 0;
            }

            Ui.Fail($"FAIL — {spec.Id} on {engineName}: {result.Failures.Count} error(s):");
            foreach (var (failure, i) in result.Failures.Select((f, i) => (f, i)))
            {
                Ui.FailItem($"[{i + 1}] {failure}");
            }

            return 1;
        }
    }

    /// <summary>Disable an artifact option the engine can't support, warning once.</summary>
    private static string? Gate(string? value, bool supported, EngineSpec engine, string flag)
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
        var diagDir = BsUiDiagnostics.DiagnosticsDirectory;
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
