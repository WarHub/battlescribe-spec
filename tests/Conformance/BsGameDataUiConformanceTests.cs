using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative GameData YAML specs against the BattleScribe UI driver.
/// Actions are executed through the BattleScribe desktop Data Editor UI via the Java agent;
/// state is read via the Java model (same as <c>BsGameDataConformanceTests</c> for state,
/// but mutations go through the real UI).
///
/// <para>
/// <b>Engine name</b>: <c>battlescribe-ui</c>
/// </para>
///
/// <para>
/// Skipped when the fixture is not available (BS binaries not present or <c>BS_UI_SKIP=true</c>).
/// </para>
///
/// <para>
/// <b>Status</b>: All mutation actions currently result in <c>NotSupportedException</c>
/// because <c>DataEditorActions.java</c> stubs are not yet implemented.
/// See <c>src/bs-ui-java-agent/src/bsspec/uiagent/DataEditorActions.java</c> and the
/// <c>bs-gamedata-ui</c> skill for the probing workflow.
/// </para>
/// </summary>
[Collection("BsGameDataUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "BsGameDataUi")]
public sealed class BsGameDataUiConformanceTests : GameDataConformanceTestBase
{
    private readonly BsGameDataUiFixture _fixture;

    public BsGameDataUiConformanceTests(ITestOutputHelper output, BsGameDataUiFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "battlescribe-ui";
    protected override string LogPrefix => "[BS-GAMEDATA-UI] ";

    protected override IGameDataEngine? GetEngine()
    {
        if (!_fixture.Available)
        {
            return null;
        }

        return _fixture.Engine;
    }

    [Theory]
    [MemberData(nameof(AllGameDataSpecs))]
    public void BsGameDataUiEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
