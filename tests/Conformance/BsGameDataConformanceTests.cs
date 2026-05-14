using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative GameData YAML specs against the BattleScribe engine (Java via IKVM).
/// This validates that the BattleScribe native data model handles all data editing operations
/// correctly — serving as the reference implementation against which NR Editor is compared.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Engine", "BsGameData")]
public sealed class BsGameDataConformanceTests : GameDataConformanceTestBase
{
    public BsGameDataConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override string EngineName => "battlescribe";
    protected override string LogPrefix => "[BS-DATA] ";

    protected override IGameDataEngine? GetEngine() => new BattleScribeGameDataEngine();

    [Theory]
    [MemberData(nameof(AllGameDataSpecs))]
    public void BsGameDataEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
