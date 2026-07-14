using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec compare</c> — the verdict-equality rail. Runs the same spec set twice, once per arm,
/// and asserts the two arms' per-spec verdicts are identical BEFORE any timing is reported. A
/// configuration change (warm-reuse, a parallelism level, any other gated behavior) that alters
/// conformance results is not an optimization, it is a regression, and catching that before it ships
/// is this command's whole reason to exist.
/// </summary>
/// <remarks>
/// <para>
/// The arms may differ along <b>two independent axes</b>, and keeping both is not redundancy:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>--policy-a</c>/<c>--policy-b</c> — that arm's <see cref="Concurrency.ConcurrencyPlan"/>, parsed by the
/// ONE shared <c>--policy</c> parser (<see cref="Concurrency.PolicyOverride.Apply"/>, also used by
/// <c>run --policy</c> and <c>serve --policy</c>) and carried as that arm's
/// <see cref="EngineSelection.PlanOverride"/> — hence into that arm's child <c>serve --policy</c>
/// args. This varies the <b>harness's decisions</b>. It is the ablation channel.
/// </description></item>
/// <item><description>
/// <c>--config-a</c>/<c>--config-b</c> — a comma-separated <c>KEY=VALUE</c> list layered onto that
/// arm's child <b>environment</b>. This varies the <i>child's environment</i>, a different thing.
/// </description></item>
/// </list>
/// <para>
/// The reuse ablation used to ride the environment axis (<c>--config-b
/// "BSSPEC_DISABLE_WARM_REUSE=1"</c>), and that is the cautionary tale this command's design answers:
/// when reuse moved to a parent-computed policy the variable was deleted, but because
/// <c>--config-*</c> is unvalidated environment injection the stale recipe kept RUNNING — injecting a
/// variable nobody read, running both arms warm, and reporting "verdicts identical, 1.00x", which
/// reads exactly like confirmation. A disconnected lever with the gauge still showing PASS is worse
/// than no lever. Ablating a policy decision now goes through the policy axis, where the decision
/// actually lives.
/// </para>
/// </remarks>
/// <remarks>
/// Before either arm is timed, a discarded warm-up pass runs the same spec set once under neither
/// config, so arm A doesn't unfairly eat the process's first-run costs (JIT, cold OS file cache,
/// first AV scan of freshly built DLLs) that arm B would then get for free. See the warm-up comment
/// in <see cref="ExecuteAsync"/>.
/// </remarks>
internal static class CompareCommand
{
    /// <summary>
    /// Resolved inputs for a comparison. Each arm's <c>--config-*</c> lives on that arm's
    /// <see cref="EngineSelection.ChildEnvironment"/> rather than beside it here: the config is not just
    /// something to hand the child at spawn time, it is an input to <see cref="EngineSelection.LoadTarget"/>
    /// (<c>--config-a NR_ENGINE_URL=https://www.newrecruit.eu</c> takes arm A live), so the selection that
    /// computes the plan and the environment that decides the endpoint must be the <em>same</em> object.
    /// Held apart, they were two facts about one arm that could disagree — and the one that decided how
    /// many browsers to spawn was the one that could not see the site.
    /// </summary>
    private sealed record CompareOptions(
        EngineSelection Selection,
        EngineSelection SelectionA,
        EngineSelection SelectionB,
        IReadOnlyList<string> Domains,
        string? Filter,
        string? SpecsDir,
        string? ExpectedFailures,
        string? AssertionEngine);

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
        var policyA = new Option<string?>("--policy-a")
        {
            Description = "Override arm A's concurrency/reuse policy — comma-separated KEY=VALUE, the " +
                "SAME vocabulary and shared parser as `run --policy`/`serve --policy`: workers=N, " +
                "reuse=on|off, reuse-roster=on|off, reuse-gamedata=on|off. This is the axis that lets " +
                "`compare` ablate a policy decision (e.g. reuse=on vs reuse=off) and still assert " +
                "verdict-equality; --config-a/--config-b remain a separate axis for genuine " +
                "environment experiments. Without this, arm A uses whatever ConcurrencyPolicy.For " +
                "picks for this machine and engine.",
        };
        var policyB = new Option<string?>("--policy-b")
        {
            Description = "Override arm B's concurrency/reuse policy — same vocabulary as --policy-a.",
        };
        var configA = new Option<string?>("--config-a")
        {
            Description = "Comma-separated KEY=VALUE environment settings applied to arm A's child processes. " +
                "Optional (default: none) — --policy-a/--policy-b is the primary axis; this one is for genuine " +
                "environment experiments and needn't be set just to vary the policy.",
        };
        var configB = new Option<string?>("--config-b")
        {
            Description = "Comma-separated KEY=VALUE environment settings applied to arm B's child processes. " +
                "Optional (default: none) — same as --config-a.",
        };

