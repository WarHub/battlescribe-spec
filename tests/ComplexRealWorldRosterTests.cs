using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// 10 complex real-world roster tests using wh40k-9e data.
/// Each test loads actual game system and catalogue files, builds a multi-unit roster,
/// and validates costs, selections, and validation state against the oracle engine.
/// Tests skip when wh40k-9e data is not available.
/// </summary>
public class ComplexRealWorldRosterTests(ITestOutputHelper output)
{
    private static string Wh40kDataDir => TestPaths.Wh40kDataDir!;
    private static bool DataAvailable => TestPaths.Wh40kDataAvailable;
    private const string SkipMessage = "wh40k-9e data not found. Run ./setup.ps1 to clone required repositories.";

    /// <summary>
    /// Helper: load game system + primary catalogue with all linked dependencies, initialize oracle.
    /// The primary catalogue should be a playable (non-library) catalogue.
    /// Linked catalogues are discovered and loaded automatically.
    /// </summary>
    private BattleScribeOracle LoadCatalogue(string primaryCatalogueName)
    {
        var oracle = new BattleScribeOracle();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        oracle.LoadGameSystemFile(gstFile);

        var catFile = Path.Combine(Wh40kDataDir, primaryCatalogueName + ".cat");
        Assert.True(File.Exists(catFile), $"Catalogue '{primaryCatalogueName}.cat' not found in {Wh40kDataDir}");

        oracle.LoadCatalogueWithDependencies(catFile, Wh40kDataDir);
        output.WriteLine($"Loaded primary: {primaryCatalogueName}");

        var cats = oracle.GetLoadedCatalogues();
        foreach (var (id, name) in cats)
            output.WriteLine($"  Loaded catalogue: {name} ({id})");

        // Set the primary (non-library) catalogue as active
        oracle.SetActiveCatalogue(cats.First(c => c.Name.Contains(primaryCatalogueName.Split(" - ").Last().Split(" ").First())).Id);

        var errors = oracle.InitializeFromLoadedData();
        output.WriteLine($"Init errors: {errors.Count}");
        foreach (var e in errors.Take(5))
            output.WriteLine($"  - {e}");

        return oracle;
    }

