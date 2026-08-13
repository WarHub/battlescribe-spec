using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The two BattleScribe lanes report the SAME raising node for an error raised inside a selection
/// reached through an entry link — and they do it by construction, not by a shared correction.
/// <para>
/// Until #426 a shared pass (<c>BattleScribeErrorPlacement</c>, and #400's <c>ReduceToTargetEntry</c>
/// inside it) rewrote both lanes' answers before anything saw them, and that pass is what made them
/// agree: the in-process adapter read the raising element live and the UI driver read it out of a
/// Java agent payload, and the correction ran over both. Retiring it removes the mechanism that
/// enforced the agreement, so the agreement now has to be measured or it is only assumed.
/// </para>
/// <para>
/// A link-reached node is the case that fails first, because it is the only one where the two
/// identities of the raising node come apart: the runtime id is a plain per-run string, while the
/// catalogue entry id is a link COMPOSITE (<c>link-unit::sse-unit</c>, see
/// docs/entry-id-construction.md) whose exact spelling is a per-lane decision — the in-process
/// adapter takes it from a live <c>BaseRosterElement</c>, the UI lane from
/// <c>EngineAccessor.collectValidationErrors</c>. <see cref="RaisedOnEntryId"/> below is stated once
/// and asserted on both, so a lane that starts flattening or re-routing it fails alone.
/// </para>
/// <para>
/// The corpus covers the raising NODE for this scenario
/// (<c>constraint/constraint-error-owner-link-reached</c>, which runs on both lanes); no spec asserts
/// the entry id, so that claim is made here or not at all.
/// </para>
/// </summary>
internal static class BattleScribeLinkReachedErrorContract
{
    internal const string ForceEntryId = "fe-1";
    internal const string CatalogueId = "cat-1";
    internal const string LinkId = "link-unit";
    internal const string TargetEntryId = "sse-unit";

    /// <summary>
    /// What both lanes must call the raising node's catalogue entry: the link ROUTE, as BattleScribe
    /// builds it, not the target it resolves to. Reducing this to <c>sse-unit</c> is what the retired
    /// pass did, and it is a normalization rather than a reading — the engine says the composite.
    /// </summary>
    internal const string RaisedOnEntryId = $"{LinkId}::{TargetEntryId}";

    internal static ProtocolGameSystem GameSystem() => new()
    {
        Id = "bs-link-reached-gs",
        Name = "Link Reached Error",
        CategoryEntries = [new ProtocolCategoryEntry { Id = "cat-troops", Name = "Troops" }],
        ForceEntries =
        [
            new ProtocolForceEntry
            {
                Id = ForceEntryId,
                Name = "Patrol",
                CategoryLinks =
                [
                    new ProtocolCategoryLink { Id = "cl-fe-troops", TargetId = "cat-troops", Name = "Troops" },
                ],
            },
        ],
    };

