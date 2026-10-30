using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// A roster engine set up from data FILES is set up exactly as well as one set up from the Protocol
/// model, and a second file setup after <see cref="IRosterEngine.Cleanup"/> sees the second data and
/// nothing of the first — stated once and run on both NewRecruit roster engines over the frozen HAR.
/// </summary>
/// <remarks>
/// <para>
/// This is the sequence a scenario spec's hand-offs drive (docs/adr/002-scenario-specs.md):
/// <c>newRoster</c> is <c>Cleanup</c> → <c>SetupFromFiles</c>, and <c>openRoster</c> adds
/// <c>LoadRoster</c> of a roster saved earlier. Until scenarios, <c>SetupFromFiles</c> only ever ran
/// once per engine session, for the two <c>dataSource</c> specs, and the NR UI driver recorded none
/// of the data it loaded — so <c>addForce</c> after it threw "Setup must be called before any
/// mutation."
/// </para>
/// <para>
/// The second data set keeps every id and changes what a stale engine would get wrong: the unit's
/// cost, its NAME (the UI driver clicks entries by name, so a lookup left over from the first setup
/// addresses a row that is no longer there) and the catalogue's name (which the Create List dialog
/// picks the faction by). Each id staying the same is the point — it is what the load-once guard and
/// every id-keyed cache in NR would mistake for "already loaded".
/// </para>
/// </remarks>
internal static class NrFileSetupContract
{
    internal const string GameSystemId = "file-setup-gs";
    internal const string ForceEntryId = "fe-file-setup";
    internal const string CatalogueId = "cat-file-setup";
    internal const string EntryId = "se-file-setup-unit";
    private const string CostTypeId = "pts";

    /// <summary>The data a first data-editor save would hand over.</summary>
    internal static readonly DataVersion First = new("Recruit", "File Setup Catalogue", 10m);

    /// <summary>The same ids after a second save: renamed, re-costed.</summary>
    internal static readonly DataVersion Second = new("Veteran", "File Setup Catalogue Revised", 20m);

    internal sealed record DataVersion(string UnitName, string CatalogueName, decimal Cost);

    /// <summary>
    /// The <c>.gst</c> and <c>.cat</c> for <paramref name="version"/>, as <see cref="CatXmlGenerator"/>
    /// writes them — the generator every Protocol-model <c>Setup</c> on these engines goes through, so
    /// the files differ from that path only in how they reach the engine.
    /// </summary>
    internal static IReadOnlyList<(string FileName, string Content)> Files(DataVersion version)
    {
        var gameSystem = new ProtocolGameSystem
        {
            Id = GameSystemId,
            Name = "File Setup System",
            CostTypes = [new ProtocolCostType { Id = CostTypeId, Name = "pts" }],
            ForceEntries = [new ProtocolForceEntry { Id = ForceEntryId, Name = "File Setup Detachment" }],
        };

        ProtocolCatalogue[] catalogues =
        [
            new()
            {
                Id = CatalogueId,
                Name = version.CatalogueName,
                GameSystemId = GameSystemId,
                SelectionEntries =
                [
                    new ProtocolSelectionEntry
                    {
                        Id = EntryId,
                        Name = version.UnitName,
                        Type = "unit",
                        Costs = [new ProtocolCostValue { Name = "pts", TypeId = CostTypeId, Value = version.Cost }],
                    },
                ],
            },
        ];

        var files = new List<(string FileName, string Content)>
        {
            ($"{GameSystemId}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)),
        };
        files.AddRange(CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues));
        return files;
    }

    /// <summary>
    /// Runs the three roster sessions on <paramref name="engine"/>: build on the first data; start
    /// again on the second and build; save, start again on the second and reopen what was saved.
    /// The caller owns the final <see cref="IRosterEngine.Cleanup"/>.
    /// </summary>
    internal static void Run(IRosterEngine engine, string engineName, ITestOutputHelper output)
    {
        // Session 1 — a new roster on the first data.
        var started = DateTime.UtcNow;
        AssertSetUp(engine.SetupFromFiles(Files(First)), engineName, "first file setup");
        output.WriteLine($"[{engineName}] first SetupFromFiles: {(DateTime.UtcNow - started).TotalSeconds:F1}s");

        var firstSelectionId = BuildAndAssert(engine, engineName, First, output);
        output.WriteLine($"[{engineName}] session 1 selection id: {firstSelectionId}");

        // Session 2 — Cleanup, then the SAME ids with new content. An engine that skips the load
        // because the game-system id is the one it already has, or that keeps the first setup's
        // name lookups, fails here: the unit it selects is the wrong name or the wrong cost.
        started = DateTime.UtcNow;
        engine.Cleanup();
        AssertSetUp(engine.SetupFromFiles(Files(Second)), engineName, "second file setup after Cleanup");
        output.WriteLine(
            $"[{engineName}] Cleanup + second SetupFromFiles: {(DateTime.UtcNow - started).TotalSeconds:F1}s");

        var savedSelectionId = BuildAndAssert(engine, engineName, Second, output);
        output.WriteLine($"[{engineName}] session 2 selection id: {savedSelectionId}");

        var savedXml = engine.ExportRosterXml();
        Assert.Contains(EntryId, savedXml, StringComparison.Ordinal);

        // Session 3 — Cleanup, the second data again, and the saved roster reopened against it.
        started = DateTime.UtcNow;
        engine.Cleanup();
        AssertSetUp(engine.SetupFromFiles(Files(Second)), engineName, "third file setup after Cleanup");
        engine.LoadRoster(savedXml);
        output.WriteLine(
            $"[{engineName}] Cleanup + SetupFromFiles + LoadRoster: {(DateTime.UtcNow - started).TotalSeconds:F1}s");

        var reopened = engine.GetRosterState();
        var selection = SingleUnit(reopened, engineName, "after LoadRoster");
        Assert.Equal(Second.UnitName, selection.Name);
        Assert.Equal(Second.Cost, CostOf(selection.Costs, engineName, "reopened selection"));
        Assert.Equal(Second.Cost, CostOf(reopened.Costs, engineName, "reopened roster"));

        // Recorded, not asserted: whether a reopen keeps the node ids a spec captured before the save
        // is an engine fact the ADR leaves to #280 to pin.
        output.WriteLine(
            $"[{engineName}] selection id saved {savedSelectionId} -> reopened {selection.Id} "
            + $"({(selection.Id == savedSelectionId ? "preserved" : "re-minted")})");
    }

