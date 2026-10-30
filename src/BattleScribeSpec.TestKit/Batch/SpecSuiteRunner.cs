using System.Diagnostics;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Scenario;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Batch;

/// <summary>
/// Runs a suite of roster, GameData and/or scenario conformance specs against an adapter, applying
/// filter/tag/engine selection and (optionally) parallel execution. This is the batch pipeline
/// extracted verbatim from the Runner so the unified CLI and the Runner shell can share it.
/// </summary>
/// <remarks>
/// <para>
/// Domain discovery rule (see <see cref="SpecSuiteOptions.Domains"/>): when
/// <see cref="SpecSuiteOptions.SpecsDirectory"/> is set explicitly, its <c>gamedata</c> subtree holds the
/// gamedata specs and its <c>scenario</c> subtree the scenario specs — mirroring the repo's own
/// <c>specs/roster</c>, <c>specs/gamedata</c> and <c>specs/scenario</c> sibling trees — and roster specs are
/// discovered everywhere else under it. A spec's category is its immediate parent folder, so
/// <c>--specs specs/</c> and <c>--specs specs/roster</c> discover the same roster specs, and an ad-hoc
/// fixture directory of category folders is a roster tree as it always was.
/// </para>
/// <para>
/// <b>Why "everywhere else", and not "the <c>roster</c> subtree when there is one".</b> Roster discovery used
/// to take every yaml under the explicit directory, so <c>--specs specs/</c> swept each gamedata spec in as a
/// roster spec and failed to load it as the wrong model. Reading a <c>roster</c> subdirectory as the roster
/// tree is the obvious fix and the wrong one: <c>specs/roster</c> has a <em>category</em> named
/// <c>roster</c>, so <c>--specs specs/roster</c> would have narrowed to it. The other domains' subtrees are
/// theirs whether or not the run includes them, and that is all roster discovery needs to know.
/// </para>
/// <para>
/// There is no fallback to the repo's tree: an explicit directory is the whole answer. When
/// <see cref="SpecSuiteOptions.SpecsDirectory"/> is null, each domain is discovered independently: roster
/// via <see cref="SpecLoader.FindRosterSpecsDirectory"/> then the embedded fallback, gamedata via
/// <see cref="SpecLoader.FindGameDataSpecsDirectory"/>, scenario via
/// <see cref="SpecLoader.FindScenarioSpecsDirectory"/> (no embedded fallback exists for either).
/// </para>
/// <para>
/// The adapter process pool is shared across domains — the same process(es) that serve roster
/// specs also serve gamedata and scenario specs, and a scenario spec drives one process's roster and
/// gamedata engines together. Before any gamedata or scenario spec runs, the first pooled process is
/// asked to <c>describe</c> itself once; a gamedata spec needs it to advertise "gamedata", a scenario
/// spec needs "roster" <em>and</em> "gamedata", and every spec whose domain the adapter does not serve
/// becomes a skip record instead of being executed.
/// </para>
/// </remarks>
public static class SpecSuiteRunner
{
    /// <summary>
    /// The identity a scenario runs under when the run names no engine (an anonymous adapter with no
    /// <see cref="SpecSuiteOptions.AssertionEngine"/>). <see cref="ScenarioRunner"/> takes a name where the
    /// other runners take null; this is the label they print for null, and as an override key it matches no
    /// spec's <c>engines:</c> entry, so only the base expectations apply — which is what null means to them.
    /// </summary>
    public const string UnnamedEngine = "(unnamed engine)";

    /// <summary>The skip reason of a gamedata spec on an adapter that does not advertise the gamedata domain.</summary>
    public const string GameDataUnsupportedReason = "Skipped: engine does not support gamedata domain";

    /// <summary>The skip reason of a scenario spec on an adapter that does not advertise both domains.</summary>
    public const string ScenarioUnsupportedReason =
        "Skipped: engine does not support both roster and gamedata, which a scenario drives together";

