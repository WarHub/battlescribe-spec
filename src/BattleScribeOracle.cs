using System.Diagnostics.CodeAnalysis;
using System.Linq;
using net.battlescribe.model.data;
using net.battlescribe.model.roster;
using JavaEngine = net.battlescribe.engine.a.f;
using JavaPerfMetrics = net.battlescribe.engine.b.e;
using JavaHashMap = java.util.HashMap;
using JavaList = java.util.List;
using JavaArrayList = java.util.ArrayList;

namespace BattleScribeSpec;

/// <summary>
/// Wraps the BattleScribe Java engine (via IKVM) to provide a C#-friendly API
/// for oracle testing. Enables running the same operations in both the original
/// BattleScribe engine and the wham/.NET implementation, then comparing results.
/// </summary>
public sealed class BattleScribeOracle : IDisposable
{
    private readonly JavaEngine _engine;
    private GameSystem? _gameSystem;
    private readonly Dictionary<string, Catalogue> _catalogues = new();
    private bool _initialized;

    public BattleScribeOracle(int threadCount = 1)
    {
        var logger = new SilentLogger();
        var perfMetrics = new JavaPerfMetrics();
        // Use dynamic to bypass C# type name ambiguity caused by obfuscated names.
        // The platform enum is net.battlescribe.engine.constants.a+e, value "d" = desktop.
        dynamic desktopPlatform = GetDesktopPlatform();
        _engine = new JavaEngine(threadCount, false, desktopPlatform, logger, perfMetrics);
    }

    private static object GetDesktopPlatform()
    {
        // IKVM maps Java enums as classes extending java.lang.Enum.
        // Fields are mangled with __<> prefix. We find field "d" by iterating all fields
        // because GetField can have issues with angle-bracket names.
        var engineAsm = typeof(JavaEngine).Assembly;
        var platformType = engineAsm.GetType("net.battlescribe.engine.constants.a+e")
            ?? throw new InvalidOperationException("Platform enum type not found in engine assembly.");
        var fields = platformType.GetFields(
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic);
        // Find the field for "d" (desktop) — the 4th enum constant (a=android, b=android-debug, c=ios, d=desktop)
        var desktopField = fields.FirstOrDefault(f => f.Name.EndsWith("d") && f.FieldType == platformType)
            ?? fields.Where(f => f.FieldType == platformType).Skip(3).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Desktop platform field not found. Available fields: {string.Join(", ", fields.Select(f => f.Name))}");
        return desktopField.GetValue(null)!;
    }

    /// <summary>
    /// Initialize the engine with a game system, catalogues, and an empty roster.
    /// </summary>
    public List<string> Initialize(GameSystem gameSystem, IReadOnlyDictionary<string, Catalogue> catalogues)
    {
        _gameSystem = gameSystem;
        _catalogues.Clear();
        foreach (var kvp in catalogues)
            _catalogues[kvp.Key] = kvp.Value;

        var roster = new Roster();
        roster.setId(java.util.UUID.randomUUID().toString());
        roster.setName("Oracle Roster");
        roster.setGameSystemId(gameSystem.getId());
        roster.setGameSystemName(gameSystem.getName());
        roster.setGameSystemRevision(gameSystem.getRevision());

        var forceMap = new JavaHashMap();
        var linkedCatMap = new JavaHashMap();
        var favouritesMap = new JavaHashMap();

        var errors = _engine.a(roster, gameSystem, forceMap, linkedCatMap, favouritesMap, true);
        _initialized = true;

        return JavaListToStringErrors(errors);
    }

    /// <summary>
    /// Convenience method: create a minimal game system and initialize the engine.
    /// </summary>
    public List<string> InitializeMinimal(string id, string name)
    {
        var gs = new GameSystem();
        gs.setId(id);
        gs.setName(name);
        gs.setRevision(1);
        gs.setBattleScribeVersion("2.03");
        gs.setAuthorName("Test");

        return Initialize(gs, new Dictionary<string, Catalogue>());
    }

    /// <summary>
    /// Get the roster name (safe wrapper for test access).
    /// </summary>
    public string? GetRosterName()
    {
        EnsureInitialized();
        return GetRoster().getName();
    }

    /// <summary>
    /// Get the game system ID from the roster.
    /// </summary>
    public string? GetRosterGameSystemId()
    {
        EnsureInitialized();
        return GetRoster().getGameSystemId();
    }

