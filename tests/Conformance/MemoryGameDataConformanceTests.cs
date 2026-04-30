using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative GameData YAML specs against the in-memory engine.
/// This validates the spec format, runner, and assertion logic work correctly.
/// When real engine adapters (NR Editor, BS DataEditor) are implemented,
/// additional test classes following this same pattern will be added.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Engine", "MemoryGameData")]
public sealed class MemoryGameDataConformanceTests : GameDataConformanceTestBase
{
    public MemoryGameDataConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override string EngineName => "memory";

    protected override IGameDataEngine? GetEngine() => new MemoryGameDataEngine();

    [Theory]
    [MemberData(nameof(AllGameDataSpecs))]
    public void MemoryEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