    public static async Task<SpecSuiteResult> RunAsync(SpecSuiteOptions options, TextWriter? progressWriter = null)
    {
        var domains = options.Domains;
        var runRoster = domains.Contains(SpecDomain.Roster.Name(), StringComparer.OrdinalIgnoreCase);
        var runGameData = domains.Contains(SpecDomain.GameData.Name(), StringComparer.OrdinalIgnoreCase);
        var runScenario = domains.Contains(SpecDomain.Scenario.Name(), StringComparer.OrdinalIgnoreCase);

        // ===== Discover specs =====
        var specsDir = options.SpecsDirectory;
        List<(string Path, string Id, string Category)>? fileSpecs = null;
        List<(string ResourceName, string Id, string Category)>? embeddedSpecs = null;
        List<(string Path, string Id, string Category)>? gameDataFileSpecs = null;
        List<(string Path, string Id, string Category)>? scenarioFileSpecs = null;

        if (specsDir is not null)
        {
            if (!Directory.Exists(specsDir))
            {
                throw new InvalidOperationException($"specs directory not found: {specsDir}");
            }

            if (runRoster)
            {
                // The other domains' subtrees are theirs whether or not this run includes them — see the
                // class remarks.
                var otherDomainTrees = new[] { SpecDomain.GameData, SpecDomain.Scenario }
                    .Select(domain => DomainSubdir(specsDir, domain))
                    .ToList();
                fileSpecs = [.. SpecLoader.DiscoverSpecs(specsDir).Where(s => !otherDomainTrees.Any(tree => IsUnder(tree, s.Path)))];
            }

            if (runGameData)
            {
                var gameDataSubdir = DomainSubdir(specsDir, SpecDomain.GameData);
                if (Directory.Exists(gameDataSubdir))
                {
                    gameDataFileSpecs = [.. SpecLoader.DiscoverGameDataSpecs(gameDataSubdir)];
                }
            }

            if (runScenario)
            {
                var scenarioSubdir = DomainSubdir(specsDir, SpecDomain.Scenario);
                if (Directory.Exists(scenarioSubdir))
                {
                    scenarioFileSpecs = [.. SpecLoader.DiscoverScenarioSpecs(scenarioSubdir)];
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

            if (runScenario)
            {
                var scenarioDir = SpecLoader.FindScenarioSpecsDirectory();
                if (scenarioDir is not null)
                {
                    scenarioFileSpecs = [.. SpecLoader.DiscoverScenarioSpecs(scenarioDir)];
                }
            }
        }

        var totalSpecs = (fileSpecs?.Count ?? embeddedSpecs?.Count ?? 0)
            + (gameDataFileSpecs?.Count ?? 0)
            + (scenarioFileSpecs?.Count ?? 0);
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
        var scenarioSpecsByResult = new Dictionary<SpecResult, ScenarioSpecFile>();
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

        IEnumerable<(string IdForLoad, string Id, string Category, Func<ScenarioSpecFile> Loader)> scenarioSpecSources =
            scenarioFileSpecs?.Select(s => (s.Path, s.Id, s.Category, (Func<ScenarioSpecFile>)(() => SpecLoader.LoadScenario(s.Path))))
            ?? [];

        // Pre-filter specs (filtering doesn't need the adapter)
        var filterLabel = filterPatterns is not null ? string.Join(",", filterPatterns) : "";
        var filteredSpecs = PreFilterSpecs(specSources, SpecDomain.Roster, filterPatterns, filterLabel, tagFilter, engineFilter, results, reportResults);
        var filteredGameDataSpecs = PreFilterSpecs(gameDataSpecSources, SpecDomain.GameData, filterPatterns, filterLabel, tagFilter, engineFilter, results, reportResults);
        // A scenario's `engines:` follows the rule its blocks do: a UI driver inherits its base engine's
        // entry (ScenarioRunner, the scenario lanes). Roster and gamedata keep this runner's exact-name rule.
        var filteredScenarioSpecs = PreFilterSpecs(scenarioSpecSources, SpecDomain.Scenario, filterPatterns, filterLabel, tagFilter, engineFilter, results, reportResults, baseEngine: assertionEngine);

        // Counted before the describe gate below can still skip some of them: "selected" is what the
        // filters asked to run, so the nothing-executed message can tell "matched nothing" from
        // "matched, then skipped" (SpecSuiteResult.NothingExecutedMessage).
        var selected = filteredSpecs.Count + filteredGameDataSpecs.Count + filteredScenarioSpecs.Count;

        // One spec, one execution path, whichever domain and whichever loop: RunOneSpec, then the result
        // filed under its domain's map and reported with its domain.
        void Record(SpecResult result, SpecFileBase spec, SpecDomain domain, string status, double durationMs, int deaths)
        {
            results.Add(result);
            durationsByResult[result] = durationMs;
            switch (domain)
            {
                case SpecDomain.Roster:
                    specsByResult[result] = (SpecFile)spec;
                    break;
                case SpecDomain.GameData:
                    gameDataSpecsByResult[result] = (GameDataSpecFile)spec;
                    break;
                case SpecDomain.Scenario:
                    scenarioSpecsByResult[result] = (ScenarioSpecFile)spec;
                    break;
            }

            reportResults.Add(new SpecResultSummary(
                result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs, deaths,
                domain.Name()));
        }

        if (workers > 1)
        {
            // Parallel execution with N adapter processes
            progressWriter?.WriteLine($"Running {selected} specs with {workers} workers...");

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

                (filteredGameDataSpecs, filteredScenarioSpecs) = await ApplyDescribeGateAsync(
                    initialProcesses[0], filteredGameDataSpecs, filteredScenarioSpecs, reportResults);

                // Channel-based process pool. Each pooled item remembers which worker index it
                // belongs to, so a death-replacement process is created with the SAME worker
                // identity (its diagnostics dir / telemetry tag) as the one it replaces.
                var processPool = System.Threading.Channels.Channel.CreateBounded<PooledAdapter>(workers);
                for (var w = 0; w < initialProcesses.Count; w++)
                {
                    processPool.Writer.TryWrite(new PooledAdapter(initialProcesses[w], w));
                }

                var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<(SpecResult Result, SpecFileBase Spec, SpecDomain Domain, string Status, double DurationMs, int AdapterDeaths)>();

                async ValueTask RunPooledAsync<TSpec>((string Id, string Category, TSpec Spec) item, SpecDomain domain, CancellationToken ct)
                    where TSpec : SpecFileBase
                {
                    var pooled = await processPool.Reader.ReadAsync(ct);
                    var proc = pooled.Process;
                    try
                    {
                        var (result, status, durationMs, deaths) = RunOneSpec(
                            ref proc, pooled.WorkerIndex, item.Spec, domain, assertionEngine, engineFilter,
                            expectedFailuresEngine, options.AdapterFactory, deathBudget, progressWriter);
                        if (!ReferenceEquals(proc, pooled.Process))
                        {
                            lock (allProcessesLock)
                            {
                                allProcesses.Add(proc);
                            }
                        }

                        concurrentResults.Add((result, item.Spec, domain, status, durationMs, deaths));
                    }
                    finally
                    {
                        processPool.Writer.TryWrite(new PooledAdapter(proc, pooled.WorkerIndex));
                    }
                }

                var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
                await Parallel.ForEachAsync(filteredSpecs, parallel, (item, ct) => RunPooledAsync(item, SpecDomain.Roster, ct));
                await Parallel.ForEachAsync(filteredGameDataSpecs, parallel, (item, ct) => RunPooledAsync(item, SpecDomain.GameData, ct));
                await Parallel.ForEachAsync(filteredScenarioSpecs, parallel, (item, ct) => RunPooledAsync(item, SpecDomain.Scenario, ct));

                // Collect results in order
                foreach (var (result, spec, domain, status, durationMs, deaths) in concurrentResults)
                {
                    Record(result, spec, domain, status, durationMs, deaths);
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

            void RunSequentially<TSpec>(List<(string Id, string Category, TSpec Spec)> specs, SpecDomain domain)
                where TSpec : SpecFileBase
            {
                foreach (var (_, _, spec) in specs)
                {
                    var (result, status, durationMs, deaths) = RunOneSpec(
                        ref proc, 0, spec, domain, assertionEngine, engineFilter, expectedFailuresEngine,
                        options.AdapterFactory, deathBudget, progressWriter);
                    allProcesses.Add(proc);
                    Record(result, spec, domain, status, durationMs, deaths);
                }
            }

            try
            {
                (filteredGameDataSpecs, filteredScenarioSpecs) = await ApplyDescribeGateAsync(
                    proc, filteredGameDataSpecs, filteredScenarioSpecs, reportResults);

                RunSequentially(filteredSpecs, SpecDomain.Roster);
                RunSequentially(filteredGameDataSpecs, SpecDomain.GameData);
                RunSequentially(filteredScenarioSpecs, SpecDomain.Scenario);
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

        return SpecSuiteResult.Create(results, reportResults, specsByResult, gameDataSpecsByResult, scenarioSpecsByResult, durationsByResult, totalSpecs, selected, sw.Elapsed, expectedFailuresEngine);
    }

    /// <summary>
    /// Applies filter/tag/engine selection to a domain's spec sources, recording skip/load-error
    /// records for excluded specs (into <paramref name="reportResults"/>, and additionally into
    /// <paramref name="results"/> for load errors) exactly as the pre-domains pipeline did for
    /// roster specs. Generic over <see cref="SpecFileBase"/> so the same logic serves roster
    /// (<see cref="SpecFile"/>), gamedata (<see cref="GameDataSpecFile"/>) and scenario
    /// (<see cref="ScenarioSpecFile"/>) specs uniformly. Every record carries its domain.
    /// </summary>
    private static List<(string Id, string Category, TSpec Spec)> PreFilterSpecs<TSpec>(
        IEnumerable<(string IdForLoad, string Id, string Category, Func<TSpec> Loader)> specSources,
        SpecDomain domain,
        IReadOnlyList<string>? filterPatterns,
        string filterLabel,
        TagFilter? tagFilter,
        string? engineFilter,
        List<SpecResult> results,
        List<SpecResultSummary> reportResults,
        string? baseEngine = null)
        where TSpec : SpecFileBase
    {
        var domainName = domain.Name();
        var filtered = new List<(string Id, string Category, TSpec Spec)>();
        foreach (var (_, id, category, loader) in specSources)
        {
            var specName = $"{category}/{id}";

            if (filterPatterns is not null && !filterPatterns.Any(p => specName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                reportResults.Add(new SpecResultSummary(id, category, "", "skipped", [$"Skipped by filter '{filterLabel}'"],
                    Domain: domainName));
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
                reportResults.Add(new SpecResultSummary(id, category, "Failed to load", "failed", failures, Domain: domainName));
                continue;
            }

            if (tagFilter is not null && !tagFilter.Matches(spec.Tags))
            {
                reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
                    [$"Skipped by tag filter '{tagFilter}'"], spec.Tags, Domain: domainName));
                continue;
            }

            if (engineFilter is not null && string.Equals(spec.GetExpectation(engineFilter, baseEngine), "skip", StringComparison.OrdinalIgnoreCase))
            {
                reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
                    [$"Skipped by engine filter '{engineFilter}'"], spec.Tags, Domain: domainName));
                continue;
            }

            filtered.Add((id, category, spec));
        }

        return filtered;
    }

    /// <summary>
    /// The pre-flight <c>describe</c> gate: asks <paramref name="proc"/> once which domains it serves — only
    /// when a gamedata or scenario spec was selected — and turns every selected spec of a domain it cannot
    /// serve into a skip record (no adapter call made). A gamedata spec needs "gamedata"; a scenario spec
    /// drives the roster and the gamedata engine together and needs both. Returns what is left to run.
    /// </summary>
    private static async Task<(List<(string Id, string Category, GameDataSpecFile Spec)> GameData, List<(string Id, string Category, ScenarioSpecFile Spec)> Scenario)>
        ApplyDescribeGateAsync(
            AdapterProcess proc,
            List<(string Id, string Category, GameDataSpecFile Spec)> gameData,
            List<(string Id, string Category, ScenarioSpecFile Spec)> scenario,
            List<SpecResultSummary> reportResults)
    {
        if (gameData.Count == 0 && scenario.Count == 0)
        {
            return (gameData, scenario);
        }

        var advertised = (await AdapterDescriber.DescribeAsync(proc)).Domains;
        var servesRoster = advertised.Contains(SpecDomain.Roster.Name());
        var servesGameData = advertised.Contains(SpecDomain.GameData.Name());

        if (gameData.Count > 0 && !servesGameData)
        {
            SkipDomain(gameData, SpecDomain.GameData, GameDataUnsupportedReason, reportResults);
            gameData = [];
        }

        if (scenario.Count > 0 && !(servesRoster && servesGameData))
        {
            SkipDomain(scenario, SpecDomain.Scenario, ScenarioUnsupportedReason, reportResults);
            scenario = [];
        }

        return (gameData, scenario);
    }

    /// <summary>Records every one of <paramref name="specs"/> as skipped for <paramref name="reason"/>.</summary>
    private static void SkipDomain<TSpec>(
        List<(string Id, string Category, TSpec Spec)> specs,
        SpecDomain domain,
        string reason,
        List<SpecResultSummary> reportResults)
        where TSpec : SpecFileBase
    {
        foreach (var (id, category, spec) in specs)
        {
            reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped", [reason], spec.Tags,
                Domain: domain.Name()));
        }
    }

