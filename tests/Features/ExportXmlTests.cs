using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Tests for the --export-xml mode of the debugger CLI.
/// Calls Program.RunAsync directly with args, verifying that XML files
/// are generated correctly from spec setup data.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExportXmlTests : IDisposable
{
    private readonly string _outputDir;

    public ExportXmlTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"bsspec-export-xml-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
        {
            Directory.Delete(_outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportXml_ProducesGstAndCatFiles()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        var exitCode = await Program.RunAsync("--export-xml", _outputDir, specPath);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_outputDir, "system.gst")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "catalogue0.cat")));
    }

    [Fact]
    public async Task ExportXml_GstContainsValidXml()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("--export-xml", _outputDir, specPath);

        var gstXml = File.ReadAllText(Path.Combine(_outputDir, "system.gst"));
        Assert.Contains("<?xml", gstXml);
        Assert.Contains("gameSystem", gstXml);
        Assert.Contains("test-gs", gstXml);
        Assert.Contains("costType", gstXml);
    }

    [Fact]
    public async Task ExportXml_CatContainsValidXml()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("--export-xml", _outputDir, specPath);

        var catXml = File.ReadAllText(Path.Combine(_outputDir, "catalogue0.cat"));
        Assert.Contains("<?xml", catXml);
        Assert.Contains("catalogue", catXml);
        Assert.Contains("cat-1", catXml);
        Assert.Contains("selectionEntry", catXml);
    }

    [Fact]
    public async Task ExportXml_GstIsDeserializable()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("--export-xml", _outputDir, specPath);

        var gstXml = File.ReadAllText(Path.Combine(_outputDir, "system.gst"));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(gstXml));
        var gs = stream.DeserializeGamesystem()!;
        Assert.Equal("test-gs", gs.Id);
        Assert.Equal("Test GS", gs.Name);
        Assert.Equal(2, gs.CostTypes.Count);
    }

    [Fact]
    public async Task ExportXml_CatIsDeserializable()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("--export-xml", _outputDir, specPath);

        var catXml = File.ReadAllText(Path.Combine(_outputDir, "catalogue0.cat"));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(catXml));
        var cat = stream.DeserializeCatalogue()!;
        Assert.Equal("cat-1", cat.Id);
        Assert.Equal("Cat", cat.Name);
        Assert.Single(cat.SelectionEntries);
    }

    [Fact]
    public async Task ExportXml_ComplexSpec_ProducesCorrectFileCount()
    {
        var specPath = FindSpec("protocol/protocol-kitchen-sink");

        var exitCode = await Program.RunAsync("--export-xml", _outputDir, specPath);

        Assert.Equal(0, exitCode);
        var gstFiles = Directory.GetFiles(_outputDir, "*.gst");
        var catFiles = Directory.GetFiles(_outputDir, "*.cat");
        Assert.Single(gstFiles);
        Assert.True(catFiles.Length >= 1, "Expected at least one .cat file");
    }

    [Fact]
    public async Task ExportXml_DoesNotRequireEngine()
    {
        // --export-xml should exit before engine creation, even with an invalid engine name
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        var exitCode = await Program.RunAsync("--export-xml", _outputDir, "--engine", "nonexistent", specPath);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_outputDir, "system.gst")));
    }

    [Fact]
    public async Task ExportXml_CreatesOutputDirectory()
    {
        var nestedDir = Path.Combine(_outputDir, "nested", "subdir");
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        var exitCode = await Program.RunAsync("--export-xml", nestedDir, specPath);

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(nestedDir));
        Assert.True(File.Exists(Path.Combine(nestedDir, "system.gst")));
    }

    private static string FindSpec(string specId)
    {
        var specsDir = SpecLoader.FindRosterSpecsDirectory()
            ?? throw new InvalidOperationException("Could not find specs directory");
        // specId can be "category/id" or just "id"
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
