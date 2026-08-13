using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The acceptance criterion for #420, stated once and asserted on every engine that can run
/// offline: <b>the id a spec can name and the id in the state model are the same id</b>.
/// <para>
/// A force's category ids arrive by two independent routes — the <c>categories</c> map returned by
/// <c>addForce</c>, and <c>CategoryState.Id</c> read back from the roster — and every lane builds
/// them from different accessors (<c>Category.getId()</c> in-process, a Java agent payload through
/// the desktop UI, <c>uid</c> in a browser). Either route can look healthy alone: a map full of
/// catalogue entry ids resolves to something, and a state id nothing cross-checks is just a string.
/// Only comparing them proves an <c>on: category &lt;nodeId&gt;</c> assertion could ever match.
/// </para>
/// <para>
/// The corpus cannot make this claim yet — nothing consumes a category node id until #423 flips
/// <c>on:</c> — so it is made here, or not at all.
/// </para>
/// </summary>
internal static class CategoryNodeIdContract
{
    /// <summary>Two categories, so a map with one entry cannot pass by accident.</summary>
    internal const string ForceEntryId = "fe-1";
    internal const string CatalogueId = "cat-1";

    internal static ProtocolGameSystem GameSystem() => new()
    {
        Id = "category-node-id-gs",
        Name = "Category Node Id",
        CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts" }],
        CategoryEntries =
        [
            new ProtocolCategoryEntry { Id = "cat-troops", Name = "Troops" },
            new ProtocolCategoryEntry { Id = "cat-hq", Name = "HQ" },
        ],
        ForceEntries =
        [
            new ProtocolForceEntry
            {
                Id = ForceEntryId,
                Name = "Detachment",
                CategoryLinks =
                [
                    new ProtocolCategoryLink { Id = "cl-troops", TargetId = "cat-troops", Name = "Troops" },
                    new ProtocolCategoryLink { Id = "cl-hq", TargetId = "cat-hq", Name = "HQ" },
                ],
            },
        ],
    };

    internal static ProtocolCatalogue[] Catalogues() =>
    [
        new()
        {
            Id = CatalogueId,
            Name = "Category Node Id Catalogue",
            GameSystemId = "category-node-id-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-1",
                    Name = "Trooper",
                    Type = "unit",
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink
                        {
                            Id = "cl-se-troops", TargetId = "cat-troops", Name = "Troops", Primary = true,
                        },
                    ],
                },
            ],
        },
    ];

    /// <summary>
    /// Asserts the contract for one engine: the step output names category nodes, and each one is
    /// the node the roster state reports under that catalogue entry.
    /// </summary>
    internal static void AssertOutputsMatchState(
        string engineName, ActionOutputs outputs, RosterState state, ITestOutputHelper output)
    {
        Assert.NotNull(outputs.ForceId);

        var force = Assert.Single(state.Forces, f => f.Id == outputs.ForceId);
        var categories = force.Categories ?? [];
        Assert.NotEmpty(categories);

        // What the state model says, in the shape the step output claims to be in. Built the same
        // way the engines build theirs (first node wins a repeated entry id) so a disagreement is
        // about the IDS, not about how the map was folded.
        var fromState = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            if (category is { EntryId: { Length: > 0 } entryId, Id: { Length: > 0 } id })
            {
                fromState.TryAdd(entryId, id);
            }
        }

        output.WriteLine($"[{engineName}] force {outputs.ForceId}");
        foreach (var category in categories)
        {
            output.WriteLine(
                $"[{engineName}]   state: entryId={category.EntryId ?? "(null)"} " +
                $"id={category.Id ?? "(null)"} name={category.Name}");
        }

        foreach (var (entryId, id) in outputs.Categories ?? [])
        {
            output.WriteLine($"[{engineName}]   output: categories.{entryId} = {id}");
        }

        // Every category the force owns has a node id. This is the claim that fails first if a
        // lane's plumbing drops the field — the NR DTO silently discards an unmapped one.
        Assert.All(categories, c => Assert.False(string.IsNullOrEmpty(c.Id),
            $"[{engineName}] category '{c.Name}' (entryId {c.EntryId ?? "null"}) has no node id in roster state."));

        Assert.NotNull(outputs.Categories);

        // The headline: same keys, same values. Not "the map is non-empty" and not "the ids look
        // like ids" — the exact set, so an output that names a node the state does not have, or
        // resolves to the catalogue entry id instead of the node, fails here.
        Assert.Equal(fromState, outputs.Categories);

        // And the ids are NODE ids, not the entry ids they are keyed by — the specific way this
        // goes wrong on NewRecruit, where `id`/`getId()` on a category return `cat-troops`.
        Assert.All(outputs.Categories!, kv => Assert.NotEqual(kv.Key, kv.Value));

        // Both categories the force entry links, addressable by name.
        Assert.Contains("cat-troops", outputs.Categories!.Keys);
        Assert.Contains("cat-hq", outputs.Categories!.Keys);

        // And a spec can actually name one: this is the path the resolver serves.
        var resolver = new ExpressionResolver();
        resolver.StoreOutputs("add-detachment", outputs);
        Assert.Equal(
            fromState["cat-troops"],
            resolver.Resolve("${{ steps.add-detachment.categories.cat-troops }}"));
    }
}

