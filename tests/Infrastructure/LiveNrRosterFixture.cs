using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a pool of NewRecruitRosterEngines pointed at a live NR site.
/// Gated by NR_ENGINE_URL env var. Pool size comes from <see cref="FixtureConcurrency"/>
/// (backed by <c>ConcurrencyPolicy</c>) — not from an env var.
/// This is the default fixture for live NR conformance tests.
/// </summary>
public sealed class LiveNrRosterFixture : IAsyncLifetime
{
    public NewRecruitEnginePool? EnginePool { get; private set; }
    public bool Available => EnginePool is not null;

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

        var concurrency = FixtureConcurrency.PoolSizeFor("newrecruit");

        using var span = FixtureTelemetry.StartInit(nameof(LiveNrRosterFixture));
        EnginePool = await NewRecruitEnginePool.CreateLiveAsync(concurrency, baseUrl, headless, visual, slowMo);
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
    }
}

[CollectionDefinition("LiveNrRoster")]
public class LiveNrRosterCollection : ICollectionFixture<LiveNrRosterFixture>
{
}
