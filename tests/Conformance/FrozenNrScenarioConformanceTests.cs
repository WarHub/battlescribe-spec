namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs every scenario spec against the store-direct New Recruit pair, frozen: the NR Editor snapshot as the
/// data editor and the NR HAR replay as the roster editor (<see cref="FrozenNrScenarioFixture"/>). Fully
/// offline. Engine name: <c>newrecruit</c>.
/// </summary>
/// <remarks>
/// NewRecruit has the BattleScribe shape across two web apps: the NR Editor exports BattleScribe XML and the NR
/// app loads it into its library (<c>docs/adr/002-scenario-specs.md</c>). Here the runner carries that XML from
/// one to the other.
/// </remarks>
[Collection("FrozenNrScenario")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrScenario")]
public sealed class FrozenNrScenarioConformanceTests : ScenarioConformanceTestBase
{
    private readonly FrozenNrScenarioFixture _fixture;

    public FrozenNrScenarioConformanceTests(ITestOutputHelper output, FrozenNrScenarioFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit";
    protected override string LogPrefix => "[FROZEN-NR-SCENARIO] ";

    protected override ScenarioEngines? GetEngines()
    {
        if (_fixture.Unavailable is { } why)
        {
            Assert.Skip(why);
        }

        return _fixture is { Data: { } data, Roster: { } roster } ? new(data, roster) : null;
    }

    [Theory]
    [MemberData(nameof(AllScenarioSpecs))]
    public void FrozenNrScenario(string specName) => RunSpec(specName);
}
