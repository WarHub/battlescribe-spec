using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative GameData YAML specs against the NR Editor UI driver
/// pointed at a live NR Editor deployment.
///
/// Requires NR_EDITOR_URL to be set. Skipped otherwise.
/// Actions are executed through real Playwright UI interactions.
/// </summary>
[Collection("LiveNrGameDataUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNrGameDataUi")]
public sealed class LiveNrGameDataUiConformanceTests : GameDataConformanceTestBase
{
    private readonly LiveNrGameDataUiFixture _fixture;

    public LiveNrGameDataUiConformanceTests(
        ITestOutputHelper output,
        LiveNrGameDataUiFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "nr-editor-ui";
    protected override string LogPrefix => "[LIVE-NR-EDITOR-UI] ";

    protected override IGameDataEngine? GetEngine()
    {
        if (!_fixture.Available)
        {
            Assert.Skip(
                "NR_EDITOR_URL not set or Playwright not available — skipping live NR Editor GameData UI tests");
            return null;
        }

        return _fixture.Engine;
    }

    [Theory]
    [MemberData(nameof(AllGameDataSpecs))]
    public void Spec(string specPath, string specName) => RunSpec(specPath, specName);
}
