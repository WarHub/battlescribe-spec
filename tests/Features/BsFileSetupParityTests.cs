using System.Reflection;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The <c>battlescribe</c> roster engine set up from XML files must be the same engine as one set up
/// from the Protocol model: <see cref="BattleScribeRosterEngine.SetupFromFiles"/> is a full equal of
/// <see cref="BattleScribeRosterEngine.Setup"/>, and a second setup on the same instance sees only its
/// own data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> File setup used to serve only the two real-world <c>dataSource</c> specs,
/// whose steps never exercised what it skipped: it filled no entry lookup (so
/// <c>selectChildEntry</c> and <c>setSelectionCount</c> threw), recorded no cost types (so
/// <c>setCostLimit</c> threw and exported roster costs kept the engine's alphabetical order), built
/// no constraint-declarer index (so a link-reached error's <c>from</c> named the wrong entry), and
/// registered only the game system's force entries. Scenario specs (ADR 002) set the roster engine
/// up from the data editor's saved files in the main suite, so every one of those gaps became a
/// wrong verdict waiting for the first scenario that touched it.
/// </para>
/// <para>
/// <b>Not a lane.</b> No <c>Engine</c> trait and no <c>Category=Conformance</c>: this checks one
/// adapter's two entry points against each other, offline and in process, and the specs are its
/// fixtures. The BsRoster lane remains the record of what the engine does with each spec.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BsFileSetupParityTests(ITestOutputHelper output)
{
    /// <summary>Both runs are the in-process BattleScribe engine, read as the BsRoster lane reads it.</summary>
    private static readonly EngineIdentity Engine = EngineIdentity.Of("battlescribe");

    /// <summary>
    /// Every inline roster spec reaches the same verdict through file setup as through Protocol
    /// setup. The files are what <see cref="CatXmlGenerator"/> writes for the spec's setup — the same
    /// XML the <c>battlescribe-ui</c> lane stages for the desktop app — so this also holds the
    /// generator to carrying everything a spec's setup says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No spec is excluded: when this was written every inline spec's setup survived the trip through
    /// XML. A spec that cannot (a field the generator drops) belongs in a named exclusion list here
    /// with its reason, never in a silent filter.
    /// </para>
    /// <para>
    /// This proves the two paths agree, not that either is right. Both now derive their lookups
    /// through one implementation, so a defect there breaks both alike and passes here — removing the
    /// catalogue force entries or the constraint-declarer index from it does exactly that. The
    /// BsRoster lane is what catches those.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryInlineRosterSpec_PassesThroughFileSetup()
    {
        var ct = TestContext.Current.CancellationToken;
        var specsDir = SpecLoader.FindRosterSpecsDirectory()
            ?? throw new InvalidOperationException("specs/roster not found from the test binaries.");

        var mismatches = new List<string>();
        var compared = 0;
        var skipped = 0;
        foreach (var (path, id, category) in SpecLoader.DiscoverSpecs(specsDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = SpecLoader.SpecName(category, id);
            var spec = SpecLoader.Load(path);

            // A dataSource spec is set up from files already; there is no Protocol setup to compare
            // it with.
            if (spec.Setup.DataSource is { Length: > 0 })
            {
                continue;
            }

            // engines: {battlescribe: skip} skips both runs, as the BsRoster lane skips the row.
            if (!spec.IsApplicableTo(Engine))
            {
                skipped++;
                continue;
            }

            var direct = Run(new BattleScribeRosterEngine(), path);
            var viaFiles = Run(new FileSetupRosterEngine(new BattleScribeRosterEngine()), path);
            compared++;

            if (direct.Passed != viaFiles.Passed)
            {
                var failed = direct.Passed ? viaFiles : direct;
                mismatches.Add(
                    $"  {name}: Protocol setup {Verdict(direct)}, file setup {Verdict(viaFiles)} — "
                    + $"first failure ({(direct.Passed ? "file" : "Protocol")} setup): {failed.Failures[0]}");
            }
        }

        output.WriteLine($"{compared} inline roster specs compared, {skipped} opted out of {Engine.Name}.");

        // The corpus is ~400 specs. A discovery that came back nearly empty compared nothing and must
        // not read as parity.
        Assert.True(compared > 300, $"Only {compared} inline roster specs were compared; discovery under {specsDir} is broken.");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {compared} roster specs reach a different verdict when the battlescribe engine "
            + "is set up from files (CatXmlGenerator's XML through SetupFromFiles) than from the Protocol model "
            + "(Setup). File setup must build everything Setup builds:\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    /// A second file setup on the same engine sees the new data and nothing of the old: no roster
    /// carried over, no stale catalogue, entry or cost type, and the first force of the new roster
    /// gets its default selections — the one-shot auto-select pass belongs to the roster, not the
    /// engine. This is what a scenario's second <c>newRoster</c>/<c>openRoster</c> does.
    /// </summary>
    [Fact]
    public void SecondFileSetup_SeesOnlyNewData()
    {
        // Driven through the interface, as the scenario runner drives it: Cleanup is a default member.
        using IRosterEngine engine = new BattleScribeRosterEngine();

        Assert.Empty(engine.SetupFromFiles(Files(DataA())));
        AssertFirstSetupBuiltARoster(engine);
        engine.Cleanup();

        Assert.Empty(engine.SetupFromFiles(Files(DataB())));
        AssertSeesOnlyDataB(engine);
    }

    /// <summary>
    /// <see cref="SecondFileSetup_SeesOnlyNewData"/> with a Protocol setup first: whichever path set
    /// the engine up last, it starts clean.
    /// </summary>
    [Fact]
    public void FileSetupAfterProtocolSetup_SeesOnlyNewData()
    {
        using IRosterEngine engine = new BattleScribeRosterEngine();

        var (gameSystem, catalogues) = DataA();
        Assert.Empty(engine.Setup(gameSystem, catalogues));
        AssertFirstSetupBuiltARoster(engine);
        engine.Cleanup();

        Assert.Empty(engine.SetupFromFiles(Files(DataB())));
        AssertSeesOnlyDataB(engine);
    }

    private static void AssertFirstSetupBuiltARoster(IRosterEngine engine)
    {
        var force = engine.AddForce("fe-1", "cat-a");
        Assert.NotNull(engine.SelectEntry(force.ForceId!, "se-a").SelectionId);
        engine.SetCostLimit("ct-a", 100);
        Assert.Single(engine.GetRosterState().Forces);
    }

    private static void AssertSeesOnlyDataB(IRosterEngine engine)
    {
        Assert.Empty(engine.GetRosterState().Forces);

        // cat-a, se-a and ct-a were in the first setup's files and are in none of these.
        var staleCatalogue = Assert.ThrowsAny<Exception>(() => engine.AddForce("fe-1", "cat-a"));
        Assert.Contains("cat-a", staleCatalogue.Message, StringComparison.Ordinal);
        Assert.Throws<SpecAddressingException>(() => engine.SetCostLimit("ct-a", 100));
        Assert.False(EntryLookupHas(engine, "se-a"), "the entry lookup still resolves se-a, an entry of the previous setup's data");

        var force = engine.AddForce("fe-1", "cat-b");

        // The first force of this roster ran the auto-select pass: se-b-default (min 1) is in.
        var selections = Assert.Single(engine.GetRosterState().Forces).Selections;
        Assert.Contains(selections, s => s.EntryId == "se-b-default");

        var staleEntry = Assert.ThrowsAny<Exception>(() => engine.SelectEntry(force.ForceId!, "se-a"));
        Assert.Contains("se-a", staleEntry.Message, StringComparison.Ordinal);

        // se-b is selectable, its child resolves through the entry lookup this setup built, and the
        // cost type this game system declares is the one a limit can be set on.
        var squad = engine.SelectEntry(force.ForceId!, "se-b");
        Assert.NotNull(squad.SelectionId);
        Assert.NotNull(engine.SelectChildEntry(force.ForceId!, squad.SelectionId!, "se-b-child").SelectionId);
        engine.SetCostLimit("ct-b", 100);
    }

    /// <summary>
    /// Does the engine's entry lookup (<c>BattleScribeEngine.GetEntryById</c>) still resolve
    /// <paramref name="entryId"/>? Asked by reflection, because no action a spec can write reaches a
    /// stale key today: every selection in a live roster comes from the current data, which
    /// re-indexes its own ids, and a reopened roster loses the selections whose entries the data no
    /// longer has. A stale key is still a wrong answer waiting for the first caller that asks for an
    /// id the new data lacks, so it is checked where it lives. The lookup returns a Java type this
    /// project does not reference, hence the invoke rather than a call.
    /// </summary>
    private static bool EntryLookupHas(IRosterEngine engine, string entryId)
    {
        var lookup = typeof(BattleScribeEngine).GetMethod(
            nameof(BattleScribeEngine.GetEntryById), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BattleScribeEngine.GetEntryById is gone; point this check at the entry lookup again.");
        return lookup.Invoke(((BattleScribeRosterEngine)engine).Engine, [entryId]) is not null;
    }

    private static (ProtocolGameSystem GameSystem, ProtocolCatalogue[] Catalogues) DataA() =>
        (GameSystem("ct-a"), [new ProtocolCatalogue
        {
            Id = "cat-a",
            Name = "Faction A",
            GameSystemId = "gs-1",
            SelectionEntries = [new ProtocolSelectionEntry { Id = "se-a", Name = "Alpha Unit", Type = "unit" }],
        }]);

    private static (ProtocolGameSystem GameSystem, ProtocolCatalogue[] Catalogues) DataB() =>
        (GameSystem("ct-b"), [new ProtocolCatalogue
        {
            Id = "cat-b",
            Name = "Faction B",
            GameSystemId = "gs-1",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-b",
                    Name = "Beta Unit",
                    Type = "unit",
                    SelectionEntries = [new ProtocolSelectionEntry { Id = "se-b-child", Name = "Beta Upgrade", Type = "upgrade" }],
                },
                new ProtocolSelectionEntry
                {
                    Id = "se-b-default",
                    Name = "Beta Mandatory",
                    Type = "unit",
                    Constraints = [new ProtocolConstraint { Id = "con-b-min", Type = "min", Value = 1 }],
                },
            ],
        }]);

    // One game system id for both data sets, as a data directory keeps it across edits; the edit
    // between them renamed its cost type and replaced the catalogue.
    private static ProtocolGameSystem GameSystem(string costTypeId) => new()
    {
        Id = "gs-1",
        Name = "Test System",
        CostTypes = [new ProtocolCostType { Id = costTypeId, Name = costTypeId }],
        ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Detachment" }],
    };

    private static List<(string FileName, string Content)> Files(
        (ProtocolGameSystem GameSystem, ProtocolCatalogue[] Catalogues) data) =>
        GeneratedFiles(data.GameSystem, data.Catalogues);

    /// <summary>The setup as files: <c>{gameSystem.Id}.gst</c>, then each catalogue in setup order.</summary>
    private static List<(string FileName, string Content)> GeneratedFiles(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var files = new List<(string FileName, string Content)>
        {
            ($"{gameSystem.Id}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)),
        };
        if (catalogues.Length > 0)
        {
            files.AddRange(CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues));
        }

        return files;
    }

    private static SpecResult Run(IRosterEngine engine, string specPath)
    {
        using (engine)
        {
            // Loaded per run: the runner fills setup defaults into the spec it is handed.
            return new RosterRunner(engine, null, Engine).Run(SpecLoader.Load(specPath));
        }
    }

    private static string Verdict(SpecResult result) => result.Passed ? "passes" : "fails";

    /// <summary>
    /// The engine under test with its Protocol setup routed through file setup: <see cref="Setup"/>
    /// writes the setup as XML and hands it to <see cref="IRosterEngine.SetupFromFiles"/>. Everything
    /// else is forwarded unchanged.
    /// </summary>
    private sealed class FileSetupRosterEngine(IRosterEngine inner) : IRosterEngine
    {
        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) =>
            inner.SetupFromFiles(GeneratedFiles(gameSystem, catalogues));

        // Every defaulted member of IRosterEngine is named here: one left out binds to the interface's
        // throwing default, and this wrapper would report "not supported" for an engine that supports
        // it (the rule ForceFailRosterEngine states).
        public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files) =>
            inner.SetupFromFiles(files);

        public void SetTestContext(string specId) => inner.SetTestContext(specId);

        public ActionOutputs AddForce(string forceEntryId, string catalogueId) => inner.AddForce(forceEntryId, catalogueId);

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) =>
            inner.AddChildForce(parentForceId, forceEntryId, catalogueId);

        public void RemoveForce(string forceId) => inner.RemoveForce(forceId);

        public ActionOutputs SelectEntry(string forceId, string entryId) => inner.SelectEntry(forceId, entryId);

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) =>
            inner.SelectChildEntry(forceId, parentSelectionId, entryId);

        public void DeselectSelection(string forceId, string selectionId) => inner.DeselectSelection(forceId, selectionId);

        public void SetSelectionCount(string forceId, string selectionId, int count) =>
            inner.SetSelectionCount(forceId, selectionId, count);

        public ActionOutputs DuplicateSelection(string forceId, string selectionId) => inner.DuplicateSelection(forceId, selectionId);

        public ActionOutputs DuplicateForce(string forceId) => inner.DuplicateForce(forceId);

        public void SetCostLimit(string costTypeId, decimal value) => inner.SetCostLimit(costTypeId, value);

        public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes) =>
            inner.SetCustomization(forceId, selectionId, categoryEntryId, customName, customNotes);

        public RosterState GetRosterState() => inner.GetRosterState();

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => inner.GetValidationErrors();

        public string ExportRosterXml() => inner.ExportRosterXml();

        public void LoadRoster(string xml) => inner.LoadRoster(xml);

        public void ReloadRoster() => inner.ReloadRoster();

        public void Cleanup() => inner.Cleanup();

        public void Dispose() => inner.Dispose();
    }
}