/// <summary>See <see cref="CategoryNodeIdContract"/> — the in-process BattleScribe lane.</summary>
[Trait("Category", "Unit")]
public class CategoryNodeIdTests(ITestOutputHelper output)
{
    [Fact]
    public void AddForce_CategoryOutputs_NameTheSameNodesTheStateReports()
    {
        using var engine = new BattleScribeRosterEngine();
        engine.SetTestContext(nameof(AddForce_CategoryOutputs_NameTheSameNodesTheStateReports));
        var setupErrors = engine.Setup(CategoryNodeIdContract.GameSystem(), CategoryNodeIdContract.Catalogues());
        Assert.Empty(setupErrors);

        var outputs = engine.AddForce(CategoryNodeIdContract.ForceEntryId, CategoryNodeIdContract.CatalogueId);

        CategoryNodeIdContract.AssertOutputsMatchState(
            "battlescribe", outputs, engine.GetRosterState(), output);
    }

    /// <summary>
    /// A second force from the same force entry mints its own category nodes. Without this, an
    /// engine that returned the force ENTRY's categories once and reused them would pass — which is
    /// exactly the substitution #419 exists to stop.
    /// </summary>
    [Fact]
    public void AddForce_TwiceFromOneForceEntry_MintsDistinctCategoryNodes()
    {
        using var engine = new BattleScribeRosterEngine();
        engine.SetTestContext(nameof(AddForce_TwiceFromOneForceEntry_MintsDistinctCategoryNodes));
        Assert.Empty(engine.Setup(CategoryNodeIdContract.GameSystem(), CategoryNodeIdContract.Catalogues()));

        var first = engine.AddForce(CategoryNodeIdContract.ForceEntryId, CategoryNodeIdContract.CatalogueId);
        var second = engine.AddForce(CategoryNodeIdContract.ForceEntryId, CategoryNodeIdContract.CatalogueId);

        Assert.NotNull(first.Categories);
        Assert.NotNull(second.Categories);
        Assert.Equal(first.Categories!.Keys.Order(), second.Categories!.Keys.Order());
        foreach (var key in first.Categories.Keys)
        {
            output.WriteLine($"[battlescribe] {key}: {first.Categories[key]} vs {second.Categories[key]}");
            Assert.NotEqual(first.Categories[key], second.Categories[key]);
        }
    }
}

