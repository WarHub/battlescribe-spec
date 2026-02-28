using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Integration tests against real wh40k-9e data.
/// These validate that the spec handles production-quality data.
/// Skipped if data is not present.
/// </summary>
public class RealWorldDataTests
{
    private static string Wh40kDataDir => TestPaths.Wh40kDataDir!;

    private static bool DataAvailable => TestPaths.Wh40kDataAvailable;

    [SkippableFact]
    public void LoadGamesystem_Succeeds()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        var node = DataLoader.LoadFile(gstFile);

        Assert.NotNull(node);
        Assert.IsType<GamesystemNode>(node);
        var gs = (GamesystemNode)node;
        Assert.Equal("2.03", gs.BattleScribeVersion);
        Assert.NotEmpty(gs.Name);
        Assert.NotEmpty(gs.Id);
    }

    [SkippableFact]
    public void LoadAllCatalogues_Succeed()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var (gamesystem, catalogues) = DataLoader.LoadDirectory(Wh40kDataDir);
        Assert.NotNull(gamesystem);
        Assert.NotEmpty(catalogues);

        foreach (var cat in catalogues)
        {
            Assert.IsType<CatalogueNode>(cat);
            var catalogueNode = (CatalogueNode)cat;
            Assert.NotEmpty(catalogueNode.Name);
            Assert.NotEmpty(catalogueNode.GamesystemId);
        }
    }

    [SkippableFact]
    public void Gamesystem_HasExpectedStructure()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        var gs = (GamesystemNode)DataLoader.LoadFile(gstFile);

        // wh40k-9e should have pts, PL, and CP cost types
        Assert.True(gs.CostTypes.Count >= 2, $"Expected ≥2 cost types, got {gs.CostTypes.Count}");

        // Should have profile types (at least Unit, Weapon)
        Assert.True(gs.ProfileTypes.Count >= 1, $"Expected ≥1 profile types, got {gs.ProfileTypes.Count}");

        // Should have category entries (HQ, Troops, Elites, etc.)
        Assert.True(gs.CategoryEntries.Count >= 5, $"Expected ≥5 categories, got {gs.CategoryEntries.Count}");

        // Should have force entries (detachments)
        Assert.True(gs.ForceEntries.Count >= 1, $"Expected ≥1 force entries, got {gs.ForceEntries.Count}");
    }

    [SkippableFact]
    public void AllCatalogues_ReferenceCorrectGamesystem()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var (gamesystem, catalogues) = DataLoader.LoadDirectory(Wh40kDataDir);
        var gs = (GamesystemNode)gamesystem!;

        foreach (var cat in catalogues)
        {
            var catalogueNode = (CatalogueNode)cat;
            Assert.Equal(gs.Id, catalogueNode.GamesystemId);
        }
    }

    [SkippableFact]
    public void Catalogues_HaveSelectionEntries()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var (_, catalogues) = DataLoader.LoadDirectory(Wh40kDataDir);

        // At least some catalogues should have selection entries
        var withEntries = catalogues.Cast<CatalogueNode>()
            .Count(c => c.SelectionEntries.Count > 0 || c.EntryLinks.Count > 0);
        Assert.True(withEntries > 0, "Expected some catalogues with entries");
    }

    [SkippableFact]
    public void Gamesystem_RoundTrip_Succeeds()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        var (success, error) = DataLoader.RoundTripTest(gstFile);
        Assert.True(success, $"Round-trip failed: {error}");
    }

    [SkippableFact]
    public void Catalogues_RoundTrip_Succeed()
    {
        Skip.IfNot(DataAvailable, "wh40k-9e data not found");

        var catFiles = Directory.GetFiles(Wh40kDataDir, "*.cat");
        var failures = new List<string>();

        foreach (var catFile in catFiles)
        {
            var (success, error) = DataLoader.RoundTripTest(catFile);
            if (!success)
            {
                failures.Add($"{Path.GetFileName(catFile)}: {error}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{catFiles.Length} round-trip failures:\n{string.Join("\n", failures)}");
    }
}