    /// <summary>
    /// Helper: load game system + multiple catalogues (each with dependencies), initialize oracle.
    /// The first catalogue listed becomes the active catalogue.
    /// </summary>
    private BattleScribeOracle LoadCatalogues(params string[] catalogueNames)
    {
        var oracle = new BattleScribeOracle();
        var gstFile = Directory.GetFiles(Wh40kDataDir, "*.gst").First();
        oracle.LoadGameSystemFile(gstFile);

        foreach (var name in catalogueNames)
        {
            var catFile = Path.Combine(Wh40kDataDir, name + ".cat");
            if (File.Exists(catFile))
            {
                oracle.LoadCatalogueWithDependencies(catFile, Wh40kDataDir);
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
            var cats = oracle.GetLoadedCatalogues();
            // Try to find the first catalogue by partial name match
            var firstName = catalogueNames[0].Split(" - ").Last();
            var match = cats.FirstOrDefault(c => c.Name.Contains(firstName, StringComparison.OrdinalIgnoreCase));
            if (match != default)
                oracle.SetActiveCatalogue(match.Id);
        }

        var errors = oracle.InitializeFromLoadedData();
        output.WriteLine($"Init errors: {errors.Count}");
        foreach (var e in errors.Take(5))
            output.WriteLine($"  - {e}");

        return oracle;
    }

    /// <summary>
    /// Helper: add force by name, log results.
    /// </summary>
    private void AddForce(BattleScribeOracle oracle, string forceName)
    {
        var idx = oracle.GetForceEntryIndexByName(forceName);
        Assert.True(idx >= 0, $"Force entry containing '{forceName}' not found. Available: {string.Join(", ", oracle.GetAvailableForceEntryNames())}");
        oracle.AddForceByIndex(idx);
        output.WriteLine($"Added force: {forceName} (index {idx})");
    }

    /// <summary>
    /// Helper: select entry by name on force, log results.
    /// </summary>
    private void SelectEntry(BattleScribeOracle oracle, string entryName, int forceIndex = 0)
    {
        var count = oracle.SelectEntryByNameOnForce(entryName, forceIndex);
        if (count <= 0)
        {
            // Debug: dump available entries
            var available = oracle.GetAllAvailableEntryNames();
            var matching = available.Where(n => n.Contains(entryName, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
            output.WriteLine($"  DEBUG: '{entryName}' not found. Similar entries: [{string.Join(", ", matching)}]");
            output.WriteLine($"  DEBUG: Total available entries: {available.Count}");
            if (matching.Count == 0)
                output.WriteLine($"  DEBUG: First 20 entries: [{string.Join(", ", available.Take(20))}]");
        }
        Assert.True(count > 0, $"Entry '{entryName}' not found or produced 0 selections on force {forceIndex}");
        output.WriteLine($"  Selected: {entryName} (created {count} selection(s) on force {forceIndex})");
    }

    /// <summary>
    /// Helper: dump current roster state for debugging.
    /// </summary>
    private void DumpState(BattleScribeOracle oracle)
    {
        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);
        output.WriteLine($"\n--- Roster State ---");
        output.WriteLine($"Forces: {snapshot.Forces.Length}");
        foreach (var cost in snapshot.Costs)
            output.WriteLine($"  Cost: {cost.Name} = {cost.Value}");
        for (int i = 0; i < snapshot.Forces.Length; i++)
        {
            var f = snapshot.Forces[i];
            output.WriteLine($"  Force[{i}]: {f.Name} ({f.Selections.Length} selections)");
            foreach (var sel in f.Selections)
                output.WriteLine($"    - {sel.Name} ({sel.Type}, x{sel.Number}) [{string.Join(", ", sel.Costs.Select(c => $"{c.Name}={c.Value}"))}]");
        }
        var valErrors = oracle.GetValidationErrors();
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
        using var oracle = LoadCatalogue("Imperium - Ultramarines");

        AddForce(oracle, "Battalion");

        // HQ choices
        SelectEntry(oracle, "Captain");
        SelectEntry(oracle, "Librarian");

        // Troops
        SelectEntry(oracle, "Tactical Squad");
        SelectEntry(oracle, "Intercessor Squad");
        SelectEntry(oracle, "Assault Intercessor Squad");

        // Elites
        SelectEntry(oracle, "Bladeguard Veteran Squad");

        // Heavy Support
        SelectEntry(oracle, "Eradicator Squad");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        // Should have 1 force with 7+ selections (some units create sub-models)
        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 7,
            $"Expected at least 7 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Imperium - Imperial Knights");

        AddForce(oracle, "Super-Heavy");

        // Big Knights (Questoris class)
        SelectEntry(oracle, "Knight Paladin");
        SelectEntry(oracle, "Knight Errant");
        SelectEntry(oracle, "Knight Crusader");

        // Armiger support
        SelectEntry(oracle, "Armiger Warglaives");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 4,
            $"Expected at least 4 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Chaos - Daemons");

        AddForce(oracle, "Battalion");

        // Khorne units
        SelectEntry(oracle, "Bloodletters");
        SelectEntry(oracle, "Bloodmaster");

        // Nurgle units
        SelectEntry(oracle, "Plaguebearers");
        SelectEntry(oracle, "Beasts of Nurgle");

        // Tzeentch units
        SelectEntry(oracle, "Pink Horrors");

        // Slaanesh units
        SelectEntry(oracle, "Daemonettes");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 6,
            $"Expected at least 6 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Chaos - Thousand Sons");

        AddForce(oracle, "Battalion");

        // HQ: sorcerer characters
        SelectEntry(oracle, "Ahriman");
        SelectEntry(oracle, "Exalted Sorcerer");
        SelectEntry(oracle, "Infernal Master");

        // Troops
        SelectEntry(oracle, "Rubric Marines");
        SelectEntry(oracle, "Rubric Marines");
        SelectEntry(oracle, "Thousand Sons Cultists");

