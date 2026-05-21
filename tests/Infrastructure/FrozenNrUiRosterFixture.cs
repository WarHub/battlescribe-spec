using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrRosterUiDriver;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture for NR UI driver conformance tests in frozen (HAR replay) mode.
/// Engines are not pooled — UI-driven tests are sequential within a single browser context
/// to avoid visual conflicts. Use NR_PARALLEL to allow small concurrency if needed.
/// </summary>
public sealed class FrozenNrUiRosterFixture : IAsyncLifetime
{
    public NrRosterUiEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_UI_FROZEN_SKIP") == "true")
        {
            return;
        }

        var harFile = HarRecorder.FindFrozenHarFile();
        if (harFile is null)
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        Engine = await NrRosterUiEngine.CreateFrozenAsync(harFile, headless: headless, slowMo: slowMo);
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

[CollectionDefinition("FrozenNrUiRoster")]
public class FrozenNrUiRosterCollection : ICollectionFixture<FrozenNrUiRosterFixture>
{
}
