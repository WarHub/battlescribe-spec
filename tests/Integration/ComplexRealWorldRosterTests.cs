using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// 10 complex real-world roster tests using wh40k-9e data.
/// Each test loads actual game system and catalogue files, builds a multi-unit roster,
/// and validates costs, selections, and validation state against the BattleScribe engine.
/// Tests skip when wh40k-9e data is not available.
/// </summary>
[Trait("Category", "Integration")]
public class ComplexRealWorldRosterTests(ITestOutputHelper output)
{
    private static string Wh40kDataDir => TestPaths.Wh40kDataDir!;
    private static bool DataAvailable => TestPaths.Wh40kDataAvailable;
    private const string SkipMessage = "wh40k-9e data not found. Run ./setup.ps1 to clone required repositories.";

    /// <summary>
    /// Helper: load game system + primary catalogue with all linked dependencies, initialize engine.
    /// The primary catalogue should be a playable (non-library) catalogue.
    /// Linked catalogues are discovered and loaded automatically.
    /// </summary>
    private BattleScribeEngine LoadCatalogue(string primaryCatalogueName)
    {
        var engine = new BattleScribeEngine();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        engine.LoadGameSystemFile(gstFile);

        var catFile = Path.Combine(Wh40kDataDir, primaryCatalogueName + ".cat");
        Assert.True(File.Exists(catFile), $"Catalogue '{primaryCatalogueName}.cat' not found in {Wh40kDataDir}");

        engine.LoadCatalogueWithDependencies(catFile, Wh40kDataDir);
        output.WriteLine($"Loaded primary: {primaryCatalogueName}");

        var cats = engine.GetLoadedCatalogues();
        foreach (var (id, name) in cats)
            output.WriteLine($"  Loaded catalogue: {name} ({id})");

        // Set the primary (non-library) catalogue as active
        engine.SetActiveCatalogue(cats.First(c => c.Name.Contains(primaryCatalogueName.Split(" - ").Last().Split(" ").First())).Id);

        var errors = engine.InitializeFromLoadedData();
        output.WriteLine($"Init errors: {errors.Count}");
        foreach (var e in errors.Take(5))
            output.WriteLine($"  - {e}");

        return engine;
    }

    /// <summary>
    /// Helper: load game system + multiple catalogues (each with dependencies), initialize engine.
    /// The first catalogue listed becomes the active catalogue.
    /// </summary>
    private BattleScribeEngine LoadCatalogues(params string[] catalogueNames)
    {
        var engine = new BattleScribeEngine();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        engine.LoadGameSystemFile(gstFile);

        foreach (var name in catalogueNames)
        {
            var catFile = Path.Combine(Wh40kDataDir, name + ".cat");
            if (File.Exists(catFile))
            {
                engine.LoadCatalogueWithDependencies(catFile, Wh40kDataDir);
                output.WriteLine($"Loaded: {Path.GetFileName(catFile)}");
            }
            else
            {
                output.WriteLine($"WARNING: Catalogue '{name}.cat' not found in {Wh40kDataDir}");
            }
        }

        // Set first catalogue as active
        if (catalogueNames.Length > 0)
        {
            var cats = engine.GetLoadedCatalogues();
            // Try to find the first catalogue by partial name match
            var firstName = catalogueNames[0].Split(" - ").Last();
            var match = cats.FirstOrDefault(c => c.Name.Contains(firstName, StringComparison.OrdinalIgnoreCase));
            if (match != default)
                engine.SetActiveCatalogue(match.Id);
        }

        var errors = engine.InitializeFromLoadedData();
        output.WriteLine($"Init errors: {errors.Count}");
        foreach (var e in errors.Take(5))
            output.WriteLine($"  - {e}");

        return engine;
    }

    /// <summary>
    /// Helper: add force by index (typically 0 for the first/only force entry), log results.
    /// </summary>
    private void AddForce(BattleScribeEngine engine, string forceName, int forceEntryIndex = 0)
    {
        engine.AddForceByIndex(forceEntryIndex);
        output.WriteLine($"Added force: {forceName} (index {forceEntryIndex})");
    }

