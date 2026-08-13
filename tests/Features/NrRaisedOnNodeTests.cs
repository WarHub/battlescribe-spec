using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The acceptance criterion for #422, on every NewRecruit lane that runs offline: <b>every
/// validation error names the roster node NewRecruit raised it on, and that node is one the state
/// model reports</b>.
/// <para>
/// Two claims, and the second is the one that has teeth. A non-null <c>RaisedOnId</c> alone proves
/// only that some string arrived — and a plausible-looking wrong string is the specific failure NR
/// invites, because <c>getId()</c> on a category returns the CATALOGUE entry and on the roster the
/// literal <c>"(roster)"</c>. So each id is matched against the ids the state read reports for that
/// kind of node: a raising node no <c>ForceState</c>/<c>SelectionState</c>/<c>CategoryState</c>
/// carries is useless to #423, which will let a spec write <c>on: selection &lt;nodeId&gt;</c>.
/// </para>
/// <para>
/// The scenario is built so the ids are load-bearing rather than decorative: two forces from ONE
/// force entry each raise the same min-forces violation, so the pair a spec matches today
/// (<c>force</c> + <c>fe-patrol</c>) is identical for both and only the raising node tells them
/// apart. That is #419's premise, executed.
/// </para>
/// <para>
/// Nothing in the corpus reads these fields yet, so this claim is made here or not at all.
/// </para>
/// </summary>
internal static class NrRaisedOnNodeContract
{
    internal const string ForceEntryId = "fe-patrol";
    internal const string CatalogueId = "cat-1";
    internal const string TrooperEntryId = "se-trooper";
    internal const string GroupOwnerEntryId = "se-parent";

    /// <summary>
    /// Three violations that cannot be resolved away, each raised on a different kind of node:
    /// <list type="bullet">
    /// <item><c>con-min-forces</c> — <c>field=forces</c> does not auto-add, and NewRecruit raises it
    /// on each force rather than on the roster, so every force carries one forever.</item>
    /// <item><c>con-min-troops</c> — satisfied by the auto-selected Trooper until it is deselected,
    /// at which point the force's Troops CATEGORY node raises it.</item>
    /// <item><c>con-gear-min</c>/<c>con-gear-max</c> — a deliberate contradiction: min=2 forces two
    /// Gear in, max=1 then fires permanently on the Gear SELECTION.</item>
    /// <item><c>con-weapon-min</c>/<c>con-weapon-max</c> — the same contradiction one level up, on a
    /// selection entry GROUP. NewRecruit raises it on the group node, and it reaches the adapter
    /// only through the flat <c>army.getErrors()</c> merge, so it is the one case that exercises the
    /// second of the two collection paths.</item>
    /// </list>
    /// </summary>
    internal static ProtocolGameSystem GameSystem() => new()
    {
        Id = "nr-raised-on-gs",
        Name = "NR Raised On Node",
        CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts" }],
        CategoryEntries = [new ProtocolCategoryEntry { Id = "cat-troops", Name = "Troops" }],
        ForceEntries =
        [
            new ProtocolForceEntry
            {
                Id = ForceEntryId,
                Name = "Patrol",
                Constraints =
                [
                    new ProtocolConstraint
                    {
                        Id = "con-min-forces", Type = "min", Value = 2, Field = "forces", Scope = "roster",
                    },
                ],
                CategoryLinks =
                [
                    new ProtocolCategoryLink { Id = "cl-fe-troops", TargetId = "cat-troops", Name = "Troops" },
                ],
            },
        ],
    };

    internal static ProtocolCatalogue[] Catalogues() =>
    [
        new()
        {
            Id = CatalogueId,
            Name = "NR Raised On Catalogue",
            GameSystemId = "nr-raised-on-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = TrooperEntryId,
                    Name = "Trooper",
                    Type = "unit",
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink
                        {
                            Id = "cl-troops", TargetId = "cat-troops", Name = "Troops", Primary = true,
                        },
                    ],
                    Constraints =
                    [
                        new ProtocolConstraint
                        {
                            Id = "con-min-troops", Type = "min", Value = 1, Field = "selections", Scope = "parent",
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
                new ProtocolSelectionEntry
                {
                    Id = GroupOwnerEntryId,
                    Name = "Parent Unit",
                    Type = "unit",
                    SelectionEntryGroups =
                    [
                        new ProtocolSelectionEntryGroup
                        {
                            Id = "seg-weapons",
                            Name = "Weapon Options",
                            DefaultSelectionEntryId = "se-sword",
                            Constraints =
                            [
                                new ProtocolConstraint
                                {
                                    Id = "con-weapon-min", Type = "min", Value = 2,
                                    Field = "selections", Scope = "parent",
                                },
                                new ProtocolConstraint
                                {
                                    Id = "con-weapon-max", Type = "max", Value = 1,
                                    Field = "selections", Scope = "parent",
                                },
                            ],
                            SelectionEntries =
                            [
                                new ProtocolSelectionEntry { Id = "se-sword", Name = "Sword", Type = "upgrade" },
                            ],
                        },
                    ],
                },
            ],
        },
    ];

