using System.Collections.Concurrent;
using System.Diagnostics;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs declarative YAML spec files against a frozen New Recruit snapshot (HAR replay).
/// Fully offline and deterministic. Uses parallel execution with a browser context pool.
/// Skipped when the HAR file doesn't exist.
/// </summary>
/// <remarks>
/// The suite is two <c>[Fact]</c> aggregates that partition it: <see cref="KitchenSink"/>, the every-push
/// smoke (<c>smoke-nr-frozen</c>), and <see cref="OtherSpecs"/>. An aggregate's name carries no spec id, so
/// <c>--filter DisplayName~kitchen-sink</c> cannot narrow it — that clause once selected nothing here and
/// the smoke step reported green — but a filter on the test's own name can, and selecting the engine runs
/// both, each spec once.
/// </remarks>
[Collection("FrozenNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrRoster")]
public sealed class FrozenNrRosterConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly FrozenNrRosterFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[FROZEN] ";

    public FrozenNrRosterConformanceTests(ITestOutputHelper output, FrozenNrRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    /// <summary>The kitchen-sink spec: proves the engine wires up, on every push.</summary>
    [Fact]
    public Task KitchenSink() => RunAsync(AggregateMode.KitchenSink);

    /// <summary>Every other applicable spec: with <see cref="KitchenSink"/>, the whole lane.</summary>
    [Fact]
    public Task OtherSpecs() => RunAsync(AggregateMode.OtherSpecs);

    private async Task RunAsync(AggregateMode part)
    {
        Assert.SkipWhen(!_fixture.Available,
            "Frozen HAR file not found (run setup.ps1) — skipping frozen NR tests");

        var allSpecs = ConformanceTestBase.AllSpecPaths();
        var pool = _fixture.EnginePool!;
        var failures = new ConcurrentBag<string>();
        var skippedSteps = new SkippedStepLog();
        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;

        // Load all specs upfront and pre-resolve datasources before parallel execution. The whole corpus
        // is read for either part: it is what the [lane] line counts `applicable` from.
        var resolver = new DataSourceResolver();
        var corpus = allSpecs.Select(s => (s.Path, s.Name, spec: SpecLoader.Load(s.Path))).ToList();
        var loadedSpecs = corpus.Where(s => AggregateLaneRun.Drives(part, s.Name)).ToList();
        resolver.WarmCache(loadedSpecs.Select(s => s.spec));

        var lane = AggregateLaneRun.Start(_output, LogPrefix, "FrozenNrRoster", part,
            selected: loadedSpecs.Count(s => s.spec.IsApplicableTo(EngineName)),
            applicable: corpus.Count(s => s.spec.IsApplicableTo(EngineName)));
        var stop = TestContext.Current.CancellationToken;

        try
        {
            await Parallel.ForEachAsync(
                loadedSpecs,
                new ParallelOptions { MaxDegreeOfParallelism = pool.Size, CancellationToken = stop },
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
                    var clock = Stopwatch.StartNew();

                    var runner = new RosterRunner(engine, resolver, EngineName);
                    var result = runner.Run(spec);
                    skippedSteps.Record(specName, result);

                    if (result.Passed && expectedToFail)
                    {
                        failures.Add($"Spec '{specName}' was expected to fail on {EngineName} but now passes!");
                        lane.Completed(specName, AggregateLaneRun.UnexpectedPass, clock.Elapsed);
                        return;
                    }

                    if (!result.Passed && expectedToFail)
                    {
                        Interlocked.Increment(ref expectedFailures);
                        lane.Completed(specName, AggregateLaneRun.ExpectedFailure, clock.Elapsed);
                        return;
                    }

                    if (!result.Passed)
                    {
                        var msg = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                            string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
                        failures.Add(msg);
                        lane.Completed(specName, AggregateLaneRun.Failed, clock.Elapsed);
                        return;
                    }

                    Interlocked.Increment(ref passed);
                    lane.Completed(specName, AggregateLaneRun.Passed, clock.Elapsed);
                });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            lane.Stop(stop);
        }

        _output.WriteLine($"{LogPrefix}Results: {passed} passed, {skipped} skipped, {expectedFailures} expected failures, {failures.Count} failures");
        _output.WriteLine($"{LogPrefix}Pool size: {pool.Size} contexts");
        skippedSteps.WriteTo(_output, LogPrefix);

        if (!failures.IsEmpty)
        {
            var message = $"{LogPrefix}{failures.Count} spec(s) failed:\n\n" +
                string.Join("\n\n", failures);
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }

    /// <summary>
    /// A stale or foreign id is an addressing failure on this lane — see
    /// <see cref="AddressingScenarios"/>. Until the JS lookups tagged their misses, every one of
    /// these passed here as NewRecruit refusing the action.
    /// </summary>
    [Fact]
    public async Task AnIdTheRosterDoesNotHave_IsAnAddressingFailure()
    {
        Assert.SkipWhen(!_fixture.Available,
            "Frozen HAR file not found (run setup.ps1) — skipping frozen NR tests");

        var wrong = new List<string>();
        foreach (var scenario in AddressingScenarios.All)
        {
            using var pooled = await _fixture.AcquireAsync(TestContext.Current.CancellationToken);
            var result = new RosterRunner(pooled.Engine, new DataSourceResolver(), EngineName)
                .Run(AddressingScenarios.Load(scenario));
            foreach (var failure in result.Failures)
            {
                _output.WriteLine($"{LogPrefix}{scenario}: {failure}");
            }

            if (AddressingScenarios.Judge(scenario, result) is { } verdict)
            {
                wrong.Add(verdict);
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n\n", wrong));
    }
}
