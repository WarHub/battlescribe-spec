using BattleScribeSpec.NrRosterUiDriver;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture for NR UI driver conformance tests against a live NR instance.
/// Gated by NR_ENGINE_URL env var.
/// </summary>
/// <remarks>
/// One browser session, and it is a real visitor on <b>newrecruit.eu</b> — so it draws that session
/// from <see cref="LiveLoadBudget"/> like every other live fixture. One is fewer than the limit, which
/// is exactly why this fixture went unnoticed: it could never breach the bound on its own, so nothing
/// made it consult one. "Cannot breach it alone" is not the same property as "is bounded", and only
/// the second one composes.
/// </remarks>
public sealed class LiveNrUiRosterFixture : IAsyncLifetime
{
    private LiveLoadLease? _lease;

    public NrRosterUiEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    /// <summary>Why no engine was created, when none was — an unset URL, or an exhausted live budget.</summary>
    public string Unavailable { get; private set; } = "NR_ENGINE_URL not set";

    public async ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        var lease = LiveLoadBudget.Reserve(nameof(LiveNrUiRosterFixture), baseUrl, 1);
        if (lease.Sessions == 0)
        {
            Unavailable = lease.Explanation;
            lease.Dispose();
            return;
        }

        using var span = FixtureTelemetry.StartInit(nameof(LiveNrUiRosterFixture));

        // The permit is returned if the engine fails to come up — see LiveLoadLease.Open.
        _lease = lease;
        Engine = await lease.OpenAsync(() => NrRosterUiEngine.CreateAsync(baseUrl, headless, slowMo));
    }

    public async ValueTask DisposeAsync()
    {
        if (Engine is not null)
        {
            await Engine.Browser.DisposeAsync();
            Engine = null;
        }

        _lease?.Dispose();
        _lease = null;
    }
}

[CollectionDefinition("LiveNrUiRoster")]
public class LiveNrUiRosterCollection : ICollectionFixture<LiveNrUiRosterFixture>
{
}
