using BattleScribeSpec.NrGameDataUiDriver;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates an NrGameDataUiEngine pointed at a live NR Editor deployment.
/// Requires network access to the NR Editor URL.
///
/// Skipped when NR_EDITOR_URL is not set.
///
/// Environment variables:
///   NR_EDITOR_URL  — URL of the live NR Editor (required; e.g. https://giloushaker.github.io/nr-editor)
///   NR_HEADLESS    — "false" to show the browser (default: true)
///   NR_SLOW_MO     — milliseconds to slow Playwright actions (for debugging)
/// </summary>
public sealed class LiveNrGameDataUiFixture : IAsyncLifetime
{
    public NrGameDataUiEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    public async ValueTask InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("NR_EDITOR_URL");
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        using var span = FixtureTelemetry.StartInit(nameof(LiveNrGameDataUiFixture));
        try
        {
            Engine = await NrGameDataUiEngine.CreateAsync(url, headless, slowMo);
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

[CollectionDefinition("LiveNrGameDataUi")]
public class LiveNrGameDataUiCollection : ICollectionFixture<LiveNrGameDataUiFixture>
{
}
