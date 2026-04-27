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

    public async ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl))
            return;

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        var visual = Environment.GetEnvironmentVariable("NR_VISUAL") == "true";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;
        Engine = await NewRecruitRosterEngine.CreateAsync(baseUrl, headless, slowMo);
        Engine.Visual = visual;
    }

    public ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;
        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition("SequentialLiveNewRecruit")]
public class SequentialLiveNewRecruitCollection : ICollectionFixture<SequentialLiveNewRecruitFixture>
{
}
