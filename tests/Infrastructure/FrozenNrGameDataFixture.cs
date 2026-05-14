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

        Engine = await NewRecruitGameDataEngine.CreateFrozenAsync(staticDir, headless, slowMo);
    }

    public ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;
        return default;
    }
}

[CollectionDefinition("FrozenNrGameData")]
public class FrozenNrGameDataCollection : ICollectionFixture<FrozenNrGameDataFixture>
{
}
