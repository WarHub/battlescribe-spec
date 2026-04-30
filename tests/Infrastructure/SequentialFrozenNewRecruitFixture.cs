using BattleScribeSpec.NewRecruit;

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

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("NR_SEQUENTIAL") is not "true")
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("NR_FROZEN_SKIP") == "true")
        {
            return;
        }

        HarFilePath = HarRecorder.FindFrozenHarFile();
        if (HarFilePath is null)
        {
            return;
        }

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        var visual = Environment.GetEnvironmentVariable("NR_VISUAL") == "true";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;
        Engine = await NewRecruitRosterEngine.CreateFrozenAsync(HarFilePath, headless: headless, slowMo: slowMo);
        Engine.Visual = visual;
    }

    public ValueTask DisposeAsync()
    {
        if (Engine is not null)
        {
            Console.Error.WriteLine(Engine.Timings.GetReport());
        }
        Engine?.Dispose();
        Engine = null;
        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition("SequentialFrozenNewRecruit")]
public class SequentialFrozenNewRecruitCollection : ICollectionFixture<SequentialFrozenNewRecruitFixture>
{
}
