using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a pool of NewRecruitRosterEngines in frozen (HAR replay) mode.
/// Each engine has its own browser context, enabling parallel execution.
/// This is the default fixture for frozen NR conformance tests.
/// </summary>
public sealed class FrozenNrRosterFixture : IAsyncLifetime
{
    public NewRecruitEnginePool? EnginePool { get; private set; }
    public bool Available => EnginePool is not null;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_FROZEN_SKIP") == "true")
        {
            return;
        }

        var harFile = HarRecorder.FindFrozenHarFile();
        if (harFile is null)
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        var visual = Environment.GetEnvironmentVariable("NR_VISUAL") == "true";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        var concurrency = 5;
        if (int.TryParse(Environment.GetEnvironmentVariable("NR_PARALLEL"), out var envConcurrency) && envConcurrency > 0)
        {
            concurrency = envConcurrency;
        }

        using var span = FixtureTelemetry.StartInit(nameof(FrozenNrRosterFixture));
        EnginePool = await NewRecruitEnginePool.CreateFrozenAsync(harFile, concurrency, headless: headless, visual: visual, slowMo: slowMo);
        FixtureTelemetry.SetPoolSize(span, EnginePool.Size);
    }

    /// <summary>Acquire an engine from the pool, wrapped in a short acquire-wait span.</summary>
    public ValueTask<PooledEngine<NewRecruitRosterEngine>> AcquireAsync(CancellationToken ct = default) =>
        FixtureTelemetry.AcquireAsync(nameof(FrozenNrRosterFixture), EnginePool!.AcquireAsync, ct);

    public async ValueTask DisposeAsync()
    {
        if (EnginePool is not null)
        {
            await EnginePool.DisposeAsync();
        }

        EnginePool = null;
    }
}

[CollectionDefinition("FrozenNrRoster")]
public class FrozenNrRosterCollection : ICollectionFixture<FrozenNrRosterFixture>
{
}
