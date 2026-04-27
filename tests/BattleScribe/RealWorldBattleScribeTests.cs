using BattleScribeSpec;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// engine tests against real wh40k-9e data.
/// Loads actual game system and catalogue files via the BattleScribe Java deserializer,
/// initializes the engine, and exercises real roster operations.
/// Skipped if data is not present.
/// </summary>
[Trait("Category", "Integration")]
public class RealWorldBattleScribeTests(ITestOutputHelper output)
{
    private static string Wh40kDataDir => TestPaths.Wh40kDataDir!;

    private static bool DataAvailable => TestPaths.Wh40kDataAvailable;

    [Fact]
    public void LoadGameSystem_ViaJavaDeserializer()
    {
        Assert.SkipUnless(DataAvailable, "wh40k-9e data not found. Run ./setup.ps1 to clone required repositories.");

        using var engine = new BattleScribeEngine();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        engine.LoadGameSystemFile(gstFile);
        var errors = engine.InitializeFromLoadedData();

        output.WriteLine($"Init errors: {errors.Count}");
        foreach (var e in errors.Take(5))
            output.WriteLine($"  - {e}");

        var forceEntries = engine.GetAvailableForceEntryNames();
        output.WriteLine($"Force entries: {string.Join(", ", forceEntries)}");
        Assert.NotEmpty(forceEntries);
    }

    [Fact]
    public void AddForce_WithRealData()
    {
        Assert.SkipUnless(DataAvailable, "wh40k-9e data not found. Run ./setup.ps1 to clone required repositories.");

        using var engine = new BattleScribeEngine();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        engine.LoadGameSystemFile(gstFile);

        var catFiles = Directory.GetFiles(Wh40kDataDir, "*.cat");
        var smCat = catFiles.FirstOrDefault(f => f.Contains("Space Marines", StringComparison.OrdinalIgnoreCase))
            ?? catFiles.First();
        engine.LoadCatalogueFile(smCat);

        engine.InitializeFromLoadedData();

        // Add first force
        var forceErrors = engine.AddForceByIndex(0);
        output.WriteLine($"Force errors: {forceErrors.Count}");

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);
        output.WriteLine($"Forces: {snapshot.Forces.Count}");
        Assert.Single(snapshot.Forces);
        output.WriteLine($"Force name: {snapshot.Forces[0].Name}");

        // Check validation
        var valErrors = engine.GetValidationErrors();
        output.WriteLine($"Validation errors: {valErrors.Count}");
        foreach (var e in valErrors.Take(10))
            output.WriteLine($"  - {e}");
    }

    [Fact]
    public void LoadAllCatalogues_ViaJavaDeserializer()
    {
        Assert.SkipUnless(DataAvailable, "wh40k-9e data not found. Run ./setup.ps1 to clone required repositories.");

        using var engine = new BattleScribeEngine();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        engine.LoadGameSystemFile(gstFile);

        var catFiles = Directory.GetFiles(Wh40kDataDir, "*.cat");
        var loaded = 0;
        var failed = new List<string>();

        foreach (var catFile in catFiles)
        {
            try
            {
                engine.LoadCatalogueFile(catFile);
                loaded++;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(catFile)}: {ex.Message}");
            }
        }

        output.WriteLine($"Loaded {loaded}/{catFiles.Length} catalogues");
        foreach (var f in failed)
            output.WriteLine($"  FAILED: {f}");

        Assert.True(loaded > 0, "Should load at least some catalogues");
        Assert.Empty(failed);
    }

    [Fact]
    public void CompareForceEntries_JavaVsWham()
    {
        Assert.SkipUnless(DataAvailable, "wh40k-9e data not found. Run ./setup.ps1 to clone required repositories.");

        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();

        // Load via wham
        var whamGs = DataLoader.LoadFile(gstFile) as WarHub.ArmouryModel.Source.GamesystemNode;
        Assert.NotNull(whamGs);

        // Load via Java
        using var engine = new BattleScribeEngine();
        engine.LoadGameSystemFile(gstFile);
        engine.InitializeFromLoadedData();

        var whamForceNames = whamGs.ForceEntries.Select(fe => fe.Name).OrderBy(x => x).ToList();
        var javaForceNames = engine.GetAvailableForceEntryNames().OrderBy(x => x).ToList();

        output.WriteLine($"wham force entries ({whamForceNames.Count}): {string.Join(", ", whamForceNames)}");
        output.WriteLine($"Java force entries ({javaForceNames.Count}): {string.Join(", ", javaForceNames)}");

        // Both should have same force entry names
        Assert.Equal(whamForceNames.Count, javaForceNames.Count);
        for (int i = 0; i < whamForceNames.Count; i++)
        {
            Assert.Equal(whamForceNames[i], javaForceNames[i]);
        }
    }
}
