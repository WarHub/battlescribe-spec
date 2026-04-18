using System.Collections.Concurrent;
using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Parallel version of frozen NR conformance tests.
/// Uses Parallel.ForEachAsync to run specs concurrently, each acquiring
/// an engine from the pool. Reports per-spec results via test output
/// and fails the single test method if any spec fails.
///
/// Coexists with the sequential <see cref="FrozenNewRecruitConformanceTests"/> —
/// run either but not both (use NR_FROZEN_SKIP or NR_PARALLEL_ONLY env vars).
/// </summary>
[Collection("ParallelFrozenNewRecruit")]
[Trait("Category", "Conformance")]
[Trait("Engine", "ParallelFrozenNewRecruit")]
public sealed class ParallelFrozenNewRecruitConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly ParallelFrozenNewRecruitFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[PARALLEL-FROZEN] ";

    public ParallelFrozenNewRecruitConformanceTests(ITestOutputHelper output, ParallelFrozenNewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task AllSpecsInParallel()
    {
        Skip.If(!_fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");

        var allSpecs = ConformanceTestBase.AllSpecs().ToList();
        var pool = _fixture.EnginePool!;
        var failures = new ConcurrentBag<string>();
        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;

        await Parallel.ForEachAsync(
            allSpecs,
            new ParallelOptions { MaxDegreeOfParallelism = pool.Size },
            async (specArgs, ct) =>
            {
                var specPath = (string)specArgs[0];
                var specName = (string)specArgs[1];

                var spec = SpecLoader.Load(specPath);

                if (!spec.IsApplicableTo(EngineName))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                var expectedToFail = spec.IsExpectedToFail(EngineName);

                using var pooled = await pool.AcquireAsync(ct);
                var engine = pooled.Engine;

                var runner = new SpecRunner(engine, new DataSourceResolver(), EngineName);
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
