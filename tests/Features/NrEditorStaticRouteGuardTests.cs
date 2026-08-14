using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The directory-escape guard on the NR Editor's frozen static-file route
/// (<see cref="NrEditorStore.IsInsideRoot"/>). It decides whether a request is served or 403'd, and
/// until #311 it had no test at all.
/// </summary>
/// <remarks>
/// <para>
/// The casing case is the reason this file exists, and it is the one a fixed expectation gets wrong.
/// The guard used to be <c>StartsWith(root, OrdinalIgnoreCase)</c> — over-permissive on a
/// case-sensitive filesystem, where <c>/tmp/STATIC/x</c> is a different directory. Hard-coding
/// <c>Ordinal</c> instead would 403 legitimate requests on Windows, where those two strings name one
/// directory. So the correct answer is <b>the filesystem's</b>, and the expectation below is derived
/// from the filesystem rather than asserted — the same pattern <c>EngineSpecTests</c> uses for
/// environment-dependent behaviour, so this test stays falsifiable on both platforms instead of being
/// skipped on one.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class NrEditorStaticRouteGuardTests : IDisposable
{
    private readonly string _parent = Path.Combine(
        Path.GetTempPath(), "bsspec-route-guard-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _root;

    public NrEditorStaticRouteGuardTests()
    {
        _root = Path.Combine(_parent, "static") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(_root, "_nuxt"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_root, "_nuxt", "app.js"), "//");
        File.WriteAllText(Path.Combine(_parent, "secret.txt"), "not yours");
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("_nuxt/app.js")]
    public void ServedAssets_AreInside(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(NrEditorStore.IsInsideRoot(_root, full), $"'{relative}' should be served");
    }

    [Fact]
    public void ATraversalOutOfTheRoot_IsRefused()
    {
        var escaped = Path.GetFullPath(Path.Combine(_root, "..", "secret.txt"));

        Assert.Equal(Path.Combine(_parent, "secret.txt"), escaped);
        Assert.False(NrEditorStore.IsInsideRoot(_root, escaped));
    }

    /// <summary>
    /// A sibling whose name merely starts with the root's. The old <c>StartsWith</c> form survived this
    /// only because the root was normalised to end in a separator — worth pinning so a future
    /// simplification that drops the trailing separator fails here rather than in production.
    /// </summary>
    [Fact]
    public void ASiblingDirectorySharingAPrefix_IsRefused()
    {
        var sibling = Path.GetFullPath(Path.Combine(_parent, "static-extra", "app.js"));

        Assert.False(NrEditorStore.IsInsideRoot(_root, sibling));
    }

    /// <summary>
    /// <b>The #311 case.</b> Whether a case-variant path is inside the root is a property of the
    /// filesystem, so the filesystem is asked: if a file created as <c>static/index.html</c> is
    /// readable as <c>STATIC/index.html</c>, the two directories are one and the guard must let it
    /// through; if it is not, they are different directories and the guard must not.
    /// </summary>
    /// <remarks>
    /// Falsifiable on both platforms, in opposite directions: hard-code <c>OrdinalIgnoreCase</c> in the
    /// guard and this fails on Linux; hard-code <c>Ordinal</c> and it fails on Windows.
    /// </remarks>
    [Fact]
    public void ACaseVariantPath_FollowsTheFilesystemsOwnAnswer()
    {
        var shouted = Path.Combine(_parent, "STATIC", "index.html");
        var filesystemTreatsThemAsOneDirectory = File.Exists(shouted);

        Assert.Equal(
            filesystemTreatsThemAsOneDirectory,
            NrEditorStore.IsInsideRoot(_root, Path.GetFullPath(shouted)));
    }

    public void Dispose() => Directory.Delete(_parent, recursive: true);
}
