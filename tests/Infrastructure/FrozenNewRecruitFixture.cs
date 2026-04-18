using BattleScribeSpec.NewRecruit;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a pool of NewRecruitRosterEngines in frozen (HAR replay) mode.
/// Each engine has its own browser context, enabling parallel execution.
/// This is the default fixture for frozen NR conformance tests.
/// </summary>
public sealed class FrozenNewRecruitFixture : IAsyncLifetime
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

[CollectionDefinition("FrozenNewRecruit")]
public class FrozenNewRecruitCollection : ICollectionFixture<FrozenNewRecruitFixture>
{
}
