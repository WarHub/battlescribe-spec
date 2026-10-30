namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs every scenario spec against the BattleScribe desktop apps: the Data Editor and the Roster Editor, each
/// driven through its real UI by the Java agent, each in its own JVM (<see cref="BsScenarioUiFixture"/>). Engine
/// name: <c>battlescribe-ui</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the normative lane.</b> What the two apps do with a hand-off is what a scenario expects of every
/// family (AGENTS.md, "What is normative"); a spec's expectation is checked here first, and a store-direct
/// engine that differs is the finding.
/// </para>
/// <para>
/// It inherits what specs say about <c>battlescribe</c> unless they name <c>battlescribe-ui</c> itself
/// (<see cref="ScenarioConformanceTestBase.BaseEngineName"/>). Not in <c>pre-push</c>: it needs the app, a
/// display and minutes. The <c>bs-ui-scenario</c> profile runs it, and CI's <c>thorough-ui-bs</c> job.
/// </para>
/// </remarks>
[Collection("BsScenarioUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "BsScenarioUi")]
public sealed class BsScenarioUiConformanceTests : ScenarioConformanceTestBase
{
    private readonly BsScenarioUiFixture _fixture;

    public BsScenarioUiConformanceTests(ITestOutputHelper output, BsScenarioUiFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "battlescribe-ui";

    /// <summary>This drives BattleScribe, so it inherits what specs say about <c>battlescribe</c>.</summary>
    protected override string BaseEngineName => "battlescribe";

    protected override string LogPrefix => "[BS-SCENARIO-UI] ";

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
    public void BsScenarioUi(string specName) => RunSpec(specName);
}
