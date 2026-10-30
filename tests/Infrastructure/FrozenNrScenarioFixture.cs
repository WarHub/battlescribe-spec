using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The store-direct New Recruit pair for scenario specs, both frozen: a <see cref="NewRecruitGameDataEngine"/>
/// over the pinned NR Editor snapshot (<c>.testdata/nr-editor/</c>) as the data editor, and a
/// <see cref="NewRecruitRosterEngine"/> replaying the NR HAR as the roster editor. Fully offline. One pair,
/// shared by every scenario row in turn: <see cref="Scenario.ScenarioRunner"/> cleans both after each spec,
/// and cleans and re-sets-up the roster editor at every <c>newRoster</c>/<c>openRoster</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built the way the engine host builds them</b> (<c>HostEngineFactory</c>): the roster engine is a context
/// of the process's shared Chromium (<c>NrBrowserHost</c>), as <see cref="FrozenNrUiRosterFixture"/>'s is, so
/// the launch options read below are exactly the ones that fixture reads — a different pair would relaunch
/// the shared browser under the other lane.
/// </para>
/// <para>
/// <b>The data engine holds <see cref="BrowserResourceRaceGate.FrozenNrGameData"/></b> for its whole alive
/// window, as <see cref="FrozenNrGameDataFixture"/> does: it launches the same independent NR Editor browser
/// that <c>NewRecruitGameDataEngineResourceMetricsTests</c> launches and counts, and that gate is what keeps the
/// two from being alive at once (see its remarks). It also serialises this lane after (or before) the
/// FrozenNrGameData lane — seconds against that lane's two minutes, and never deadlocked: this fixture takes
/// one gate, so there is no second one to wait for while holding it.
/// </para>
/// <para>
/// Skipped when either snapshot is missing (run <c>setup.ps1</c>). A failed browser launch is not a skip:
/// like <see cref="FrozenNrRosterFixture"/> and <see cref="FrozenNrGameDataFixture"/>, this fixture lets it
/// fail the lane.
/// </para>
/// </remarks>
public sealed class FrozenNrScenarioFixture : IAsyncLifetime
{
    private bool _holdsGate;

    public NewRecruitGameDataEngine? Data { get; private set; }

    public NewRecruitRosterEngine? Roster { get; private set; }

    /// <summary>Why the pair is not available on this machine, or <see langword="null"/> when it is.</summary>
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var harFile = HarRecorder.FindFrozenHarFile(RepoRoot.FromBinaries);
        var staticDir = NewRecruitGameDataEngine.FindFrozenStaticDir(RepoRoot.FromBinaries);
        if (harFile is null || staticDir is null)
        {
            Unavailable = "run setup.ps1 — "
                + (harFile is null ? "the frozen New Recruit snapshot (.testdata/newrecruit-har/newrecruit.har)" : "")
                + (harFile is null && staticDir is null ? " and " : "")
                + (staticDir is null ? "the pinned NR Editor snapshot (.testdata/nr-editor/)" : "")
                + " is missing — skipping the frozen NR scenario lane";
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        await BrowserResourceRaceGate.FrozenNrGameData.WaitAsync();
        _holdsGate = true;
        try
        {
            using var span = FixtureTelemetry.StartInit(nameof(FrozenNrScenarioFixture));
            Data = await NewRecruitGameDataEngine.CreateFrozenAsync(staticDir, headless, slowMo);
            Roster = await NewRecruitRosterEngine.CreateFrozenAsync(harFile, headless: headless, slowMo: slowMo);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Roster?.Dispose();
            Data?.Dispose();
        }
        finally
        {
            Roster = null;
            Data = null;
            if (_holdsGate)
            {
                _holdsGate = false;
                BrowserResourceRaceGate.FrozenNrGameData.Release();
            }
        }

        return default;
    }
}

[CollectionDefinition("FrozenNrScenario")]
public class FrozenNrScenarioCollection : ICollectionFixture<FrozenNrScenarioFixture>
{
}