        var command = new Command(
            "compare",
            "Run the same spec set under two configurations and assert the verdicts are identical before reporting the timing delta.");
        engineOptions.AddTo(command);
        foreach (var option in new Option[] { filter, specs, expectedFailures, assertionEngine, policyA, policyB, configA, configB })
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

                // Each arm gets its own EngineSelection.PlanOverride — RunCommand.ApplyPolicyOverride
                // is the shared wrapper around PolicyOverride.Apply (the one --policy parser); calling
                // it here rather than re-implementing it keeps `compare` on the same vocabulary as
                // `run --policy`/`serve --policy`. Thrown synchronously (before ExecuteAsync's first
                // await), so it is caught by this method's own try/catch below like every other
                // input error.
                // The arm's --config-* is attached BEFORE its --policy is applied, because the config can
                // change where the arm's engine points (NR_ENGINE_URL) and therefore what its base plan
                // and its allowed worker ceiling are. Applied the other way round, an arm sent live by
                // --config-a would have had its plan computed against a frozen HAR's measured optimum.
                var selectionA = RunCommand.ApplyPolicyOverride(
                    selection with { ChildEnvironment = ParseConfig(parseResult.GetValue(configA), "--config-a") },
                    parseResult.GetValue(policyA),
                    Ui.Warn);
                var selectionB = RunCommand.ApplyPolicyOverride(
                    selection with { ChildEnvironment = ParseConfig(parseResult.GetValue(configB), "--config-b") },
                    parseResult.GetValue(policyB),
                    Ui.Warn);

                var options = new CompareOptions(
                    selection,
                    selectionA,
                    selectionB,
                    domains,
                    parseResult.GetValue(filter),
                    parseResult.GetValue(specs),
                    parseResult.GetValue(expectedFailures),
                    parseResult.GetValue(assertionEngine));
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

        // Each arm resolves its OWN worker count: options.SelectionA/SelectionB carry independent
        // PlanOverrides (a --policy-a "workers=N" need not equal --policy-b's), so the describe-probe
        // clamp must run once per arm rather than once for the whole comparison — the bug this
        // replaces resolved workers ONCE and reused it for both arms, silently ignoring any per-arm
        // --policy-a/--policy-b worker override. The untimed warm-up pass uses neither arm's plan
        // (see the comment below), so it resolves against options.Selection's own unmodified plan.
        var workersWarmup = await RunBatch.ResolveWorkersAsync(selection, selection.EffectivePlan.Workers, Ui.Warn).ConfigureAwait(false);
        var workersA = await RunBatch.ResolveWorkersAsync(options.SelectionA, options.SelectionA.EffectivePlan.Workers, Ui.Warn).ConfigureAwait(false);
        var workersB = await RunBatch.ResolveWorkersAsync(options.SelectionB, options.SelectionB.EffectivePlan.Workers, Ui.Warn).ConfigureAwait(false);

        Ui.Info($"Engine: {engineLabel}");
        Ui.Info($"Domains: {string.Join(", ", options.Domains)}");

