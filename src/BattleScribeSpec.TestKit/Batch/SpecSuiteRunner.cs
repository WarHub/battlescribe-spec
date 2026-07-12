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

        using var adapterProcess = workers <= 1 ? options.AdapterFactory() : null;

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

            var adapterProcesses = new List<AdapterProcess>();
            try
            {
                for (var w = 0; w < workers; w++)
                {
                    adapterProcesses.Add(options.AdapterFactory());
                }

                var gameDataSupported = filteredGameDataSpecs.Count == 0
                    || (await AdapterDescriber.DescribeAsync(adapterProcesses[0])).Domains.Contains("gamedata");
                if (filteredGameDataSpecs.Count > 0 && !gameDataSupported)
                {
                    SkipGameDataDomain(filteredGameDataSpecs, reportResults);
                    filteredGameDataSpecs = [];
                }

                // Channel-based process pool
                var processPool = System.Threading.Channels.Channel.CreateBounded<AdapterProcess>(workers);
                foreach (var proc in adapterProcesses)
                {
                    processPool.Writer.TryWrite(proc);
                }

                var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<(SpecResult Result, SpecFileBase Spec, bool IsGameData, string Status, double DurationMs)>();

                await Parallel.ForEachAsync(
                    filteredSpecs,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    async (item, ct) =>
                    {
                        var (id, category, spec) = item;
                        var proc = await processPool.Reader.ReadAsync(ct);
                        try
                        {
                            var (result, status, durationMs) = RunOneSpec(
                                proc, spec, isGameData: false, assertionEngine, engineFilter, expectedFailuresEngine);
                            concurrentResults.Add((result, spec, false, status, durationMs));
                        }
                        finally
                        {
                            processPool.Writer.TryWrite(proc);
                        }
                    });

                await Parallel.ForEachAsync(
                    filteredGameDataSpecs,
                    new ParallelOptions { MaxDegreeOfParallelism = workers },
                    async (item, ct) =>
                    {
                        var (id, category, spec) = item;
                        var proc = await processPool.Reader.ReadAsync(ct);
                        try
                        {
                            var (result, status, durationMs) = RunOneSpec(
                                proc, spec, isGameData: true, assertionEngine, engineFilter, expectedFailuresEngine);
                            concurrentResults.Add((result, spec, true, status, durationMs));
                        }
                        finally
                        {
                            processPool.Writer.TryWrite(proc);
                        }
                    });

                // Collect results in order
                foreach (var (result, spec, isGameData, status, durationMs) in concurrentResults)
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

                    reportResults.Add(new SpecResultSummary(result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs));
                }
            }
            finally
            {
                // Dispose adapter processes regardless of success, describe-gate failure,
                // or a Parallel.ForEachAsync exception — otherwise the N child processes leak.
                foreach (var proc in adapterProcesses)
                {
                    proc.Dispose();
                }
            }
        }
        else
        {
            // Sequential execution with single adapter process
            var gameDataSupported = filteredGameDataSpecs.Count == 0
                || (await AdapterDescriber.DescribeAsync(adapterProcess!)).Domains.Contains("gamedata");
            if (filteredGameDataSpecs.Count > 0 && !gameDataSupported)
            {
                SkipGameDataDomain(filteredGameDataSpecs, reportResults);
                filteredGameDataSpecs = [];
            }

            foreach (var (id, category, spec) in filteredSpecs)
            {
                var (result, status, durationMs) = RunOneSpec(
                    adapterProcess!, spec, isGameData: false, assertionEngine, engineFilter, expectedFailuresEngine);
                results.Add(result);
                specsByResult[result] = spec;
                durationsByResult[result] = durationMs;
                reportResults.Add(new SpecResultSummary(
                    result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs));
            }

            foreach (var (id, category, spec) in filteredGameDataSpecs)
            {
                var (result, status, durationMs) = RunOneSpec(
                    adapterProcess!, spec, isGameData: true, assertionEngine, engineFilter, expectedFailuresEngine);
                results.Add(result);
                gameDataSpecsByResult[result] = spec;
                durationsByResult[result] = durationMs;
                reportResults.Add(new SpecResultSummary(
                    result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs));
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
    /// Execute one spec against an adapter. This is the single per-spec execution path — the
    /// sequential and parallel loops, roster and gamedata, all funnel through here, so timing,
    /// tracing and verdict computation exist exactly once.
    /// </summary>
    private static (SpecResult Result, string Status, double DurationMs) RunOneSpec(
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
        return (result, status, sw.Elapsed.TotalMilliseconds);
    }

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
}