    /// <summary>
    /// Add a force to the roster using the specified catalogue and force entry.
    /// </summary>
    public (Force? Force, List<string> Errors) AddForce(
        Catalogue catalogue,
        ForceEntry forceEntry,
        IReadOnlyDictionary<string, Catalogue>? linkedCatalogues = null)
    {
        EnsureInitialized();

        var linkedCatMap = new JavaHashMap();
        if (linkedCatalogues != null)
        {
            foreach (var kvp in linkedCatalogues)
                linkedCatMap.put(kvp.Key, kvp.Value);
        }

        var favourites = new JavaArrayList();
        var errors = new JavaArrayList();

        var force = _engine.b(_gameSystem, catalogue, linkedCatMap, forceEntry, favourites, errors);

        return (force, JavaListToStringErrors(errors));
    }

    /// <summary>
    /// Select (add) an entry under the given parent, returning the created selection(s).
    /// </summary>
    public List<Selection> SelectEntry(BaseSelectionParent parent, SelectionEntry entry)
    {
        EnsureInitialized();
        var javaList = _engine.b(parent, entry);
        return JavaListToList<Selection>(javaList);
    }

    /// <summary>
    /// Deselect (remove) a selection, returning affected selections.
    /// </summary>
    public List<Selection> DeselectEntry(Selection selection)
    {
        EnsureInitialized();
        var javaList = _engine.m(selection);
        return JavaListToList<Selection>(javaList);
    }

    /// <summary>
    /// Set the number of selections for an entry under a parent.
    /// </summary>
    public void SetNumSelections(BaseSelectionParent parent, SelectionEntry entry, int count)
    {
        EnsureInitialized();
        _engine.a(parent, entry, count);
    }

    /// <summary>
    /// Duplicate a selection.
    /// </summary>
    public Selection? DuplicateSelection(Selection selection)
    {
        EnsureInitialized();
        return _engine.k(selection);
    }

    /// <summary>
    /// Remove a force from the roster.
    /// </summary>
    public bool RemoveForce(Force force)
    {
        EnsureInitialized();
        return _engine.g(force);
    }

    /// <summary>
    /// Set cost limit for a cost type.
    /// </summary>
    public void SetCostLimit(CostType costType, double value)
    {
        EnsureInitialized();
        _engine.a(costType, value);
    }

    /// <summary>
    /// Get all validation errors for the current roster.
    /// </summary>
    public List<string> GetValidationErrors()
    {
        EnsureInitialized();
        var javaErrors = _engine.q();
        return JavaListToStringErrors(javaErrors);
    }

    /// <summary>
    /// Check if the roster has any validation errors.
    /// </summary>
    public bool HasValidationErrors()
    {
        EnsureInitialized();
        return _engine.r();
    }

    /// <summary>
    /// Get the current roster.
    /// </summary>
    public Roster GetRoster()
    {
        EnsureInitialized();
        return _engine.a();
    }

    /// <summary>
    /// Get all forces in the roster (flat list).
    /// </summary>
    public List<Force> GetForces()
    {
        EnsureInitialized();
        return JavaListToList<Force>(_engine.l());
    }

    /// <summary>
    /// Get all selections in the roster (flat list).
    /// </summary>
    public List<Selection> GetAllSelections()
    {
        EnsureInitialized();
        return JavaListToList<Selection>(_engine.n());
    }

    /// <summary>
    /// Get roster costs.
    /// </summary>
    public List<Cost> GetRosterCosts()
    {
        EnsureInitialized();
        return JavaListToList<Cost>(GetRoster().getCosts());
    }

    /// <summary>
    /// Whether the engine is currently loading/processing.
    /// </summary>
    public bool IsLoading => _engine.p();

    // ===== High-level encapsulated API (hides Java types from callers) =====

    private Catalogue? _setupCatalogue;
    private readonly List<ForceEntry> _setupForceEntries = [];
    private readonly List<SelectionEntry> _setupSelectionEntries = [];

    /// <summary>
    /// Set up the oracle with a Patrol force entry (no units).
    /// </summary>
    public void SetupWithPatrolForce()
    {
        var forceEntry = JavaModelFactory.CreateForceEntry("fe-patrol", "Patrol");
        var gs = JavaModelFactory.CreateGameSystem(forceEntries: [forceEntry]);
        _setupCatalogue = JavaModelFactory.CreateCatalogue("cat-1", "Cat", "test-gs");
        _setupForceEntries.Clear();
        _setupForceEntries.Add(forceEntry);
        _setupSelectionEntries.Clear();

        Initialize(gs, new Dictionary<string, Catalogue> { ["cat-1"] = _setupCatalogue });
    }

