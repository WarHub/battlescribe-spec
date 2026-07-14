using BattleScribeSpec.Concurrency;
using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a pool of NewRecruitRosterEngines pointed at a live NR site.
/// Gated by NR_ENGINE_URL env var. Pool size comes from <see cref="FixtureConcurrency"/>
/// (backed by <c>ConcurrencyPolicy</c>) — not from an env var.
/// This is the default fixture for live NR conformance tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one fixture in the repo that puts traffic on a third party's production website</b>
/// (<c>newrecruit.eu</c> — run by volunteers, not by us), so it is the one fixture that passes
/// <see cref="LoadTarget.ThirdPartyLive"/>. Its pool is therefore
/// <c>ConcurrencyPolicy.ThirdPartyLiveLoadLimit</c> = <b>2</b>, and NOT the <c>newrecruit</c> engine's
/// measured <c>ContextPoolSize</c> of 4 — which was fitted by sweeping <c>nr-frozen</c>, a HAR file on
/// local disk, in a measurement that never sent a request to newrecruit.eu.
/// </para>
/// <para>
/// The two lanes share an engine and they must not share a pool size. If you change this line, read
/// <c>ConcurrencyPolicy.ThirdPartyLiveLoadLimit</c> first — it quotes the commit that chose the 2, and
/// <c>ConcurrencyConfigurationDriftTests.LiveFixture_DeclaresThirdPartyLive_SoTheLoadLimitApplies</c>
/// will go red if this argument becomes <see cref="LoadTarget.Local"/>.
/// </para>
/// </remarks>
public sealed class LiveNrRosterFixture : IAsyncLifetime
{
    private LiveLoadLease? _lease;

    public NewRecruitEnginePool? EnginePool { get; private set; }
    public bool Available => EnginePool is not null;

    /// <summary>Why this fixture opened no pool, when it did not — an unset URL, or an exhausted live budget.</summary>
    public string Unavailable { get; private set; } = "NR_ENGINE_URL not set";

    public async ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        var visual = Environment.GetEnvironmentVariable("NR_VISUAL") == "true";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        // ThirdPartyLive: every context in this pool is a real visitor on newrecruit.eu.
        var concurrency = FixtureConcurrency.PoolSizeFor("newrecruit", LoadTarget.ThirdPartyLive);

        // ...and the policy's answer is a ceiling for THIS fixture, not for the process. Whatever other
        // live fixtures are alive in this test host draw on the same site's budget, so take what is
        // actually left rather than what we would like — see LiveLoadBudget for the 2 + 1 = 3 this closes.
        var lease = LiveLoadBudget.Reserve(nameof(LiveNrRosterFixture), baseUrl, concurrency);
        if (lease.Sessions == 0)
        {
            Unavailable = lease.Explanation;
            lease.Dispose();
            return;
        }

        using var span = FixtureTelemetry.StartInit(nameof(LiveNrRosterFixture));

        // OpenAsync returns the permits if the pool fails to come up (site down, Playwright launch
        // failure): a fixture holding no sessions must hold no permits, or an outage starves every later
        // fixture and they skip blaming the budget. See LiveLoadLease.Open.
        _lease = lease;
        EnginePool = await lease.OpenAsync(() =>
            NewRecruitEnginePool.CreateLiveAsync(lease.Sessions, baseUrl, headless, visual, slowMo));
        FixtureTelemetry.SetPoolSize(span, EnginePool.Size);
    }

    /// <summary>Acquire an engine from the pool, wrapped in a short acquire-wait span.</summary>
    public ValueTask<PooledEngine<NewRecruitRosterEngine>> AcquireAsync(CancellationToken ct = default) =>
        FixtureTelemetry.AcquireAsync(nameof(LiveNrRosterFixture), EnginePool!.AcquireAsync, ct);

    public async ValueTask DisposeAsync()
    {
        if (EnginePool is not null)
        {
            await EnginePool.DisposeAsync();
        }

        EnginePool = null;

        // The sessions are closed; give them back before the next fixture asks for them.
        _lease?.Dispose();
        _lease = null;
    }
}

[CollectionDefinition("LiveNrRoster")]
public class LiveNrRosterCollection : ICollectionFixture<LiveNrRosterFixture>
{
}
