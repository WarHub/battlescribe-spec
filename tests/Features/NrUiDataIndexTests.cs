using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <see cref="NrUiDataIndex"/> — what the NR UI driver reads out of the <c>.gst</c>/<c>.cat</c> it hands
/// NewRecruit, on both setup paths. Offline: no browser, just the XML.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NrUiDataIndexTests
{
    private static IReadOnlyList<(string FileName, string Content)> Generate(
        ProtocolGameSystem gameSystem, params ProtocolCatalogue[] catalogues)
    {
        var files = new List<(string FileName, string Content)>
        {
            ($"{gameSystem.Id}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)),
        };
        files.AddRange(CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues));
        return files;
    }

    private static readonly ProtocolGameSystem GameSystem = new()
    {
        Id = "gs-idx",
        Name = "Index System",
        ForceEntries =
        [
            new ProtocolForceEntry
            {
                Id = "fe-outer",
                Name = "Outer Detachment",
                ForceEntries = [new ProtocolForceEntry { Id = "fe-inner", Name = "Inner Detachment" }],
            },
        ],
        SharedSelectionEntries = [new ProtocolSelectionEntry { Id = "sse-gs", Name = "System Shared", Type = "upgrade" }],
    };

    [Fact]
    public void Names_EveryEntryGroupAndForceEntry_WhereverItIsDeclared()
    {
        var catalogue = new ProtocolCatalogue
        {
            Id = "cat-idx",
            Name = "Index Catalogue",
            GameSystemId = GameSystem.Id,
            ForceEntries = [new ProtocolForceEntry { Id = "fe-cat", Name = "Catalogue Detachment" }],
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-unit",
                    Name = "Squad",
                    Type = "unit",
                    SelectionEntryGroups =
                    [
                        new ProtocolSelectionEntryGroup
                        {
                            Id = "seg-weapons",
                            Name = "Weapons",
                            SelectionEntries = [new ProtocolSelectionEntry { Id = "se-gun", Name = "Gun", Type = "upgrade" }],
                        },
                    ],
                },
            ],
        };

        var index = NrUiDataIndex.FromFiles(Generate(GameSystem, catalogue));

        Assert.Equal("gs-idx", index.GameSystemId);
        Assert.Equal("Index System", index.GameSystemName);
        Assert.Equal("Outer Detachment", index.ForceEntryName("fe-outer"));
        Assert.Equal("Inner Detachment", index.ForceEntryName("fe-inner"));
        Assert.Equal("Catalogue Detachment", index.ForceEntryName("fe-cat"));
        Assert.Equal("fe-unknown", index.ForceEntryName("fe-unknown"));

        foreach (var (id, name) in new[]
        {
            ("se-unit", "Squad"), ("seg-weapons", "Weapons"), ("se-gun", "Gun"),
            // The game system's own shared entries: the container the Protocol-model walk once missed.
            ("sse-gs", "System Shared"),
        })
        {
            Assert.True(index.TryGetEntryName(id, out var actual), $"'{id}' is not indexed.");
            Assert.Equal(name, actual);
        }

        Assert.False(index.TryGetEntryName("se-unknown", out _));
    }

    /// <summary>
    /// A link reads as its own name; an unnamed one as its target's — and that holds when the target
    /// is declared after the link, which the Protocol-model walk this replaced got wrong (it handed
    /// back the target's raw id, a label no NR panel carries).
    /// </summary>
    [Fact]
    public void Links_TakeTheirOwnName_ElseTheTargetsWhereverItIsDeclared()
    {
        var catalogue = new ProtocolCatalogue
        {
            Id = "cat-links",
            Name = "Links Catalogue",
            GameSystemId = GameSystem.Id,
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-holder",
                    Name = "Holder",
                    Type = "unit",
                    EntryLinks =
                    [
                        new ProtocolEntryLink { Id = "el-unnamed", Name = "", TargetId = "sse-late", Type = "selectionEntry" },
                        new ProtocolEntryLink { Id = "el-named", Name = "Custom Label", TargetId = "sse-late", Type = "selectionEntry" },
                    ],
                },
            ],
            SharedSelectionEntries = [new ProtocolSelectionEntry { Id = "sse-late", Name = "Late Target", Type = "upgrade" }],
        };

        var index = NrUiDataIndex.FromFiles(Generate(GameSystem, catalogue));

        Assert.True(index.TryGetEntryName("el-unnamed", out var unnamed));
        Assert.Equal("Late Target", unnamed);
        Assert.True(index.TryGetEntryName("el-named", out var named));
        Assert.Equal("Custom Label", named);
    }

    [Fact]
    public void LoadOrder_IsGameSystem_ThenPrimary_ThenLibraryCatalogues_ThenEverythingElse()
    {
        var library = new ProtocolCatalogue { Id = "cat-lib", Name = "Library", GameSystemId = GameSystem.Id, Library = true };
        var primary = new ProtocolCatalogue { Id = "cat-main", Name = "Main", GameSystemId = GameSystem.Id };
        var generated = Generate(GameSystem, library, primary);
        List<(string FileName, string Content)> files = [("README.md", "# not xml"), .. generated];

        var index = NrUiDataIndex.FromFiles(files);

        Assert.Equal(
            ["gs-idx.gst", "cat-main.cat", "cat-lib.cat", "README.md"],
            index.LoadOrder.Select(f => f.FileName));
        Assert.Equal(["cat-main", "cat-lib"], index.Catalogues.Select(c => c.Id));
        Assert.True(index.Catalogues[1].Library);

        // The Create List dialog: the catalogue asked for, else the first that is not a library.
        Assert.Equal("Library", index.CatalogueNameFor("cat-lib"));
        Assert.Equal("Main", index.CatalogueNameFor(null));
        Assert.Equal("Main", index.CatalogueNameFor("cat-not-loaded"));
    }

    /// <summary>
    /// Files written by an editor carry the BattleScribe schema namespace; names are read by local
    /// name, so they index the same.
    /// </summary>
    [Fact]
    public void NamespacedXml_IndexesTheSame()
    {
        const string gst = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gameSystem id="gs-ns" name="Namespaced" revision="1" battleScribeVersion="2.03"
                        xmlns="http://www.battlescribe.net/schema/gameSystemSchema">
              <forceEntries><forceEntry id="fe-ns" name="Namespaced Force" hidden="false"/></forceEntries>
            </gameSystem>
            """;
        const string cat = """
            <?xml version="1.0" encoding="UTF-8"?>
            <catalogue id="cat-ns" name="Namespaced Catalogue" library="false" gameSystemId="gs-ns"
                       revision="1" battleScribeVersion="2.03"
                       xmlns="http://www.battlescribe.net/schema/catalogueSchema">
              <selectionEntries><selectionEntry id="se-ns" name="Namespaced Unit" type="unit" hidden="false"/></selectionEntries>
            </catalogue>
            """;

        var index = NrUiDataIndex.FromFiles([("Namespaced.gst", gst), ("Namespaced.cat", cat)]);

        Assert.Equal("gs-ns", index.GameSystemId);
        Assert.Equal("Namespaced Force", index.ForceEntryName("fe-ns"));
        Assert.True(index.TryGetEntryName("se-ns", out var name));
        Assert.Equal("Namespaced Unit", name);
        Assert.Equal("Namespaced Catalogue", index.CatalogueNameFor(null));
    }

    [Theory]
    [InlineData("no game system", "no game system")]
    [InlineData("two game systems", "two game systems")]
    [InlineData("malformed", "'broken.cat' is not well-formed XML")]
    [InlineData("wrong root", "'odd.cat' has a <roster> root")]
    public void UnreadableFileSets_AreRefused_NamingTheProblem(string shape, string expected)
    {
        var gst = ("gs-idx.gst", CatXmlGenerator.GenerateGameSystemXml(GameSystem));
        IReadOnlyList<(string FileName, string Content)> files = shape switch
        {
            "no game system" => [("README.md", "text")],
            "two game systems" => [gst, ("other.gst", CatXmlGenerator.GenerateGameSystemXml(GameSystem))],
            "malformed" => [gst, ("broken.cat", "<catalogue id=")],
            "wrong root" => [gst, ("odd.cat", "<roster id=\"r\"/>")],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var ex = Assert.Throws<InvalidDataException>(() => NrUiDataIndex.FromFiles(files));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}