        ArmResult armA;
        ArmResult armB;
        try
        {
            // #<bug>: whichever arm ran first was systematically slower — not a real effect of
            // --config-a/--config-b, but an artifact of the *first* process this invocation spawns
            // paying costs the second one gets for free (JIT, OS page cache for the adapter's
            // assemblies, first-open AV scan of freshly built DLLs, etc). Identical configs proved it:
            // "A" vs "A" measured a ~3.4x slowdown for arm A on a bad run. A discarded warm-up pass —
            // same spec set, same filter/domains, but NEITHER arm's config NOR either arm's policy
            // override — pays that first-process tax once, untimed, so arm A and arm B then both
            // start from an equally warm machine. It deliberately does NOT use ConfigA/ConfigB or
            // SelectionA/SelectionB: applying either arm's config or policy override here would
            // pre-warm that arm's own config/policy-specific state (e.g. a warm-reuse cache) and bias
            // the comparison right back in its favor.
            Ui.Rule("Warm-up (untimed, discarded)");
            var warmup = await RunArmAsync(options, selection, workersWarmup, "warmup").ConfigureAwait(false);
            Ui.Info(FormattableString.Invariant(
                $"Warm-up pass ran the same spec set once under neither config in {warmup.Wall.TotalSeconds:F1}s (discarded — its only purpose is to equalize JIT/OS-cache state before arm A and arm B are timed)."));

            Ui.Rule("Arm A");
            armA = await RunArmAsync(options, options.SelectionA, workersA, "a").ConfigureAwait(false);

            Ui.Rule("Arm B");
            armB = await RunArmAsync(options, options.SelectionB, workersB, "b").ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }

        var statusA = ToStatusMap(armA.Suite);
        var statusB = ToStatusMap(armB.Suite);
        var deathsA = ToDeathMap(armA.Suite);
        var deathsB = ToDeathMap(armB.Suite);

