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

        await new BsUiDataStaging().StageDataFilesAsync(_outputDir, gameSystem.Id, xmlFiles);

        var stagedDir = Path.Combine(_outputDir, gameSystem.Id);
        Assert.True(Directory.Exists(stagedDir));
        Assert.True(File.Exists(Path.Combine(stagedDir, $"{gameSystem.Id}.gst")));
        Assert.True(File.Exists(Path.Combine(stagedDir, $"{catalogues[0].Id}.cat")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "index.bsi")));
    }

    /// <summary>
    /// One stager, two specs: the second spec's game system must be the only one left on disk.
    /// </summary>
    [Fact]
    public async Task StageDataFilesAsync_RemovesTheGameSystemItStagedForThePreviousSpec()
    {
        var staging = new BsUiDataStaging();

        await staging.StageDataFilesAsync(_outputDir, "system-a", FilesFor("system-a"));
        await staging.StageDataFilesAsync(_outputDir, "system-b", FilesFor("system-b"));

        Assert.False(Directory.Exists(Path.Combine(_outputDir, "system-a")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "system-b", "system-b.gst")));
        Assert.Equal(["system-b"], new DirectoryInfo(_outputDir).GetDirectories().Select(d => d.Name));
    }

    /// <summary>
    /// A second stager is a second engine: it must leave the first engine's staged data alone. Rules
    /// out a sweep ("delete every directory but the one I am staging"), which would have one engine
    /// deleting the other's data mid-run the first time two shared an
    /// <see cref="BsUiOptions.IsolatedHomePath"/>.
    /// </summary>
    [Fact]
    public async Task StageDataFilesAsync_LeavesAnotherStagersGameSystemAlone()
    {
        await new BsUiDataStaging().StageDataFilesAsync(_outputDir, "system-a", FilesFor("system-a"));
        await new BsUiDataStaging().StageDataFilesAsync(_outputDir, "system-b", FilesFor("system-b"));

        Assert.True(File.Exists(Path.Combine(_outputDir, "system-a", "system-a.gst")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "system-b", "system-b.gst")));
    }

    /// <summary>
    /// Retirement is best-effort: the current spec is staged whether or not the previous directory
    /// can be removed. The open handle stands in for BattleScribe, which holds its loaded data files
    /// open so Windows refuses the delete. Says nothing about whether <c>system-a</c> survives —
    /// POSIX unlinks an open file happily, so either assertion would pass on one platform only.
    /// </summary>
    [Fact]
    public async Task StageDataFilesAsync_StagesTheCurrentSpecEvenWhenTheOldDirectoryIsLocked()
    {
        var staging = new BsUiDataStaging();
        await staging.StageDataFilesAsync(_outputDir, "system-a", FilesFor("system-a"));

        using (File.Open(
            Path.Combine(_outputDir, "system-a", "system-a.gst"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            await staging.StageDataFilesAsync(_outputDir, "system-b", FilesFor("system-b"));
        }

        Assert.True(File.Exists(Path.Combine(_outputDir, "system-b", "system-b.gst")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "system-b", "index.bsi")));
    }

    private static (string FileName, string Content)[] FilesFor(string gameSystemId) =>
    [
        ($"{gameSystemId}.gst",
            $"""<gameSystem id="{gameSystemId}" name="{gameSystemId}" battleScribeVersion="2.03" revision="1"/>"""),
        ($"{gameSystemId}-cat.cat",
            $"""<catalogue id="{gameSystemId}-cat" name="{gameSystemId} cat" battleScribeVersion="2.03" revision="1"/>"""),
    ];

    [Fact]
    public void BuildIndexXml_ListsGameSystemAndCatalogueFiles()
    {
        var spec = SpecLoader.Load(FindSpec("cost/cost-hidden-limit-validation"));
        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);
        var xmlFiles = BuildXmlFiles(gameSystem, catalogues);

        var indexXml = BsUiDataStaging.BuildIndexXml(xmlFiles);

        Assert.Contains("dataIndex", indexXml);
        Assert.Contains($"filePath=\"{gameSystem.Id}.gst\"", indexXml);
        Assert.Contains($"filePath=\"{catalogues[0].Id}.cat\"", indexXml);
        Assert.Contains($"dataId=\"{gameSystem.Id}\"", indexXml);
        Assert.Contains($"dataId=\"{catalogues[0].Id}\"", indexXml);
    }

    [Fact]
    public void BuildIndexXml_ReadsIdsAndNamesFromRawFiles_GameSystemFirst()
    {
        // The dataSource path has no Protocol objects — these are real BattleScribe files, and
        // everything the index needs is already in them. Catalogue first on the way in, to prove
        // the game system is hoisted: BattleScribe reads the index in order and a catalogue whose
        // system has not been seen yet is not attached to one.
        var files = new (string FileName, string Content)[]
        {
            ("Some Faction.cat", """<catalogue id="cat-x" name="Some Faction" battleScribeVersion="2.03" revision="7"/>"""),
            ("The System.gst", """<gameSystem id="sys-x" name="The System" battleScribeVersion="2.03" revision="3"/>"""),
            ("README.md", "not xml at all"),
        };

        var indexXml = BsUiDataStaging.BuildIndexXml(files);

        Assert.Contains("""dataId="sys-x" dataName="The System" """, indexXml);
        Assert.Contains("""dataId="cat-x" dataName="Some Faction" """, indexXml);
        Assert.DoesNotContain("README", indexXml);
        Assert.True(
            indexXml.IndexOf("sys-x", StringComparison.Ordinal) < indexXml.IndexOf("cat-x", StringComparison.Ordinal),
            "the game system entry must precede the catalogue entries");

        // Revisions come off the files rather than being hardcoded to 1.
        Assert.Contains("""dataRevision="3""", indexXml);
        Assert.Contains("""dataRevision="7""", indexXml);
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
