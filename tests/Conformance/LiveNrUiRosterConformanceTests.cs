using System.Diagnostics;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs the kitchen-sink spec against the NR UI driver in live mode.
/// Gated by NR_ENGINE_URL env var.
/// </summary>
[Collection("LiveNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNrUiRoster")]
public sealed class LiveNrUiRosterConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly LiveNrUiRosterFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[LIVE-UI] ";

    /// <summary>
    /// The spec(s) this UI driver runs against. Only kitchen-sink for now.
    /// </summary>
    private static readonly string[] TargetSpecs = ["protocol/protocol-kitchen-sink"];

    public LiveNrUiRosterConformanceTests(ITestOutputHelper output, LiveNrUiRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [Fact]
    public async Task AllSpecs()
    {
        Assert.SkipWhen(!_fixture.Available, _fixture.Unavailable);

        var engine = _fixture.Engine!;
        var allSpecs = ConformanceTestBase.AllSpecPaths();
        var resolver = new DataSourceResolver();

        // The whole corpus is read: it is what the [lane] line counts `applicable` from. This lane drives
        // its target specs only, so it always runs in smoke mode.
        var corpus = allSpecs.Select(s => (s.Path, s.Name, spec: SpecLoader.Load(s.Path))).ToList();
        var loadedSpecs = corpus
            .Where(s => TargetSpecs.Contains(s.Name))
            .ToList();
        resolver.WarmCache(loadedSpecs.Select(s => s.spec));

        Assert.SkipWhen(loadedSpecs.Count == 0,
            $"No matching specs found for targets: {string.Join(", ", TargetSpecs)}");

        var lane = AggregateLaneRun.Start(_output, LogPrefix, "LiveNrUiRoster", AggregateMode.Smoke,
            selected: loadedSpecs.Count(s => s.spec.IsApplicableTo(EngineName)),
            applicable: corpus.Count(s => s.spec.IsApplicableTo(EngineName)));
        var stop = TestContext.Current.CancellationToken;

        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;
        var failures = new List<string>();
        var skippedSteps = new SkippedStepLog();

        foreach (var (specPath, specName, spec) in loadedSpecs)
        {
            lane.ThrowIfStopped(stop);
            if (!spec.IsApplicableTo(EngineName))
            {
                skipped++;
                continue;
            }

            var expectedToFail = spec.IsExpectedToFail(EngineName);
            var clock = Stopwatch.StartNew();
            engine.SetTestContext(specName);

            var runner = new RosterRunner(engine, resolver, EngineName);
            var result = runner.Run(spec);
            engine.Cleanup();
            skippedSteps.Record(specName, result);

            if (result.Passed && expectedToFail)
            {
                failures.Add($"Spec '{specName}' was expected to fail on {EngineName} but now passes!");
                lane.Completed(specName, AggregateLaneRun.UnexpectedPass, clock.Elapsed);
                continue;
            }

            if (!result.Passed && expectedToFail)
            {
                expectedFailures++;
                lane.Completed(specName, AggregateLaneRun.ExpectedFailure, clock.Elapsed);
                continue;
            }

            if (!result.Passed)
            {
                var msg = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                    string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
                failures.Add(msg);
                lane.Completed(specName, AggregateLaneRun.Failed, clock.Elapsed);
                continue;
            }

            passed++;
            lane.Completed(specName, AggregateLaneRun.Passed, clock.Elapsed);
        }

        _output.WriteLine($"{LogPrefix}Results: {passed} passed, {skipped} skipped, {expectedFailures} expected failures, {failures.Count} failures");
        skippedSteps.WriteTo(_output, LogPrefix);

        if (failures.Count > 0)
        {
            var message = $"{LogPrefix}{failures.Count} spec(s) failed:\n\n" +
                string.Join("\n\n", failures);
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
