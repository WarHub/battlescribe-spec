using BattleScribeSpec.NewRecruit;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Single-engine fixture for sequential live NR tests and smoke tests.
/// Gated by NR_ENGINE_URL env var (same as before).
/// Shared by <see cref="SequentialLiveNewRecruitConformanceTests"/> and
/// <see cref="LiveNewRecruitSmokeTests"/>.
/// </summary>
public sealed class SequentialLiveNewRecruitFixture : IAsyncLifetime
{
    public NewRecruitRosterEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl))
            return;

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        Engine = await NewRecruitRosterEngine.CreateAsync(baseUrl, headless);
    }

    public Task DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;
        return Task.CompletedTask;
    }
}

[CollectionDefinition("SequentialLiveNewRecruit")]
public class SequentialLiveNewRecruitCollection : ICollectionFixture<SequentialLiveNewRecruitFixture>
{
}
