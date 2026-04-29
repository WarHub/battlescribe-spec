using System.Collections.Concurrent;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs declarative YAML spec files against the live New Recruit web engine via Playwright.
/// Uses parallel execution with a browser context pool.
/// Skipped when NR_ENGINE_URL is not set.
/// </summary>
[Collection("LiveNewRecruit")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNewRecruit")]
public sealed class LiveNewRecruitConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly LiveNewRecruitFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[LIVE] ";

    public LiveNewRecruitConformanceTests(ITestOutputHelper output, LiveNewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [Fact]
    public async Task AllSpecs()
    {
        Assert.SkipWhen(!_fixture.Available,
            "NR_ENGINE_URL not set — skipping live NR conformance tests");

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

                using var pooled = await pool.AcquireAsync(ct);
                var engine = pooled.Engine;

                var runner = new SpecRunner(engine, resolver, EngineName);
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
