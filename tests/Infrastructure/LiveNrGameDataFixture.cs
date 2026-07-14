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
/// <remarks>
/// The NR Editor is hosted on <b>giloushaker.github.io</b> — someone else's server, and a different
/// someone from <c>newrecruit.eu</c>. Its session is drawn from that host's own
/// <see cref="LiveLoadBudget"/>: one constant, one budget per third party, because a courtesy limit on
/// one site says nothing about another.
/// </remarks>
public sealed class LiveNrGameDataFixture : IAsyncLifetime
{
    private LiveLoadLease? _lease;

    public NewRecruitGameDataEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    /// <summary>Why no engine was created, when none was — an unset URL, or an exhausted live budget.</summary>
    public string Unavailable { get; private set; } = "NR_EDITOR_URL not set";

    public async ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_EDITOR_URL");
        if (string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        _lease = LiveLoadBudget.Reserve(nameof(LiveNrGameDataFixture), baseUrl, 1);
        if (_lease.Sessions == 0)
        {
            Unavailable = _lease.Explanation;
            return;
        }

        using var span = FixtureTelemetry.StartInit(nameof(LiveNrGameDataFixture));
        Engine = await NewRecruitGameDataEngine.CreateAsync(baseUrl, headless, slowMo);
    }

    public ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;

        _lease?.Dispose();
        _lease = null;
        return default;
    }
}

[CollectionDefinition("LiveNrGameData")]
public class LiveNrGameDataCollection : ICollectionFixture<LiveNrGameDataFixture>
{
}
