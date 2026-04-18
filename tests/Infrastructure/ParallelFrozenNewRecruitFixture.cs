using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a pool of NewRecruitRosterEngines in frozen mode.
/// Each engine has its own browser context with HAR replay, enabling parallel execution.
/// </summary>
public sealed class ParallelFrozenNewRecruitFixture : IAsyncLifetime
{
    public NewRecruitEnginePool? EnginePool { get; private set; }
    public bool Available => EnginePool is not null;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_FROZEN_SKIP") == "true")
            return;

        var harFile = HarRecorder.FindFrozenHarFile();
        if (harFile is null)
            return;

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";

        // Default to 5 parallel contexts; override with NR_PARALLEL env var
        var concurrency = 5;
        if (int.TryParse(Environment.GetEnvironmentVariable("NR_PARALLEL"), out var envConcurrency) && envConcurrency > 0)
            concurrency = envConcurrency;

        EnginePool = await NewRecruitEnginePool.CreateFrozenAsync(harFile, concurrency, headless: headless);
    }

    public async Task DisposeAsync()
    {
        if (EnginePool is not null)
            await EnginePool.DisposeAsync();
        EnginePool = null;
    }
}

[CollectionDefinition("ParallelFrozenNewRecruit")]
public class ParallelFrozenNewRecruitCollection : ICollectionFixture<ParallelFrozenNewRecruitFixture>
{
}
