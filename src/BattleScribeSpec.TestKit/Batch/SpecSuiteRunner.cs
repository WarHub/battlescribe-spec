using System.Diagnostics;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Batch;

/// <summary>
/// Runs a suite of roster conformance specs against an adapter, applying filter/tag/engine
/// selection and (optionally) parallel execution. This is the batch pipeline extracted verbatim
/// from the Runner so the unified CLI and the Runner shell can share it.
/// </summary>
public static class SpecSuiteRunner
{
    public static async Task<SpecSuiteResult> RunAsync(SpecSuiteOptions options, TextWriter? progressWriter = null)
    {
        // ===== Discover specs =====
        var specsDir = options.SpecsDirectory;
        List<(string Path, string Id, string Category)>? fileSpecs = null;
        List<(string ResourceName, string Id, string Category)>? embeddedSpecs = null;

        if (specsDir is not null)
        {
            if (!Directory.Exists(specsDir))
            {
                throw new InvalidOperationException($"specs directory not found: {specsDir}");
            }

            fileSpecs = [.. SpecLoader.DiscoverSpecs(specsDir)];
        }
        else
        {
            // Try filesystem first, then embedded
            specsDir = SpecLoader.FindRosterSpecsDirectory();
            if (specsDir is not null)
            {
                fileSpecs = [.. SpecLoader.DiscoverSpecs(specsDir)];
            }
            else
            {
                embeddedSpecs = [.. SpecLoader.DiscoverEmbeddedSpecs()];
            }
        }

        var totalSpecs = fileSpecs?.Count ?? embeddedSpecs?.Count ?? 0;
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
        var skipped = 0;
        var sw = Stopwatch.StartNew();

        IEnumerable<(string IdForLoad, string Id, string Category, Func<SpecFile> Loader)> specSources;
        if (fileSpecs is not null)
        {
            specSources = fileSpecs.Select(s => (s.Path, s.Id, s.Category, (Func<SpecFile>)(() => SpecLoader.Load(s.Path))));
        }
        else
        {
            specSources = embeddedSpecs!.Select(s => (s.ResourceName, s.Id, s.Category, (Func<SpecFile>)(() => SpecLoader.LoadEmbedded(s.ResourceName))));
        }

        // Pre-filter specs (filtering doesn't need the adapter)
        var filterLabel = filterPatterns is not null ? string.Join(",", filterPatterns) : "";
        var filteredSpecs = new List<(string Id, string Category, SpecFile Spec)>();
        foreach (var (_, id, category, loader) in specSources)
        {
            var specName = $"{category}/{id}";

            if (filterPatterns is not null && !filterPatterns.Any(p => specName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                reportResults.Add(new SpecResultSummary(id, category, "", "skipped", [$"Skipped by filter '{filterLabel}'"]));
                continue;
            }

            SpecFile spec;
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
                skipped++;
                reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
                    [$"Skipped by tag filter '{tagFilter}'"], spec.Tags));
                continue;
            }

            if (engineFilter is not null && !spec.IsApplicableTo(engineFilter))
            {
                skipped++;
                reportResults.Add(new SpecResultSummary(id, category, spec.Description, "skipped",
                    [$"Skipped by engine filter '{engineFilter}'"], spec.Tags));
                continue;
            }

            filteredSpecs.Add((id, category, spec));
        }

        if (workers > 1)
        {
            // Parallel execution with N adapter processes
            progressWriter?.WriteLine($"Running {filteredSpecs.Count} specs with {workers} workers...");

            var adapterProcesses = new List<AdapterProcess>();
            for (var w = 0; w < workers; w++)
            {
                adapterProcesses.Add(options.AdapterFactory());
            }

            // Channel-based process pool
            var processPool = System.Threading.Channels.Channel.CreateBounded<AdapterProcess>(workers);
            foreach (var proc in adapterProcesses)
            {
                processPool.Writer.TryWrite(proc);
            }

            var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<(SpecResult Result, SpecFile Spec, string Status)>();

            await Parallel.ForEachAsync(
                filteredSpecs,
                new ParallelOptions { MaxDegreeOfParallelism = workers },
                async (item, ct) =>
                {
                    var (id, category, spec) = item;
                    var proc = await processPool.Reader.ReadAsync(ct);
                    try
                    {
                        var timeout = spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
                        using var engine = new JsonProtocolEngine(proc, timeout);
                        var runner = new RosterRunner(engine, new DataSourceResolver(), assertionEngine ?? engineFilter);
                        var result = runner.Run(spec);

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

                        concurrentResults.Add((result, spec, status));
                    }
                    finally
                    {
                        processPool.Writer.TryWrite(proc);
                    }
                });

            // Collect results in order
            foreach (var (result, spec, status) in concurrentResults)
            {
                results.Add(result);
                specsByResult[result] = spec;
                reportResults.Add(new SpecResultSummary(result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags));
            }

            // Dispose adapter processes
            foreach (var proc in adapterProcesses)
            {
                proc.Dispose();
            }
        }
        else
        {
            // Sequential execution with single adapter process
            foreach (var (id, category, spec) in filteredSpecs)
            {
                var timeout = spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
                using var engine = new JsonProtocolEngine(adapterProcess!, timeout);
                var runner = new RosterRunner(engine, new DataSourceResolver(), assertionEngine ?? engineFilter);
                var result = runner.Run(spec);
                results.Add(result);
                specsByResult[result] = spec;

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

                reportResults.Add(new SpecResultSummary(result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags));
            }
        }

        sw.Stop();

        return SpecSuiteResult.Create(results, reportResults, specsByResult, totalSpecs, sw.Elapsed, expectedFailuresEngine);
    }
}
