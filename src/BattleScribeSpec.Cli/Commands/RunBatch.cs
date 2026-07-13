using System.Globalization;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Telemetry;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Batch execution for <c>bs-spec run</c>: <c>--all</c> runs the whole spec suite over an engine
/// (via <see cref="SpecSuiteRunner"/>), and <c>--matrix</c> renders a markdown compatibility
/// matrix from <c>*-conformance.json</c> reports. Dispatched from <see cref="RunCommand"/>.
/// </summary>
internal static class RunBatch
{
    /// <summary>Resolved inputs for an <c>--all</c> batch run.</summary>
    internal sealed record BatchOptions(
        EngineSelection Selection,
        IReadOnlyList<string> Domains,
        string Output,
        string? SpecsDir,
        string? Filter,
        string? Tags,
        string? ReportPath,
        string? ExpectedFailures,
        string? AssertionEngine,
        int Workers);

    /// <summary>Run the full spec suite over the selected engine (<c>bs-spec run --all</c>).</summary>
    public static async Task<int> ExecuteAsync(BatchOptions options)
    {
        var selection = options.Selection;
        var engineLabel = selection.EngineName ?? selection.Display;

        var workers = await ResolveWorkersAsync(selection, options.Workers, Ui.Warn);

        var filterPatterns = options.Filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } patterns ? patterns : null;
        var assertionEngine = options.AssertionEngine ?? selection.AssertionEngineName;

        Ui.Info($"Engine: {engineLabel}");
        Ui.Info($"Domains: {string.Join(", ", options.Domains)}");

        var runId = Guid.NewGuid().ToString("N")[..8];
        var artifactPath = Path.Combine("artifacts", "telemetry", $"run-{runId}");

