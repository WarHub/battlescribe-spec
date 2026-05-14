using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative YAML spec files against the BattleScribe (Java) engine.
/// To add a new engine, create another test class with the same pattern and a different IRosterEngine.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Engine", "BsRoster")]
public sealed class BsRosterConformanceTests : ConformanceTestBase
{
    public BsRosterConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override string EngineName => "battlescribe";

    protected override IRosterEngine? GetEngine() => new BattleScribeRosterEngine();

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void BsRosterEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
