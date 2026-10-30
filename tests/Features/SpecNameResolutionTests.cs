namespace BattleScribeSpec.Tests;

/// <summary>
/// <see cref="SpecLoader.ResolveRosterSpec"/>, <see cref="SpecLoader.ResolveGameDataSpec"/> and
/// <see cref="SpecLoader.ResolveScenarioSpec"/>: the name → file index every spec-carrying theory row is
/// resolved through (see <see cref="TheoryRowIdentityTests"/> for why rows carry names).
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecNameResolutionTests
{
    [Fact]
    public void EverySpecOnDisk_ResolvesToItsOwnFile()
    {
        var resolved = 0;
        foreach (var (dir, discover, resolve) in new (string?, Func<string, IEnumerable<(string Path, string Id, string Category)>>, Func<string, string>)[]
        {
            (SpecLoader.FindRosterSpecsDirectory(), SpecLoader.DiscoverSpecs, SpecLoader.ResolveRosterSpec),
            (SpecLoader.FindGameDataSpecsDirectory(), SpecLoader.DiscoverGameDataSpecs, SpecLoader.ResolveGameDataSpec),
            (SpecLoader.FindScenarioSpecsDirectory(), SpecLoader.DiscoverScenarioSpecs, SpecLoader.ResolveScenarioSpec),
        })
        {
            Assert.NotNull(dir);
            Assert.NotEmpty(discover(dir));
            foreach (var (path, id, category) in discover(dir))
            {
                Assert.Equal(path, resolve(SpecLoader.SpecName(category, id)));
                resolved++;
            }
        }

        Assert.True(resolved > 0, "No spec was found to resolve, so this test checked nothing.");
    }

    /// <summary>
    /// A real roster spec, whose near-misses below must not resolve. Spelled here rather than in the
    /// rows: an <c>[InlineData]</c> argument is rendered into the test's name, and a real spec's
    /// name there would make AGENTS.md's one-spec recipe, <c>--filter "DisplayName~&lt;id&gt;"</c>, run
    /// these rows alongside that spec's own.
    /// </summary>
    private const string ARealRosterSpec = "roundtrip/roundtrip-load-roster";

    /// <summary>How a name can miss the spec it means.</summary>
    public enum Misspelling
    {
        /// <summary>A name no spec has.</summary>
        NoSuchSpec,

        /// <summary>A real spec's id without its category.</summary>
        IdWithoutCategory,

        /// <summary>A real spec's file name — its name with <c>.yaml</c>.</summary>
        FileName,
    }

    [Theory]
    [InlineData(Misspelling.NoSuchSpec)]
    [InlineData(Misspelling.IdWithoutCategory)]
    [InlineData(Misspelling.FileName)]
    public void AnUnknownName_ThrowsNamingTheDirectorySearched(Misspelling misspelling)
    {
        _ = SpecLoader.ResolveRosterSpec(ARealRosterSpec); // the near-misses are near something real
        var name = misspelling switch
        {
            Misspelling.NoSuchSpec => "no-such-category/no-such-spec",
            Misspelling.IdWithoutCategory => ARealRosterSpec[(ARealRosterSpec.IndexOf('/', StringComparison.Ordinal) + 1)..],
            Misspelling.FileName => ARealRosterSpec + ".yaml",
            _ => throw new ArgumentOutOfRangeException(nameof(misspelling)),
        };

        var ex = Assert.Throws<ArgumentException>(() => SpecLoader.ResolveRosterSpec(name));
        Assert.Contains($"'{name}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SpecLoader.FindRosterSpecsDirectory()!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("'category/id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARosterName_IsNotResolvedAsGameData()
    {
        // The two domains are indexed separately: a roster spec's name must not find a GameData
        // file of the same name, or the wrong lane would run it.
        _ = SpecLoader.ResolveRosterSpec(ARealRosterSpec);
        Assert.Throws<ArgumentException>(() => SpecLoader.ResolveGameDataSpec(ARealRosterSpec));
    }

    /// <summary>
    /// The scenario index is a third one, and neither of the others reaches into it: a scenario spec's name
    /// finds no roster or GameData file, and a roster spec's name finds no scenario — so a scenario lane
    /// runs scenarios and nothing else. The unknown-name message names the scenario directory.
    /// </summary>
    [Fact]
    public void TheScenarioIndex_IsItsOwn()
    {
        var aRealScenario = SpecLoader.DiscoverScenarioSpecs(SpecLoader.FindScenarioSpecsDirectory()!)
            .Select(static s => SpecLoader.SpecName(s.Category, s.Id))
            .First();
        _ = SpecLoader.ResolveScenarioSpec(aRealScenario);

        Assert.Throws<ArgumentException>(() => SpecLoader.ResolveRosterSpec(aRealScenario));
        Assert.Throws<ArgumentException>(() => SpecLoader.ResolveGameDataSpec(aRealScenario));
        var ex = Assert.Throws<ArgumentException>(() => SpecLoader.ResolveScenarioSpec(ARealRosterSpec));
        Assert.Contains(SpecLoader.FindScenarioSpecsDirectory()!, ex.Message, StringComparison.Ordinal);
    }
}
