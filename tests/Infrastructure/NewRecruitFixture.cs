using BattleScribeSpec.NewRecruit;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a single NewRecruitRosterEngine browser session
/// for all tests in the "NewRecruit" collection. Avoids launching 280+ browsers.
/// </summary>
public sealed class NewRecruitFixture : IAsyncLifetime
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

[CollectionDefinition("NewRecruit")]
public class NewRecruitCollection : ICollectionFixture<NewRecruitFixture>
{
}
