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
/// <remarks>
/// Drives the live NR Editor on <b>giloushaker.github.io</b> — a third party's server — so its session
/// is drawn from that host's <see cref="LiveLoadBudget"/>, the one home of the live load limit.
/// </remarks>
public sealed class LiveNrGameDataUiFixture : IAsyncLifetime
{
    private LiveLoadLease? _lease;

    public NrGameDataUiEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    /// <summary>Why no engine was created, when none was — an unset URL, or an exhausted live budget.</summary>
    public string Unavailable { get; private set; } = "NR_EDITOR_URL not set";

    public async ValueTask InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("NR_EDITOR_URL");
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        _lease = LiveLoadBudget.Reserve(nameof(LiveNrGameDataUiFixture), url, 1);
        if (_lease.Sessions == 0)
        {
            Unavailable = _lease.Explanation;
            return;
        }

        using var span = FixtureTelemetry.StartInit(nameof(LiveNrGameDataUiFixture));
        try
        {
            Engine = await NrGameDataUiEngine.CreateAsync(url, headless, slowMo);
        }
        catch (Microsoft.Playwright.PlaywrightException ex)
        {
            // Playwright browsers not installed — skip gracefully, saying so rather than blaming the URL.
            Unavailable = $"Playwright is not available — skipping live NR Editor GameData UI tests ({ex.Message})";
        }
    }

    public async ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;

        _lease?.Dispose();
        _lease = null;
        await ValueTask.CompletedTask;
    }
}

[CollectionDefinition("LiveNrGameDataUi")]
public class LiveNrGameDataUiCollection : ICollectionFixture<LiveNrGameDataUiFixture>
{
}
