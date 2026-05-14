using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Sequential version of live NR conformance tests (one spec at a time).
/// Useful for debugging individual spec failures without parallel noise.
/// Skipped by default — set NR_SEQUENTIAL=true and NR_ENGINE_URL to run.
/// </summary>
[Collection("SequentialLiveNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNrRoster")]
[Trait("Mode", "Sequential")]
public sealed class SequentialLiveNrRosterConformanceTests : ConformanceTestBase
{
    private readonly SequentialLiveNrRosterFixture _fixture;

    public SequentialLiveNrRosterConformanceTests(ITestOutputHelper output, SequentialLiveNrRosterFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit";
    protected override string LogPrefix => "[SEQ-LIVE] ";

    protected override IRosterEngine? GetEngine()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("NR_SEQUENTIAL") is not "true",
            "Sequential tests skipped by default — set NR_SEQUENTIAL=true to run");
        Assert.SkipWhen(!_fixture.Available,
            "NR_ENGINE_URL not set — skipping live NR conformance tests");
        return _fixture.Engine!;
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void LiveNrRosterEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
