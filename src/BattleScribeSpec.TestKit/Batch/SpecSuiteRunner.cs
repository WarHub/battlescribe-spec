using System.Diagnostics;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Batch;

/// <summary>
/// Runs a suite of roster and/or GameData conformance specs against an adapter, applying
/// filter/tag/engine selection and (optionally) parallel execution. This is the batch pipeline
/// extracted verbatim from the Runner so the unified CLI and the Runner shell can share it.
/// </summary>
/// <remarks>
/// Domain discovery rule (see <see cref="SpecSuiteOptions.Domains"/>): when
/// <see cref="SpecSuiteOptions.SpecsDirectory"/> is set explicitly, roster specs are discovered
/// directly under it (unchanged from before domains existed). For the gamedata domain, only a
/// "gamedata" subtree under the explicit directory is used — mirroring the repo's own convention
/// of <c>specs/roster</c> and <c>specs/gamedata</c> as sibling trees. There is no fallback: if the
/// explicit directory has no such subtree (e.g. <c>--specs</c> points directly at a roster-only
/// tree), zero gamedata specs are discovered — <see cref="SpecLoader.FindGameDataSpecsDirectory"/>
/// is never consulted in this case. When <see cref="SpecSuiteOptions.SpecsDirectory"/> is null,
/// each domain is discovered independently: roster via
/// <see cref="SpecLoader.FindRosterSpecsDirectory"/> then the embedded fallback, gamedata via
/// <see cref="SpecLoader.FindGameDataSpecsDirectory"/> (no embedded fallback exists for gamedata
/// specs).
///
/// The adapter process pool is shared across domains — the same process(es) that serve roster
/// specs also serve gamedata specs. Before any gamedata spec runs, the first pooled process is
/// asked to <c>describe</c> itself once; if it doesn't advertise the "gamedata" domain, every
/// gamedata spec becomes a skip record instead of being executed.
/// </remarks>
public static class SpecSuiteRunner
{
    public static async Task<SpecSuiteResult> RunAsync(SpecSuiteOptions options, TextWriter? progressWriter = null)
    {
        var domains = options.Domains;
        var runRoster = domains.Contains("roster", StringComparer.OrdinalIgnoreCase);
        var runGameData = domains.Contains("gamedata", StringComparer.OrdinalIgnoreCase);

        // ===== Discover specs =====
        var specsDir = options.SpecsDirectory;
        List<(string Path, string Id, string Category)>? fileSpecs = null;
        List<(string ResourceName, string Id, string Category)>? embeddedSpecs = null;
        List<(string Path, string Id, string Category)>? gameDataFileSpecs = null;

        if (specsDir is not null)
        {
            if (!Directory.Exists(specsDir))
            {
                throw new InvalidOperationException($"specs directory not found: {specsDir}");
            }

            if (runRoster)
            {
                fileSpecs = [.. SpecLoader.DiscoverSpecs(specsDir)];
            }

            if (runGameData)
            {
                var gameDataSubdir = Path.Combine(specsDir, "gamedata");
                if (Directory.Exists(gameDataSubdir))
                {
                    gameDataFileSpecs = [.. SpecLoader.DiscoverGameDataSpecs(gameDataSubdir)];
                }
            }
        }
        else
        {
            if (runRoster)
            {
                // Try filesystem first, then embedded
                var rosterDir = SpecLoader.FindRosterSpecsDirectory();
                if (rosterDir is not null)
                {
                    fileSpecs = [.. SpecLoader.DiscoverSpecs(rosterDir)];
                }
                else
                {
                    embeddedSpecs = [.. SpecLoader.DiscoverEmbeddedSpecs()];
                }
            }

            if (runGameData)
            {
                var gameDataDir = SpecLoader.FindGameDataSpecsDirectory();
                if (gameDataDir is not null)
                {
                    gameDataFileSpecs = [.. SpecLoader.DiscoverGameDataSpecs(gameDataDir)];
                }
            }
        }

        var totalSpecs = (fileSpecs?.Count ?? embeddedSpecs?.Count ?? 0) + (gameDataFileSpecs?.Count ?? 0);
        if (totalSpecs == 0)
        {
            throw new InvalidOperationException("no spec files found.");
        }

        var filterPatterns = options.FilterPatterns;
        var tagFilter = options.TagFilter;
        var engineFilter = options.EngineFilter;
        var expectedFailuresEngine = options.ExpectedFailuresEngine;
        var assertionEngine = options.AssertionEngine;
        var workers = options.Workers;

        // Shared across every worker (sequential has exactly one) so the adapter-death recovery
        // cap (see AdapterDeathBudget) is enforced per RUN, not per worker.
        var deathBudget = new AdapterDeathBudget(options.MaxAdapterDeaths);

        // ===== Run specs =====
        var results = new List<SpecResult>();
        var reportResults = new List<SpecResultSummary>();
        var specsByResult = new Dictionary<SpecResult, SpecFile>();
        var gameDataSpecsByResult = new Dictionary<SpecResult, GameDataSpecFile>();
        var durationsByResult = new Dictionary<SpecResult, double>();
        var sw = Stopwatch.StartNew();

        IEnumerable<(string IdForLoad, string Id, string Category, Func<SpecFile> Loader)> specSources;
        if (fileSpecs is not null)
        {
            specSources = fileSpecs.Select(s => (s.Path, s.Id, s.Category, (Func<SpecFile>)(() => SpecLoader.Load(s.Path))));
        }
        else if (embeddedSpecs is not null)
        {
            specSources = embeddedSpecs.Select(s => (s.ResourceName, s.Id, s.Category, (Func<SpecFile>)(() => SpecLoader.LoadEmbedded(s.ResourceName))));
        }
        else
        {
            specSources = [];
        }

        IEnumerable<(string IdForLoad, string Id, string Category, Func<GameDataSpecFile> Loader)> gameDataSpecSources =
            gameDataFileSpecs?.Select(s => (s.Path, s.Id, s.Category, (Func<GameDataSpecFile>)(() => SpecLoader.LoadGameData(s.Path))))
            ?? [];

        // Pre-filter specs (filtering doesn't need the adapter)
        var filterLabel = filterPatterns is not null ? string.Join(",", filterPatterns) : "";
        var filteredSpecs = PreFilterSpecs(specSources, filterPatterns, filterLabel, tagFilter, engineFilter, results, reportResults);
        var filteredGameDataSpecs = PreFilterSpecs(gameDataSpecSources, filterPatterns, filterLabel, tagFilter, engineFilter, results, reportResults);

        if (workers > 1)
        {
            // Parallel execution with N adapter processes
            progressWriter?.WriteLine($"Running {filteredSpecs.Count + filteredGameDataSpecs.Count} specs with {workers} workers...");

            // Tracks EVERY process ever created for this run (originals + any adapter-death
            // replacements) so all of them get disposed at the end — a replacement is never part
            // of the original N-element list a plain foreach-dispose would walk.
            var allProcesses = new HashSet<AdapterProcess>();
            var allProcessesLock = new object();
            try
            {
                var initialProcesses = new List<AdapterProcess>();
                for (var w = 0; w < workers; w++)
                {
                    var p = options.AdapterFactory(w);
                    initialProcesses.Add(p);
                    allProcesses.Add(p);
                }

                var gameDataSupported = filteredGameDataSpecs.Count == 0
                    || (await AdapterDescriber.DescribeAsync(initialProcesses[0])).Domains.Contains("gamedata");
                if (filteredGameDataSpecs.Count > 0 && !gameDataSupported)
                {
                    SkipGameDataDomain(filteredGameDataSpecs, reportResults);
                    filteredGameDataSpecs = [];
                }

                // Channel-based process pool. Each pooled item remembers which worker index it
                // belongs to, so a death-replacement process is created with the SAME worker
                // identity (its diagnostics dir / telemetry tag) as the one it replaces.
                var processPool = System.Threading.Channels.Channel.CreateBounded<PooledAdapter>(workers);
                for (var w = 0; w < initialProcesses.Count; w++)
                {
                    processPool.Writer.TryWrite(new PooledAdapter(initialProcesses[w], w));
                }

                var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<(SpecResult Result, SpecFileBase Spec, bool IsGameData, string Status, double DurationMs, int AdapterDeaths)>();

                async ValueTask RunPooledAsync<TSpec>((string Id, string Category, TSpec Spec) item, bool isGameData, CancellationToken ct)
                    where TSpec : SpecFileBase
                {
                    var pooled = await processPool.Reader.ReadAsync(ct);
                    var proc = pooled.Process;
                    try
                    {
                        var (result, status, durationMs, deaths) = RunOneSpec(
                            ref proc, pooled.WorkerIndex, item.Spec, isGameData, assertionEngine, engineFilter,
                            expectedFailuresEngine, options.AdapterFactory, deathBudget, progressWriter);
                        if (!ReferenceEquals(proc, pooled.Process))
                        {
                            lock (allProcessesLock)
                            {
                                allProcesses.Add(proc);
                            }
                        }

                        concurrentResults.Add((result, item.Spec, isGameData, status, durationMs, deaths));
                    }
                    finally
                    {
                        processPool.Writer.TryWrite(new PooledAdapter(proc, pooled.WorkerIndex));
                    }
                }

                await Parallel.ForEachAsync(
                    filteredSpecs,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    (item, ct) => RunPooledAsync(item, isGameData: false, ct));

                await Parallel.ForEachAsync(
                    filteredGameDataSpecs,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    (item, ct) => RunPooledAsync(item, isGameData: true, ct));

                // Collect results in order
                foreach (var (result, spec, isGameData, status, durationMs, deaths) in concurrentResults)
                {
                    results.Add(result);
                    durationsByResult[result] = durationMs;
                    if (isGameData)
                    {
                        gameDataSpecsByResult[result] = (GameDataSpecFile)spec;
                    }
                    else
                    {
                        specsByResult[result] = (SpecFile)spec;
                    }

                    reportResults.Add(new SpecResultSummary(
                        result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs, deaths));
                }
            }
            finally
            {
                // Dispose every process ever created (originals + adapter-death replacements)
                // regardless of success, describe-gate failure, or a Parallel.ForEachAsync
                // exception — otherwise a child process leaks.
                foreach (var proc in allProcesses)
                {
                    proc.Dispose();
                }
            }
        }
        else
        {
            // Sequential execution with a single adapter process — replaced in place (via `proc`
            // being passed by ref into RunOneSpec) when it dies mid-batch.
            var proc = options.AdapterFactory(0);
            var allProcesses = new HashSet<AdapterProcess> { proc };
            try
            {
                var gameDataSupported = filteredGameDataSpecs.Count == 0
                    || (await AdapterDescriber.DescribeAsync(proc)).Domains.Contains("gamedata");
                if (filteredGameDataSpecs.Count > 0 && !gameDataSupported)
                {
                    SkipGameDataDomain(filteredGameDataSpecs, reportResults);
                    filteredGameDataSpecs = [];
                }

                foreach (var (id, category, spec) in filteredSpecs)
                {
                    var (result, status, durationMs, deaths) = RunOneSpec(
                        ref proc, 0, spec, isGameData: false, assertionEngine, engineFilter, expectedFailuresEngine,
                        options.AdapterFactory, deathBudget, progressWriter);
                    allProcesses.Add(proc);
                    results.Add(result);
                    specsByResult[result] = spec;
                    durationsByResult[result] = durationMs;
                    reportResults.Add(new SpecResultSummary(
                        result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs, deaths));
                }

                foreach (var (id, category, spec) in filteredGameDataSpecs)
                {
                    var (result, status, durationMs, deaths) = RunOneSpec(
                        ref proc, 0, spec, isGameData: true, assertionEngine, engineFilter, expectedFailuresEngine,
                        options.AdapterFactory, deathBudget, progressWriter);
                    allProcesses.Add(proc);
                    results.Add(result);
                    gameDataSpecsByResult[result] = spec;
                    durationsByResult[result] = durationMs;
                    reportResults.Add(new SpecResultSummary(
                        result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs, deaths));
                }
            }
            finally
            {
                foreach (var p in allProcesses)
                {
                    p.Dispose();
                }
            }
        }

        sw.Stop();

        return SpecSuiteResult.Create(results, reportResults, specsByResult, gameDataSpecsByResult, durationsByResult, totalSpecs, sw.Elapsed, expectedFailuresEngine);
    }