    /// <summary>Drives the scenario and asserts the contract.</summary>
    internal static void Run(string engineName, IRosterEngine engine, ITestOutputHelper output)
    {
        Drive(engine);
        AssertEveryErrorNamesAStateNode(engineName, engine.GetRosterState(), output);
    }

    /// <summary>Builds the roster the contract is stated about, and nothing else.</summary>
    internal static void Drive(IRosterEngine engine)
    {
        var setupErrors = engine.Setup(GameSystem(), Catalogues());
        Assert.True(setupErrors.Count == 0, $"Setup failed: {string.Join("; ", setupErrors)}");

        var first = engine.AddForce(ForceEntryId, CatalogueId);
        var second = engine.AddForce(ForceEntryId, CatalogueId);
        Assert.NotNull(first.ForceId);
        Assert.NotNull(second.ForceId);
        Assert.NotEqual(first.ForceId, second.ForceId);

        // Empty the first force so its Troops category raises the min violation while the second
        // force keeps the Gear one — three kinds of raising node alive at the same time.
        Assert.NotNull(first.Selections);
        var trooper = Assert.Contains(TrooperEntryId, first.Selections!);
        engine.DeselectSelection(first.ForceId!, trooper);

        // The entry-group violation, which arrives only through the flat merge.
        engine.SelectEntry(second.ForceId!, GroupOwnerEntryId);
    }

    /// <summary>
    /// Every error names a raising node, and every raising node is one the state read reports.
    /// </summary>
    internal static void AssertEveryErrorNamesAStateNode(
        string engineName, RosterState state, ITestOutputHelper output)
    {
        // kind -> the ids the state model reports for that kind, built from the state read back —
        // a different extractor from the one that produced the errors, which is what makes this a
        // cross-check rather than a restatement.
        var byKind = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["force"] = new(StringComparer.Ordinal),
            ["category"] = new(StringComparer.Ordinal),
            ["selection"] = new(StringComparer.Ordinal),
        };

        void CollectSelection(SelectionState selection)
        {
            if (selection.Id is { Length: > 0 } id)
            {
                byKind["selection"].Add(id);
            }

            foreach (var child in selection.Children)
            {
                CollectSelection(child);
            }
        }

        void CollectForce(ForceState force)
        {
            if (force.Id is { Length: > 0 } id)
            {
                byKind["force"].Add(id);
            }

            foreach (var category in force.Categories ?? [])
            {
                if (category.Id is { Length: > 0 } categoryId)
                {
                    byKind["category"].Add(categoryId);
                }
            }

            foreach (var selection in force.Selections)
            {
                CollectSelection(selection);
            }

            foreach (var child in force.ChildForces ?? [])
            {
                CollectForce(child);
            }
        }

        foreach (var force in state.Forces)
        {
            CollectForce(force);
        }

        foreach (var error in state.ValidationErrors)
        {
            output.WriteLine(
                $"[{engineName}] owner={error.OwnerType} {error.OwnerEntryId} " +
                $"raisedOn={error.RaisedOnType} {error.RaisedOnId} :: {error.Message}");
        }

        Assert.NotEmpty(state.ValidationErrors);

        foreach (var error in state.ValidationErrors)
        {
            Assert.False(string.IsNullOrEmpty(error.RaisedOnType),
                $"[{engineName}] no raising node type on: {error.Message}");
            Assert.False(string.IsNullOrEmpty(error.RaisedOnId),
                $"[{engineName}] no raising node id on: {error.Message}");

            // Two kinds of raising node the state model has no record of, so they are named rather
            // than looked up: the roster (RosterState carries no id — both BattleScribe lanes have
            // the same hole) and a selection entry GROUP, which NewRecruit materialises as a real
            // node with its own errors while every engine's state model flattens it away. The group
            // case is asserted on its own below rather than waved through here.
            if (error.RaisedOnType is "roster" or "group")
            {
                continue;
            }

            var known = Assert.Contains(error.RaisedOnType!, byKind);
            Assert.True(known.Contains(error.RaisedOnId!),
                $"[{engineName}] raising node '{error.RaisedOnType} {error.RaisedOnId}' for " +
                $"\"{error.Message}\" is not a {error.RaisedOnType} the state model reports. " +
                $"Known {error.RaisedOnType} ids: {string.Join(", ", known.Order(StringComparer.Ordinal))}");
        }

