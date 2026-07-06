using BattleScribeSpec.NrGameDataUiDriver;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a pool of NrGameDataUiEngines in frozen (static file) mode.
/// Each engine has its own browser context (one shared Chromium browser), enabling parallel
/// execution. Serves the NR Editor gh-pages deployment from .testdata/nr-editor/ via Playwright
/// route interception. Fully offline and deterministic — no network access needed.
///
/// Reuses the same NR Editor static snapshot as <see cref="FrozenNrGameDataFixture"/>
/// (non-UI engine). No additional setup step needed.
///
/// Skipped when:
///   - .testdata/nr-editor/ directory doesn't exist (run setup.ps1)
///   - NR_EDITOR_UI_FROZEN_SKIP=true
///
/// Environment variables:
///   NR_HEADLESS               — "false" to show the browser (default: true)
///   NR_SLOW_MO                — milliseconds to slow Playwright actions (for debugging)
///   NR_PARALLEL               — number of parallel browser contexts (default: 5)
///   NR_EDITOR_UI_FROZEN_SKIP  — "true" to skip all frozen NR Editor UI tests
/// </summary>
public sealed class FrozenNrGameDataUiFixture : IAsyncLifetime
{
    public NrGameDataUiEnginePool? EnginePool { get; private set; }
    public bool Available => EnginePool is not null;

    /// <summary>Number of parallel engines (browser contexts) in the pool, or 0 when unavailable.</summary>
    public int Size => EnginePool?.Size ?? 0;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_EDITOR_UI_FROZEN_SKIP") == "true")
        {
            return;
        }

        var staticDir = NrGameDataUiEngine.FindFrozenStaticDir();
        if (staticDir is null)
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        var concurrency = 5;
        if (int.TryParse(Environment.GetEnvironmentVariable("NR_PARALLEL"), out var envConcurrency) && envConcurrency > 0)
        {
            concurrency = envConcurrency;
        }

        try
        {
            EnginePool = await NrGameDataUiEnginePool.CreateFrozenAsync(staticDir, concurrency, headless, slowMo);
        }
        catch (Microsoft.Playwright.PlaywrightException)
        {
            // Playwright browsers not installed — skip gracefully
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (EnginePool is not null)
        {
            await EnginePool.DisposeAsync();
        }

        EnginePool = null;
    }
}

[CollectionDefinition("FrozenNrGameDataUi")]
public class FrozenNrGameDataUiCollection : ICollectionFixture<FrozenNrGameDataUiFixture>
{
}