    /// <summary>
    /// Helper: select entry by name on force, log results.
    /// </summary>
    private void SelectEntry(BattleScribeEngine engine, string entryName, int forceIndex = 0)
    {
        var idx = engine.SelectEntryByName(forceIndex, entryName);
        Assert.True(idx >= 0, $"Entry '{entryName}' not found on force {forceIndex}");
        output.WriteLine($"  Selected: {entryName} (index {idx} on force {forceIndex})");
    }

    /// <summary>
    /// Helper: dump current roster state for debugging.
    /// </summary>
    private void DumpState(BattleScribeEngine engine)
    {
        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);
        output.WriteLine($"\n--- Roster State ---");
        output.WriteLine($"Forces: {snapshot.Forces.Count}");
        foreach (var cost in snapshot.Costs)
            output.WriteLine($"  Cost: {cost.Name} = {cost.Value}");
        for (int i = 0; i < snapshot.Forces.Count; i++)
        {
            var f = snapshot.Forces[i];
            output.WriteLine($"  Force[{i}]: {f.Name} ({f.Selections.Count} selections)");
            foreach (var sel in f.Selections)
                output.WriteLine($"    - {sel.Name} ({sel.Type}, x{sel.Number}) [{string.Join(", ", sel.Costs.Select(c => $"{c.Name}={c.Value}"))}]");
        }
        var valErrors = engine.GetValidationErrors();
        output.WriteLine($"Validation errors: {valErrors.Count}");
        foreach (var e in valErrors.Take(10))
            output.WriteLine($"  ! {e}");
    }

    // =========================================================================
    // Roster 1: Space Marines Battalion — "Ultramarines Strike Force"
    // Exercises: cost aggregation, min HQ/Troops constraints, child models
    // =========================================================================
    [SkippableFact]
    public void Roster01_SpaceMarinesBattalion()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        // Ultramarines is a playable chapter catalogue that links to SM library
        using var engine = LoadCatalogue("Imperium - Ultramarines");

        AddForce(engine, "Battalion");

        // HQ choices
        SelectEntry(engine, "Captain");
        SelectEntry(engine, "Librarian");

        // Troops
        SelectEntry(engine, "Tactical Squad");
        SelectEntry(engine, "Intercessor Squad");
        SelectEntry(engine, "Assault Intercessor Squad");

        // Elites
        SelectEntry(engine, "Bladeguard Veteran Squad");

        // Heavy Support
        SelectEntry(engine, "Eradicator Squad");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        // Should have 1 force with 7+ selections (some units create sub-models)
        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 7,
            $"Expected at least 7 selections, got {snapshot.Forces[0].Selections.Count}");

        // Costs should be positive (pts and PL at minimum)
        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts cost, got {ptsCost.Value}");
        output.WriteLine($"Total pts: {ptsCost.Value}");

        var plCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("PL") == true);
        if (plCost != null)
        {
            Assert.True(plCost.Value > 0, $"Expected positive PL cost, got {plCost.Value}");
            output.WriteLine($"Total PL: {plCost.Value}");
        }

        // Selection names should include our units
        var selNames = snapshot.Forces[0].Selections.Select(s => s.Name).ToList();
        Assert.Contains(selNames, n => n != null && n.Contains("Captain"));
        Assert.Contains(selNames, n => n != null && n.Contains("Tactical"));
    }

    // =========================================================================
    // Roster 2: Imperial Knights Super-Heavy — "Knight Household"
    // Exercises: non-standard force org, expensive models, 1900 conditionGroups
    // =========================================================================
    [SkippableFact]
    public void Roster02_ImperialKnightsSuperHeavy()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        using var engine = LoadCatalogue("Imperium - Imperial Knights");

        AddForce(engine, "Super-Heavy");

        // Big Knights (Questoris class)
        SelectEntry(engine, "Knight Paladin");
        SelectEntry(engine, "Knight Errant");
        SelectEntry(engine, "Knight Crusader");

        // Armiger support
        SelectEntry(engine, "Armiger Warglaives");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 4,
            $"Expected at least 4 selections, got {snapshot.Forces[0].Selections.Count}");

        // Knights are expensive — total pts should be very high
        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value >= 500, $"Expected at least 500 pts for a knight army, got {ptsCost.Value}");
        output.WriteLine($"Knight army pts: {ptsCost.Value}");

        // Verify knight names are present
        var selNames = snapshot.Forces[0].Selections.Select(s => s.Name).ToList();
        Assert.Contains(selNames, n => n != null && n.Contains("Paladin"));
    }

    // =========================================================================
    // Roster 3: Chaos Daemons Multi-God — "Pandemonium Host"
    // Exercises: god-faction conditions, 788 conditionGroups, cross-faction
    // =========================================================================
    [SkippableFact]
    public void Roster03_ChaosDaemonsMultiGod()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        using var engine = LoadCatalogue("Chaos - Daemons");

        AddForce(engine, "Battalion");

        // Khorne units
        SelectEntry(engine, "Bloodletters");
        SelectEntry(engine, "Bloodmaster");

        // Nurgle units
        SelectEntry(engine, "Plaguebearers");
        SelectEntry(engine, "Beasts of Nurgle");

        // Tzeentch units
        SelectEntry(engine, "Pink Horrors");

        // Slaanesh units
        SelectEntry(engine, "Daemonettes");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 6,
            $"Expected at least 6 selections, got {snapshot.Forces[0].Selections.Count}");

        // Multi-god army should have positive costs
        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Daemon host pts: {ptsCost.Value}");

        // Should have mix of unit names from different gods
        var selNames = snapshot.Forces[0].Selections.Select(s => s.Name).ToList();
        Assert.Contains(selNames, n => n != null && n.Contains("Bloodletter"));
        Assert.Contains(selNames, n => n != null && n.Contains("Plaguebearer"));
        Assert.Contains(selNames, n => n != null && n.Contains("Daemonette"));
    }

    // =========================================================================
    // Roster 4: Thousand Sons Psychic — "Cabal of Sorcerers"
    // Exercises: 3 catalogue imports, psychic powers, modifier chains
    // =========================================================================
    [SkippableFact]
    public void Roster04_ThousandSonsPsychic()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        using var engine = LoadCatalogue("Chaos - Thousand Sons");

        AddForce(engine, "Battalion");

        // HQ: sorcerer characters
        SelectEntry(engine, "Ahriman");
        SelectEntry(engine, "Exalted Sorcerer");
        SelectEntry(engine, "Infernal Master");

        // Troops
        SelectEntry(engine, "Rubric Marines");
        SelectEntry(engine, "Rubric Marines");
        SelectEntry(engine, "Thousand Sons Cultists");

        // Elite
        SelectEntry(engine, "Scarab Occult Terminators");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 7,
            $"Expected at least 7 selections, got {snapshot.Forces[0].Selections.Count}");

        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Thousand Sons pts: {ptsCost.Value}");

        // Verify Ahriman was selected
        var selNames = snapshot.Forces[0].Selections.Select(s => s.Name).ToList();
        Assert.Contains(selNames, n => n != null && n.Contains("Ahriman"));
        Assert.Contains(selNames, n => n != null && n.Contains("Rubric"));
    }

    // =========================================================================
    // Roster 5: Astra Militarum Combined Arms — "Cadian Shock Force"
    // Exercises: library imports, large selection count, multi-force
    // =========================================================================
    [SkippableFact]
    public void Roster05_AstraMilitarumCombinedArms()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        // Astra Militarum (playable) links to AM Library, Elysians, DKoK, Assassinorum
        using var engine = LoadCatalogue("Imperium - Astra Militarum");

        AddForce(engine, "Battalion");

        // HQ
        SelectEntry(engine, "Cadian Castellan");
        SelectEntry(engine, "Cadian Castellan");

        // Troops (large infantry)
        SelectEntry(engine, "Cadian Shock Troops");
        SelectEntry(engine, "Cadian Shock Troops");
        SelectEntry(engine, "Cadian Shock Troops");
        SelectEntry(engine, "Catachan Jungle Fighters");

        // Vehicles
        SelectEntry(engine, "Leman Russ Battle Tanks");
        SelectEntry(engine, "Basilisk");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 8,
            $"Expected at least 8 selections, got {snapshot.Forces[0].Selections.Count}");

        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Guard army pts: {ptsCost.Value}");
    }

    // =========================================================================
    // Roster 6: Necrons Dynasty — "Szarekhan Phalanx"
    // Exercises: dynasty keyword modifiers, 658 conditionGroups
    // =========================================================================
    [SkippableFact]
    public void Roster06_NecronsDynasty()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        using var engine = LoadCatalogue("Necrons");

        AddForce(engine, "Battalion");

        // HQ
        SelectEntry(engine, "Overlord");
        SelectEntry(engine, "Royal Warden");

        // Troops
        SelectEntry(engine, "Necron Warriors");
        SelectEntry(engine, "Necron Warriors");
        SelectEntry(engine, "Immortals");

        // Elite
        SelectEntry(engine, "Lychguard");
        SelectEntry(engine, "Canoptek Wraiths");

        // Heavy
        SelectEntry(engine, "Lokhust Heavy Destroyers");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 8,
            $"Expected at least 8 selections, got {snapshot.Forces[0].Selections.Count}");

        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Necron phalanx pts: {ptsCost.Value}");

        var selNames = snapshot.Forces[0].Selections.Select(s => s.Name).ToList();
        Assert.Contains(selNames, n => n != null && n.Contains("Overlord"));
        Assert.Contains(selNames, n => n != null && n.Contains("Warrior"));
    }

    // =========================================================================
    // Roster 7: Tyranids Swarm — "Hive Fleet Leviathan"
    // Exercises: high model counts, many cheap units, swarm mechanics
    // =========================================================================
    [SkippableFact]
    public void Roster07_TyranidsSwarm()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        using var engine = LoadCatalogue("Tyranids");

        AddForce(engine, "Battalion");

        // HQ
        SelectEntry(engine, "Hive Tyrant");
        SelectEntry(engine, "Broodlord");

        // Troops — swarm units
        SelectEntry(engine, "Termagants");
        SelectEntry(engine, "Termagants");
        SelectEntry(engine, "Hormagaunts");
        SelectEntry(engine, "Hormagaunts");
        SelectEntry(engine, "Genestealers");

        // Fast Attack
        SelectEntry(engine, "Gargoyles");

        // Heavy Support
        SelectEntry(engine, "Carnifexes");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 9,
            $"Expected at least 9 selections, got {snapshot.Forces[0].Selections.Count}");

        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Swarm army pts: {ptsCost.Value}");

        var selNames = snapshot.Forces[0].Selections.Select(s => s.Name).ToList();
        Assert.Contains(selNames, n => n != null && n.Contains("Termagant"));
    }

    // =========================================================================
    // Roster 8: Dark Angels — "Deathwing Assault"
    // Exercises: catalogue chain (DA→SM→GST), chapter-specific entries
    // =========================================================================
    [SkippableFact]
    public void Roster08_DarkAngelsDeathwing()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        // Dark Angels is playable; auto-loads linked SM, Assassinorum, Inquisition, FW
        using var engine = LoadCatalogue("Imperium - Dark Angels");

        AddForce(engine, "Vanguard");

        // DA-specific HQ (Deathwing Strikemaster is DA-unique)
        SelectEntry(engine, "Deathwing Strikemaster");

        // DA-specific chaplain variant
        SelectEntry(engine, "Interrogator-Chaplain in Terminator Armor");

        // Deathwing units (DA-specific terminators)
        SelectEntry(engine, "Deathwing Terminator Squad");
        SelectEntry(engine, "Deathwing Knights");
        SelectEntry(engine, "Deathwing Command Squad");

        // Inherited from Space Marines (proves catalogue chain works)
        SelectEntry(engine, "Bladeguard Veteran Squad");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 6,
            $"Expected at least 6 selections, got {snapshot.Forces[0].Selections.Count}");

        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Dark Angels pts: {ptsCost.Value}");

        var selNames = snapshot.Forces[0].Selections.Select(s => s.Name).ToList();
        Assert.Contains(selNames, n => n != null && n.Contains("Deathwing"));
    }

    // =========================================================================
    // Roster 9: Adeptus Custodes Elite — "Shield Host"
    // Exercises: 4 catalogue imports, tight constraints, expensive models
    // =========================================================================
    [SkippableFact]
    public void Roster09_AdeptusCustodesElite()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        using var engine = LoadCatalogue("Imperium - Adeptus Custodes");

        AddForce(engine, "Patrol");

        // HQ — Shield-Captain
        SelectEntry(engine, "Shield-Captain");

        // Troops
        SelectEntry(engine, "Custodian Guard Squad");

        // Elites (the core of Custodes armies)
        SelectEntry(engine, "Allarus Custodians");
        SelectEntry(engine, "Custodian Wardens");

        // Fast Attack
        SelectEntry(engine, "Vertus Praetors");

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        Assert.Single(snapshot.Forces);
        Assert.True(snapshot.Forces[0].Selections.Count >= 5,
            $"Expected at least 5 selections, got {snapshot.Forces[0].Selections.Count}");

        // Custodes are expensive — even small armies cost a lot
        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value >= 300, $"Expected at least 300 pts for Custodes, got {ptsCost.Value}");
        output.WriteLine($"Custodes pts: {ptsCost.Value}");
    }

    // =========================================================================
    // Roster 10: Craftworlds + Harlequins Allied — "Aeldari Warhost"
    // Exercises: multi-catalogue roster, shared Aeldari Library, 2 forces
    // =========================================================================
    [SkippableFact]
    public void Roster10_AeldariAlliedWarhost()
    {
        Skip.IfNot(DataAvailable, SkipMessage);
        // Craftworlds + Harlequins: both link to Aeldari Library
        // LoadCatalogues loads both with all dependencies
        using var engine = LoadCatalogues("Aeldari - Craftworlds", "Aeldari - Harlequins");

        var cats = engine.GetLoadedCatalogues();
        output.WriteLine($"Loaded catalogues: {string.Join(", ", cats.Select(c => c.Name))}");

        // Find the Craftworlds catalogue to set as active for first force
        var cwCat = cats.FirstOrDefault(c => c.Name.Contains("Craftworld"));
        if (cwCat != default)
            engine.SetActiveCatalogue(cwCat.Id);

        AddForce(engine, "Patrol");

        // Craftworlds units from Aeldari Library
        SelectEntry(engine, "Farseer");
        SelectEntry(engine, "Dire Avengers");
        SelectEntry(engine, "Wraithguard");

        // Add a second Patrol for Harlequins
        var harleCat = cats.FirstOrDefault(c => c.Name.Contains("Harlequin"));
        if (harleCat != default)
            engine.SetActiveCatalogue(harleCat.Id);

        AddForce(engine, "Patrol");

        // Harlequin units from Aeldari Library
        SelectEntry(engine, "Troupe", forceIndex: 1);
        SelectEntry(engine, "Shadowseer", forceIndex: 1);

        DumpState(engine);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);

        // Should have 2 forces
        Assert.Equal(2, snapshot.Forces.Count);
        output.WriteLine($"Force 0: {snapshot.Forces[0].Name} ({snapshot.Forces[0].Selections.Count} selections)");
        output.WriteLine($"Force 1: {snapshot.Forces[1].Name} ({snapshot.Forces[1].Selections.Count} selections)");

        // Both forces should have selections
        Assert.True(snapshot.Forces[0].Selections.Count >= 3,
            $"Expected at least 3 selections in Craftworlds force, got {snapshot.Forces[0].Selections.Count}");
        Assert.True(snapshot.Forces[1].Selections.Count >= 2,
            $"Expected at least 2 selections in Harlequins force, got {snapshot.Forces[1].Selections.Count}");

        // Costs aggregated across both forces
        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Allied warhost pts: {ptsCost.Value}");
    }
}