        // The scenario's three kinds are all present — otherwise a lane that reported only the
        // easiest one would pass the loop above by having nothing else to get wrong.
        var kinds = state.ValidationErrors
            .Select(e => e.RaisedOnType!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("force", kinds);
        Assert.Contains("category", kinds);
        Assert.Contains("selection", kinds);

        // The entry-group violation, and the only error here that reaches the adapter through the
        // flat `army.getErrors()` merge instead of the node walk. Its raising node is a group node:
        // real in NewRecruit, absent from every engine's state model, and NOT the parent selection
        // the owner attribution reconstructs. Typed `group` because the node was asked what it is —
        // there is no fifth kind to guess from the error alone.
        var groupErrors = state.ValidationErrors.Where(e => e.RaisedOnType == "group").ToList();
        var groupError = Assert.Single(groupErrors);
        Assert.All(byKind, kv => Assert.DoesNotContain(groupError.RaisedOnId!, kv.Value));
        Assert.NotEqual(groupError.OwnerEntryId, groupError.RaisedOnId);

        // The payoff. Two forces built from ONE force entry each raise the min-forces violation, so
        // every field a spec can match on today is identical between them — this is exactly the
        // "three selections of se-unit-a are one and the same thing to the matcher" of #419, and the
        // raising node is what separates them.
        var forceErrors = state.ValidationErrors.Where(e => e.RaisedOnType == "force").ToList();
        Assert.Equal(2, forceErrors.Count);
        Assert.Single(forceErrors.Select(e => (e.OwnerType, e.OwnerEntryId)).Distinct());
        Assert.Equal(2, forceErrors.Select(e => e.RaisedOnId).Distinct().Count());
    }
}

/// <summary>
/// See <see cref="NrRaisedOnNodeContract"/> — the NewRecruit lane, over the frozen HAR.
/// <para>
/// <c>Category=Conformance</c> despite not being a spec: it drives a real Chromium, and that trait
/// is what keeps browser tests out of CI's offline unit step.
/// </para>
/// </summary>
[Collection("FrozenNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrRoster")]
public sealed class FrozenNrRaisedOnNodeTests(ITestOutputHelper output, FrozenNrRosterFixture fixture)
{
    [Fact]
    public async Task EveryValidationError_NamesTheRosterNodeItWasRaisedOn()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");

        using var handle = await fixture.AcquireAsync(TestContext.Current.CancellationToken);
        var engine = handle.Engine;
        engine.SetTestContext(nameof(EveryValidationError_NamesTheRosterNodeItWasRaisedOn));

        try
        {
            NrRaisedOnNodeContract.Run("newrecruit", engine, output);
        }
        finally
        {
            engine.Cleanup();
        }
    }
}

/// <summary>
/// The identity check that does not go through <c>NewRecruitStateReader</c> at all: what the adapter
/// reports as the raising node, against what NewRecruit itself hangs on the error object.
/// <para>
/// This exists because "the field is populated and looks like an id" is satisfied by several wrong
/// answers. NewRecruit offers two other references per error — <c>error.parent</c>, a bare handle
/// carrying the raising node's <c>uid</c> and nothing else, and <c>error.hash</c>. Only the first is
/// the raising node. <b>The hash is not a second name for it</b>: its first segment is the node the
/// constraint COUNTS OVER — the one the message names — which coincides with the raising node only
/// when the constraint's scope is <c>self</c>. Measured across the roster corpus on 2026-08-13: 142
/// errors where <c>error.parent</c> agrees with the reported raising node and none where it does
/// not, against 72 of 142 where the hash prefix names a different node. That asymmetry is asserted
/// here rather than only written down, because reading the hash is the plausible shortcut and it
/// produces a real node id that is the wrong node.
/// </para>
/// <para>
/// One lane is enough: both NewRecruit engines run the same <c>JsHelpers</c> extraction against the
/// same object graph, and the UI lane's own copy of the contract above covers the plumbing.
/// </para>
/// </summary>
[Collection("FrozenNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrRoster")]
public sealed class FrozenNrRaisedOnIdentityTests(ITestOutputHelper output, FrozenNrRosterFixture fixture)
{
    /// <summary>
    /// NewRecruit's own two references per error, collected the way the adapter collects them —
    /// constraint state primed everywhere first, then walk, then the flat merge, deduped by hash —
    /// so this and the adapter are looking at the same errors in the same order.
    /// </summary>
    private const string ErrorIdentityJs = """
        () => {
          const army = window.__bsspec_list ? window.__bsspec_list()?.army : null;
          if (!army) return '[]';
          try { army.checkConstraints(); } catch(e) {}
          const forces = army.getForces?.() || [];
          for (const f of forces) {
            try { f.checkConstraints?.(); } catch(e) {}
            for (const c of (f.getCategories?.() || [])) { try { c.checkConstraints?.(); } catch(e) {} }
            (function w(sels){ for (const s of sels) { try { s.checkConstraints?.(); } catch(e) {} w(s.getSelections?.() || []); } })(f.getSelections?.() || []);
          }
          const out = [];
          const seen = new Set();
          const add = e => {
            const h = e.hash || '';
            if (h && seen.has(h)) return;
            if (h) seen.add(h);
            out.push({
              message: (e.msg || e.message || e.text || '').replace(/<[^>]*>/g, ''),
              hash: h || null,
              parentUid: e.parent?.uid ?? null
            });
          };
          for (const e of (army.errors || [])) add(e);
          for (const f of forces) {
            for (const e of (f.errors || [])) add(e);
            for (const c of (f.getCategories?.() || [])) { for (const e of (c.errors || [])) add(e); }
            (function w(sels){ for (const s of sels) { for (const e of (s.errors || [])) add(e); w(s.getSelections?.() || []); } })(f.getSelections?.() || []);
          }
          try { for (const e of (army.getErrors?.() || [])) add(e); } catch(ex) {}
          return JSON.stringify(out);
        }
        """;