    private static string DomainSubdir(string specsDir, SpecDomain domain) => Path.Combine(specsDir, domain.Name());

    /// <summary>
    /// Whether <paramref name="path"/> lies inside <paramref name="root"/>, by <see cref="Path.GetRelativePath"/>
    /// so the platform's own path-casing rule applies — the rule <c>Directory.Exists</c> applied when the
    /// subtree was looked for.
    /// </summary>
    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
    /// <remarks>
    /// <b>Death counting (code review follow-up, see MINOR 3 in the #304 hygiene review):</b>
    /// <paramref name="proc"/>'s <c>HasExited</c> is snapshotted into <c>wasAlreadyDead</c> BEFORE
    /// this attempt runs. That is what lets every branch below tell "this specific process instance
    /// just crashed for the first time" (always a real, distinct death — always recorded) apart from
    /// "the caller handed us a corpse we (or a sibling spec) already recorded" (a repeat — never
    /// recorded again). The previous version gated recording on the run's aggregate
    /// <c>deathBudget.IsExceeded</c> instead, which conflated the two: once the cap had tripped
    /// anywhere, a genuinely new crash on a completely different pooled process (a live scenario
    /// under parallel workers) silently stopped being counted.
    /// </remarks>
    private static (SpecResult Result, string Status, double DurationMs, int AdapterDeaths) RunOneSpec(
        ref AdapterProcess proc,
        int workerIndex,
        SpecFileBase spec,
        SpecDomain domain,
        string? assertionEngine,
        string? engineFilter,
        string? expectedFailuresEngine,
        Func<int, AdapterProcess> adapterFactory,
        AdapterDeathBudget deathBudget,
        TextWriter? progressWriter)
    {
        var wasAlreadyDead = proc.HasExited;

        var (result, status, durationMs) = RunOneSpecAttempt(
            proc, spec, domain, assertionEngine, engineFilter, expectedFailuresEngine);
        if (!proc.HasExited)
        {
            return (result, status, durationMs, 0);
        }

        // The adapter process died during this attempt (setup, a step, or best-effort teardown) —
        // or was already dead when we were handed it (a repeat use of a corpse the cap has already
        // stopped replacing).
        if (result.Passed)
        {
            // Nothing to rescue — the spec's own verdict already succeeded (e.g. the process died
            // during best-effort teardown, after every assertion had already passed). The crash is
            // still real and must stay visible, and the process must be replaced before the next
            // spec uses it, but a death must never flip an already-genuine pass into a failure.
            if (!wasAlreadyDead)
            {
                RecordDeath(deathBudget, progressWriter);
            }

            if (!deathBudget.IsExceeded)
            {
                proc = TryReplaceProcess(proc, workerIndex, adapterFactory, deathBudget, progressWriter).Process;
            }

            return (result, status, durationMs, 1);
        }

        // The spec itself failed AND the process died: a genuine candidate for the rescue retry —
        // unless the run's death cap is already spent, in which case stop replacing and fail
        // outright without spending a retry on an engine that's already shown it's dying
        // deterministically.
        if (wasAlreadyDead && deathBudget.IsExceeded)
        {
            // Pure repeat: this exact corpse was already dead (and its death already recorded)
            // before this attempt even started, and the cap was already tripped. Fail fast without
            // touching the counter or calling the factory again.
            return (CapExceededResult(spec, deathBudget.MaxDeaths), "failed", durationMs, 1);
        }

        if (!wasAlreadyDead)
        {
            // A genuinely new, distinct crash — always counted, regardless of whether some OTHER
            // process (this worker's earlier one, or a sibling worker's) already tripped the cap.
            RecordDeath(deathBudget, progressWriter);
        }

        if (deathBudget.IsExceeded)
        {
            // Either this very death tipped the budget over, or a sibling worker's already had —
            // either way, stop replacing/retrying for this spec.
            return (CapExceededResult(spec, deathBudget.MaxDeaths), "failed", durationMs, 1);
        }

        var replacement = TryReplaceProcess(proc, workerIndex, adapterFactory, deathBudget, progressWriter);
        proc = replacement.Process;
        if (!replacement.Succeeded)
        {
            // adapterFactory itself threw while spawning the replacement — see TryReplaceProcess.
            // Fail this spec now with a clear reason instead of attempting to run on the inert
            // sentinel it handed back; `proc` still carries that sentinel forward so the caller (and,
            // in the parallel path, the pool) always has a non-null process to keep going with.
            return (ReplacementFailedResult(spec, replacement.Error!), "failed", durationMs, 2);
        }

        var (retryResult, retryStatus, retryDurationMs) = RunOneSpecAttempt(
            proc, spec, domain, assertionEngine, engineFilter, expectedFailuresEngine);
        if (!proc.HasExited)
        {
            // Retried on a fresh process and it did not die — the retry's verdict wins, but the
            // death that preceded it is still recorded (never silently swallowed).
            return (retryResult, retryStatus, retryDurationMs, 1);
        }

        // The retry ALSO died. The replacement was alive when the retry began, so this is always a
        // NEW, distinct crash — always counted.
        if (!deathBudget.IsExceeded)
        {
            RecordDeath(deathBudget, progressWriter);
        }

        var failed = AdapterDeathResult(spec);
        if (!deathBudget.IsExceeded)
        {
            proc = TryReplaceProcess(proc, workerIndex, adapterFactory, deathBudget, progressWriter).Process;
        }

        return (failed, "failed", retryDurationMs, 2);
    }