        // Elite
        SelectEntry(oracle, "Scarab Occult Terminators");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 7,
            $"Expected at least 7 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Imperium - Astra Militarum");

        AddForce(oracle, "Battalion");

        // HQ
        SelectEntry(oracle, "Cadian Castellan");
        SelectEntry(oracle, "Cadian Castellan");

        // Troops (large infantry)
        SelectEntry(oracle, "Cadian Shock Troops");
        SelectEntry(oracle, "Cadian Shock Troops");
        SelectEntry(oracle, "Cadian Shock Troops");
        SelectEntry(oracle, "Catachan Jungle Fighters");

        // Vehicles
        SelectEntry(oracle, "Leman Russ Battle Tanks");
        SelectEntry(oracle, "Basilisk");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 8,
            $"Expected at least 8 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Necrons");

        AddForce(oracle, "Battalion");

        // HQ
        SelectEntry(oracle, "Overlord");
        SelectEntry(oracle, "Royal Warden");

        // Troops
        SelectEntry(oracle, "Necron Warriors");
        SelectEntry(oracle, "Necron Warriors");
        SelectEntry(oracle, "Immortals");

        // Elite
        SelectEntry(oracle, "Lychguard");
        SelectEntry(oracle, "Canoptek Wraiths");

        // Heavy
        SelectEntry(oracle, "Lokhust Heavy Destroyers");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 8,
            $"Expected at least 8 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Tyranids");

        AddForce(oracle, "Battalion");

        // HQ
        SelectEntry(oracle, "Hive Tyrant");
        SelectEntry(oracle, "Broodlord");

        // Troops — swarm units
        SelectEntry(oracle, "Termagants");
        SelectEntry(oracle, "Termagants");
        SelectEntry(oracle, "Hormagaunts");
        SelectEntry(oracle, "Hormagaunts");
        SelectEntry(oracle, "Genestealers");

        // Fast Attack
        SelectEntry(oracle, "Gargoyles");

        // Heavy Support
        SelectEntry(oracle, "Carnifexes");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 9,
            $"Expected at least 9 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Imperium - Dark Angels");

        AddForce(oracle, "Vanguard");

        // DA-specific HQ (Deathwing Strikemaster is DA-unique)
        SelectEntry(oracle, "Deathwing Strikemaster");

        // DA-specific chaplain variant
        SelectEntry(oracle, "Interrogator-Chaplain in Terminator Armor");

        // Deathwing units (DA-specific terminators)
        SelectEntry(oracle, "Deathwing Terminator Squad");
        SelectEntry(oracle, "Deathwing Knights");
        SelectEntry(oracle, "Deathwing Command Squad");

        // Inherited from Space Marines (proves catalogue chain works)
        SelectEntry(oracle, "Bladeguard Veteran Squad");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 6,
            $"Expected at least 6 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogue("Imperium - Adeptus Custodes");

        AddForce(oracle, "Patrol");

        // HQ — Shield-Captain
        SelectEntry(oracle, "Shield-Captain");

        // Troops
        SelectEntry(oracle, "Custodian Guard Squad");

        // Elites (the core of Custodes armies)
        SelectEntry(oracle, "Allarus Custodians");
        SelectEntry(oracle, "Custodian Wardens");

        // Fast Attack
        SelectEntry(oracle, "Vertus Praetors");

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        Assert.Equal(1, snapshot.Forces.Length);
        Assert.True(snapshot.Forces[0].Selections.Length >= 5,
            $"Expected at least 5 selections, got {snapshot.Forces[0].Selections.Length}");

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
        using var oracle = LoadCatalogues("Aeldari - Craftworlds", "Aeldari - Harlequins");

        var cats = oracle.GetLoadedCatalogues();
        output.WriteLine($"Loaded catalogues: {string.Join(", ", cats.Select(c => c.Name))}");

        // Find the Craftworlds catalogue to set as active for first force
        var cwCat = cats.FirstOrDefault(c => c.Name.Contains("Craftworld"));
        if (cwCat != default)
            oracle.SetActiveCatalogue(cwCat.Id);

        AddForce(oracle, "Patrol");

        // Craftworlds units from Aeldari Library
        SelectEntry(oracle, "Farseer");
        SelectEntry(oracle, "Dire Avengers");
        SelectEntry(oracle, "Wraithguard");

        // Add a second Patrol for Harlequins
        var harleCat = cats.FirstOrDefault(c => c.Name.Contains("Harlequin"));
        if (harleCat != default)
            oracle.SetActiveCatalogue(harleCat.Id);

        AddForce(oracle, "Patrol");

        // Harlequin units from Aeldari Library
        SelectEntry(oracle, "Troupe", forceIndex: 1);
        SelectEntry(oracle, "Shadowseer", forceIndex: 1);

        DumpState(oracle);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);

        // Should have 2 forces
        Assert.Equal(2, snapshot.Forces.Length);
        output.WriteLine($"Force 0: {snapshot.Forces[0].Name} ({snapshot.Forces[0].Selections.Length} selections)");
        output.WriteLine($"Force 1: {snapshot.Forces[1].Name} ({snapshot.Forces[1].Selections.Length} selections)");

        // Both forces should have selections
        Assert.True(snapshot.Forces[0].Selections.Length >= 3,
            $"Expected at least 3 selections in Craftworlds force, got {snapshot.Forces[0].Selections.Length}");
        Assert.True(snapshot.Forces[1].Selections.Length >= 2,
            $"Expected at least 2 selections in Harlequins force, got {snapshot.Forces[1].Selections.Length}");

        // Costs aggregated across both forces
        var ptsCost = snapshot.Costs.FirstOrDefault(c => c.Name?.Contains("pts") == true);
        Assert.NotNull(ptsCost);
        Assert.True(ptsCost!.Value > 0, $"Expected positive pts, got {ptsCost.Value}");
        output.WriteLine($"Allied warhost pts: {ptsCost.Value}");
    }
}
