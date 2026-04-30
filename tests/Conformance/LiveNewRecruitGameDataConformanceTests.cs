using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs GameData specs against the live NR Editor via Playwright.
/// Skipped when NR_EDITOR_URL is not set.
///
/// GameData specs that don't have "nr-editor" in their engines 'skip' list
/// will run. Specs can mark engines as expected failures with engines.nr-editor: fail.
/// </summary>
[Collection("LiveNewRecruitGameData")]
[Trait("Category", "Conformance")]
[Trait("Engine", "NrEditor")]
public sealed class LiveNewRecruitGameDataConformanceTests : GameDataConformanceTestBase
{
    private readonly LiveNewRecruitGameDataFixture _fixture;

    public LiveNewRecruitGameDataConformanceTests(
        ITestOutputHelper output,
        LiveNewRecruitGameDataFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "nr-editor";
    protected override string LogPrefix => "[NR-EDITOR] ";

    protected override IGameDataEngine? GetEngine()
    {
        if (!_fixture.Available)
        {
            Assert.Skip("NR_EDITOR_URL not set — skipping NR Editor GameData tests");
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
