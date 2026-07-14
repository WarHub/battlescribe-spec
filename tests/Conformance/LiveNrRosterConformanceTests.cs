using System.Collections.Concurrent;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs declarative YAML spec files against the live New Recruit web engine via Playwright.
/// Uses parallel execution with a browser context pool.
/// Skipped when NR_ENGINE_URL is not set.
/// </summary>
[Collection("LiveNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNrRoster")]
public sealed class LiveNrRosterConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly LiveNrRosterFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[LIVE] ";

    public LiveNrRosterConformanceTests(ITestOutputHelper output, LiveNrRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [Fact]
    public async Task AllSpecs()
    {
        // _fixture.Unavailable, not a hardcoded "NR_ENGINE_URL not set": a pool can also be absent
        // because another live fixture in this process holds the site's whole load budget, and a skip
        // that misreports WHY is how a throttled lane looks like an unconfigured one.
        Assert.SkipWhen(!_fixture.Available, _fixture.Unavailable);

        var allSpecs = ConformanceTestBase.AllSpecPaths();
        var pool = _fixture.EnginePool!;
        var failures = new ConcurrentBag<string>();
        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;

        // Load all specs upfront and pre-resolve datasources before parallel execution
        var resolver = new DataSourceResolver();
        var loadedSpecs = allSpecs.Select(s => (
            s.Path,
            s.Name,
            spec: SpecLoader.Load(s.Path)
        )).ToList();
        resolver.WarmCache(loadedSpecs.Select(s => s.spec));

        await Parallel.ForEachAsync(
            loadedSpecs,
            new ParallelOptions { MaxDegreeOfParallelism = pool.Size },
            async (item, ct) =>
            {
                var (specPath, specName, spec) = item;

                if (!spec.IsApplicableTo(EngineName))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                var expectedToFail = spec.IsExpectedToFail(EngineName);

                using var pooled = await _fixture.AcquireAsync(ct);
                var engine = pooled.Engine;

                var runner = new RosterRunner(engine, resolver, EngineName);
                var result = runner.Run(spec);

                if (result.Passed && expectedToFail)
                {
                    failures.Add($"Spec '{specName}' was expected to fail on {EngineName} but now passes!");
                    return;
                }

                if (!result.Passed && expectedToFail)
                {
                    Interlocked.Increment(ref expectedFailures);
                    return;
                }

                if (!result.Passed)
                {
                    var msg = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                        string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
                    failures.Add(msg);
                    return;
                }

                Interlocked.Increment(ref passed);
            });

        _output.WriteLine($"{LogPrefix}Results: {passed} passed, {skipped} skipped, {expectedFailures} expected failures, {failures.Count} failures");
        _output.WriteLine($"{LogPrefix}Pool size: {pool.Size} contexts");

        if (!failures.IsEmpty)
        {
            var message = $"{LogPrefix}{failures.Count} spec(s) failed:\n\n" +
                string.Join("\n\n", failures);
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
