namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public sealed class DataSourceResolverTests
{
    [Fact]
    public void Resolve_LocalProvider_ReturnsDirectoryPath()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "MySystem.gst"), "<gst />");
        File.WriteAllText(Path.Combine(tempDir.Path, "MyCatalogue.cat"), "<cat />");

        var resolver = new DataSourceResolver();
        var resolved = resolver.Resolve($"local:{tempDir.Path}");

        Assert.Equal(tempDir.Path, resolved);
    }

    [Fact]
    public void FindGameSystem_AndCatalogue_MatchCaseInsensitiveContains()
    {
        using var tempDir = new TempDirectory();
        var gstPath = Path.Combine(tempDir.Path, "Warhammer 40,000.gst");
        var catPath = Path.Combine(tempDir.Path, "Space Marines.cat");
        File.WriteAllText(gstPath, "<gst />");
        File.WriteAllText(catPath, "<cat />");

        var resolver = new DataSourceResolver();

        Assert.Equal(gstPath, DataSourceResolver.FindGameSystem(tempDir.Path, "warHAMMER"));
        Assert.Equal(catPath, DataSourceResolver.FindCatalogue(tempDir.Path, "MARINES"));
    }

    [Fact]
    public void Resolve_InvalidProvider_Throws()
    {
        var resolver = new DataSourceResolver();
        var uri = new DataSourceUri("invalid", "org", "repo", null);

        Assert.Throws<NotSupportedException>(() => resolver.Resolve(uri));
    }

    [Fact]
    public void Resolve_LocalProvider_EmptyOrMissingDirectory_Throws()
    {
        var resolver = new DataSourceResolver();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => resolver.Resolve(new DataSourceUri("local", "", "", null)));
        Assert.Throws<DirectoryNotFoundException>(() => resolver.Resolve($"local:{missing}"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bsspec-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
