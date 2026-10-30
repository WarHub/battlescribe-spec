namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs every scenario spec against the in-process BattleScribe pair: <see cref="BattleScribeGameDataEngine"/>
/// as the data editor and <see cref="BattleScribeRosterEngine"/> as the roster editor, both the IKVM-compiled
/// BattleScribe engine. Engine name: <c>battlescribe</c>.
/// </summary>
/// <remarks>
/// A fresh pair per spec, as <see cref="BsRosterConformanceTests"/> and <see cref="BsGameDataConformanceTests"/>
/// each take a fresh engine: the two are unrelated object graphs in one process, and nothing crosses between
/// them but the XML the runner carries (<c>docs/adr/002-scenario-specs.md</c>, "How the real applications are
/// put together").
/// </remarks>
[Trait("Category", "Conformance")]
[Trait("Engine", "BsScenario")]
public sealed class BsScenarioConformanceTests : ScenarioConformanceTestBase
{
    public BsScenarioConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override string EngineName => "battlescribe";
    protected override string LogPrefix => "[BS-SCENARIO] ";

    protected override ScenarioEngines? GetEngines()
        => new(new BattleScribeGameDataEngine(), new BattleScribeRosterEngine(), DisposeAfterSpec: true);

    [Theory]
    [MemberData(nameof(AllScenarioSpecs))]
    public void BsScenario(string specName) => RunSpec(specName);
}