    private sealed record NrErrorIdentity(string Message, string? Hash, string? ParentUid);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task RaisedOnId_IsNewRecruitsOwnParentReference_AndNotTheHashPrefix()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");

        using var handle = await fixture.AcquireAsync(TestContext.Current.CancellationToken);
        var engine = handle.Engine;
        engine.SetTestContext(nameof(RaisedOnId_IsNewRecruitsOwnParentReference_AndNotTheHashPrefix));

        try
        {
            NrRaisedOnNodeContract.Drive(engine);

            var reported = engine.GetRosterState().ValidationErrors;
            var json = await engine.Browser.Page.EvaluateAsync<string>(ErrorIdentityJs);
            var native = System.Text.Json.JsonSerializer.Deserialize<List<NrErrorIdentity>>(
                json, JsonOptions)!;

            foreach (var n in native)
            {
                output.WriteLine($"native: parent={n.ParentUid} hash={n.Hash} :: {n.Message}");
            }

            Assert.Equal(reported.Count, native.Count);
            Assert.All(native, n => Assert.False(string.IsNullOrEmpty(n.ParentUid),
                $"NewRecruit reported no parent handle for \"{n.Message}\" — the adapter's raising " +
                "node would have nothing to come from."));

            // As multisets: two forces built from one entry raise the identical message, so pairing
            // by text cannot tell them apart and would report agreement it has not checked.
            Assert.Equal(
                native.Select(n => n.ParentUid!).Order(StringComparer.Ordinal),
                reported.Select(e => e.RaisedOnId!).Order(StringComparer.Ordinal));

            // And the hash prefix is a different claim. Not merely "we did not use it" — on this
            // roster it names a different node for at least one error, so an implementation that
            // read it would be wrong rather than redundant.
            var prefixes = native
                .Where(n => n.Hash is not null && n.Hash.Contains("::", StringComparison.Ordinal))
                .Select(n => (n.Message, Prefix: n.Hash!.Split("::")[0], n.ParentUid))
                .ToList();
            Assert.NotEmpty(prefixes);
            var divergent = prefixes.Where(p => p.Prefix != p.ParentUid).ToList();
            foreach (var p in divergent)
            {
                output.WriteLine(
                    $"hash prefix {p.Prefix} != raising node {p.ParentUid} :: {p.Message}");
            }

            Assert.NotEmpty(divergent);
        }
        finally
        {
            engine.Cleanup();
        }
    }
}

/// <summary>
/// See <see cref="NrRaisedOnNodeContract"/> — the NewRecruit UI lane, over the frozen HAR.
/// <para>
/// It shares <c>NewRecruitStateReader</c> with the store-direct engine, but not the path that builds
/// the roster: this driver mints its forces and drops its selection through NR's own UI. Sharing the
/// reader is a reason to expect the same answer, not a measurement that it arrives — the roster the
/// reader is pointed at is built differently, and the ids are per-node.
/// </para>
/// </summary>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class FrozenNrUiRaisedOnNodeTests(ITestOutputHelper output, FrozenNrUiRosterFixture fixture)
{
    [Fact]
    public void EveryValidationError_NamesTheRosterNodeItWasRaisedOn()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file not found, NR_UI_FROZEN_SKIP=true, or Playwright browsers missing "
            + "— skipping frozen NR UI tests");

        var engine = fixture.Engine!;
        engine.SetTestContext(nameof(EveryValidationError_NamesTheRosterNodeItWasRaisedOn));

        try
        {
            NrRaisedOnNodeContract.Run("newrecruit-ui", engine, output);
        }
        finally
        {
            // One browser context for the whole collection — see FrozenNrUiRosterFixture.
            engine.Cleanup();
        }
    }
}
