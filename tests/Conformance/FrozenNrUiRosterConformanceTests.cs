using System.Diagnostics;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs every applicable roster spec through the NR UI driver in frozen (HAR replay) mode, one after
/// another in one browser. Actions are executed through Playwright UI interactions; state is read via JS.
/// Skipped when the HAR file or the Playwright browsers are missing.
/// Sequential by design — UI interactions cannot run concurrently in one browser context.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two tests partition the suite.</b> <see cref="KitchenSink"/> is the fast half — kitchen-sink exercises
/// core protocol conformance, and its trailing <c>expectedFile</c> step drives the UI export path (Export
/// button → .ros) inside the same flow — which <c>smoke-nr-ui</c> and <c>pre-push</c> select alone.
/// <see cref="OtherSpecs"/> is everything else, ~27 minutes, which only <c>nr-ui-frozen</c> (CI's thorough
/// <c>Full frozen NR UI roster</c> step) adds. Both share this class's fixture, so the full lane is still
/// one browser running every spec in turn.
/// </para>
/// <para>
/// The full set used to be opt-in through an environment switch, and the obvious hazard of an opt-in is
/// that the thorough lane silently stops opting in and nobody notices a suite shrinking from 363 specs to 1
/// — which is the exact failure this lane already had once (<c>docs/warm-reuse.md</c>: "CI never caught the
/// original bug because the NR-UI roster lane runs a single spec"). Which half a run drives is now the test
/// it selected, printed as <c>[lane] FrozenNrUiRoster mode=… selected=N applicable=M</c>
/// (<see cref="AggregateLaneRun"/>), and the full lane is a profile name of its own on CI's step, where a
/// reviewer sees it change.
/// </para>
/// <para>
/// The full set is every applicable roster spec. It used to be a hand-maintained category allow-list,
/// because running everything selected 28 failures in categories nobody had classified, and shipping those
/// as expected-failures would have been inventing declarations rather than earning them. Every one of them
/// has since been classified: all remaining failures carry an <c>engines: {newrecruit-ui: …}</c>
/// declaration in the spec itself saying which NR-UI limitation or driver gap they are
/// (docs/nr-ui-roster-coverage.md), so a new spec is covered the day it lands. The declarations are
/// <c>fail</c> rather than <c>skip</c> wherever a future NR release could plausibly lift the limitation, so
/// the spec still RUNS and an unexpected pass is reported.
/// </para>
/// </remarks>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class FrozenNrUiRosterConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly FrozenNrUiRosterFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[FROZEN-UI] ";

    /// <summary>The concrete engine this lane drives, as specs address it.</summary>
    private const string EngineIdentity = "newrecruit-ui";

    /// <summary>
    /// The spec's expectation for this lane — <c>pass</c>, <c>fail</c> or <c>skip</c>.
    /// <para>
    /// Most specific wins, matching <see cref="RosterRunner"/>: a spec that names
    /// <c>newrecruit-ui</c> means this driver specifically; otherwise it inherits whatever it says
    /// for the base <c>newrecruit</c> engine. Checking only the base name is why a
    /// <c>newrecruit-ui</c> entry used to have no effect here at all.
    /// </para>
    /// </summary>
    private static string ExpectationFor(SpecFile spec)
        => spec.Engines is not null && spec.Engines.ContainsKey(EngineIdentity)
            ? spec.GetExpectation(EngineIdentity)
            : spec.GetExpectation(EngineName);

    public FrozenNrUiRosterConformanceTests(ITestOutputHelper output, FrozenNrUiRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    /// <summary>The kitchen-sink spec: the fast half, on every push and in <c>pre-push</c>.</summary>
    [Fact]
    public Task KitchenSink() => RunAsync(AggregateMode.KitchenSink);

    /// <summary>Every other applicable spec, in the same browser: with <see cref="KitchenSink"/>, the whole lane.</summary>
    [Fact]
    public Task OtherSpecs() => RunAsync(AggregateMode.OtherSpecs);

    private async Task RunAsync(AggregateMode part)
    {
        Assert.SkipWhen(!_fixture.Available,
            "Frozen HAR file or Playwright browsers missing (run setup.ps1) — skipping frozen NR UI tests");

        var engine = _fixture.Engine!;
        var allSpecs = ConformanceTestBase.AllSpecPaths();
        var resolver = new DataSourceResolver();

        static bool Applies(SpecFile spec) => !string.Equals(ExpectationFor(spec), "skip", StringComparison.OrdinalIgnoreCase);

        // The whole corpus is read for either part: it is what the [lane] line counts `applicable` from.
        var corpus = allSpecs.Select(s => (s.Path, s.Name, spec: SpecLoader.Load(s.Path))).ToList();
        var loadedSpecs = corpus
            .Where(s => AggregateLaneRun.Drives(part, s.Name))
            .Where(s => Applies(s.spec))
            .ToList();
        resolver.WarmCache(loadedSpecs.Select(s => s.spec));

        var lane = AggregateLaneRun.Start(_output, LogPrefix, "FrozenNrUiRoster", part,
            selected: loadedSpecs.Count, applicable: corpus.Count(s => Applies(s.spec)));

        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;
        var failures = new List<string>();
        var skippedSteps = new SkippedStepLog();

        // Sequential execution — UI interactions require a single-browser flow
        var stop = TestContext.Current.CancellationToken;
        foreach (var (specPath, specName, spec) in loadedSpecs)
        {
            lane.ThrowIfStopped(stop);
            var expectation = ExpectationFor(spec);
            if (string.Equals(expectation, "skip", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var expectedToFail = string.Equals(expectation, "fail", StringComparison.OrdinalIgnoreCase);
            var clock = Stopwatch.StartNew();
            engine.SetTestContext(specName);

            // Both identities: this drives `newrecruit-ui`, and a spec addressing that name by its
            // own must be honoured. Passing only the base name is what made
            // `engines: {newrecruit-ui: …}` silently inert here — the same collapse RosterRunner's
            // own remarks describe.
            var runner = new RosterRunner(engine, resolver, EngineName, EngineIdentity);
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

        // Where the wall-clock went, when asked (NR_UI_TIMINGS=1). Printed for passing runs too —
        // the whole point is to measure a lane that works, not to explain one that broke.
        if (NrUiTiming.Enabled)
        {
            _output.WriteLine(NrUiTiming.Report(loadedSpecs.Count));
        }

        if (failures.Count > 0)
        {
            var message = $"{LogPrefix}{failures.Count} spec(s) failed:\n\n" +
                string.Join("\n\n", failures);
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }

    /// <summary>
    /// A stale or foreign id is an addressing failure on this lane — see
    /// <see cref="AddressingScenarios"/>. Runs in the fast lanes too, not only the full set: it is seven
    /// short rosters, and it is the only check that this driver's lookups say what they are.
    /// </summary>
    /// <remarks>
    /// This lane never passed one of these as a refusal — its misses came out as capability gaps
    /// and 20-second timeouts instead — but it did act on a selection filed under the other force,
    /// because its row lookups take a selection uid and nothing else.
    /// </remarks>
    [Fact]
    public void AnIdTheRosterDoesNotHave_IsAnAddressingFailure()
    {
        Assert.SkipWhen(!_fixture.Available,
            "Frozen HAR file or Playwright browsers missing (run setup.ps1) — skipping frozen NR UI tests");

        var engine = _fixture.Engine!;
        var wrong = new List<string>();
        foreach (var scenario in AddressingScenarios.All)
        {
            engine.SetTestContext($"addressing/{scenario}");
            var result = new RosterRunner(engine, new DataSourceResolver(), EngineName, EngineIdentity)
                .Run(AddressingScenarios.Load(scenario));
            engine.Cleanup();
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