    private static string? BuildAndAssert(
        IRosterEngine engine, string engineName, DataVersion version, ITestOutputHelper output)
    {
        var force = engine.AddForce(ForceEntryId, CatalogueId);
        Assert.False(string.IsNullOrEmpty(force.ForceId), $"[{engineName}] addForce minted no force.");

        var selected = engine.SelectEntry(force.ForceId!, EntryId);
        Assert.False(string.IsNullOrEmpty(selected.SelectionId), $"[{engineName}] selectEntry minted no selection.");

        var state = engine.GetRosterState();
        foreach (var f in state.Forces)
        {
            output.WriteLine($"[{engineName}]   force {f.Id} '{f.Name}' catalogue={f.CatalogueId}");
            foreach (var s in f.Selections)
            {
                output.WriteLine(
                    $"[{engineName}]     selection {s.Id} '{s.Name}' entry={s.EntryId} "
                    + $"costs=[{string.Join(", ", s.Costs.Select(c => $"{c.TypeId}={c.Value}"))}]");
            }
        }

        var selection = SingleUnit(state, engineName, $"with the {version.UnitName} data");
        Assert.Equal(selected.SelectionId, selection.Id);
        Assert.Equal(version.UnitName, selection.Name);
        Assert.Equal(version.Cost, CostOf(selection.Costs, engineName, "selection"));
        Assert.Equal(version.Cost, CostOf(state.Costs, engineName, "roster"));
        return selection.Id;
    }

    private static SelectionState SingleUnit(RosterState state, string engineName, string when)
    {
        var force = Assert.Single(state.Forces);
        Assert.Equal(CatalogueId, force.CatalogueId);
        var selection = Assert.Single(force.Selections);
        Assert.True(selection.EntryId == EntryId,
            $"[{engineName}] {when}: the selection's entry is '{selection.EntryId}', expected '{EntryId}'.");
        return selection;
    }

    private static decimal CostOf(IReadOnlyList<CostState> costs, string engineName, string what)
    {
        var cost = costs.FirstOrDefault(c => c.TypeId == CostTypeId);
        Assert.True(cost is not null,
            $"[{engineName}] the {what} has no '{CostTypeId}' cost: "
            + $"[{string.Join(", ", costs.Select(c => $"{c.TypeId}={c.Value}"))}]");
        return cost.Value;
    }

    private static void AssertSetUp(IReadOnlyList<string> errors, string engineName, string what)
        => Assert.True(errors.Count == 0, $"[{engineName}] {what} failed: {string.Join("; ", errors)}");
}

/// <summary>
/// See <see cref="NrFileSetupContract"/> — the store-direct NewRecruit engine, over the frozen HAR.
/// <para>
/// <c>Category=Conformance</c> despite not being a spec: this drives a real Chromium, and
/// <c>Category!=Conformance</c> is what keeps browser tests out of CI's offline unit step. The
/// <c>Engine</c> trait is what places it — <c>nr-frozen</c> and <c>pre-push</c> run it.
/// </para>
/// </summary>
[Collection("FrozenNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrRoster")]
public sealed class FrozenNrFileSetupTests(ITestOutputHelper output, FrozenNrRosterFixture fixture)
{
    [Fact]
    public async Task FileSetup_ReSetupAfterCleanup_AndReopen_SeeOnlyTheLatestData()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file not found (run setup.ps1) — skipping frozen NR tests");

        using var handle = await fixture.AcquireAsync(TestContext.Current.CancellationToken);
        var engine = handle.Engine;
        engine.SetTestContext(nameof(FileSetup_ReSetupAfterCleanup_AndReopen_SeeOnlyTheLatestData));

        try
        {
            NrFileSetupContract.Run(engine, "newrecruit", output);
        }
        finally
        {
            // The pooled engine is shared: leaving this test's list behind is what
            // NrListCleanupRegressionTests exists to prevent.
            engine.Cleanup();
        }
    }
}

/// <summary>
/// See <see cref="NrFileSetupContract"/> — the NewRecruit UI driver, over the frozen HAR. The engine
/// whose file setup used to record nothing, so its first <c>addForce</c> refused to run and every
/// entry it clicks by name had no name to click by.
/// </summary>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class FrozenNrUiFileSetupTests(ITestOutputHelper output, FrozenNrUiRosterFixture fixture)
{
    [Fact]
    public void FileSetup_ReSetupAfterCleanup_AndReopen_SeeOnlyTheLatestData()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file or Playwright browsers missing (run setup.ps1) "
            + "— skipping frozen NR UI tests");

        var engine = fixture.Engine!;
        engine.SetTestContext(nameof(FileSetup_ReSetupAfterCleanup_AndReopen_SeeOnlyTheLatestData));

        try
        {
            NrFileSetupContract.Run(engine, "newrecruit-ui", output);
        }
        finally
        {
            // One browser context for the whole collection — see FrozenNrUiRosterFixture.
            engine.Cleanup();
        }
    }
}
