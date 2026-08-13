using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <c>${{ steps.… }}</c> resolution against stored step outputs, exercised where the corpus cannot
/// yet reach it.
/// <para>
/// The <c>categories</c> map has no consumer in the spec corpus — <c>on:</c> becomes node-addressed
/// in #423, and until then nothing writes
/// <c>${{ steps.add-patrol.categories.cat-troops }}</c> in a YAML file. So the resolver's half of
/// #420 is proved here or it is not proved at all: a field path added to
/// <see cref="ExpressionResolver"/> and never exercised is indistinguishable from one that throws.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ExpressionResolverTests
{
    private static ExpressionResolver WithOutputs(ActionOutputs outputs, string stepId = "add-patrol")
    {
        var resolver = new ExpressionResolver();
        resolver.StoreOutputs(stepId, outputs);
        return resolver;
    }

    [Fact]
    public void Resolve_CategoriesPath_ReturnsTheCategoryNodeId()
    {
        var resolver = WithOutputs(new ActionOutputs
        {
            ForceId = "force-node-1",
            Categories = new Dictionary<string, List<string>>
            {
                ["cat-troops"] = ["cat-node-1"],
                ["cat-hq"] = ["cat-node-2"],
            },
        });

        Assert.Equal("cat-node-1", resolver.Resolve("${{ steps.add-patrol.categories.cat-troops }}"));
        Assert.Equal("cat-node-2", resolver.Resolve("${{ steps.add-patrol.categories.cat-hq }}"));
    }

    /// <summary>
    /// The category is keyed by its CATALOGUE entry id and resolves to a RUNTIME node id — the
    /// whole point of the map. A resolver that echoed its key back would satisfy a test that only
    /// checked "something came out".
    /// </summary>
    [Fact]
    public void Resolve_CategoriesPath_DoesNotReturnTheKeyItWasAskedFor()
    {
        var resolver = WithOutputs(new ActionOutputs
        {
            Categories = new Dictionary<string, List<string>> { ["cat-troops"] = ["ig1q6t7"] },
        });

        Assert.Equal("ig1q6t7", resolver.Resolve("${{ steps.add-patrol.categories.cat-troops }}"));
    }

    [Fact]
    public void Resolve_UnknownCategory_NamesTheKeysThatWereAvailable()
    {
        var resolver = WithOutputs(new ActionOutputs
        {
            Categories = new Dictionary<string, List<string>>
            {
                ["cat-troops"] = ["cat-node-1"],
                ["cat-hq"] = ["cat-node-2"],
            },
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve("${{ steps.add-patrol.categories.cat-elites }}"));

        // A spec that names a category its force never linked is a typo or a wrong step id, and
        // both are diagnosed by seeing what WAS there — the same courtesy the selections map does.
        Assert.Contains("cat-elites", ex.Message);
        Assert.Contains("categories map", ex.Message);
        Assert.Contains("cat-troops", ex.Message);
        Assert.Contains("cat-hq", ex.Message);
    }

    /// <summary>
    /// A step with no categories at all must fail the same way, not with a null dereference — this
    /// is every non-force-minting action.
    /// </summary>
    [Fact]
    public void Resolve_CategoryOnAStepThatMintedNoForce_FailsWithAnEmptyAvailableList()
    {
        var resolver = WithOutputs(new ActionOutputs { SelectionId = "sel-1" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve("${{ steps.add-patrol.categories.cat-troops }}"));

        Assert.Contains("cat-troops", ex.Message);
        Assert.Contains("Available: []", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownField_ListsCategoriesAmongTheSupportedPaths()
    {
        var resolver = WithOutputs(new ActionOutputs { ForceId = "force-node-1" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve("${{ steps.add-patrol.categoryId }}"));

        Assert.Contains("categories.<categoryEntryId>", ex.Message);
    }

    /// <summary>
    /// Snapshot-write templatization: an exported roster carries category node ids as
    /// <c>&lt;category id="…"&gt;</c>, and a snapshot that baked in one run's would fail on the
    /// next. The reverse index turns them back into the step reference that produced them.
    /// </summary>
    [Fact]
    public void BuildIdReverseIndex_MapsCategoryNodeIdsBackToTheirStepReference()
    {
        var resolver = WithOutputs(new ActionOutputs
        {
            ForceId = "force-node-1",
            Selections = new Dictionary<string, List<string>> { ["se-lasgun"] = ["sel-node-1"] },
            Categories = new Dictionary<string, List<string>> { ["cat-troops"] = ["cat-node-1"] },
        });

        var index = resolver.BuildIdReverseIndex();

        Assert.Equal("${{ steps.add-patrol.forceId }}", index["force-node-1"]);
        Assert.Equal("${{ steps.add-patrol.selections.se-lasgun }}", index["sel-node-1"]);
        Assert.Equal("${{ steps.add-patrol.categories.cat-troops }}", index["cat-node-1"]);
    }

    /// <summary>
    /// The round trip the templatization depends on: whatever token the index writes into a
    /// snapshot must resolve back to the id it replaced.
    /// </summary>
    [Fact]
    public void BuildIdReverseIndex_ProducesTokensThatResolveBackToTheSameIds()
    {
        var resolver = WithOutputs(new ActionOutputs
        {
            ForceId = "force-node-1",
            Categories = new Dictionary<string, List<string>> { ["cat-troops"] = ["cat-node-1"] },
        });

        foreach (var (id, token) in resolver.BuildIdReverseIndex())
        {
            Assert.Equal(id, resolver.Resolve(token));
        }
    }

    // ── Siblings of one entry (#428) ─────────────────────────────────

    private static ExpressionResolver WithTwoUnitAs() => WithOutputs(new ActionOutputs
    {
        ForceId = "force-node-1",
        Selections = new Dictionary<string, List<string>> { ["se-unit-a"] = ["sel-node-1", "sel-node-2"] },
        Categories = new Dictionary<string, List<string>> { ["cat-troops"] = ["cat-node-1", "cat-node-2"] },
    });

    /// <summary>
    /// The hard constraint the shape change had to hold: every reference written before <c>[n]</c>
    /// existed keeps meaning what it meant. The bare form is the FIRST node, so the ~50 assertions
    /// #424 migrated do not shift onto a sibling.
    /// </summary>
    [Fact]
    public void Resolve_BareKey_IsTheFirstNode_NotTheLast()
    {
        var resolver = WithTwoUnitAs();

        Assert.Equal("sel-node-1", resolver.Resolve("${{ steps.add-patrol.selections.se-unit-a }}"));
        Assert.Equal("cat-node-1", resolver.Resolve("${{ steps.add-patrol.categories.cat-troops }}"));

        // …and is exactly what [0] says, so the two spellings are the same address.
        Assert.Equal(
            resolver.Resolve("${{ steps.add-patrol.selections.se-unit-a }}"),
            resolver.Resolve("${{ steps.add-patrol.selections.se-unit-a[0] }}"));
    }

    [Fact]
    public void Resolve_IndexedKey_NamesTheNthSiblingOfThatEntry()
    {
        var resolver = WithTwoUnitAs();

        Assert.Equal("sel-node-2", resolver.Resolve("${{ steps.add-patrol.selections.se-unit-a[1] }}"));
        Assert.Equal("cat-node-2", resolver.Resolve("${{ steps.add-patrol.categories.cat-troops[1] }}"));
    }

    /// <summary>
    /// Loud, not null. A spec that asks for the third of two has stopped describing the roster it
    /// runs against, and the count is what tells its author which sibling they meant.
    /// </summary>
    [Fact]
    public void Resolve_IndexPastTheEnd_SaysHowManyNodesThereAre()
    {
        var resolver = WithTwoUnitAs();

        var ex = Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve("${{ steps.add-patrol.selections.se-unit-a[2] }}"));

        Assert.Contains("2 node(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("index 2 is out of range", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0..1", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("se-unit-a[]")]
    [InlineData("se-unit-a[x]")]
    [InlineData("se-unit-a[-1]")]
    [InlineData("se-unit-a[1")]
    [InlineData("[0]")]
    public void Resolve_MalformedIndex_IsRejectedRatherThanReadAsAKey(string path)
    {
        var resolver = WithTwoUnitAs();

        var ex = Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve($"${{{{ steps.add-patrol.selections.{path} }}}}"));

        Assert.Contains("malformed sibling index", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Snapshot stability across the shape change: the first node still writes back as the bare key,
    /// so a snapshot templatized before siblings were addressable is byte-identical to one written
    /// after. Only the siblings that used to be silently dropped gain a token.
    /// </summary>
    [Fact]
    public void BuildIdReverseIndex_WritesTheFirstNodeBare_AndItsSiblingsIndexed()
    {
        var index = WithTwoUnitAs().BuildIdReverseIndex();

        Assert.Equal("${{ steps.add-patrol.selections.se-unit-a }}", index["sel-node-1"]);
        Assert.Equal("${{ steps.add-patrol.selections.se-unit-a[1] }}", index["sel-node-2"]);
        Assert.Equal("${{ steps.add-patrol.categories.cat-troops }}", index["cat-node-1"]);
        Assert.Equal("${{ steps.add-patrol.categories.cat-troops[1] }}", index["cat-node-2"]);
    }
}