    /// <summary>
    /// The scenario of <c>constraint/constraint-error-owner-link-reached</c>: Gear's <c>min: 2</c>
    /// auto-adds two, its <c>max: 1</c> then fires permanently, and BattleScribe raises the violation
    /// on the element that counted the children — the link-reached Elite Guard.
    /// </summary>
    internal static ProtocolCatalogue[] Catalogues() =>
    [
        new()
        {
            Id = CatalogueId,
            Name = "Link Reached Catalogue",
            GameSystemId = "bs-link-reached-gs",
            SharedSelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = TargetEntryId,
                    Name = "Elite Guard",
                    Type = "unit",
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink
                        {
                            Id = "cl-sse-troops", TargetId = "cat-troops", Name = "Troops", Primary = true,
                        },
                    ],
                    SelectionEntries =
                    [
                        new ProtocolSelectionEntry
                        {
                            Id = "se-gear",
                            Name = "Gear",
                            Type = "upgrade",
                            Constraints =
                            [
                                new ProtocolConstraint
                                {
                                    Id = "con-gear-min", Type = "min", Value = 2,
                                    Field = "selections", Scope = "parent",
                                },
                                new ProtocolConstraint
                                {
                                    Id = "con-gear-max", Type = "max", Value = 1,
                                    Field = "selections", Scope = "parent",
                                },
                            ],
                        },
                    ],
                },
            ],
            EntryLinks =
            [
                new ProtocolEntryLink
                {
                    Id = LinkId, Name = "Elite Guard", TargetId = TargetEntryId, Type = "selectionEntry",
                },
            ],
        },
    ];

    /// <summary>Builds the roster and asserts the contract for one lane.</summary>
    internal static void Run(string engineName, IRosterEngine engine, ITestOutputHelper output)
    {
        var setupErrors = engine.Setup(GameSystem(), Catalogues());
        Assert.True(setupErrors.Count == 0, $"Setup failed: {string.Join("; ", setupErrors)}");

        var force = engine.AddForce(ForceEntryId, CatalogueId);
        Assert.NotNull(force.ForceId);

        var unit = engine.SelectEntry(force.ForceId!, LinkId);
        Assert.False(string.IsNullOrEmpty(unit.SelectionId),
            $"[{engineName}] selectEntry reported no selection id — nothing to compare a raising node against.");

        var errors = engine.GetRosterState().ValidationErrors;
        foreach (var e in errors)
        {
            output.WriteLine(
                $"[{engineName}] raisedOn={e.RaisedOnType} {e.RaisedOnId} entry={e.RaisedOnEntryId} " +
                $"from={e.EntryId}/{e.ConstraintId} :: {e.Message}");
        }

        var error = Assert.Single(errors);

        // The scenario is the intended one. `from` is the constraint and the entry that declares it,
        // and neither engine has ever disputed this pair — if it moved, the assertions below would be
        // about a different error.
        Assert.Equal("se-gear", error.EntryId);
        Assert.Equal("con-gear-max", error.ConstraintId);

        // The raising node, by the id this run minted: the link-reached Elite Guard that counted its
        // children, named by the very output a spec's `on:` resolves through.
        Assert.Equal("selection", error.RaisedOnType);
        Assert.Equal(unit.SelectionId, error.RaisedOnId);

        // And by its catalogue entry — the claim the two lanes have to agree on independently, since
        // the runtime id above is minted per run and cannot be compared across them.
        Assert.Equal(RaisedOnEntryId, error.RaisedOnEntryId);
    }
}

/// <summary>
/// See <see cref="BattleScribeLinkReachedErrorContract"/> — the in-process BattleScribe lane.
/// </summary>
[Trait("Category", "Unit")]
public class BattleScribeLinkReachedErrorTests(ITestOutputHelper output)
{
    [Fact]
    public void LinkReachedError_NamesTheLinkReachedSelection_AndItsCompositeEntry()
    {
        using var engine = new BattleScribeRosterEngine();
        engine.SetTestContext(nameof(LinkReachedError_NamesTheLinkReachedSelection_AndItsCompositeEntry));

        BattleScribeLinkReachedErrorContract.Run("battlescribe", engine, output);
    }
}

/// <summary>
/// See <see cref="BattleScribeLinkReachedErrorContract"/> — the BattleScribe desktop UI lane, the
/// half whose errors travel as JSON out of the Java agent and which <c>pre-push</c> never runs.
/// </summary>
[Collection("BsRosterUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "BsRosterUi")]
public sealed class BsRosterUiLinkReachedErrorTests(ITestOutputHelper output, BsRosterUiFixture fixture)
{
    [Fact]
    public void LinkReachedError_NamesTheLinkReachedSelection_AndItsCompositeEntry()
    {
        Assert.SkipWhen(!fixture.Available,
            "BS UI artifacts not found (run setup.ps1) or BS_UI_SKIP=true — skipping BS Roster UI tests");

        var engine = fixture.Engine!;
        engine.SetTestContext(nameof(LinkReachedError_NamesTheLinkReachedSelection_AndItsCompositeEntry));

        try
        {
            BattleScribeLinkReachedErrorContract.Run("battlescribe-ui", engine, output);
        }
        finally
        {
            // One desktop app, shared across every test in this collection.
            engine.Cleanup();
        }
    }
}
