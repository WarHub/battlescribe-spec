using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs declarative YAML spec files against the NR UI driver in live mode.
/// Gated by NR_ENGINE_URL env var.
/// </summary>
[Collection("LiveNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNrUiRoster")]
public sealed class LiveNrUiRosterConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly LiveNrUiRosterFixture _fixture;
    private const string EngineName = "nr-ui";
    private const string LogPrefix = "[LIVE-UI] ";

    public LiveNrUiRosterConformanceTests(ITestOutputHelper output, LiveNrUiRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [Fact]
    public async Task AllSpecs()
    {
        Assert.SkipWhen(!_fixture.Available,
            "NR_ENGINE_URL not set — skipping live NR UI tests");

        var engine = _fixture.Engine!;
        var allSpecs = ConformanceTestBase.AllSpecPaths();
        var resolver = new DataSourceResolver();

        var loadedSpecs = allSpecs.Select(s => (
            s.Path,
            s.Name,
            spec: SpecLoader.Load(s.Path)
        )).ToList();
        resolver.WarmCache(loadedSpecs.Select(s => s.spec));

        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;
        var failures = new List<string>();

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
