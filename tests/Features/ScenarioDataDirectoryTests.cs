using BattleScribeSpec.Scenario;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The scenario's data directory names each file from the XML's own root — what the data editor wrote —
/// and keeps first-write order however often a file is saved again.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ScenarioDataDirectoryTests
{
    [Fact]
    public void Files_AreNamedByRootIdAndKind()
    {
        var directory = new ScenarioDataDirectory();
        directory.Write("""<gameSystem id="gs-1" name="GS"/>""");
        directory.Write("""<catalogue id="cat-1" name="Cat"/>""");

        Assert.Equal(["gs-1.gst", "cat-1.cat"], directory.Files.Select(f => f.FileName));
        Assert.Equal(["cat-1"], directory.CatalogueIds);
    }

    [Fact]
    public void Rewrite_ReplacesInPlace()
    {
        var directory = new ScenarioDataDirectory();
        directory.Write("""<gameSystem id="gs-1"/>""");
        directory.Write("""<catalogue id="cat-1" revision="1"/>""");
        directory.Write("""<catalogue id="cat-2"/>""");
        directory.Write("""<catalogue id="cat-1" revision="2"/>""");

        Assert.Equal(["gs-1.gst", "cat-1.cat", "cat-2.cat"], directory.Files.Select(f => f.FileName));
        Assert.Contains("revision=\"2\"", directory.Files[1].Content, StringComparison.Ordinal);
        Assert.Equal(["cat-1", "cat-2"], directory.CatalogueIds);
    }

    [Fact]
    public void Write_ReplacingTheOpenedFile_TakesItsPlace()
    {
        var directory = new ScenarioDataDirectory();
        directory.Write("""<gameSystem id="gs-1"/>""");
        directory.Write("""<catalogue id="cat-1"/>""");
        directory.Write("""<catalogue id="cat-2"/>""");
        directory.Write("""<catalogue id="cat-9"/>""", replaces: "cat-1");

        Assert.Equal(["gs-1.gst", "cat-9.cat", "cat-2.cat"], directory.Files.Select(f => f.FileName));
    }

    [Fact]
    public void Write_RenamingOntoAnotherFile_IsRefused()
    {
        var directory = new ScenarioDataDirectory();
        directory.Write("""<catalogue id="cat-1"/>""");
        directory.Write("""<catalogue id="cat-2"/>""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => directory.Write("""<catalogue id="cat-2"/>""", replaces: "cat-1"));

        Assert.Contains("'cat-1' as 'cat-2'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["cat-1.cat", "cat-2.cat"], directory.Files.Select(f => f.FileName)); // refused, not half-applied
    }

    [Fact]
    public void Namespaced_RootsAreRecognised()
    {
        var directory = new ScenarioDataDirectory();
        directory.Write("""<?xml version="1.0" encoding="UTF-8"?><catalogue id="cat-1" xmlns="http://www.battlescribe.net/schema/catalogueSchema"/>""");

        Assert.Equal("cat-1.cat", Assert.Single(directory.Files).FileName);
    }

    [Fact]
    public void UnknownRoot_IsNamed()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScenarioDataDirectory().Write("""<roster id="r-1"/>"""));

        Assert.Contains("<roster>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingId_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScenarioDataDirectory().Write("""<catalogue name="Cat"/>"""));

        Assert.Contains("no id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedXml_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScenarioDataDirectory().Write("<catalogue id="));

        Assert.Contains("not well-formed", ex.Message, StringComparison.Ordinal);
    }
}
