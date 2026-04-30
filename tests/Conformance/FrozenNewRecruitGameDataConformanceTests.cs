using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs GameData specs against a frozen NR Editor snapshot (static file serving).
/// Fully offline and deterministic. Skipped when .testdata/nr-editor/ is missing.
///
/// Uses the same spec set as <see cref="LiveNewRecruitGameDataConformanceTests"/>
/// but served from local files instead of a live deployment.
/// </summary>
[Collection("FrozenNewRecruitGameData")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrEditor")]
public sealed class FrozenNewRecruitGameDataConformanceTests : GameDataConformanceTestBase
{
    private readonly FrozenNewRecruitGameDataFixture _fixture;

    public FrozenNewRecruitGameDataConformanceTests(
        ITestOutputHelper output,
        FrozenNewRecruitGameDataFixture fixture)
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