/// <summary>
/// See <see cref="CategoryNodeIdContract"/> — the BattleScribe desktop UI lane.
/// <para>
/// The one lane whose category ids travel as JSON from a Java agent, and the one nothing else
/// checks: `pre-push` excludes it deliberately, so without this the agent could stop emitting the
/// field and every offline gate would stay green. No corpus spec asserts a category id, so a spec
/// run here proves only that nothing broke.
/// </para>
/// </summary>
[Collection("BsRosterUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "BsRosterUi")]
public sealed class BsRosterUiCategoryNodeIdTests(ITestOutputHelper output, BsRosterUiFixture fixture)
{
    [Fact]
    public void AddForce_CategoryOutputs_NameTheSameNodesTheStateReports()
    {
        Assert.SkipWhen(!fixture.Available,
            "BS UI artifacts not found (run setup.ps1) or BS_UI_SKIP=true — skipping BS Roster UI tests");

        var engine = fixture.Engine!;
        engine.SetTestContext(nameof(AddForce_CategoryOutputs_NameTheSameNodesTheStateReports));

        try
        {
            var setupErrors = engine.Setup(CategoryNodeIdContract.GameSystem(), CategoryNodeIdContract.Catalogues());
            Assert.True(setupErrors.Count == 0, $"Setup failed: {string.Join("; ", setupErrors)}");

            // The first addForce in this lane is the New Roster dialog, not the Add Force one — a
            // different Java action that builds its outputs through the same helper.
            var outputs = engine.AddForce(CategoryNodeIdContract.ForceEntryId, CategoryNodeIdContract.CatalogueId);

            CategoryNodeIdContract.AssertOutputsMatchState(
                "battlescribe-ui", outputs, engine.GetRosterState(), output);
        }
        finally
        {
            // One desktop app, shared across every spec in this collection.
            engine.Cleanup();
        }
    }
}

/// <summary>
/// See <see cref="CategoryNodeIdContract"/> — the NewRecruit lane, over the frozen HAR.
/// <para>
/// <c>Category=Conformance</c> despite not being a spec: this drives a real Chromium, and
/// <c>Category!=Conformance</c> is what keeps browser tests out of CI's offline unit step. The
/// <c>Engine</c> trait is what places it — <c>core</c> excludes it, <c>nr-frozen</c> and
/// <c>pre-push</c> run it.
/// </para>
/// </summary>
[Collection("FrozenNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrRoster")]
public sealed class FrozenNrCategoryNodeIdTests(ITestOutputHelper output, FrozenNrRosterFixture fixture)
{
    [Fact]
    public async Task AddForce_CategoryOutputs_NameTheSameNodesTheStateReports()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");

        using var handle = await fixture.AcquireAsync(TestContext.Current.CancellationToken);
        var engine = handle.Engine;
        engine.SetTestContext(nameof(AddForce_CategoryOutputs_NameTheSameNodesTheStateReports));

        try
        {
            var setupErrors = engine.Setup(CategoryNodeIdContract.GameSystem(), CategoryNodeIdContract.Catalogues());
            Assert.True(setupErrors.Count == 0, $"Setup failed: {string.Join("; ", setupErrors)}");

            var outputs = engine.AddForce(CategoryNodeIdContract.ForceEntryId, CategoryNodeIdContract.CatalogueId);

            CategoryNodeIdContract.AssertOutputsMatchState(
                "newrecruit", outputs, engine.GetRosterState(), output);
        }
        finally
        {
            // The pooled engine is shared: leaving this spec's list behind is what
            // NrListCleanupRegressionTests exists to prevent.
            engine.Cleanup();
        }
    }
}

/// <summary>
/// See <see cref="CategoryNodeIdContract"/> — the NewRecruit UI lane, over the frozen HAR.
/// <para>
/// It shares the state reader with the store-direct NR engine, but not the path that produces the
/// output: this driver mints its force through NR's own Create List / Add Force UI and reads the
/// categories afterwards. "Shares the reader" is a reason to expect agreement, not a measurement of
/// it, and the failure this guards — an output read from a re-hydrated `currentList.army` while the
/// state came from the captured `__bsspec.army`, or the reverse — produces two sets of real ids
/// that simply are not each other's.
/// </para>
/// </summary>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class FrozenNrUiCategoryNodeIdTests(ITestOutputHelper output, FrozenNrUiRosterFixture fixture)
{
    [Fact]
    public void AddForce_CategoryOutputs_NameTheSameNodesTheStateReports()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file not found, NR_UI_FROZEN_SKIP=true, or Playwright browsers missing "
            + "— skipping frozen NR UI tests");

        var engine = fixture.Engine!;
        engine.SetTestContext(nameof(AddForce_CategoryOutputs_NameTheSameNodesTheStateReports));

        try
        {
            var setupErrors = engine.Setup(CategoryNodeIdContract.GameSystem(), CategoryNodeIdContract.Catalogues());
            Assert.True(setupErrors.Count == 0, $"Setup failed: {string.Join("; ", setupErrors)}");

            var outputs = engine.AddForce(CategoryNodeIdContract.ForceEntryId, CategoryNodeIdContract.CatalogueId);

            CategoryNodeIdContract.AssertOutputsMatchState(
                "newrecruit-ui", outputs, engine.GetRosterState(), output);
        }
        finally
        {
            // One browser context for the whole collection — see FrozenNrUiRosterFixture.
            engine.Cleanup();
        }
    }
}
