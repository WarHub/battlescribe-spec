using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative YAML spec files against the BattleScribe (Java) engine.
/// To add a new engine, create another test class with the same pattern and a different IRosterEngine.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Engine", "BattleScribe")]
public sealed class SpecConformanceTests : ConformanceTestBase
{
    public SpecConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override string EngineName => "battlescribe";

    protected override IRosterEngine? GetEngine() => new BattleScribeRosterEngine();

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void BattleScribeEngine(string specPath, string specName) => RunSpec(specPath, specName);
}