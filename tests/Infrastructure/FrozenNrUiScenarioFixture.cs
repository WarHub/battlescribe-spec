using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.NrRosterUiDriver;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The New Recruit UI pair for scenario specs, both frozen: an <see cref="NrGameDataUiEngine"/> driving the
/// pinned NR Editor snapshot (<c>.testdata/nr-editor/</c>) through its UI as the data editor, and an
/// <see cref="NrRosterUiEngine"/> driving the NR app over the HAR replay as the roster editor. Fully offline.
/// One pair, shared by every scenario row in turn, as <see cref="FrozenNrScenarioFixture"/> shares its own.
/// </summary>
/// <remarks>
/// <para>
/// The roster driver is a context of the process's shared Chromium (<c>NrBrowserHost</c>), with the launch
/// options <see cref="FrozenNrUiRosterFixture"/> reads, so neither relaunches the browser under the other.
/// </para>
/// <para>
/// <b>The data driver holds <see cref="BrowserResourceRaceGate.FrozenNrGameDataUi"/></b> for its whole alive
/// window, as <see cref="FrozenNrGameDataUiFixture"/> does: <c>NrGameDataUiEngineResourceMetricsTests</c>
/// launches and counts the same self-owned NR Editor UI browser (see the gate's remarks). One gate, so this
/// fixture never waits on a second while holding it.
/// </para>
/// <para>
/// Skipped when either snapshot or the Playwright browsers are missing (run <c>setup.ps1</c>), as both UI
/// fixtures skip; any other failure to bring a driver up fails the lane (<see cref="PlaywrightBrowsers"/>).
/// </para>
/// </remarks>
public sealed class FrozenNrUiScenarioFixture : IAsyncLifetime
{
    private bool _holdsGate;

    public NrGameDataUiEngine? Data { get; private set; }

    public NrRosterUiEngine? Roster { get; private set; }

    /// <summary>Why the pair is not available on this machine, or <see langword="null"/> when it is.</summary>
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var harFile = HarRecorder.FindFrozenHarFile(RepoRoot.FromBinaries);
        var staticDir = NrGameDataUiEngine.FindFrozenStaticDir(RepoRoot.FromBinaries);
        if (harFile is null || staticDir is null)
        {
            Unavailable = "run setup.ps1 — "
                + (harFile is null ? "the frozen New Recruit snapshot (.testdata/newrecruit-har/newrecruit.har)" : "")
                + (harFile is null && staticDir is null ? " and " : "")
                + (staticDir is null ? "the pinned NR Editor snapshot (.testdata/nr-editor/)" : "")
                + " is missing — skipping the frozen NR UI scenario lane";
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        await BrowserResourceRaceGate.FrozenNrGameDataUi.WaitAsync();
        _holdsGate = true;
        try
        {
            using var span = FixtureTelemetry.StartInit(nameof(FrozenNrUiScenarioFixture));
            Data = await NrGameDataUiEngine.CreateFrozenAsync(staticDir, headless, slowMo);
            Roster = await NrRosterUiEngine.CreateFrozenAsync(harFile, headless: headless, slowMo: slowMo);
        }
        catch (Microsoft.Playwright.PlaywrightException ex) when (PlaywrightBrowsers.AreMissing(ex))
        {
            // The Playwright browsers are not installed: the lane skips, and the engine-composition check
            // fails a profile that claims it, naming the fix (FrozenNrUiRosterFixture).
            await DisposeAsync();
            Unavailable = "run setup.ps1 — the Playwright browsers are missing — skipping the frozen NR UI scenario lane";
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
                BrowserResourceRaceGate.FrozenNrGameDataUi.Release();
            }
        }

        return default;
    }
}

[CollectionDefinition("FrozenNrUiScenario")]
public class FrozenNrUiScenarioCollection : ICollectionFixture<FrozenNrUiScenarioFixture>
{
}
