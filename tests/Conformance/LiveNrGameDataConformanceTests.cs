using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs GameData specs against the live NR Editor via Playwright.
/// Skipped when NR_EDITOR_URL is not set.
///
/// GameData specs that don't have "newrecruit" in their engines 'skip' list
/// will run. Specs can mark engines as expected failures with engines.newrecruit: fail.
/// </summary>
[Collection("LiveNrGameData")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNrGameData")]
public sealed class LiveNrGameDataConformanceTests : GameDataConformanceTestBase
{
    private readonly LiveNrGameDataFixture _fixture;

    public LiveNrGameDataConformanceTests(
        ITestOutputHelper output,
        LiveNrGameDataFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit";
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
