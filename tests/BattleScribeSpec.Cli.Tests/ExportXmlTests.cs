using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Tests for the export-xml subcommand of the bs-spec CLI.
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

        var exitCode = await Program.RunAsync("export-xml", specPath, _outputDir);

        Assert.Equal(0, exitCode);
        var specDir = Path.Combine(_outputDir, "cost-hidden-limit-validation");
        Assert.True(File.Exists(Path.Combine(specDir, "cost-hidden-limit-validation.gst")));
        Assert.True(File.Exists(Path.Combine(specDir, "cost-hidden-limit-validation.cat")));
    }

    [Fact]
    public async Task ExportXml_GstContainsValidXml()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("export-xml", specPath, _outputDir);

        var gstXml = File.ReadAllText(Path.Combine(_outputDir, "cost-hidden-limit-validation", "cost-hidden-limit-validation.gst"));
        Assert.Contains("<?xml", gstXml);
        Assert.Contains("gameSystem", gstXml);
        Assert.Contains("cost-hidden-limit-validation", gstXml);
        Assert.Contains("costType", gstXml);
    }

    [Fact]
    public async Task ExportXml_CatContainsValidXml()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("export-xml", specPath, _outputDir);

        var catXml = File.ReadAllText(Path.Combine(_outputDir, "cost-hidden-limit-validation", "cost-hidden-limit-validation.cat"));
        Assert.Contains("<?xml", catXml);
        Assert.Contains("catalogue", catXml);
        Assert.Contains("cat-1", catXml);
        Assert.Contains("selectionEntry", catXml);
    }

    [Fact]
    public async Task ExportXml_GstIsDeserializable()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("export-xml", specPath, _outputDir);

        var gstXml = File.ReadAllText(Path.Combine(_outputDir, "cost-hidden-limit-validation", "cost-hidden-limit-validation.gst"));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(gstXml));
        var gs = stream.DeserializeGamesystem()!;
        Assert.Equal("cost-hidden-limit-validation", gs.Id);
        Assert.Equal("cost-hidden-limit-validation", gs.Name);
        Assert.Equal(2, gs.CostTypes.Count);
    }

    [Fact]
    public async Task ExportXml_CatIsDeserializable()
    {
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        await Program.RunAsync("export-xml", specPath, _outputDir);

        var catXml = File.ReadAllText(Path.Combine(_outputDir, "cost-hidden-limit-validation", "cost-hidden-limit-validation.cat"));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(catXml));
        var cat = stream.DeserializeCatalogue()!;
        Assert.Equal("cat-1", cat.Id);
        Assert.Equal("cost-hidden-limit-validation", cat.Name);
        Assert.Single(cat.SelectionEntries);
    }

    [Fact]
    public async Task ExportXml_ComplexSpec_ProducesCorrectFileCount()
    {
        var specPath = FindSpec("protocol/protocol-kitchen-sink");

        var exitCode = await Program.RunAsync("export-xml", specPath, _outputDir);

        Assert.Equal(0, exitCode);
        var specDir = Path.Combine(_outputDir, "protocol-kitchen-sink");
        var gstFiles = Directory.GetFiles(specDir, "*.gst");
        var catFiles = Directory.GetFiles(specDir, "*.cat");
        Assert.Single(gstFiles);
        Assert.True(catFiles.Length >= 1, "Expected at least one .cat file");
    }

    [Fact]
    public async Task ExportXml_RequiresNoEngine()
    {
        // export-xml exposes no --engine/--ui options at all — it never touches an engine.
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        var exitCode = await Program.RunAsync("export-xml", specPath, _outputDir);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_outputDir, "cost-hidden-limit-validation", "cost-hidden-limit-validation.gst")));
    }

    [Fact]
    public async Task ExportXml_CreatesOutputDirectory()
    {
        var nestedDir = Path.Combine(_outputDir, "nested", "subdir");
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        var exitCode = await Program.RunAsync("export-xml", specPath, nestedDir);

        Assert.Equal(0, exitCode);
        var specDir = Path.Combine(nestedDir, "cost-hidden-limit-validation");
        Assert.True(Directory.Exists(specDir));
        Assert.True(File.Exists(Path.Combine(specDir, "cost-hidden-limit-validation.gst")));
    }

    [Fact]
    public async Task ExportXml_RejectsUnknownOptions()
    {
        // Under subcommands, options that don't apply to export-xml (e.g. --engine, --probe)
        // are parse errors rather than being silently ignored.
        var specPath = FindSpec("cost/cost-hidden-limit-validation");

        var exitCode = await Program.RunAsync("export-xml", specPath, _outputDir, "--engine", "nonexistent");

        Assert.NotEqual(0, exitCode);
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
