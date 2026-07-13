using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a NewRecruitGameDataEngine in frozen (static file) mode.
/// Serves the NR Editor gh-pages deployment from .testdata/nr-editor/ via Playwright route interception.
/// Fully offline and deterministic — no network access needed.
///
/// Skipped when:
///   - .testdata/nr-editor/ directory doesn't exist (run setup.ps1)
///   - NR_FROZEN_SKIP=true
///
/// Environment variables:
///   NR_HEADLESS    — "false" to show the browser (default: true)
///   NR_SLOW_MO     — milliseconds to slow down Playwright actions (for debugging)
///   NR_FROZEN_SKIP — "true" to skip frozen tests entirely
/// </summary>
public sealed class FrozenNrGameDataFixture : IAsyncLifetime
{
    public NewRecruitGameDataEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_FROZEN_SKIP") == "true")
        {
            return;
        }

        var staticDir = NewRecruitGameDataEngine.FindFrozenStaticDir();
        if (staticDir is null)
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        // Held for the engine's entire alive window (released in DisposeAsync, after teardown) so
        // NewRecruitGameDataEngineResourceMetricsTests's own independent CreateFrozenAsync call
        // can never be alive at the same time as this engine's browser — see
        // BrowserResourceRaceGate's remarks for why plain [Collection] membership isn't enough.
        await BrowserResourceRaceGate.FrozenNrGameData.WaitAsync();
        try
        {
            using var span = FixtureTelemetry.StartInit(nameof(FrozenNrGameDataFixture));
            Engine = await NewRecruitGameDataEngine.CreateFrozenAsync(staticDir, headless, slowMo);
        }
        catch
        {
            BrowserResourceRaceGate.FrozenNrGameData.Release();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Engine is not null)
        {
            Engine.Dispose();
            BrowserResourceRaceGate.FrozenNrGameData.Release();
        }

        Engine = null;
        return default;
    }
}

[CollectionDefinition("FrozenNrGameData")]
public class FrozenNrGameDataCollection : ICollectionFixture<FrozenNrGameDataFixture>
{
}
