using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public sealed class BsUiDataStagingTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"bsspec-bs-ui-stage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
        {
            Directory.Delete(_outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task StageDataFilesAsync_CreatesGameSystemSubfolderAndIndex()
    {
        var spec = SpecLoader.Load(FindSpec("cost/cost-hidden-limit-validation"));
        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);
        var xmlFiles = BuildXmlFiles(gameSystem, catalogues);

        await BsUiDataStaging.StageDataFilesAsync(_outputDir, gameSystem, catalogues, xmlFiles);

        var stagedDir = Path.Combine(_outputDir, gameSystem.Id);
        Assert.True(Directory.Exists(stagedDir));
        Assert.True(File.Exists(Path.Combine(stagedDir, $"{gameSystem.Id}.gst")));
        Assert.True(File.Exists(Path.Combine(stagedDir, $"{catalogues[0].Id}.cat")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "index.bsi")));
    }

    [Fact]
    public void BuildIndexXml_ListsGameSystemAndCatalogueFiles()
    {
        var spec = SpecLoader.Load(FindSpec("cost/cost-hidden-limit-validation"));
        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);
        var xmlFiles = BuildXmlFiles(gameSystem, catalogues);

        var indexXml = BsUiDataStaging.BuildIndexXml(gameSystem, catalogues, xmlFiles);

        Assert.Contains("dataIndex", indexXml);
        Assert.Contains($"filePath=\"{gameSystem.Id}.gst\"", indexXml);
        Assert.Contains($"filePath=\"{catalogues[0].Id}.cat\"", indexXml);
        Assert.Contains($"dataId=\"{gameSystem.Id}\"", indexXml);
        Assert.Contains($"dataId=\"{catalogues[0].Id}\"", indexXml);
    }

    private static IReadOnlyList<(string FileName, string Content)> BuildXmlFiles(
        ProtocolGameSystem gameSystem,
        ProtocolCatalogue[] catalogues)
    {
        var files = new List<(string FileName, string Content)>
        {
            ($"{gameSystem.Id}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem))
        };

        foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
        {
            files.Add((fileName, xml));
        }

        return files;
    }

    private static string FindSpec(string specId)
    {
        var specsDir = SpecLoader.FindRosterSpecsDirectory()
            ?? throw new InvalidOperationException("Could not find specs directory");
        string? category = null;
        var id = specId;
        if (specId.Contains('/'))
        {
            var parts = specId.Split('/', 2);
            category = parts[0];
            id = parts[1];
        }

        var match = SpecLoader.DiscoverSpecs(specsDir)
            .FirstOrDefault(s => s.Id == id && (category is null || s.Category == category));
        if (match.Path is null)
        {
            throw new FileNotFoundException($"Spec not found: {specId}");
        }

        return match.Path;
    }
}
