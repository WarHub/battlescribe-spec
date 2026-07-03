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
    /// The spec(s) this UI driver runs against. Only kitchen-sink — the NR UI driver validates core
    /// protocol conformance, not all 312 specs. Note: the frozen HAR only records a single
    /// roster-creation flow, so exactly one roster-building spec can run here; the roster export UI
    /// (roster-fractional-cost-export) is therefore exercised by the live UI suite instead.
    /// </summary>
    private static readonly string[] TargetSpecs = ["protocol/protocol-kitchen-sink"];

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

        var loadedSpecs = allSpecs
            .Where(s => TargetSpecs.Contains(s.Name))
            .Select(s => (s.Path, s.Name, spec: SpecLoader.Load(s.Path)))
            .ToList();
        resolver.WarmCache(loadedSpecs.Select(s => s.spec));

        Assert.SkipWhen(loadedSpecs.Count == 0,
            $"No matching specs found for targets: {string.Join(", ", TargetSpecs)}");

        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;
        var failures = new List<string>();

        // Sequential execution — UI interactions require a single-browser flow
        foreach (var (specPath, specName, spec) in loadedSpecs)
        {
            if (!spec.IsApplicableTo(EngineName))
            {
                skipped++;
                continue;
            }

            var expectedToFail = spec.IsExpectedToFail(EngineName);
            engine.SetTestContext(specName);

            var runner = new RosterRunner(engine, resolver, EngineName);
            var result = runner.Run(spec);
            engine.Cleanup();

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

        if (failures.Count > 0)
        {
            var message = $"{LogPrefix}{failures.Count} spec(s) failed:\n\n" +
                string.Join("\n\n", failures);
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
