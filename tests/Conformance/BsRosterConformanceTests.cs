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

    /// <summary>A stale or foreign id is an addressing failure on this lane — see <see cref="AddressingScenarios"/>.</summary>
    [Theory]
    [MemberData(nameof(AddressingScenarios.Names), MemberType = typeof(AddressingScenarios))]
    public void BsRosterEngine_AnIdTheRosterDoesNotHave_IsAnAddressingFailure(string scenario)
        => RunAddressingScenario(scenario);
}
