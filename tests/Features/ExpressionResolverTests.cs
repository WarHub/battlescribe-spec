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
            Categories = new Dictionary<string, string>
            {
                ["cat-troops"] = "cat-node-1",
                ["cat-hq"] = "cat-node-2",
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
            Categories = new Dictionary<string, string> { ["cat-troops"] = "ig1q6t7" },
        });

        Assert.Equal("ig1q6t7", resolver.Resolve("${{ steps.add-patrol.categories.cat-troops }}"));
    }

    [Fact]
    public void Resolve_UnknownCategory_NamesTheKeysThatWereAvailable()
    {
        var resolver = WithOutputs(new ActionOutputs
        {
            Categories = new Dictionary<string, string>
            {
                ["cat-troops"] = "cat-node-1",
                ["cat-hq"] = "cat-node-2",
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
            Selections = new Dictionary<string, string> { ["se-lasgun"] = "sel-node-1" },
            Categories = new Dictionary<string, string> { ["cat-troops"] = "cat-node-1" },
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
            Categories = new Dictionary<string, string> { ["cat-troops"] = "cat-node-1" },
        });

        foreach (var (id, token) in resolver.BuildIdReverseIndex())
        {
            Assert.Equal(id, resolver.Resolve(token));
        }
    }
}
