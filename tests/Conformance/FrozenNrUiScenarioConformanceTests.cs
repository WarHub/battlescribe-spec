namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs every scenario spec against the New Recruit UI pair, frozen: the NR Editor snapshot driven through its
/// UI as the data editor and the NR app over the HAR replay driven through its UI as the roster editor
/// (<see cref="FrozenNrUiScenarioFixture"/>). Fully offline. Engine name: <c>newrecruit-ui</c>.
/// </summary>
/// <remarks>
/// A UI driver produces what its base engine produces, so a spec's <c>newrecruit</c> declarations apply here
/// unless it names <c>newrecruit-ui</c> itself (<see cref="ScenarioConformanceTestBase.Engine"/>).
/// </remarks>
[Collection("FrozenNrUiScenario")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiScenario")]
public sealed class FrozenNrUiScenarioConformanceTests : ScenarioConformanceTestBase
{
    private readonly FrozenNrUiScenarioFixture _fixture;

    public FrozenNrUiScenarioConformanceTests(ITestOutputHelper output, FrozenNrUiScenarioFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit-ui";

    protected override string LogPrefix => "[FROZEN-NR-UI-SCENARIO] ";

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
    public void FrozenNrUiScenario(string specName) => RunSpec(specName);
}
