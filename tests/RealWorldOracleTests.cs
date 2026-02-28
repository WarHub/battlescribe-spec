using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Oracle tests against real wh40k-9e data.
/// Loads actual game system and catalogue files via the BattleScribe Java deserializer,
/// initializes the engine, and exercises real roster operations.
/// Skipped if data is not present.
/// </summary>
public class RealWorldOracleTests(ITestOutputHelper output)
{
    private static string Wh40kDataDir => TestPaths.Wh40kDataDir!;

    private static bool DataAvailable => TestPaths.Wh40kDataAvailable;

    [SkippableFact]
    public void LoadGameSystem_ViaJavaDeserializer()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        using var oracle = new BattleScribeOracle();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        oracle.LoadGameSystemFile(gstFile);
        var errors = oracle.InitializeFromLoadedData();

        output.WriteLine($"Init errors: {errors.Count}");
        foreach (var e in errors.Take(5))
            output.WriteLine($"  - {e}");

        var forceEntries = oracle.GetAvailableForceEntryNames();
        output.WriteLine($"Force entries: {string.Join(", ", forceEntries)}");
        Assert.NotEmpty(forceEntries);
    }

    [SkippableFact]
    public void LoadGameSystemAndCatalogue_ViaJavaDeserializer()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        using var oracle = new BattleScribeOracle();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        oracle.LoadGameSystemFile(gstFile);

        // Load a specific catalogue (Space Marines is common)
        var catFiles = Directory.GetFiles(Wh40kDataDir, "*.cat");
        var smCat = catFiles.FirstOrDefault(f => f.Contains("Space Marines", StringComparison.OrdinalIgnoreCase))
            ?? catFiles.First();
        oracle.LoadCatalogueFile(smCat);

        var errors = oracle.InitializeFromLoadedData();
        output.WriteLine($"Loaded {Path.GetFileName(smCat)}");
        output.WriteLine($"Init errors: {errors.Count}");

        var forceEntries = oracle.GetAvailableForceEntryNames();
        output.WriteLine($"Force entries: {string.Join(", ", forceEntries)}");
        Assert.NotEmpty(forceEntries);
    }

    [SkippableFact]
    public void AddForce_WithRealData()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        using var oracle = new BattleScribeOracle();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        oracle.LoadGameSystemFile(gstFile);

        var catFiles = Directory.GetFiles(Wh40kDataDir, "*.cat");
        var smCat = catFiles.FirstOrDefault(f => f.Contains("Space Marines", StringComparison.OrdinalIgnoreCase))
            ?? catFiles.First();
        oracle.LoadCatalogueFile(smCat);

        oracle.InitializeFromLoadedData();

        // Add first force
        var forceErrors = oracle.AddForceByIndex(0);
        output.WriteLine($"Force errors: {forceErrors.Count}");

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);
        output.WriteLine($"Forces: {snapshot.Forces.Length}");
        Assert.Equal(1, snapshot.Forces.Length);
        output.WriteLine($"Force name: {snapshot.Forces[0].Name}");

        // Check validation
        var valErrors = oracle.GetValidationErrors();
        output.WriteLine($"Validation errors: {valErrors.Count}");
        foreach (var e in valErrors.Take(10))
            output.WriteLine($"  - {e}");
    }

    [SkippableFact]
    public void LoadAllCatalogues_ViaJavaDeserializer()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        using var oracle = new BattleScribeOracle();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        oracle.LoadGameSystemFile(gstFile);

        var catFiles = Directory.GetFiles(Wh40kDataDir, "*.cat");
        var loaded = 0;
        var failed = new List<string>();

        foreach (var catFile in catFiles)
        {
            try
            {
                oracle.LoadCatalogueFile(catFile);
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

    [SkippableFact]
    public void CompareForceEntries_JavaVsWham()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();

        // Load via wham
        var whamGs = DataLoader.LoadFile(gstFile) as WarHub.ArmouryModel.Source.GamesystemNode;
        Assert.NotNull(whamGs);

        // Load via Java
        using var oracle = new BattleScribeOracle();
        oracle.LoadGameSystemFile(gstFile);
        oracle.InitializeFromLoadedData();

        var whamForceNames = whamGs.ForceEntries.Select(fe => fe.Name).OrderBy(x => x).ToList();
        var javaForceNames = oracle.GetAvailableForceEntryNames().OrderBy(x => x).ToList();

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
