using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec compare</c> — the verdict-equality rail. Runs the same spec set twice, once per
/// <c>--config-*</c> arm, each arm's child adapter processes getting their own extra environment
/// (a comma-separated <c>KEY=VALUE</c> list). Before any timing is reported, the two arms' per-spec
/// verdicts are asserted identical — a configuration change (warm-reuse, a parallelism level, any
/// other environment-gated behavior) that alters conformance results is not an optimization, it is a
/// regression, and this command's whole reason to exist is to catch that before it ships.
/// </summary>
/// <remarks>
/// This replaces the retired <c>scripts/bench-warm-reuse.ps1</c>, generalized from "warm vs cold"
/// (<c>BSSPEC_DISABLE_WARM_REUSE=1</c>) to any pair of child-process environment configurations —
/// including the parallelism levels a future auto-tuner would need to prove verdict-neutral.
/// </remarks>
internal static class CompareCommand
{
    private sealed record CompareOptions(
        EngineSelection Selection,
        IReadOnlyList<string> Domains,
        string? Filter,
        string? SpecsDir,
        string? ExpectedFailures,
        string? AssertionEngine,
        int Workers,
        IReadOnlyDictionary<string, string> ConfigA,
        IReadOnlyDictionary<string, string> ConfigB);

    /// <summary>One arm's outcome: the suite result, measured wall time, and trace summary (empty when telemetry produced no local artifact).</summary>
    private sealed record ArmResult(SpecSuiteResult Suite, TimeSpan Wall, TraceSummary Trace);