    /// <summary>One execution attempt of one spec — the pre-#304 body of <c>RunOneSpec</c>, unchanged except for the adapter-death telemetry tag at the end.</summary>
    private static (SpecResult Result, string Status, double DurationMs) RunOneSpecAttempt(
        AdapterProcess proc,
        SpecFileBase spec,
        SpecDomain domain,
        string? assertionEngine,
        string? engineFilter,
        string? expectedFailuresEngine)
    {
        using var activity = HarnessTelemetry.StartSpec(spec.Id, spec.Category, domain.Name());

        var sw = Stopwatch.StartNew();
        SpecResult result;
        if (domain == SpecDomain.GameData)
        {
            using var engine = new JsonProtocolGameDataEngine(proc, null);
            var runner = new GameDataRunner(engine, assertionEngine ?? engineFilter);
            result = runner.Run((GameDataSpecFile)spec);
        }
        else if (domain == SpecDomain.Scenario)
        {
            // One process, both editors: the adapter keeps its roster and gamedata engines in separate
            // slots, so the two wrappers drive them side by side. Each sends `teardown` when disposed, which
            // resets both slots; the second is a no-op. The scenario's identity is the concrete engine, with
            // the assertion engine as the base whose declarations apply when the spec names no concrete one.
            using var dataEngine = new JsonProtocolGameDataEngine(proc, null);
            using var rosterEngine = new JsonProtocolEngine(proc, null);
            var runner = new ScenarioRunner(dataEngine, rosterEngine, engineFilter ?? assertionEngine ?? UnnamedEngine, assertionEngine);
            result = runner.Run((ScenarioSpecFile)spec);
        }
        else
        {
            var rosterSpec = (SpecFile)spec;
            var timeout = rosterSpec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
            using var engine = new JsonProtocolEngine(proc, timeout);
            var runner = new RosterRunner(engine, new DataSourceResolver(), assertionEngine ?? engineFilter, engineFilter);
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

    /// <summary>
    /// Disposes a dead process and tries to create its replacement via the caller's factory (same
    /// worker index — same diagnostics/telemetry identity). Never throws: <paramref name="adapterFactory"/>
    /// is plausibly unreliable right after a crash (the engine binary is gone, the JVM won't start,
    /// the machine is out of memory), and this is reachable MID-RUN (#304's replacement path), not
    /// just at startup — an uncaught exception here would fault <c>Parallel.ForEachAsync</c> and
    /// crash the entire batch. A factory failure is itself treated as an adapter-death-class event
    /// (recorded and counted against <paramref name="deathBudget"/>, same as a process that starts
    /// and then crashes) so a permanently broken factory still degrades to "the cap trips and the
    /// remainder fails fast" instead of being retried forever or propagating out uncaught.
    /// </summary>
    /// <returns>
    /// On success, the new live process. On failure, <paramref name="dead"/> itself (already
    /// disposed) is returned as an inert sentinel — <c>proc</c> must never become null, because the
    /// parallel path's pool write-back (<c>processPool.Writer.TryWrite</c> in the caller's
    /// <c>finally</c>) must always have a non-null <see cref="AdapterProcess"/> to hand back, or a
    /// worker blocks forever on <c>Channel&lt;T&gt;.Reader.ReadAsync</c>. Any subsequent attempt
    /// against the sentinel reports <see cref="AdapterProcess.HasExited"/> immediately without
    /// calling this same broken factory again.
    /// </returns>
    private static AdapterReplacement TryReplaceProcess(
        AdapterProcess dead, int workerIndex, Func<int, AdapterProcess> adapterFactory,
        AdapterDeathBudget deathBudget, TextWriter? progressWriter)
    {
        dead.Dispose();
        try
        {
            return new AdapterReplacement(adapterFactory(workerIndex), true, null);
        }
        catch (Exception ex)
        {
            RecordDeath(deathBudget, progressWriter);
            return new AdapterReplacement(dead, false, ex);
        }
    }

    /// <summary>Result of <see cref="TryReplaceProcess"/>: either a live replacement (<see cref="Succeeded"/>) or the disposed original process kept as a non-null sentinel, with the factory's exception.</summary>
    private readonly record struct AdapterReplacement(AdapterProcess Process, bool Succeeded, Exception? Error);

    private const string AdapterDeathTag = "ADAPTER DEATH";

    private static SpecResult AdapterDeathResult(SpecFileBase spec) =>
        new(spec.Id, spec.Category, spec.Description,
            [$"{AdapterDeathTag}: the adapter process died while running this spec, and a retry on a fresh process also died. Failing without a further retry."]);

    private static SpecResult CapExceededResult(SpecFileBase spec, int maxDeaths) =>
        new(spec.Id, spec.Category, spec.Description,
            [$"{AdapterDeathTag}: the adapter process died while running this spec, and this run's adapter-death cap ({maxDeaths}) has already been reached — no further replacement/retry will be attempted for the remainder of this run."]);

    /// <summary>The replacement process itself could not be spawned (see <see cref="TryReplaceProcess"/>) — a distinct failure mode from <see cref="AdapterDeathResult"/> (both attempts ran and both died).</summary>
    private static SpecResult ReplacementFailedResult(SpecFileBase spec, Exception error) =>
        new(spec.Id, spec.Category, spec.Description,
            [$"{AdapterDeathTag}: the adapter process died while running this spec, and a replacement process could not be started ({error.GetType().Name}: {error.Message}). Failing without a further retry."]);

    /// <summary>
    /// Computes the passed/failed/expected-failure/unexpected-pass display status for a spec's own
    /// verdict. <b>Deliberately NOT used</b> for <see cref="AdapterDeathResult"/>/
    /// <see cref="CapExceededResult"/>/<see cref="ReplacementFailedResult"/> (code review follow-up,
    /// MINOR 4 in the #304 hygiene review): those three always report <c>"failed"</c> even when
    /// <see cref="SpecSuiteOptions.ExpectedFailuresEngine"/> is set and the spec is marked
    /// expected-to-fail. This is a deliberate choice, not an oversight — an adapter death (a process
    /// crash) is a categorically different outcome from an "expected failure" (a known, semantic
    /// assertion mismatch), and routing it through <see cref="ComputeStatus"/> would let a genuine
    /// infrastructure crash on an expected-to-fail spec silently read as "expected-failure" in the
    /// report/JSON/<c>compare</c> output — exactly the row a human scanning for crashes would skip
    /// past. <see cref="SpecSuiteResult.Create"/>'s exit-code math still independently reclassifies
    /// these results via <c>spec.IsExpectedToFail</c>, so the exit code is correct either way; only
    /// the display string is affected.
    /// </summary>
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
