using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Source-generated (AOT-safe) JSON context for the gamedata single-spec state dump.
/// Default (Pascal-case) naming + indentation, matching the historical reflection-based
/// <c>JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })</c>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GameDataState))]
internal partial class GameDataDumpJsonContext : JsonSerializerContext;

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
        int? BreakAt)
    {
        public bool Headless => !Headed;
    }

    public static Command Create()
    {
        var spec = new Argument<string?>("spec")
        {
            Description = "Spec file path, spec ID (e.g. \"selection/selection-page\"), or \"-\" for stdin. Omit with --all or --matrix.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var engineOptions = new EngineOptions();
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Output format. Single-spec: tree|json (default tree). --all: summary|json|github-actions (default summary).",
        };
        output.Validators.Add(result =>
        {
            // Reject genuinely-unknown values at parse time; the per-mode narrowing
            // (tree|json vs summary|json|github-actions) is enforced at runtime below.
            var value = result.GetValueOrDefault<string>();
            if (value is not null && value is not ("tree" or "json" or "summary" or "github-actions"))
            {
                result.AddError($"'{value}' is not a valid value for --output. Expected one of: tree, json, summary, github-actions.");
            }
        });
        var json = new Option<bool>("--json") { Description = "Shortcut for --output json (single-spec only)." };
        var all = new Option<bool>("--all")
        {
            Description = "Run the whole spec suite over the engine (batch mode) instead of a single spec.",
        };
        var matrix = new Option<string?>("--matrix")
        {
            Description = "Read *-conformance.json reports from <dir> and print a markdown compatibility matrix.",
        };
        var specs = new Option<string?>("--specs")
        {
            Description = "Specs directory for --all (default: discovered repo specs, else embedded).",
        };
        var filter = new Option<string?>("--filter")
        {
            Description = "Only run specs whose category/id matches (comma-separated, OR logic) (--all).",
        };
        var tags = new Option<string?>("--tags")
        {
            Description = "Tag filter expression for --all (comma-separated, +/- prefix).",
        };
        var report = new Option<string?>("--report")
        {
            Description = "Write a conformance report JSON to <path> (--all).",
        };
        var expectedFailures = new Option<string?>("--expected-failures")
        {
            Description = "Engine name for spec-level expected failures (--all).",
        };
        var assertionEngine = new Option<string?>("--assertion-engine")
        {
            Description = "Engine name for step-level assertion overrides (--all; defaults to the engine identity).",
        };
        var policy = new Option<string?>("--policy")
        {
            Description = "Override the concurrency/reuse policy, comma-separated KEY=VALUE: workers=N, " +
                "reuse=on|off, reuse-roster=on|off, reuse-gamedata=on|off. Without this, the policy " +
                "(ConcurrencyPolicy.For — machine + engine) picks the worker count and reuse decision " +
                "by itself; this exists to diagnose or ablate, not to operate.",
        };
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
        var breakAt = new Option<int?>("--break")
        {
            Description = "Pause before step <n> and drop into a REPL / inspection prompt.",
        };

        var command = new Command("run", "Execute a spec end-to-end against an engine and report pass/fail.");
        command.Arguments.Add(spec);
        engineOptions.AddTo(command);
        foreach (var option in new Option[]
        {
            output, json, all, matrix, specs, filter, tags, report, expectedFailures, assertionEngine, policy,
            allSteps, screenshots, timeline, record, saveRoster, breakAt,
        })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, _) =>
        {
            try
            {
                var specInput = parseResult.GetValue(spec);
                var runAll = parseResult.GetValue(all);
                var matrixDir = parseResult.GetValue(matrix);

                // Exactly one mode selector: <spec>, --all, or --matrix.
                var modeCount = (specInput is not null ? 1 : 0) + (runAll ? 1 : 0) + (matrixDir is not null ? 1 : 0);
                if (modeCount == 0)
                {
                    throw new CliInputException("Specify exactly one of: <spec>, --all, or --matrix <dir>.");
                }

                if (modeCount > 1)
                {
                    throw new CliInputException("<spec>, --all, and --matrix are mutually exclusive; choose exactly one.");
                }

                if (matrixDir is not null)
                {
                    return Task.FromResult(RunBatch.ExecuteMatrix(matrixDir));
                }

                if (runAll)
                {
                    // --json is a single-spec shortcut; batch runs pick the format via --output.
                    if (parseResult.GetValue(json))
                    {
                        throw new CliInputException("--json is only valid for a single-spec run; use --output json under --all.");
                    }

                    var batchOutput = parseResult.GetValue(output);
                    if (batchOutput is not null && batchOutput is not ("summary" or "json" or "github-actions"))
                    {
                        throw new CliInputException($"--output '{batchOutput}' is not valid for --all; use summary, json, or github-actions.");
                    }

                    // Resolve validates --gamedata/--roster exclusivity, --ui, and the engine identity.
                    var selection = ApplyPolicyOverride(
                        engineOptions.Resolve(parseResult, specInput: null), parseResult.GetValue(policy), Ui.Warn);

                    // Batch runs both domains by default; --gamedata/--roster narrow. Resolve already
                    // rejected the both-set case, so the remaining cases are single-domain or neither→both.
                    IReadOnlyList<string> domains =
                        (parseResult.GetValue(engineOptions.Gamedata), parseResult.GetValue(engineOptions.Roster)) switch
                        {
                            (true, false) => ["gamedata"],
                            (false, true) => ["roster"],
                            _ => ["roster", "gamedata"],
                        };

                    foreach (var (set, flag) in new (bool Set, string Flag)[]
                    {
                        (parseResult.GetValue(screenshots) is not null, "--screenshots"),
                        (parseResult.GetValue(timeline) is not null, "--timeline"),
                        (parseResult.GetValue(record) is not null, "--record"),
                        (parseResult.GetValue(saveRoster) is not null, "--save-roster"),
                        (parseResult.GetValue(breakAt) is not null, "--break"),
                        (parseResult.GetValue(allSteps), "--all-steps"),
                    })
                    {
                        if (set)
                        {
                            Ui.Warn($"{flag} is ignored in --all batch mode.");
                        }
                    }

                    var batch = new RunBatch.BatchOptions(
                        selection,
                        domains,
                        batchOutput ?? "summary",
                        parseResult.GetValue(specs),
                        parseResult.GetValue(filter),
                        parseResult.GetValue(tags),
                        parseResult.GetValue(report),
                        parseResult.GetValue(expectedFailures),
                        parseResult.GetValue(assertionEngine));
                    return RunBatch.ExecuteAsync(batch);
                }

                // Single-spec mode (unchanged behavior).
                var outputStr = parseResult.GetValue(output);
                if (outputStr is not null && outputStr is not ("tree" or "json"))
                {
                    throw new CliInputException($"--output '{outputStr}' is not valid for a single-spec run; use tree or json.");
                }

                RejectInertPolicyKeys(parseResult.GetValue(policy));

                var format = parseResult.GetValue(json) || outputStr == "json" ? OutputFormat.Json : OutputFormat.Tree;
                var options = new RunOptions(
                    Spec: specInput!,
                    Engine: ApplyPolicyOverride(
                        engineOptions.Resolve(parseResult, specInput), parseResult.GetValue(policy), Ui.Warn),
                    Format: format,
                    Headed: parseResult.GetValue(engineOptions.Headed),
                    AllSteps: parseResult.GetValue(allSteps),
                    ScreenshotsDir: parseResult.GetValue(screenshots),
                    TimelinePath: parseResult.GetValue(timeline),
                    RecordPath: parseResult.GetValue(record),
                    SaveRosterDir: parseResult.GetValue(saveRoster),
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

    /// <summary>
    /// Reject <c>--policy</c> keys that a <b>single-spec</b> run cannot act on, instead of accepting
    /// them and doing nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>workers=N</c> sizes the pool of adapter processes <c>run --all</c> spreads a spec suite
    /// across (<c>RunBatch</c> → <c>SpecSuiteRunner</c>). A single-spec run spawns exactly one
    /// adapter and the child never reads the key, so <c>--policy workers=8</c> was accepted,
    /// forwarded to the child, and completely inert — with no warning. This repo's standard is that a
    /// flag is accepted or rejected, never silently dropped (#305); an inert knob tells the user they
    /// configured something when they did not, which is worse than an error.
    /// </para>
    /// <para>
    /// The <c>reuse*</c> keys stay legal here: they reach the child (<c>serve --policy</c>) and do set
    /// its engine's reuse behaviour, so a single-spec run is a legitimate way to poke at a warm vs
    /// cold engine even though one spec means one setup.
    /// </para>
    /// </remarks>
    /// <param name="policyRaw">The raw <c>--policy k=v,...</c> string, or null when omitted.</param>
    /// <exception cref="CliInputException">A key was given that a single-spec run cannot honour.</exception>
    internal static void RejectInertPolicyKeys(string? policyRaw)
    {
        IReadOnlySet<string> keys;
        try
        {
            keys = PolicyOverride.Keys(policyRaw);
        }
        catch (FormatException ex)
        {
            throw new CliInputException(ex.Message);
        }

        if (keys.Contains("workers"))
        {
            throw new CliInputException(
                "--policy: 'workers' is meaningless for a single-spec run — one spec runs in exactly one " +
                "adapter process, so there is nothing to spread across workers. Use it with --all (which " +
                "spreads a suite across N adapters), or drop it.");
        }
    }

    /// <summary>
    /// Apply a <c>--policy</c> override on top of the machine/engine's own <see cref="ConcurrencyPolicy"/>
    /// answer, and return <paramref name="selection"/> with <see cref="EngineSelection.PlanOverride"/> set
    /// accordingly. Null/absent <paramref name="policyRaw"/> leaves <paramref name="selection"/> untouched
    /// — no override was asked for, so none is fabricated (a launchable adapter must only see
    /// <see cref="EngineSelection.PlanOverride"/> become non-null when the user actually typed
    /// <c>--policy</c>; a launchable entry that receives one throws — see
    /// <see cref="EngineSelection.ResolveLaunch"/>).
    /// </summary>
    /// <param name="selection">The resolved engine selection to override.</param>
    /// <param name="policyRaw">The raw <c>--policy k=v,...</c> string, or null when omitted.</param>
    /// <param name="warn">
    /// Sink for the "policy override, not capability mismatch" warning: forcing reuse on for a domain
    /// the engine's <see cref="EngineProfile"/> does not declare reuse-safe is ALLOWED (it is exactly
    /// the ablation <c>bs-spec compare</c> needs to prove reuse-safety) but is never silent.
    /// </param>
    /// <returns><paramref name="selection"/>, with <see cref="EngineSelection.PlanOverride"/> set when an override was given.</returns>
    /// <exception cref="CliInputException"><paramref name="policyRaw"/> fails to parse (see <see cref="PolicyOverride.Apply"/>).</exception>
    internal static EngineSelection ApplyPolicyOverride(EngineSelection selection, string? policyRaw, Action<string> warn)
    {
        if (policyRaw is null)
        {
            return selection;
        }

        // The base plan is the policy's answer FOR THIS LOAD TARGET, not a machine-width one that the
        // override then edits. --policy only replaces the keys it names, so a base plan computed without
        // the load target would let `--policy reuse-roster=on` — a flag that says nothing about workers —
        // hand a live NewRecruit run ceil(cpuCount × k) browsers through the untouched Workers field.
        var loadTarget = selection.LoadTarget;
        var basePlan = ConcurrencyPolicy.For(MachineProfile.Current(), selection.Entry.Profile, loadTarget);
        ConcurrencyPlan overridden;
        try
        {
            overridden = PolicyOverride.Apply(policyRaw, basePlan);
        }
        catch (FormatException ex)
        {
            throw new CliInputException(ex.Message);
        }

        // An explicit override may lower the load on a third party's site; it may not raise it. Rejected
        // rather than clamped, because this repo's rule is that a flag is honoured or refused, never
        // silently dropped (#305) — and because a user who typed `workers=32` at newrecruit.eu should be
        // told no, not quietly given 2 and left believing they got 32. (EffectivePlan clamps regardless;
        // that is the backstop, not the answer.)
        if (loadTarget == LoadTarget.ThirdPartyLive
            && (overridden.Workers > ConcurrencyPolicy.ThirdPartyLiveLoadLimit
                || overridden.PoolSize > ConcurrencyPolicy.ThirdPartyLiveLoadLimit))
        {
            throw new CliInputException(
                $"--policy: engine '{selection.EngineName ?? selection.Display}' resolves to a third party's " +
                $"live service for this run, so its concurrency is a load question, not a throughput one. " +
                $"It is held to {ConcurrencyPolicy.ThirdPartyLiveLoadLimit} concurrent sessions " +
                $"(ConcurrencyPolicy.ThirdPartyLiveLoadLimit) and no override may raise that — you asked for " +
                $"workers={overridden.Workers}, pool={overridden.PoolSize}. Lower it, or point the engine at a " +
                $"local endpoint (unset NR_ENGINE_URL to replay the frozen HAR, which is what the measured " +
                $"worker count was fitted against).");
        }

        if (overridden.ReuseRoster && !selection.Entry.Profile.ReuseSafeRoster)
        {
            warn("forcing reuse on for the roster domain on an engine not declared reuse-safe; " +
                "verdicts may change — use `bs-spec compare` to check.");
        }

        if (overridden.ReuseGameData && !selection.Entry.Profile.ReuseSafeGameData)
        {
            warn("forcing reuse on for the gamedata domain on an engine not declared reuse-safe; " +
                "verdicts may change — use `bs-spec compare` to check.");
        }

        return selection with { PlanOverride = overridden };
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
        AdapterProcess? process = null;
        DescribeResult described;
        try
        {
            process = options.Engine.StartProcess();
            described = await AdapterDescriber.DescribeAsync(process);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error starting engine: {ex.Message}");
            process?.Dispose();
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

            var aborted = false;
            if (options.BreakAt is { } breakStep)
            {
                // Give the REPL's own engine the same longer timeout as setup when the spec
                // has a dataSource (mirrors JsonProtocolEngine's ctor above), else the default.
                var replTimeout = spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
                runner.OnBeforeStep = (stepIndex, step) =>
                {
                    if (stepIndex == breakStep)
                    {
                        // Returns false when the user types `quit`, which aborts the run.
                        var resume = ProtocolBreakRepl.Run(process, stepIndex, StepFormatter.DescribeStep(step), replTimeout);
                        if (!resume)
                        {
                            aborted = true;
                        }

                        return resume;
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

            // Artifact finalization (timeline write, like the recording stop above) happens
            // regardless of abort: it reflects whatever steps actually ran before `quit`, and
            // the engine process is still alive (never disposed on the REPL's abort path), so
            // there's nothing stopping us from writing out what was captured so far.
            if (timeline is not null && options.TimelinePath is not null)
            {
                timeline.Write(options.TimelinePath, result.Failures.Count == 0, result.Failures);
                Ui.Info($"Timeline report: {options.TimelinePath}");
            }

            Ui.Blank();
            if (aborted)
            {
                Ui.Warn($"Run aborted at step {options.BreakAt} (quit).");
                return 130;
            }

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

        // Spawn the engine as a child adapter process and drive it entirely over the JSON-line
        // protocol (mirrors RunRosterAsync's handshake). The describe result's Domains tells us
        // whether this adapter can serve gamedata at all.
        AdapterProcess? process = null;
        DescribeResult described;
        try
        {
            process = options.Engine.StartProcess();
            described = await AdapterDescriber.DescribeAsync(process);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error starting engine: {ex.Message}");
            process?.Dispose();
            return 1;
        }

        using (process)
        {
            if (!described.Domains.Contains("gamedata"))
            {
                Ui.Warn($"engine '{options.Engine.Display}' does not support the gamedata domain (skipped).");
                return 0;
            }

            IGameDataEngine engine = new JsonProtocolGameDataEngine(process);
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
                    Console.Out.WriteLine(JsonSerializer.Serialize(state, GameDataDumpJsonContext.Default.GameDataState));
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
        // The directory is suffixed per worker (bs-ui-diagnostics-w<N>) under parallelism, so
        // enumerate every worker's directory rather than assuming a single unsuffixed one.
        var overrideDir = Environment.GetEnvironmentVariable("BS_UI_DIAGNOSTICS_DIR");
        string[] diagDirs;
        if (overrideDir is not null)
        {
            diagDirs = Directory.Exists(overrideDir) ? [overrideDir] : [];
        }
        else
        {
            diagDirs = FindWorkerDiagnosticsDirs();
        }

        if (diagDirs.Length == 0)
        {
            return;
        }

        var diagFiles = diagDirs
            .SelectMany(dir => Directory.GetFiles(dir, "*.txt"))
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

    /// <summary>
    /// Finds every per-worker diagnostics directory (<c>artifacts/bs-ui-diagnostics</c> plus any
    /// <c>-w&lt;N&gt;</c> suffixed siblings) so dumps from every parallel worker are reported, not
    /// just an unsuffixed directory that only a single-worker run would ever populate.
    /// </summary>
    private static string[] FindWorkerDiagnosticsDirs()
    {
        var artifactsDir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        return Directory.Exists(artifactsDir)
            ? Directory.GetDirectories(artifactsDir, "bs-ui-diagnostics*")
            : [];
    }
}
