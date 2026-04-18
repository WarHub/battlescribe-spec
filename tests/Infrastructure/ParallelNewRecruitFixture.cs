using BattleScribeSpec.NewRecruit;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a pool of NewRecruitRosterEngines pointed at a live NR site.
/// Gated by NR_ENGINE_URL env var. Lower default concurrency (2) to avoid rate limiting.
/// </summary>
public sealed class ParallelNewRecruitFixture : IAsyncLifetime
{
    public NewRecruitEnginePool? EnginePool { get; private set; }
    public bool Available => EnginePool is not null;

    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl))
            return;

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";

        // Lower default concurrency for live mode to avoid rate limiting
        var concurrency = 2;
        if (int.TryParse(Environment.GetEnvironmentVariable("NR_PARALLEL"), out var envConcurrency) && envConcurrency > 0)
            concurrency = envConcurrency;

        EnginePool = await NewRecruitEnginePool.CreateLiveAsync(concurrency, baseUrl, headless);
    }

    public async Task DisposeAsync()
    {
        if (EnginePool is not null)
            await EnginePool.DisposeAsync();
        EnginePool = null;
    }
}

[CollectionDefinition("ParallelNewRecruit")]
public class ParallelNewRecruitCollection : ICollectionFixture<ParallelNewRecruitFixture>
{
}
