using BattleScribeSpec.NrRosterUiDriver;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture for NR UI driver conformance tests against a live NR instance.
/// Gated by NR_ENGINE_URL env var.
/// </summary>
public sealed class LiveNrUiRosterFixture : IAsyncLifetime
{
    public NrRosterUiEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    public async ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        using var span = FixtureTelemetry.StartInit(nameof(LiveNrUiRosterFixture));
        Engine = await NrRosterUiEngine.CreateAsync(baseUrl, headless, slowMo);
    }

    public async ValueTask DisposeAsync()
    {
        if (Engine is not null)
        {
            await Engine.Browser.DisposeAsync();
            Engine = null;
        }
    }
}

[CollectionDefinition("LiveNrUiRoster")]
public class LiveNrUiRosterCollection : ICollectionFixture<LiveNrUiRosterFixture>
{
}