        // The collector must be disposed (which force-flushes the parent's TracerProvider and
        // MeterProvider) BEFORE TraceSummary.FromArtifact reads the artifact back — otherwise the
        // "run" span and the metrics batched inside the SDK never reach disk. Hence an explicit
        // `await using` block rather than a `using var` declaration scoped to the whole method:
        // that would only dispose when THIS method returns, which is too late to read anything.
        int exitCode;
        bool hasLocalArtifact;
        await using (var collector = await HarnessCollector.StartAsync(artifactPath))
        {
            hasLocalArtifact = collector.HasLocalArtifact;
            if (collector.Enabled)
            {
                Ui.Info(collector.HasLocalArtifact
                    ? $"Telemetry: {artifactPath}.traces.pb"
                    : $"Telemetry: exporting to {collector.Endpoint} (externally-set collector)");
            }

            // Wraps the whole suite so every spec (and every child span nested under it via
            // traceparent) has a single run-level ancestor.
            using var runSpan = HarnessTelemetry.StartOp("run");
            runSpan?.SetTag("bsspec.engine", engineLabel);
            runSpan?.SetTag("bsspec.workers", workers);

            // CI/CD + VCS semantic conventions, only when the standard GitHub Actions env vars are
            // present. cicd.* and vcs.* are Release Candidate stability; test.* below is Development.
            if (Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") is { Length: > 0 } workflow)
            {
                var server = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
                var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
                var githubRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");

                runSpan?.SetTag("cicd.pipeline.name", workflow);
                runSpan?.SetTag("cicd.pipeline.run.id", githubRunId);
                runSpan?.SetTag("cicd.pipeline.run.url.full", $"{server}/{repo}/actions/runs/{githubRunId}");
                runSpan?.SetTag("cicd.pipeline.task.type", "test");
                runSpan?.SetTag("vcs.repository.url.full", $"{server}/{repo}");
                runSpan?.SetTag("vcs.ref.head.name", Environment.GetEnvironmentVariable("GITHUB_REF_NAME"));
                runSpan?.SetTag("vcs.ref.head.revision", Environment.GetEnvironmentVariable("GITHUB_SHA"));
            }

            SpecSuiteResult result;
            try
            {
                result = await SpecSuiteRunner.RunAsync(
                    new SpecSuiteOptions
                    {
                        SpecsDirectory = options.SpecsDir,
                        FilterPatterns = filterPatterns,
                        TagFilter = TagFilter.Parse(options.Tags),
                        EngineFilter = selection.EngineName,
                        ExpectedFailuresEngine = options.ExpectedFailures,
                        AssertionEngine = assertionEngine,
                        Workers = workers,
                        Domains = options.Domains,
                        AdapterFactory = workerIndex =>
                        {
                            var index = workerIndex.ToString(CultureInfo.InvariantCulture);
                            var env = new Dictionary<string, string>(collector.ChildEnvironment)
                            {
                                ["BSSPEC_WORKER_INDEX"] = index,
                                // Without a per-worker service.instance.id, all N workers collapse into
                                // one resource in any backend and "which worker ran this spec?" stays
                                // unanswerable — which is the whole point of attribution.
                                ["OTEL_RESOURCE_ATTRIBUTES"] = $"service.instance.id={index}",
                            };
                            return selection.StartProcess(env);
                        },
                    },
                    progressWriter: Console.Error);
            }
            catch (Exception ex)
            {
                runSpan?.SetTag("test.suite.run.status", "failure");
                Ui.Error(ex.Message);
                return 1;
            }

            runSpan?.SetTag("test.suite.run.status", result.Failed > 0 ? "failure" : "success");

            switch (options.Output)
            {
                case "json":
                    SpecSuiteOutput.WriteJson(result, selection.EngineName, Console.Out);
                    break;
                case "github-actions":
                    SpecSuiteOutput.WriteGitHubActions(result, selection.EngineName, Console.Out);
                    break;
                default:
                    SpecSuiteOutput.WriteSummary(result, selection.EngineName, Console.Out);
                    break;
            }

            if (options.ReportPath is not null)
            {
                SpecSuiteOutput.WriteConformanceReport(result, options.ReportPath, selection.EngineName, assertionEngine, Console.Out);
            }

            exitCode = result.ExitCode;
        }

        // Fail-open: no local artifact (collector disabled, or externally-exported) means nothing
        // to read. TraceSummary.FromArtifact is itself fail-open too, but skipping the call
        // entirely avoids printing a misleading all-zero table when there was never any artifact.
        if (hasLocalArtifact)
        {
            var summary = TraceSummary.FromArtifact(artifactPath);
            if (summary.SpecCount > 0)
            {
                Console.Error.WriteLine();
                summary.WriteTable(Console.Error);
                summary.AppendToGitHubStepSummary($"Trace summary — {engineLabel}");
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Resolve the effective worker count for a batch/compare run: the registry's
    /// <c>MaxParallel</c> ceiling first, then (when that leaves more than one worker) a one-shot
    /// live describe probe — spawn one adapter process, describe it, dispose it — refines the
    /// count further when the adapter advertises a lower ceiling than the registry knows about.
    /// Both maxima use 0 = unlimited. Shared by <see cref="ExecuteAsync"/> and
    /// <c>CompareCommand</c> so the describe-probe clamp exists exactly once — <c>compare</c>
    /// stresses parallelism at least as hard as <c>run --all</c> and must not skip it.
    /// </summary>
    internal static async Task<int> ResolveWorkersAsync(EngineSelection selection, int requested, Action<string> warn)
    {
        var registryMax = selection.Entry.MaxParallel;
        var registryClamped = registryMax > 0 && requested > registryMax ? registryMax : requested;

        var describedMax = 0;
        if (registryClamped > 1)
        {
            try
            {
                using var probe = selection.StartProcess();
                var described = await AdapterDescriber.DescribeAsync(probe);
                describedMax = described.Capabilities.MaxParallel;
            }
            catch (Exception ex)
            {
                warn($"describe probe failed ({ex.Message}); proceeding without a describe-based worker clamp.");
            }
        }

        return ClampWorkers(requested, registryMax, describedMax, warn);
    }

    /// <summary>
    /// Clamp a requested worker count to the engine's parallelism ceilings. Both
    /// <paramref name="registryMax"/> and <paramref name="describedMax"/> use 0 = unlimited (no
    /// clamp). Warns once per clamp. Pure/testable: it never spawns a process.
    /// </summary>
    internal static int ClampWorkers(int requested, int registryMax, int describedMax, Action<string> warn)
    {
        var effective = requested;
        if (registryMax > 0 && effective > registryMax)
        {
            warn($"engine registry limits parallelism to {registryMax}; reducing --workers {requested} to {registryMax}.");
            effective = registryMax;
        }

        if (describedMax > 0 && effective > describedMax)
        {
            warn($"engine described a max parallelism of {describedMax}; reducing workers to {describedMax}.");
            effective = describedMax;
        }

        return effective;
    }

    /// <summary>
    /// Port of the Runner's <c>--matrix</c> block: read <c>*-conformance.json</c> reports from
    /// <paramref name="matrixDir"/> and print a markdown compatibility matrix to stdout.
    /// </summary>
    public static int ExecuteMatrix(string matrixDir)
    {
        if (!Directory.Exists(matrixDir))
        {
            Ui.Error($"matrix directory not found: {matrixDir}");
            return 1;
        }

        var files = Directory.GetFiles(matrixDir, "*-conformance.json");
        if (files.Length == 0)
        {
            Ui.Error($"no *-conformance.json files found in {matrixDir}");
            return 1;
        }

        var reports = files.Select(CompatibilityMatrix.LoadReport).ToArray();
        Console.WriteLine(CompatibilityMatrix.GenerateMarkdown(reports));
        return 0;
    }
}
