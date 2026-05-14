using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs GameData specs against a frozen NR Editor snapshot (static file serving).
/// Fully offline and deterministic. Skipped when .testdata/nr-editor/ is missing.
///
/// Uses the same spec set as <see cref="LiveNrGameDataConformanceTests"/>
/// but served from local files instead of a live deployment.
/// </summary>
[Collection("FrozenNrGameData")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrGameData")]
public sealed class FrozenNrGameDataConformanceTests : GameDataConformanceTestBase
{
    private readonly FrozenNrGameDataFixture _fixture;

    public FrozenNrGameDataConformanceTests(
        ITestOutputHelper output,
        FrozenNrGameDataFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "nr-editor";
    protected override string LogPrefix => "[FROZEN-NR-EDITOR] ";

    protected override IGameDataEngine? GetEngine()
    {
        if (!_fixture.Available)
        {
            Assert.Skip("NR Editor static files not found (run setup.ps1) or NR_FROZEN_SKIP=true — skipping frozen NR Editor GameData tests");
            return null;
        }

        return _fixture.Engine;
    }

    [Theory]
    [MemberData(nameof(AllGameDataSpecs))]
    public void Spec(string specPath, string specName)
    {
        RunSpec(specPath, specName);
    }
}
