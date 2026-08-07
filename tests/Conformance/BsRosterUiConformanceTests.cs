using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs the roster specs against the BattleScribe desktop UI driver.
/// </summary>
/// <remarks>
/// <para>
/// Actions go through BattleScribe's real Roster Editor UI via the Java agent — the New Roster and
/// Add Force dialogs, the roster and catalogue trees, the edit panel — while state is read from the
/// Java model. Engine name: <c>battlescribe-ui</c>.
/// </para>
/// <para>
/// <b>Why this class exists.</b> Its sibling <see cref="BsGameDataUiConformanceTests"/> has covered
/// the Data Editor since it was written, and CI's <c>thorough-ui-bs</c> job filtered on
/// <c>Engine=BsGameDataUi</c> — so the ROSTER half of the same driver stack had no conformance lane
/// at all. <c>BsUiRosterEngine</c> and <c>RosterActions.java</c> were exercised only through
/// <c>bs-spec serve</c> and one teardown test.
/// </para>
/// <para>
/// The cost of that gap was concrete. <c>createRosterAction</c> chose a catalogue, slept 300ms, and
/// chose a force entry — but choosing a catalogue repopulates the force-entry combo asynchronously,
/// and <c>selectComboBoxItemById</c> falls back to <c>toString().contains(id)</c>. Losing that race
/// meant selecting from the PREVIOUS catalogue's list, which the spec corpus makes plausible by
/// reusing ids such as <c>fe-1</c> across catalogues: a wrong roster, reported as success. Nothing
/// ran that could have caught it.
/// </para>
/// <para>
/// <b>Expected failures are declared, not hidden.</b> Specs the BS UI genuinely cannot drive carry
/// <c>engines: {battlescribe-ui: fail}</c> in the spec itself, so they still RUN and an unexpected
/// pass is reported — the same contract the NR-UI roster lane uses.
/// </para>
/// <para>
/// Sequential by design: one desktop app instance handles one spec at a time. Sharded on the same
/// <c>Shard</c> trait as the gamedata lane so CI's existing 2-way matrix covers both halves.
/// </para>
/// <para>
/// Skipped when the fixture is unavailable (BattleScribe artifacts or the agent JAR absent — run
/// <c>setup.ps1</c>) or <c>BS_UI_SKIP=true</c>.
/// </para>
/// </remarks>
[Collection("BsRosterUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "BsRosterUi")]
public sealed class BsRosterUiConformanceTests : ConformanceTestBase
{
    private readonly BsRosterUiFixture _fixture;

    public BsRosterUiConformanceTests(ITestOutputHelper output, BsRosterUiFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "battlescribe-ui";

    /// <summary>
    /// This drives BattleScribe, so it inherits what specs say about <c>battlescribe</c> unless
    /// they name <c>battlescribe-ui</c> specifically — see <see cref="BaseEngineName"/>.
    /// </summary>
    protected override string BaseEngineName => "battlescribe";

    protected override IRosterEngine? GetEngine()
    {
        if (!_fixture.Available)
        {
            Assert.Skip(
                "BS UI artifacts not found (run setup.ps1) or BS_UI_SKIP=true "
                + "— skipping BS Roster UI tests");
            return null;
        }

        return _fixture.Engine;
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void BsRosterUiEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
