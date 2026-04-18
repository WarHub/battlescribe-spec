using BattleScribeSpec.NewRecruit;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Single-engine fixture for sequential frozen NR tests.
/// Only initializes when NR_SEQUENTIAL=true is set, to avoid launching
/// an extra browser during normal test runs.
/// Useful for debugging individual spec failures without parallel noise.
/// </summary>
public sealed class SequentialFrozenNewRecruitFixture : IAsyncLifetime
{
    public NewRecruitRosterEngine? Engine { get; private set; }
    public bool Available => Engine is not null;
    public string? HarFilePath { get; private set; }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_SEQUENTIAL") is not "true")
            return;

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
            Console.Error.WriteLine(Engine.Timings.GetReport());
        }
        Engine?.Dispose();
        Engine = null;
        return Task.CompletedTask;
    }
}

[CollectionDefinition("SequentialFrozenNewRecruit")]
public class SequentialFrozenNewRecruitCollection : ICollectionFixture<SequentialFrozenNewRecruitFixture>
{
}
