using BattleScribeSpec.NewRecruit;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture that creates a NewRecruitRosterEngine in frozen (HAR replay) mode.
/// Uses a pre-recorded HAR snapshot downloaded from WarHub/newrecruit-har GitHub Releases.
/// No live NR website or env var needed. Skipped when the HAR file doesn't exist or NR_FROZEN_SKIP=true.
/// </summary>
public sealed class FrozenNewRecruitFixture : IAsyncLifetime
{
    public NewRecruitRosterEngine? Engine { get; private set; }
    public bool Available => Engine is not null;
    public string? HarFilePath { get; private set; }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_FROZEN_SKIP") == "true")
            return;

        HarFilePath = HarRecorder.FindFrozenHarFile();
        if (HarFilePath is null)
            return;

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        Engine = await NewRecruitRosterEngine.CreateFrozenAsync(HarFilePath, headless: headless);
    }

    public Task DisposeAsync()
    {
        if (Engine is not null)
        {
            // Dump timing report to console for performance analysis
            Console.Error.WriteLine(Engine.Timings.GetReport());
        }
        Engine?.Dispose();
        Engine = null;
        return Task.CompletedTask;
    }
}

[CollectionDefinition("FrozenNewRecruit")]
public class FrozenNewRecruitCollection : ICollectionFixture<FrozenNewRecruitFixture>
{
}
