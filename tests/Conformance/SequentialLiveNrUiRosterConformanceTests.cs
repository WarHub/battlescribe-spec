using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Per-spec Theory version of live NR UI conformance tests.
/// Each spec is an individual test case, enabling filtering via DisplayName~.
/// Gated by NR_ENGINE_URL env var.
/// Usage: dotnet test --filter "Engine=LiveNrUiRoster&amp;DisplayName~kitchen-sink"
/// </summary>
[Collection("LiveNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNrUiRoster")]
public sealed class SequentialLiveNrUiRosterConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly LiveNrUiRosterFixture _fixture;
    private const string EngineName = "newrecruit";
    private const string LogPrefix = "[SEQ-UI] ";

    public SequentialLiveNrUiRosterConformanceTests(ITestOutputHelper output, LiveNrUiRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [Theory]
    [MemberData(nameof(ConformanceTestBase.AllSpecs), MemberType = typeof(ConformanceTestBase))]
    public void LiveNrUiRosterEngine(string specPath, string specName)
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set — skipping live NR UI tests");

        var spec = SpecLoader.Load(specPath);
        if (!spec.IsApplicableTo(EngineName))
        {
            _output.WriteLine($"{LogPrefix}Skipping spec: {specName} — not applicable to {EngineName}");
            return;
        }

        var expectedToFail = spec.IsExpectedToFail(EngineName);
        _output.WriteLine($"{LogPrefix}Running spec: {specName}{(expectedToFail ? " [EXPECTED FAILURE]" : "")}");

        var engine = _fixture.Engine!;
        engine.SetTestContext(specName);
        var runner = new RosterRunner(engine, new DataSourceResolver(), EngineName);
        var result = runner.Run(spec);
        engine.Cleanup();

        if (result.Passed && expectedToFail)
        {
            Assert.Fail($"{LogPrefix}Spec '{specName}' expected to fail on {EngineName} but now passes!");
        }

        if (!result.Passed && expectedToFail)
        {
            _output.WriteLine($"{LogPrefix}[EXPECTED FAILURE] {specName} failed as expected");
            return;
        }

        if (!result.Passed)
        {
            var msg = $"{LogPrefix}Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
            _output.WriteLine(msg);
            Assert.Fail(msg);
        }
    }
}
