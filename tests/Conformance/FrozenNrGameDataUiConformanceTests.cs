using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative GameData YAML specs against the NR Editor UI driver
/// in frozen (static file serving) mode.
///
/// Actions are executed through real Playwright UI interactions (context menus, tree clicks,
/// property panel edits). State is read from NR Editor's Pinia editorStore after each mutation.
///
/// Skipped when .testdata/nr-editor/ is missing (run setup.ps1) or NR_EDITOR_UI_FROZEN_SKIP=true.
/// Sequential — UI interactions cannot run concurrently in one browser context.
/// </summary>
[Collection("FrozenNrGameDataUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrGameDataUi")]
public sealed class FrozenNrGameDataUiConformanceTests : GameDataConformanceTestBase
{
    private readonly FrozenNrGameDataUiFixture _fixture;

    public FrozenNrGameDataUiConformanceTests(
        ITestOutputHelper output,
        FrozenNrGameDataUiFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit-ui";
    protected override string LogPrefix => "[FROZEN-NR-EDITOR-UI] ";

    protected override IGameDataEngine? GetEngine()
    {
        if (!_fixture.Available)
        {
            Assert.Skip(
                "NR Editor static files not found (run setup.ps1) or NR_EDITOR_UI_FROZEN_SKIP=true " +
                "— skipping frozen NR Editor GameData UI tests");
            return null;
        }

        return _fixture.Engine;
    }

    [Theory]
    [MemberData(nameof(AllGameDataSpecs))]
    public void Spec(string specPath, string specName) => RunSpec(specPath, specName);
}
