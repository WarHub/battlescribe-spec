using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs the kitchen-sink spec against the NR UI driver in frozen (HAR replay) mode.
/// Actions are executed through Playwright UI interactions; state is read via JS.
/// Skipped when the HAR file doesn't exist or NR_UI_FROZEN_SKIP=true.
/// Sequential by design — UI interactions cannot run concurrently in one browser context.
/// </summary>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class FrozenNrUiRosterConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly FrozenNrUiRosterFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[FROZEN-UI] ";

    /// <summary>
    /// The spec(s) the fast lane runs. Kitchen-sink alone: it exercises core protocol conformance and
    /// its trailing <c>expectedFile</c> step drives the UI export path (Export button → .ros) inside
    /// the same flow.
    /// </summary>
    /// <remarks>
    /// This used to be the ONLY set, justified as "the frozen HAR supports a single roster-creation
    /// flow per run". That limit no longer exists — <c>NewRecruitBrowser</c>'s HAR fallback
    /// benign-fulfills <c>/api/</c> calls precisely so the SPA stops hanging across repeated roster
    /// flows, and 56 consecutive roster creations in one session are now measured
    /// (docs/nr-ui-roster-coverage.md). Set <see cref="FullVariable"/> for the full set.
    /// </remarks>
    private static readonly string[] SmokeSpecs = ["protocol/protocol-kitchen-sink"];

    /// <summary>
    /// Opt in to running every applicable roster spec instead of just kitchen-sink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opt-IN rather than opt-out, deliberately. The full set is ~49 specs at ~15s each — right for
    /// the thorough suite, and wrong for the every-push lane and for <c>pre-push</c>, which exist to
    /// be fast. This keeps every lane exactly as quick as it is today unless it asks otherwise.
    /// </para>
    /// <para>
    /// The obvious hazard of an opt-in is that the thorough lane silently stops opting in and nobody
    /// notices a suite shrinking from 49 specs to 1 — which is the exact failure this lane already
    /// had once (<c>docs/warm-reuse.md</c>: "CI never caught the original bug because the NR-UI
    /// roster lane runs a single spec"). Two things guard it: the run logs which mode it chose and
    /// how many specs that selected, and
    /// <c>ConcurrencyConfigurationDriftTests.ThoroughNrUiRosterStep_RunsTheFullSpecSet</c> fails if
    /// the CI step stops setting this.
    /// </para>
    /// </remarks>
    internal const string FullVariable = "NR_UI_ROSTER_FULL";

    private static bool RunFullSet =>
        Environment.GetEnvironmentVariable(FullVariable) is "1" or "true";

    /// <summary>
    /// The categories the full set covers: the ones whose NR-UI coverage has actually been measured
    /// and whose failures are declared (docs/nr-ui-roster-coverage.md). 56 specs, ~14 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not "everything applicable", and the difference is not small.</b> Running the whole roster
    /// suite through this driver selects <b>363</b> specs and takes roughly 90 minutes — and 28 of
    /// them fail, in categories nobody has looked at. Those 28 are not known NR-UI limitations; they
    /// are simply unmeasured, and shipping them as expected-failures would be inventing a
    /// declaration rather than earning one.
    /// </para>
    /// <para>
    /// So the rule is: a category joins this list once its failures have been measured and
    /// classified, the same way these four were. That keeps every entry here a statement someone
    /// checked, and keeps the lane's runtime honest.
    /// </para>
    /// </remarks>
    private static readonly string[] MeasuredCategories =
        ["force/", "cost/", "entry-group/", "gamesystem/", "selection/", "condition/"];

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

    [Fact]
    public async Task AllSpecs()
    {
        Assert.SkipWhen(!_fixture.Available,
            "Frozen HAR file not found or NR_UI_FROZEN_SKIP=true — skipping frozen NR UI tests");

        var engine = _fixture.Engine!;
        var allSpecs = ConformanceTestBase.AllSpecPaths();
        var resolver = new DataSourceResolver();

        var full = RunFullSet;
        var loadedSpecs = allSpecs
            .Where(s => full
                ? MeasuredCategories.Any(c => s.Name.StartsWith(c, StringComparison.Ordinal))
                : SmokeSpecs.Contains(s.Name))
            .Select(s => (s.Path, s.Name, spec: SpecLoader.Load(s.Path)))
            .Where(s => !string.Equals(ExpectationFor(s.spec), "skip", StringComparison.OrdinalIgnoreCase))
            .ToList();
        resolver.WarmCache(loadedSpecs.Select(s => s.spec));

        // Say which mode ran and how big it was. A lane that quietly stops opting in shows up here as
        // "1 spec" next to a step called "Full frozen NR UI roster", instead of as silence.
        _output.WriteLine(
            $"{LogPrefix}mode={(full ? $"FULL ({FullVariable} set)" : "smoke (kitchen-sink)")}, "
            + $"{loadedSpecs.Count} spec(s) selected");

        Assert.False(loadedSpecs.Count == 0,
            full
                ? $"{FullVariable} is set but no applicable specs were discovered."
                : $"No matching specs found for targets: {string.Join(", ", SmokeSpecs)}");

        // A "full" run that selected a single spec is the shrink this guard exists to catch.
        Assert.False(full && loadedSpecs.Count < 2,
            $"{FullVariable} is set but only {loadedSpecs.Count} spec(s) were selected — the full set "
            + "should be the whole applicable suite, not kitchen-sink.");

        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;
        var failures = new List<string>();
        var skippedSteps = new SkippedStepLog();

        // Sequential execution — UI interactions require a single-browser flow
        foreach (var (specPath, specName, spec) in loadedSpecs)
        {
            var expectation = ExpectationFor(spec);
            if (string.Equals(expectation, "skip", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var expectedToFail = string.Equals(expectation, "fail", StringComparison.OrdinalIgnoreCase);
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
                continue;
            }

            if (!result.Passed && expectedToFail)
            {
                expectedFailures++;
                continue;
            }

            if (!result.Passed)
            {
                var msg = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                    string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
                failures.Add(msg);
                continue;
            }

            passed++;
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