    /// <summary>
    /// Set up the oracle with a Patrol force and a unit entry.
    /// </summary>
    public void SetupWithPatrolAndUnit(bool withCosts = false)
    {
        var costs = withCosts
            ? new[] { JavaModelFactory.CreateCost("pts", "pts", 100.0) }
            : null;
        var unitEntry = JavaModelFactory.CreateSelectionEntry("se-unit", "Marine Squad", "unit", costs: costs);
        var forceEntry = JavaModelFactory.CreateForceEntry("fe-patrol", "Patrol");

        var costTypes = withCosts
            ? new[] { JavaModelFactory.CreateCostType("pts", "pts", defaultCostLimit: 2000) }
            : null;
        var gs = JavaModelFactory.CreateGameSystem(forceEntries: [forceEntry], costTypes: costTypes);
        _setupCatalogue = JavaModelFactory.CreateCatalogue("cat-1", "Cat", "test-gs",
            selectionEntries: [unitEntry]);

        _setupForceEntries.Clear();
        _setupForceEntries.Add(forceEntry);
        _setupSelectionEntries.Clear();
        _setupSelectionEntries.Add(unitEntry);

        Initialize(gs, new Dictionary<string, Catalogue> { ["cat-1"] = _setupCatalogue });
    }

    /// <summary>
    /// Add a force by index (from setup force entries).
    /// </summary>
    public List<string> AddForceByIndex(int index)
    {
        EnsureInitialized();
        if (_setupCatalogue is null)
            throw new InvalidOperationException("Call SetupWith* before AddForceByIndex.");
        var (_, errors) = AddForce(_setupCatalogue, _setupForceEntries[index]);
        return errors;
    }

    /// <summary>
    /// Select the first available selection entry on the first force.
    /// Returns the number of selections created.
    /// </summary>
    public int SelectFirstAvailableEntry()
    {
        EnsureInitialized();
        var forces = GetForces();
        if (forces.Count == 0)
            throw new InvalidOperationException("No forces available. Call AddForceByIndex first.");
        if (_setupSelectionEntries.Count == 0)
            throw new InvalidOperationException("No selection entries configured. Use SetupWithPatrolAndUnit.");
        var selections = SelectEntry(forces[0], _setupSelectionEntries[0]);
        return selections.Count;
    }

    /// <summary>
    /// Get the number of forces in the roster.
    /// </summary>
    public int GetForceCount()
    {
        EnsureInitialized();
        return GetForces().Count;
    }

    /// <summary>
    /// Get total selection count in the roster (flat list).
    /// </summary>
    public int GetAllSelectionCount()
    {
        EnsureInitialized();
        return GetAllSelections().Count;
    }

    /// <summary>
    /// Deselect the first selection in the roster.
    /// </summary>
    public void DeselectFirstSelection()
    {
        EnsureInitialized();
        var selections = GetAllSelections();
        if (selections.Count == 0)
            throw new InvalidOperationException("No selections to deselect.");
        DeselectEntry(selections[0]);
    }

    /// <summary>
    /// Remove the first force in the roster.
    /// </summary>
    public bool RemoveFirstForce()
    {
        EnsureInitialized();
        var forces = GetForces();
        if (forces.Count == 0)
            throw new InvalidOperationException("No forces to remove.");
        return RemoveForce(forces[0]);
    }

    /// <summary>
    /// Get roster costs as (name, value) pairs for display.
    /// </summary>
    public List<(string Name, double Value)> GetRosterCostsSummary()
    {
        EnsureInitialized();
        return GetRosterCosts()
            .Select(c => (c.getName() ?? "?", c.getValue()))
            .ToList();
    }

    // ===== Spec-based API (accepts pure .NET spec records) =====

    /// <summary>
    /// Set up the oracle from a complete scenario spec. Returns initialization errors.
    /// </summary>
    public List<string> SetupFromSpec(ScenarioSpec scenario)
    {
        var costTypes = scenario.GameSystem.CostTypes?.Select(ct =>
            JavaModelFactory.CreateCostType(ct.Id, ct.Name, ct.DefaultCostLimit)).ToArray();

        var forceEntries = scenario.GameSystem.ForceEntries?.Select(fe =>
            JavaModelFactory.CreateForceEntry(fe.Id, fe.Name)).ToArray();

        var gs = JavaModelFactory.CreateGameSystem(
            id: scenario.GameSystem.Id,
            name: scenario.GameSystem.Name,
            costTypes: costTypes,
            forceEntries: forceEntries);

        var selectionEntries = scenario.Catalogue.SelectionEntries?
            .Select(BuildSelectionEntry).ToArray();

        var cat = JavaModelFactory.CreateCatalogue(
            scenario.Catalogue.Id, scenario.Catalogue.Name,
            scenario.Catalogue.GameSystemId,
            selectionEntries: selectionEntries);

        _setupCatalogue = cat;
        _setupForceEntries.Clear();
        if (forceEntries != null)
            _setupForceEntries.AddRange(forceEntries);
        _setupSelectionEntries.Clear();
        if (selectionEntries != null)
            _setupSelectionEntries.AddRange(selectionEntries);

        return Initialize(gs, new Dictionary<string, Catalogue> { [scenario.Catalogue.Id] = cat });
    }

