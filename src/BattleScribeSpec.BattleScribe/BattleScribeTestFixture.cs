using WarHub.ArmouryModel.Source;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec;

/// <summary>
/// Test fixture that initializes both the BattleScribe Java engine (engine) and
/// the wham data model with the same data, enabling engine comparison tests.
/// </summary>
public sealed class BattleScribeTestFixture : IDisposable
{
    public BattleScribeEngine Engine { get; }

    // wham-side data
    public GamesystemNode? Gamesystem { get; private set; }
    public RosterNode? Roster { get; private set; }
    public Dictionary<string, CatalogueNode> Catalogues { get; } = [];

    // Java-side data (kept for engine operations)
    private net.battlescribe.model.data.GameSystem? _javaGameSystem;
    private readonly Dictionary<string, net.battlescribe.model.data.Catalogue> _javaCatalogues = [];
    private readonly List<net.battlescribe.model.data.ForceEntry> _javaForceEntries = [];
    private readonly List<net.battlescribe.model.data.SelectionEntry> _javaSelectionEntries = [];

    public BattleScribeTestFixture()
    {
        Engine = new BattleScribeEngine();
    }

    /// <summary>
    /// Set up a minimal game system in both engines.
    /// </summary>
    public void SetupMinimalGameSystem(string id = "test-gs", string name = "Test Game System")
    {
        // Java side
        _javaGameSystem = JavaModelFactory.CreateGameSystem(id: id, name: name);
        Engine.Initialize(_javaGameSystem, _javaCatalogues);

        // wham side
        Gamesystem = new GamesystemCore
        {
            Id = id,
            Name = name,
            Revision = 1,
            BattleScribeVersion = "2.03",
        }.ToNode();

        Roster = new RosterCore
        {
            Id = System.Guid.NewGuid().ToString(),
            Name = "Test Roster",
            GameSystemId = id,
            GameSystemName = name,
            GameSystemRevision = 1,
        }.ToNode();
    }

    /// <summary>
    /// Set up a game system with a cost type, force entry, and catalogue containing a unit.
    /// </summary>
    public void SetupWithUnit(
        string unitName = "Marine Squad",
        double unitCost = 100.0,
        string costTypeName = "pts")
    {
        var gsId = "test-gs";
        var catId = "cat-1";
        var feId = "fe-patrol";
        var seId = "se-unit";
        var ctId = "pts";

        // --- Java side ---
        var javaCostType = JavaModelFactory.CreateCostType(ctId, costTypeName, 2000);
        var javaForceEntry = JavaModelFactory.CreateForceEntry(feId, "Patrol");
        var javaUnit = JavaModelFactory.CreateSelectionEntry(seId, unitName, "unit",
            costs: [JavaModelFactory.CreateCost(costTypeName, ctId, unitCost)]);
        _javaGameSystem = JavaModelFactory.CreateGameSystem(
            id: gsId,
            costTypes: [javaCostType],
            forceEntries: [javaForceEntry]);
        var javaCat = JavaModelFactory.CreateCatalogue(catId, "Test Cat", gsId,
            selectionEntries: [javaUnit]);
        _javaCatalogues[catId] = javaCat;
        _javaForceEntries.Clear();
        _javaForceEntries.Add(javaForceEntry);
        _javaSelectionEntries.Clear();
        _javaSelectionEntries.Add(javaUnit);

        Engine.Initialize(_javaGameSystem, _javaCatalogues);

        // --- wham side ---
        Gamesystem = new GamesystemCore
        {
            Id = gsId,
            Name = "Test Game System",
            Revision = 1,
            BattleScribeVersion = "2.03",
            CostTypes = [new CostTypeCore { Id = ctId, Name = costTypeName, DefaultCostLimit = 2000m }],
            ForceEntries = [new ForceEntryCore { Id = feId, Name = "Patrol" }],
        }.ToNode();

        var whamCat = new CatalogueCore
        {
            Id = catId,
            Name = "Test Cat",
            GamesystemId = gsId,
            Revision = 1,
            BattleScribeVersion = "2.03",
            SelectionEntries =
            [
                new SelectionEntryCore
                {
                    Id = seId,
                    Name = unitName,
                    Type = SelectionEntryKind.Unit,
                    Costs = [new CostCore { Name = costTypeName, TypeId = ctId, Value = (decimal)unitCost }],
                }
            ],
        }.ToNode();

        Catalogues[catId] = whamCat;

        Roster = new RosterCore
        {
            Id = System.Guid.NewGuid().ToString(),
            Name = "Test Roster",
            GameSystemId = gsId,
            GameSystemName = "Test Game System",
            GameSystemRevision = 1,
        }.ToNode();
    }

    /// <summary>
    /// Add the first force entry via the Java engine and return the snapshot.
    /// </summary>
    public List<string> AddForce(int forceIndex = 0)
    {
        if (_javaGameSystem is null || _javaCatalogues.Count == 0)
        {
            throw new InvalidOperationException("Call SetupWithUnit first.");
        }

        var cat = _javaCatalogues.Values.First();
        var (_, errors) = Engine.AddForce(cat, _javaForceEntries[forceIndex]);
        return errors;
    }

    /// <summary>
    /// Select a unit entry via the Java engine.
    /// </summary>
    public int SelectEntry(int entryIndex = 0)
    {
        var forces = Engine.GetForces();
        if (forces.Count == 0)
        {
            throw new InvalidOperationException("No forces. Call AddForce first.");
        }

        var selections = Engine.SelectEntry(forces[0], _javaSelectionEntries[entryIndex]);
        return selections.Count;
    }

    /// <summary>
    /// Capture the current Java engine state as a snapshot for comparison.
    /// </summary>
    public RosterState CaptureEngineSnapshot()
        => ModelConverter.CaptureEngineSnapshot(Engine);

    /// <summary>
    /// Capture the wham roster state as a snapshot for comparison.
    /// </summary>
    public RosterState CaptureWhamSnapshot(IReadOnlyList<string>? errors = null)
    {
        if (Roster is null)
        {
            throw new InvalidOperationException("Roster not initialized.");
        }

        return ModelConverter.CaptureWhamSnapshot(Roster, errors);
    }

    public void Dispose()
    {
        Engine.Dispose();
    }
}
