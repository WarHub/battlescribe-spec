using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a NewRecruitGameDataEngine pointed at a live NR Editor.
/// Gated by NR_EDITOR_URL env var (required — no default URL to prevent accidental live hits).
///
/// Environment variables:
///   NR_EDITOR_URL  — URL of the NR Editor app (e.g., https://giloushaker.github.io/nr-editor/)
///   NR_HEADLESS    — "false" to show the browser (default: true)
///   NR_SLOW_MO     — milliseconds to slow down Playwright actions (for debugging)
/// </summary>
public sealed class LiveNewRecruitGameDataFixture : IAsyncLifetime
{
    public NewRecruitGameDataEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    public async ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_EDITOR_URL");
        if (string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        Engine = await NewRecruitGameDataEngine.CreateAsync(baseUrl, headless, slowMo);
    }

    public ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;
        return default;
    }
}

[CollectionDefinition("LiveNewRecruitGameData")]
public class LiveNewRecruitGameDataCollection : ICollectionFixture<LiveNewRecruitGameDataFixture>
{
}
