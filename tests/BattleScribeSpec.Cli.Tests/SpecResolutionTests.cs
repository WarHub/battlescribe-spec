namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Tests spec discovery: engine-domain inference from a spec path/id, and that the
/// loader resolves all advertised id forms (bare id and category/id) anchored at
/// the roster specs directory.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecResolutionTests
{
    [Theory]
    [InlineData("specs/gamedata/entry/add-entry-basic.yaml", "gamedata")]
    [InlineData("specs/roster/selection/selection-page.yaml", "roster")]
    [InlineData("plain-id-with-no-hint", "roster")]
    [InlineData("-", "roster")]
    public void InferEngineType_UsesPathHints(string input, string expected)
    {
        Assert.Equal(expected, SpecLoading.InferEngineType(input));
    }

    [Fact]
    public void InferEngineType_DetectsGamedataByBareId()
    {
        // A bare gamedata spec id (no path hint) is resolved against specs/gamedata.
        Assert.Equal("gamedata", SpecLoading.InferEngineType("add-entry-basic"));
    }

    /// <summary>
    /// <b>A directory the spec merely lives under does not get to name its domain.</b> Domain
    /// inference used to substring-scan the ABSOLUTE path, which carries every directory above the
    /// checkout — so a repo cloned into <c>gamedata-tools/</c> (a CI workspace, an agent worktree, a
    /// home directory) routed every roster spec in it to the gamedata engine. Nothing about those
    /// directories is about the spec.
    /// </summary>
    /// <remarks>
    /// Falsifiable: restore the old tail — <c>Path.GetFullPath(input).ToLowerInvariant()</c> scanned
    /// with <c>Contains("gamedata")</c> — and this returns "gamedata".
    /// </remarks>
    [Fact]
    public void InferEngineType_IgnoresAnAncestorDirectoryThatMerelyContainsTheWord()
    {
        using var workspace = new TempWorkspace("gamedata-tools");
        var spec = workspace.WriteSpec("some-roster-spec.yaml");

        Assert.Equal("roster", SpecLoading.InferEngineType(spec));
    }

    /// <summary>
    /// The other half: a spec outside the repo's <c>specs/</c> tree still reads its domain from the
    /// path, so the fix above must not have flattened the hint into a constant. A <em>segment</em>
    /// named gamedata is a statement of intent and is honoured, on any platform and in any casing —
    /// unlike the containment check, this is user input, not a filesystem lookup.
    /// </summary>
    [Theory]
    [InlineData("gamedata")]
    [InlineData("GameData")]
    public void InferEngineType_HonoursAGamedataSegment_OutsideTheSpecsTree(string segment)
    {
        using var workspace = new TempWorkspace(segment);
        var spec = workspace.WriteSpec("some-spec.yaml");

        Assert.Equal("gamedata", SpecLoading.InferEngineType(spec));
    }

    /// <summary>
    /// A spec that IS in the repo's tree is classified by containment rather than by spelling, and
    /// the absolute form must agree with the relative one — the absolute path is where the ancestor
    /// noise lives, so it is the form that used to be able to disagree.
    /// </summary>
    [Fact]
    public void InferEngineType_AgreesWithItselfOnRelativeAndAbsoluteFormsOfARepoSpec()
    {
        var rosterDir = SpecLoader.FindRosterSpecsDirectory();
        Assert.NotNull(rosterDir);
        var spec = Directory.EnumerateFiles(rosterDir, "*.yaml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();

        Assert.Equal("roster", SpecLoading.InferEngineType(spec));
        Assert.Equal("roster", SpecLoading.InferEngineType(Path.GetFullPath(spec)));
    }

    /// <summary>A throwaway directory named by its caller, so the NAME is the thing under test.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _root;

        public TempWorkspace(string directoryName)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "bsspec-infer-" + Guid.NewGuid().ToString("N")[..8],
                directoryName);
            Directory.CreateDirectory(_root);
        }

        public string WriteSpec(string fileName)
        {
            var path = Path.Combine(_root, fileName);
            File.WriteAllText(path, "id: irrelevant\n");
            return path;
        }

        public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
    }

    [Theory]
    [InlineData("cost-hidden-limit-validation")]            // bare id
    [InlineData("cost/cost-hidden-limit-validation")]       // category/id
    public void LoadSpec_ResolvesAdvertisedIdForms(string specId)
    {
        var spec = SpecLoading.LoadSpec(specId);
        Assert.Equal("cost-hidden-limit-validation", spec.Id);
    }

    [Fact]
    public void LoadSpec_ThrowsForUnknownSpec()
    {
        Assert.Throws<FileNotFoundException>(() => SpecLoading.LoadSpec("no-such-spec-anywhere"));
    }
}
