using BattleScribeSpec.NrGameDataUiDriver;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates an NrGameDataUiEngine in frozen (static file) mode.
/// Serves the NR Editor gh-pages deployment from .testdata/nr-editor/ via Playwright
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
///   NR_EDITOR_UI_FROZEN_SKIP  — "true" to skip all frozen NR Editor UI tests
/// </summary>
public sealed class FrozenNrGameDataUiFixture : IAsyncLifetime
{
    public NrGameDataUiEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

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

        try
        {
            Engine = await NrGameDataUiEngine.CreateFrozenAsync(staticDir, headless, slowMo);
        }
        catch (Microsoft.Playwright.PlaywrightException)
        {
            // Playwright browsers not installed — skip gracefully
        }
    }

    public async ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;
        await ValueTask.CompletedTask;
    }
}

[CollectionDefinition("FrozenNrGameDataUi")]
public class FrozenNrGameDataUiCollection : ICollectionFixture<FrozenNrGameDataUiFixture>
{
}