        var allIds = statusA.Keys.Union(statusB.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var divergences = new List<string>();
        foreach (var id in allIds)
        {
            var a = statusA.GetValueOrDefault(id, "(not run)");
            var b = statusB.GetValueOrDefault(id, "(not run)");
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                // #304: retry-on-adapter-death makes a divergence non-deterministic in the presence
                // of crashes — if one arm's retry rescued a spec and the other's didn't (or didn't
                // even crash), a verdict divergence here is really a flake, not a conformance
                // regression. Annotate it so it's explicable rather than mysterious.
                var deathA = deathsA.GetValueOrDefault(id, 0);
                var deathB = deathsB.GetValueOrDefault(id, 0);
                var deathNote = (deathA, deathB) switch
                {
                    (0, 0) => "",
                    _ => $" [adapter death recorded: A={deathA}, B={deathB} — likely a flake, not a conformance regression]",
                };
                divergences.Add($"{id}: A={a} B={b}{deathNote}");
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
            armA.Trace.AppendToGitHubStepSummary($"Trace summary — {engineLabel} (arm A)");
        }

        if (armB.Trace.SpecCount > 0)
        {
            Ui.Blank();
            Ui.Info("Arm B trace summary:");
            armB.Trace.WriteTable(Console.Error);
            armB.Trace.AppendToGitHubStepSummary($"Trace summary — {engineLabel} (arm B)");
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
    /// Build the id -> adapter-death-count map for one arm, keyed identically to
    /// <see cref="ToStatusMap"/> — lets a verdict divergence be cross-referenced against whether an
    /// adapter actually died while running that spec in either arm (see #304).
    /// </summary>
    private static Dictionary<string, int> ToDeathMap(SpecSuiteResult suite) =>
        suite.ReportResults.ToDictionary(r => $"{r.Category}/{r.SpecId}", r => r.AdapterDeaths, StringComparer.Ordinal);

    /// <summary>
    /// Run the full spec set for one arm: its own <see cref="HarnessCollector"/> (so <see cref="TraceSummary"/>
    /// can report cold-starts/reuses/peak live resources independently per arm), its own
    /// <see cref="EngineSelection"/> (so a per-arm <c>--policy-a</c>/<c>--policy-b</c>
    /// <see cref="EngineSelection.PlanOverride"/> reaches that arm's child <c>serve --policy</c>
    /// and no other), and its own child environment (the collector's OTel wiring layered under the
    /// arm's <c>--config-*</c> settings, so a config can override telemetry wiring but never the
    /// other way around).
    /// </summary>
    private static async Task<ArmResult> RunArmAsync(
        CompareOptions options, EngineSelection selection, int workers, string armLabel)
    {
        // The arm's --config-* is NOT re-applied here. It lives on the selection
        // (EngineSelection.ChildEnvironment), and EngineSelection.StartProcess is what layers it onto the
        // child — the same object, and the same composed environment, that EngineSelection.LoadTarget
        // derived this arm's plan from. Copying it into the spawn environment separately (as this method
        // used to) is a second assembly of "the environment the child sees", and a second assembly is a
        // second thing to get wrong. The warm-up arm passes options.Selection, which carries no config —
        // that is exactly what "under neither config" means, and it now needs no special case.
        var filterPatterns = options.Filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } patterns ? patterns : null;

        var runId = Guid.NewGuid().ToString("N")[..8];
        var artifactRoot = Path.Combine("artifacts", "telemetry");
        var artifactPath = Path.Combine(artifactRoot, $"compare-{armLabel}-{runId}");

        // Bound artifacts/telemetry/'s growth before adding to it — see RunBatch/TelemetryRetention.
        TelemetryRetention.Sweep(artifactRoot, currentArtifactBasePath: artifactPath);

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
                    EngineFilter = selection.EngineName,
                    ExpectedFailuresEngine = options.ExpectedFailures,
                    AssertionEngine = options.AssertionEngine ?? selection.AssertionEngineName,
                    Workers = workers,
                    Domains = options.Domains,
                    AdapterFactory = workerIndex =>
                    {
                        var index = workerIndex.ToString(CultureInfo.InvariantCulture);
                        var env = new Dictionary<string, string>(collector.ChildEnvironment)
                        {
                            ["BSSPEC_WORKER_INDEX"] = index,
                            ["OTEL_RESOURCE_ATTRIBUTES"] = $"service.instance.id={index}",
                        };

                        // The arm's --config-* rides on the selection and is applied by StartProcess.
                        return selection.StartProcess(env);
                    },
                },
                progressWriter: Console.Error).ConfigureAwait(false);
        }

        sw.Stop();

        var trace = hasLocalArtifact ? TraceSummary.FromArtifact(artifactPath) : TraceSummary.Empty;
        return new ArmResult(result, sw.Elapsed, trace);
    }

    /// <summary>
    /// Parse a comma-separated <c>KEY=VALUE</c> list; an empty/null string yields an empty (no extra
    /// environment) map. The keys are carried <b>verbatim</b>, exactly as the user typed them.
    /// </summary>
    /// <remarks>
    /// <b>This map is an overlay, not an answer.</b> Nothing reads an environment variable out of it —
    /// not the load target, not the plan. It is handed to <c>EngineSelection.ChildEnvironment</c>, and the
    /// question "what will the child see for <c>NR_ENGINE_URL</c>?" is answered by composing the child's
    /// real environment (<c>AdapterProcess.ComposeChildEnvironment</c>), whose comparer is the OS's own.
    /// That is why the comparer here does not matter and must not be made to matter: a
    /// <c>StringComparer.Ordinal</c> lookup on this dictionary is precisely the second, disagreeing
    /// implementation of "what does this variable name mean" that let <c>--config-a
    /// "nr_engine_url=https://www.newrecruit.eu"</c> defeat the third-party load limit on Windows.
    /// </remarks>
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