    public static Command Create()
    {
        var engineOptions = new EngineOptions();
        var filter = new Option<string?>("--filter")
        {
            Description = "Only run specs whose category/id matches (comma-separated, OR logic).",
        };
        var specs = new Option<string?>("--specs")
        {
            Description = "Specs directory (default: discovered repo specs, else embedded).",
        };
        var expectedFailures = new Option<string?>("--expected-failures")
        {
            Description = "Engine name for spec-level expected failures, applied to both arms " +
                "(mirrors 'run --all'; a spec annotated as expected-to-fail reports expected-failure/" +
                "unexpected-pass here instead of failed/passed).",
        };
        var assertionEngine = new Option<string?>("--assertion-engine")
        {
            Description = "Engine name for step-level assertion overrides, applied to both arms " +
                "(mirrors 'run --all'; defaults to the engine identity).",
        };
        var workers = new Option<int>("--workers")
        {
            Description = "Run each arm with N adapter processes (default: 1).",
            DefaultValueFactory = _ => 1,
        };
        var configA = new Option<string>("--config-a")
        {
            Description = "Comma-separated KEY=VALUE environment settings applied to arm A's child processes. May be empty.",
            Required = true,
        };
        var configB = new Option<string>("--config-b")
        {
            Description = "Comma-separated KEY=VALUE environment settings applied to arm B's child processes. May be empty.",
            Required = true,
        };

        var command = new Command(
            "compare",
            "Run the same spec set under two configurations and assert the verdicts are identical before reporting the timing delta.");
        engineOptions.AddTo(command);
        foreach (var option in new Option[] { filter, specs, expectedFailures, assertionEngine, workers, configA, configB })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, _) =>
        {
            try
            {
                var selection = engineOptions.Resolve(parseResult, specInput: null);

                IReadOnlyList<string> domains =
                    (parseResult.GetValue(engineOptions.Gamedata), parseResult.GetValue(engineOptions.Roster)) switch
                    {
                        (true, false) => ["gamedata"],
                        (false, true) => ["roster"],
                        _ => ["roster", "gamedata"],
                    };

                var workerCount = parseResult.GetValue(workers);
                if (workerCount < 1)
                {
                    throw new CliInputException("--workers must be at least 1.");
                }

                var options = new CompareOptions(
                    selection,
                    domains,
                    parseResult.GetValue(filter),
                    parseResult.GetValue(specs),
                    parseResult.GetValue(expectedFailures),
                    parseResult.GetValue(assertionEngine),
                    workerCount,
                    ParseConfig(parseResult.GetValue(configA), "--config-a"),
                    ParseConfig(parseResult.GetValue(configB), "--config-b"));
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

    private static async Task<int> ExecuteAsync(CompareOptions options)
    {
        var selection = options.Selection;
        var engineLabel = selection.EngineName ?? selection.Display;

        var workers = await RunBatch.ResolveWorkersAsync(selection, options.Workers, Ui.Warn).ConfigureAwait(false);

        Ui.Info($"Engine: {engineLabel}");
        Ui.Info($"Domains: {string.Join(", ", options.Domains)}");

        ArmResult armA;
        ArmResult armB;
        try
        {
            Ui.Rule("Arm A");
            armA = await RunArmAsync(options, workers, options.ConfigA, "a").ConfigureAwait(false);

            Ui.Rule("Arm B");
            armB = await RunArmAsync(options, workers, options.ConfigB, "b").ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }

        var statusA = ToStatusMap(armA.Suite);
        var statusB = ToStatusMap(armB.Suite);

        var allIds = statusA.Keys.Union(statusB.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var divergences = new List<string>();
        foreach (var id in allIds)
        {
            var a = statusA.GetValueOrDefault(id, "(not run)");
            var b = statusB.GetValueOrDefault(id, "(not run)");
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                divergences.Add($"{id}: A={a} B={b}");
            }
        }

        Ui.Blank();
        if (divergences.Count > 0)
        {
            Ui.Fail($"VERDICT DIVERGENCE — {divergences.Count} of {allIds.Count} spec(s) differ between config A and config B:");
            foreach (var divergence in divergences)
            {
                Ui.FailItem(divergence);
            }

            Ui.Blank();
            Ui.Error("A configuration change that alters conformance results is not an optimization — it is a regression.");
            return 1;
        }

        // The verdict comparison deliberately spans EVERY spec the run reported, skips included: a
        // config that changes what gets skipped has changed conformance results just as surely as one
        // that flips a pass to a fail. The per-spec timing denominator, however, must be the specs
        // that actually EXECUTED — dividing a saving by the skipped ones too would understate it by
        // however many specs the --filter excluded (here: 54 executed out of 113 reported).
        var executed = allIds.Count(id => statusA.GetValueOrDefault(id, "(not run)") is not ("skipped" or "(not run)"));
        var skipped = allIds.Count - executed;

        Ui.Pass(skipped > 0
            ? $"Verdicts identical across {allIds.Count} spec(s) ({executed} executed, {skipped} skipped)."
            : $"Verdicts identical across {allIds.Count} spec(s).");

        WriteTiming(armA, armB, executed);

        if (armA.Trace.SpecCount > 0)
        {
            Ui.Blank();
            Ui.Info("Arm A trace summary:");
            armA.Trace.WriteTable(Console.Error);
        }

        if (armB.Trace.SpecCount > 0)
        {
            Ui.Blank();
            Ui.Info("Arm B trace summary:");
            armB.Trace.WriteTable(Console.Error);
        }

        return 0;
    }

    private static void WriteTiming(ArmResult armA, ArmResult armB, int specCount)
    {
        var wallA = armA.Wall.TotalSeconds;
        var wallB = armB.Wall.TotalSeconds;
        var absSaving = wallB - wallA;
        var perSpecSaving = specCount > 0 ? absSaving / specCount : 0;
        var speedup = wallA > 0 ? wallB / wallA : 0;

        Ui.Blank();
        Ui.Info(FormattableString.Invariant($"Timing (A vs B), over {specCount} executed spec(s):"));
        Ui.Info(FormattableString.Invariant($"  A wall:            {wallA:F1}s"));
        Ui.Info(FormattableString.Invariant($"  B wall:            {wallB:F1}s"));
        Ui.Info(FormattableString.Invariant($"  abs. saving (B-A): {absSaving:F1}s"));
        Ui.Info(FormattableString.Invariant($"  per-spec saving:   {perSpecSaving:F2}s"));
        Ui.Info(FormattableString.Invariant($"  speedup (B/A):     {speedup:F2}x"));
    }

    /// <summary>Build the id -> status map ("category/specId" -> passed/failed/expected-failure/unexpected-pass/skipped) for one arm.</summary>
    private static Dictionary<string, string> ToStatusMap(SpecSuiteResult suite) =>
        suite.ReportResults.ToDictionary(r => $"{r.Category}/{r.SpecId}", r => r.Status, StringComparer.Ordinal);

    /// <summary>
    /// Run the full spec set for one arm: its own <see cref="HarnessCollector"/> (so <see cref="TraceSummary"/>
    /// can report cold-starts/reuses/peak live resources independently per arm) and its own child
    /// environment (the collector's OTel wiring layered under the arm's <c>--config-*</c> settings, so
    /// a config can override telemetry wiring but never the other way around).
    /// </summary>
    private static async Task<ArmResult> RunArmAsync(
        CompareOptions options, int workers, IReadOnlyDictionary<string, string> config, string armLabel)
    {
        var filterPatterns = options.Filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } patterns ? patterns : null;

        var runId = Guid.NewGuid().ToString("N")[..8];
        var artifactPath = Path.Combine("artifacts", "telemetry", $"compare-{armLabel}-{runId}");

        var sw = Stopwatch.StartNew();
        SpecSuiteResult result;
        bool hasLocalArtifact;
        await using (var collector = await HarnessCollector.StartAsync(artifactPath).ConfigureAwait(false))
        {
            hasLocalArtifact = collector.HasLocalArtifact;

            result = await SpecSuiteRunner.RunAsync(
                new SpecSuiteOptions
                {
                    SpecsDirectory = options.SpecsDir,
                    FilterPatterns = filterPatterns,
                    EngineFilter = options.Selection.EngineName,
                    ExpectedFailuresEngine = options.ExpectedFailures,
                    AssertionEngine = options.AssertionEngine ?? options.Selection.AssertionEngineName,
                    Workers = workers,
                    Domains = options.Domains,
                    AdapterFactory = workerIndex =>
                    {
                        var index = workerIndex.ToString(CultureInfo.InvariantCulture);
                        var env = new Dictionary<string, string>(collector.ChildEnvironment);
                        foreach (var (key, value) in config)
                        {
                            env[key] = value;
                        }

                        env["BSSPEC_WORKER_INDEX"] = index;
                        env["OTEL_RESOURCE_ATTRIBUTES"] = $"service.instance.id={index}";
                        return options.Selection.StartProcess(env);
                    },
                },
                progressWriter: Console.Error).ConfigureAwait(false);
        }

        sw.Stop();

        var trace = hasLocalArtifact ? TraceSummary.FromArtifact(artifactPath) : TraceSummary.Empty;
        return new ArmResult(result, sw.Elapsed, trace);
    }

    /// <summary>Parse a comma-separated <c>KEY=VALUE</c> list; an empty/null string yields an empty (no extra environment) map.</summary>
    private static IReadOnlyDictionary<string, string> ParseConfig(string? raw, string flagName)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(raw))
        {
            return config;
        }

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new CliInputException($"{flagName}: invalid entry '{entry}' — expected KEY=VALUE.");
            }

            var key = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            config[key] = value;
        }

        return config;
    }
}