    private static SelectionEntry BuildSelectionEntry(SelectionEntrySpec spec)
    {
        var costs = spec.Costs?.Select(c => JavaModelFactory.CreateCost(c.Name, c.TypeId, c.Value)).ToArray();
        var constraints = spec.Constraints?.Select(c =>
            JavaModelFactory.CreateConstraint(c.Id, c.Type, c.Value, c.Field, c.Scope)).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var childEntries = spec.ChildEntries?.Select(BuildSelectionEntry).ToArray();

        return JavaModelFactory.CreateSelectionEntry(
            spec.Id, spec.Name, spec.Type,
            costs: costs,
            constraints: constraints,
            modifiers: modifiers,
            selectionEntries: childEntries);
    }

    private static Modifier BuildModifier(ModifierSpec spec)
    {
        var conditions = spec.Conditions?.Select(c =>
            JavaModelFactory.CreateCondition(c.Type, c.Value, c.Field, c.Scope, c.ChildId,
                percentValue: c.PercentValue)).ToArray();

        return JavaModelFactory.CreateModifier(spec.Type, spec.Field, spec.Value, conditions: conditions);
    }

    /// <summary>
    /// Get name of first selection in first force (for modifier testing).
    /// </summary>
    public string? GetFirstSelectionName()
    {
        EnsureInitialized();
        var selections = GetAllSelections();
        return selections.Count > 0 ? selections[0].getName() : null;
    }

    /// <summary>
    /// Get all selection names in the roster.
    /// </summary>
    public List<string?> GetAllSelectionNames()
    {
        EnsureInitialized();
        return GetAllSelections().Select(s => s.getName()).ToList();
    }

    /// <summary>
    /// Diagnostic: list types in the BattleScribeEngine assembly for debugging IKVM type resolution.
    /// </summary>
    public static List<string> DiagnosticListEngineTypes()
    {
        var engineAsm = typeof(JavaEngine).Assembly;
        var result = new List<string>();
        foreach (var t in engineAsm.GetTypes()
            .Where(t => t.FullName?.Contains("constants") == true
                     || t.FullName?.Contains("engine.b") == true)
            .OrderBy(t => t.FullName))
        {
            result.Add($"{t.FullName} | IsEnum={t.IsEnum} | IsClass={t.IsClass} | Base={t.BaseType?.Name}");
            if (t.FullName == "net.battlescribe.engine.constants.a+e")
            {
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic))
                {
                    result.Add($"  Field: {f.Name} | Type={f.FieldType.Name} | Static={f.IsStatic}");
                }
            }
        }
        return result;
    }

    public void Dispose()
    {
        // No explicit Java resource cleanup needed with IKVM
    }

    [MemberNotNull(nameof(_gameSystem))]
    private void EnsureInitialized()
    {
        if (!_initialized || _gameSystem is null)
            throw new InvalidOperationException("Oracle not initialized. Call Initialize() first.");
    }

    private static List<T> JavaListToList<T>(JavaList? javaList)
    {
        if (javaList is null) return [];
        var result = new List<T>(javaList.size());
        var iter = javaList.iterator();
        while (iter.hasNext())
        {
            result.Add((T)iter.next());
        }
        return result;
    }

    private static List<string> JavaListToStringErrors(JavaList? javaList)
    {
        if (javaList is null) return [];
        var result = new List<string>(javaList.size());
        var iter = javaList.iterator();
        while (iter.hasNext())
        {
            var item = iter.next();
            result.Add(item?.ToString() ?? "(null error)");
        }
        return result;
    }

    /// <summary>
    /// Silent logger implementation for the BattleScribe engine.
    /// </summary>
    private sealed class SilentLogger : java.lang.Object, net.battlescribe.engine.b.c
    {
        public void a(string message) { }
        public void b(string message) { }
    }
}