    /// <summary>
    /// Applies filter/tag/engine selection to a domain's spec sources, recording skip/load-error
    /// records for excluded specs (into <paramref name="reportResults"/>, and additionally into
    /// <paramref name="results"/> for load errors) exactly as the pre-domains pipeline did for
    /// roster specs. Generic over <see cref="SpecFileBase"/> so the same logic serves both roster
    /// (<see cref="SpecFile"/>) and gamedata (<see cref="GameDataSpecFile"/>) specs uniformly.
    /// </summary>
    private static List<(string Id, string Category, TSpec Spec)> PreFilterSpecs<TSpec>(
        IEnumerable<(string IdForLoad, string Id, string Category, Func<TSpec> Loader)> specSources,
        IReadOnlyList<string>? filterPatterns,
        string filterLabel,
        TagFilter? tagFilter,
        string? engineFilter,
        List<SpecResult> results,
        List<SpecResultSummary> reportResults)
        where TSpec : SpecFileBase
    {
        var filtered = new List<(string Id, string Category, TSpec Spec)>();
        foreach (var (_, id, category, loader) in specSources)
        {
            var specName = $"{category}/{id}";

            if (filterPatterns is not null && !filterPatterns.Any(p => specName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                reportResults.Add(new SpecResultSummary(id, category, "", "skipped", [$"Skipped by filter '{filterLabel}'"]));
                continue;
            }

            TSpec spec;
            try
            {
                spec = loader();
            }
            catch (Exception ex)
            {
                var failures = new List<string> { $"Load error: {ex.Message}" };
                results.Add(new SpecResult(id, category, "Failed to load", failures));
                reportResults.Add(new SpecResultSummary(id, category, "Failed to load", "failed", failures));
                continue;
            }

            if (tagFilter is not null && !tagFilter.Matches(spec.Tags))
            {
                reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
                    [$"Skipped by tag filter '{tagFilter}'"], spec.Tags));
                continue;
            }

            if (engineFilter is not null && !spec.IsApplicableTo(engineFilter))
            {
                reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
                    [$"Skipped by engine filter '{engineFilter}'"], spec.Tags));
                continue;
            }

            filtered.Add((id, category, spec));
        }

        return filtered;
    }

    /// <summary>
    /// Records every filtered gamedata spec as a skip (no adapter call made) when the pre-flight
    /// describe handshake shows the adapter doesn't advertise the "gamedata" domain.
    /// </summary>
    private static void SkipGameDataDomain(
        List<(string Id, string Category, GameDataSpecFile Spec)> filteredGameDataSpecs,
        List<SpecResultSummary> reportResults)
    {
        foreach (var (id, category, spec) in filteredGameDataSpecs)
        {
            reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
                ["Skipped: engine does not support gamedata domain"], spec.Tags));
        }
    }

    /// <summary>
    /// Execute one spec against an adapter, with #304's adapter-death recovery: if the process died
    /// during the attempt, retry ONCE on a fresh replacement process (the death is recorded either
    /// way, via the returned tuple's <c>AdapterDeaths</c> count); if the retry also dies, fail
    /// the spec with a clear adapter-death reason; if the run's <see cref="AdapterDeathBudget"/> is
    /// already spent, stop replacing and fail outright instead of spending a retry. This is THE
    /// single per-spec execution path — the sequential and parallel loops, roster and gamedata, all
    /// funnel through here, so the recovery policy exists exactly once. <paramref name="proc"/> is
    /// passed by ref so a replacement transparently becomes the caller's process going forward
    /// (returned to the pool in the parallel path, or kept as the sequential loop's sole process).
    /// </summary>
    private static (SpecResult Result, string Status, double DurationMs, int AdapterDeaths) RunOneSpec(
        ref AdapterProcess proc,
        int workerIndex,
        SpecFileBase spec,
        bool isGameData,
        string? assertionEngine,
        string? engineFilter,
        string? expectedFailuresEngine,
        Func<int, AdapterProcess> adapterFactory,
        AdapterDeathBudget deathBudget,
        TextWriter? progressWriter)
    {
        var (result, status, durationMs) = RunOneSpecAttempt(
            proc, spec, isGameData, assertionEngine, engineFilter, expectedFailuresEngine);
        if (!proc.HasExited)
        {
            return (result, status, durationMs, 0);
        }

        // The adapter process died during this attempt (setup, a step, or best-effort teardown).
        if (result.Passed)
        {
            // Nothing to rescue — the spec's own verdict already succeeded (e.g. the process died
            // during best-effort teardown, after every assertion had already passed). The crash is
            // still real and must stay visible, and the process must be replaced before the next
            // spec uses it, but a death must never flip an already-genuine pass into a failure.
            if (!deathBudget.IsExceeded)
            {
                RecordDeath(deathBudget, progressWriter);
                if (!deathBudget.IsExceeded)
                {
                    proc = ReplaceProcess(proc, workerIndex, adapterFactory);
                }
            }

            return (result, status, durationMs, 1);
        }

        // The spec itself failed AND the process died: a genuine candidate for the rescue retry —
        // unless the run's death cap is already spent, in which case stop replacing and fail
        // outright without spending a retry on an engine that's already shown it's dying
        // deterministically.
        if (deathBudget.IsExceeded)
        {
            return (CapExceededResult(spec, deathBudget.MaxDeaths), "failed", durationMs, 1);
        }

        RecordDeath(deathBudget, progressWriter);
        if (deathBudget.IsExceeded)
        {
            // This very death tipped the budget over — stop replacing, fail outright, no retry spent.
            return (CapExceededResult(spec, deathBudget.MaxDeaths), "failed", durationMs, 1);
        }

        proc = ReplaceProcess(proc, workerIndex, adapterFactory);
        var (retryResult, retryStatus, retryDurationMs) = RunOneSpecAttempt(
            proc, spec, isGameData, assertionEngine, engineFilter, expectedFailuresEngine);
        if (!proc.HasExited)
        {
            // Retried on a fresh process and it did not die — the retry's verdict wins, but the
            // death that preceded it is still recorded (never silently swallowed).
            return (retryResult, retryStatus, retryDurationMs, 1);
        }

        // The retry ALSO died: fail this spec with the adapter-death reason, replace (unless the
        // cap now stops us), and let the caller continue with the remaining specs.
        if (!deathBudget.IsExceeded)
        {
            RecordDeath(deathBudget, progressWriter);
        }

        var failed = AdapterDeathResult(spec);
        if (!deathBudget.IsExceeded)
        {
            proc = ReplaceProcess(proc, workerIndex, adapterFactory);
        }

        return (failed, "failed", retryDurationMs, 2);
    }

    /// <summary>One execution attempt of one spec — the pre-#304 body of <c>RunOneSpec</c>, unchanged except for the adapter-death telemetry tag at the end.</summary>
    private static (SpecResult Result, string Status, double DurationMs) RunOneSpecAttempt(
        AdapterProcess proc,
        SpecFileBase spec,
        bool isGameData,
        string? assertionEngine,
        string? engineFilter,
        string? expectedFailuresEngine)
    {
        using var activity = HarnessTelemetry.StartSpec(
            spec.Id,
            spec.Category,
            isGameData ? "gamedata" : "roster");

        var sw = Stopwatch.StartNew();
        SpecResult result;
        if (isGameData)
        {
            using var engine = new JsonProtocolGameDataEngine(proc, null);
            var runner = new GameDataRunner(engine, assertionEngine ?? engineFilter);
            result = runner.Run((GameDataSpecFile)spec);
        }
        else
        {
            var rosterSpec = (SpecFile)spec;
            var timeout = rosterSpec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
            using var engine = new JsonProtocolEngine(proc, timeout);
            var runner = new RosterRunner(engine, new DataSourceResolver(), assertionEngine ?? engineFilter);
            result = runner.Run(rosterSpec);
        }

        sw.Stop();

        var status = ComputeStatus(result, spec, expectedFailuresEngine);
        HarnessTelemetry.SetVerdict(activity, status);

        // Checked here (right where `proc` is in scope) rather than by the caller re-deriving it —
        // this is the one place the attempt and the process it ran on are both directly at hand.
        if (proc.HasExited)
        {
            HarnessTelemetry.SetAdapterDeath(activity);
        }

        return (result, status, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Records one adapter death: bumps the shared budget, the resource-death counter, and (once, exactly when the cap first trips) a progress-writer warning naming the cap.</summary>
    private static void RecordDeath(AdapterDeathBudget deathBudget, TextWriter? progressWriter)
    {
        var count = deathBudget.Increment();
        ResourceMetrics.Died("adapter-process");
        if (count == deathBudget.MaxDeaths + 1)
        {
            progressWriter?.WriteLine(
                $"adapter-death cap ({deathBudget.MaxDeaths}) reached — no further adapter " +
                "replacement/retry will be attempted for the remainder of this run.");
        }
    }

    /// <summary>Disposes a dead process and creates its replacement via the caller's factory (same worker index — same diagnostics/telemetry identity).</summary>
    private static AdapterProcess ReplaceProcess(AdapterProcess dead, int workerIndex, Func<int, AdapterProcess> adapterFactory)
    {
        dead.Dispose();
        return adapterFactory(workerIndex);
    }

    private const string AdapterDeathTag = "ADAPTER DEATH";

    private static SpecResult AdapterDeathResult(SpecFileBase spec) =>
        new(spec.Id, spec.Category, spec.Description,
            [$"{AdapterDeathTag}: the adapter process died while running this spec, and a retry on a fresh process also died. Failing without a further retry."]);

    private static SpecResult CapExceededResult(SpecFileBase spec, int maxDeaths) =>
        new(spec.Id, spec.Category, spec.Description,
            [$"{AdapterDeathTag}: the adapter process died while running this spec, and this run's adapter-death cap ({maxDeaths}) has already been reached — no further replacement/retry will be attempted for the remainder of this run."]);

    private static string ComputeStatus(SpecResult result, SpecFileBase spec, string? expectedFailuresEngine)
    {
        var status = result.Passed ? "passed" : "failed";
        if (expectedFailuresEngine is not null)
        {
            var isExpectedFail = spec.IsExpectedToFail(expectedFailuresEngine);
            if (!result.Passed && isExpectedFail)
            {
                status = "expected-failure";
            }
            else if (result.Passed && isExpectedFail)
            {
                status = "unexpected-pass";
            }
        }

        return status;
    }

    /// <summary>
    /// One pooled adapter process plus the worker index it was created for (see
    /// <see cref="SpecSuiteOptions.AdapterFactory"/>) — carried through the parallel path's
    /// <see cref="System.Threading.Channels.Channel{T}"/> so a death-replacement is spawned with the
    /// same worker identity as the process it replaces, rather than an anonymous one.
    /// </summary>
    private sealed record PooledAdapter(AdapterProcess Process, int WorkerIndex);
}
