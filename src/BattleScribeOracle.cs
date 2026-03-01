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
    private readonly List<CostType> _setupCostTypes = [];
    private readonly Dictionary<string, SelectionEntry> _entryLookup = new();

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
    /// Select a specific entry by index on a specific force by index.
    /// Entry index refers to _setupSelectionEntries order.
    /// </summary>
    public List<Selection> SelectEntryByIndex(int forceIndex, int entryIndex)
    {
        EnsureInitialized();
        var forces = GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        if (entryIndex < 0 || entryIndex >= _setupSelectionEntries.Count)
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        return SelectEntry(forces[forceIndex], _setupSelectionEntries[entryIndex]);
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

    // ===== File-loading API (loads real XML data via SimpleXML Persister) =====

    /// <summary>
    /// Load a game system from a .gst XML file using SimpleXML deserialization.
    /// Bypasses DataUtils wrapper to avoid IKVM cross-assembly class loading issues.
    /// </summary>
    public void LoadGameSystemFile(string gstFilePath)
    {
        var gs = DeserializeXml<GameSystem>(gstFilePath);
        _gameSystem = gs;
    }

    /// <summary>
    /// Load a catalogue from a .cat XML file using SimpleXML deserialization.
    /// </summary>
    public void LoadCatalogueFile(string catFilePath)
    {
        var cat = DeserializeXml<Catalogue>(catFilePath);
        _catalogues[cat.getId()] = cat;
    }

    /// <summary>
    /// Deserialize an XML file to a Java model type using SimpleXML Persister.
    /// This replicates what DataUtils does internally without cross-assembly issues.
    /// </summary>
    private static T DeserializeXml<T>(string filePath) where T : class
    {
        // Use the default Persister with strict=false (matching @Root(strict=false) annotations)
        var persister = new org.simpleframework.xml.core.Persister();
        var file = new java.io.File(filePath);
        // Get java.lang.Class from .NET Type using IKVM intrinsics
        var javaClass = java.lang.Class.forName(typeof(T).FullName!.Replace('+', '$'));
        var result = persister.read(javaClass, file, false);
        return (T)(result ?? throw new InvalidOperationException(
            $"SimpleXML deserialization returned null for {filePath}"));
    }

    /// <summary>
    /// Initialize the engine after loading game system and catalogues from files.
    /// </summary>
    public List<string> InitializeFromLoadedData()
    {
        if (_gameSystem is null)
            throw new InvalidOperationException("Load a game system file first.");

        // Populate force entries from loaded data
        _setupForceEntries.Clear();
        var feIter = _gameSystem.getForceEntries().iterator();
        while (feIter.hasNext())
            _setupForceEntries.Add((ForceEntry)feIter.next());

        // Set up catalogue reference for AddForceByIndex
        if (_catalogues.Count > 0)
            _setupCatalogue = _catalogues.Values.First();

        return Initialize(_gameSystem, _catalogues);
    }

    /// <summary>
    /// Get list of force entry names (for test inspection).
    /// </summary>
    public List<string> GetAvailableForceEntryNames()
    {
        return _setupForceEntries.Select(fe => fe.getName() ?? "?").ToList();
    }

    /// <summary>
    /// Get available selection entry names from a catalogue.
    /// </summary>
    public List<string> GetCatalogueSelectionEntryNames(string catalogueId)
    {
        if (!_catalogues.TryGetValue(catalogueId, out var cat))
            return [];
        var result = new List<string>();
        var iter = cat.getSelectionEntries().iterator();
        while (iter.hasNext())
        {
            var se = (SelectionEntry)iter.next();
            result.Add(se.getName() ?? "?");
        }
        return result;
    }

    /// <summary>
    /// Select a catalogue selection entry by name (first match) on the first force.
    /// Returns count of selections created, or -1 if not found.
    /// </summary>
    public int SelectCatalogueEntryByName(string entryName, string catalogueId)
    {
        EnsureInitialized();
        if (!_catalogues.TryGetValue(catalogueId, out var cat))
            return -1;

        var forces = GetForces();
        if (forces.Count == 0) return -1;

        var iter = cat.getSelectionEntries().iterator();
        while (iter.hasNext())
        {
            var se = (SelectionEntry)iter.next();
            if (se.getName() == entryName)
            {
                var sels = SelectEntry(forces[0], se);
                return sels.Count;
            }
        }
        return -1;
    }

    // ===== Spec-based API (accepts pure .NET spec records) =====

    /// <summary>
    /// Set up the oracle from a complete scenario spec. Returns initialization errors.
    /// </summary>
    public List<string> SetupFromSpec(ScenarioSpec scenario)
    {
        var costTypes = scenario.GameSystem.CostTypes?.Select(ct =>
            JavaModelFactory.CreateCostType(ct.Id, ct.Name, ct.DefaultCostLimit, ct.Hidden, ct.Limit)).ToArray();

        var forceEntries = scenario.GameSystem.ForceEntries?.Select(fe =>
            JavaModelFactory.CreateForceEntry(fe.Id, fe.Name,
                categoryLinks: fe.CategoryLinks?.Select(cl =>
                    JavaModelFactory.CreateCategoryLink(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray())).ToArray();

        var categoryEntries = scenario.GameSystem.CategoryEntries?.Select(ce =>
            JavaModelFactory.CreateCategoryEntry(ce.Id, ce.Name)).ToArray();

        var gs = JavaModelFactory.CreateGameSystem(
            id: scenario.GameSystem.Id,
            name: scenario.GameSystem.Name,
            costTypes: costTypes,
            forceEntries: forceEntries,
            categoryEntries: categoryEntries);

        var selectionEntries = scenario.Catalogue.SelectionEntries?
            .Select(BuildSelectionEntry).ToArray();
        var entryLinks = scenario.Catalogue.EntryLinks?.Select(BuildEntryLink).ToArray();

        var cat = JavaModelFactory.CreateCatalogue(
            scenario.Catalogue.Id, scenario.Catalogue.Name,
            scenario.Catalogue.GameSystemId,
            selectionEntries: selectionEntries,
            entryLinks: entryLinks);

        _setupCatalogue = cat;
        _setupForceEntries.Clear();
        if (forceEntries != null)
            _setupForceEntries.AddRange(forceEntries);
        _setupSelectionEntries.Clear();
        if (selectionEntries != null)
            _setupSelectionEntries.AddRange(selectionEntries);
        _setupCostTypes.Clear();
        if (costTypes != null)
            _setupCostTypes.AddRange(costTypes);
        _entryLookup.Clear();
        if (selectionEntries != null)
            foreach (var se in selectionEntries)
                IndexEntries(se);

        return Initialize(gs, new Dictionary<string, Catalogue> { [scenario.Catalogue.Id] = cat });
    }

    private static SelectionEntry BuildSelectionEntry(SelectionEntrySpec spec)
    {
        var costs = spec.Costs?.Select(c => JavaModelFactory.CreateCost(c.Name, c.TypeId, c.Value)).ToArray();
        var constraints = spec.Constraints?.Select(c =>
            JavaModelFactory.CreateConstraint(c.Id, c.Type, c.Value, c.Field, c.Scope,
                c.Shared, c.IncludeChildSelections, c.IncludeChildForces)).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var childEntries = spec.ChildEntries?.Select(BuildSelectionEntry).ToArray();
        var categoryLinks = spec.CategoryLinks?.Select(cl =>
            JavaModelFactory.CreateCategoryLink(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray();

        var entry = JavaModelFactory.CreateSelectionEntry(
            spec.Id, spec.Name, spec.Type,
            hidden: spec.Hidden,
            costs: costs,
            constraints: constraints,
            modifiers: modifiers,
            selectionEntries: childEntries,
            categoryLinks: categoryLinks,
            collective: spec.Collective);

        if (spec.ModifierGroups != null)
            foreach (var mg in spec.ModifierGroups)
                entry.getModifierGroups().add(BuildModifierGroup(mg));

        if (spec.SelectionEntryGroups != null)
            foreach (var seg in spec.SelectionEntryGroups)
                entry.getSelectionEntryGroups().add(BuildSelectionEntryGroup(seg));

        if (spec.Rules != null)
            foreach (var ruleSpec in spec.Rules)
            {
                var ruleModifiers = ruleSpec.Modifiers?.Select(BuildModifier).ToArray();
                var rule = JavaModelFactory.CreateRule(ruleSpec.Id, ruleSpec.Name, ruleSpec.Description,
                    ruleSpec.Hidden, ruleSpec.Page, ruleModifiers);
                entry.getRules().add(rule);
            }

        if (spec.Profiles != null)
            foreach (var profileSpec in spec.Profiles)
            {
                var chars = profileSpec.Characteristics?.Select(c =>
                    JavaModelFactory.CreateCharacteristic(c.Name, c.TypeId, c.Value)).ToArray();
                var profileModifiers = profileSpec.Modifiers?.Select(BuildModifier).ToArray();
                var profile = JavaModelFactory.CreateProfile(profileSpec.Id, profileSpec.Name,
                    profileSpec.TypeId, profileSpec.TypeName, profileSpec.Hidden, chars, profileModifiers);
                entry.getProfiles().add(profile);
            }

        if (spec.InfoGroups != null)
            foreach (var igSpec in spec.InfoGroups)
            {
                var igProfiles = igSpec.Profiles?.Select(ps =>
                {
                    var cs = ps.Characteristics?.Select(c =>
                        JavaModelFactory.CreateCharacteristic(c.Name, c.TypeId, c.Value)).ToArray();
                    var ms = ps.Modifiers?.Select(BuildModifier).ToArray();
                    return JavaModelFactory.CreateProfile(ps.Id, ps.Name, ps.TypeId, ps.TypeName, ps.Hidden, cs, ms);
                }).ToArray();
                var igRules = igSpec.Rules?.Select(rs =>
                {
                    var ms = rs.Modifiers?.Select(BuildModifier).ToArray();
                    return JavaModelFactory.CreateRule(rs.Id, rs.Name, rs.Description, rs.Hidden, rs.Page, ms);
                }).ToArray();
                var igModifiers = igSpec.Modifiers?.Select(BuildModifier).ToArray();
                entry.getInfoGroups().add(
                    JavaModelFactory.CreateInfoGroup(igSpec.Id, igSpec.Name, igSpec.Hidden, igProfiles, igRules, igModifiers));
            }

        if (spec.EntryLinks != null)
            foreach (var el in spec.EntryLinks)
                entry.getEntryLinks().add(BuildEntryLink(el));

        if (!string.IsNullOrEmpty(spec.Page))
            entry.setPage(spec.Page);

        return entry;
    }

    private static SelectionEntryGroup BuildSelectionEntryGroup(SelectionEntryGroupSpec spec)
    {
        var constraints = spec.Constraints?.Select(c =>
            JavaModelFactory.CreateConstraint(c.Id, c.Type, c.Value, c.Field, c.Scope,
                c.Shared, c.IncludeChildSelections, c.IncludeChildForces)).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var childEntries = spec.SelectionEntries?.Select(BuildSelectionEntry).ToArray();

        return JavaModelFactory.CreateSelectionEntryGroup(
            spec.Id, spec.Name,
            hidden: spec.Hidden,
            defaultSelectionEntryId: spec.DefaultSelectionEntryId,
            selectionEntries: childEntries,
            constraints: constraints,
            modifiers: modifiers);
    }

    private static EntryLink BuildEntryLink(EntryLinkSpec spec)
    {
        var costs = spec.Costs?.Select(c => JavaModelFactory.CreateCost(c.Name, c.TypeId, c.Value)).ToArray();
        var constraints = spec.Constraints?.Select(c =>
            JavaModelFactory.CreateConstraint(c.Id, c.Type, c.Value, c.Field, c.Scope,
                c.Shared, c.IncludeChildSelections, c.IncludeChildForces)).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var categoryLinks = spec.CategoryLinks?.Select(cl =>
            JavaModelFactory.CreateCategoryLink(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray();

        return JavaModelFactory.CreateEntryLink(
            spec.Id, spec.Name, spec.TargetId, spec.Type, spec.Hidden,
            costs, constraints, modifiers, categoryLinks);
    }

    private static Modifier BuildModifier(ModifierSpec spec)
    {
        var conditions = spec.Conditions?.Select(c =>
            JavaModelFactory.CreateCondition(c.Type, c.Value, c.Field, c.Scope, c.ChildId,
                c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray();

        var conditionGroups = spec.ConditionGroups?.Select(BuildConditionGroup).ToArray();

        var repeats = spec.Repeats?.Select(r =>
            JavaModelFactory.CreateRepeat(r.Value, r.Repeats, r.Field, r.Scope, r.ChildId,
                r.RoundUp, r.Shared, r.IncludeChildSelections, r.IncludeChildForces, r.PercentValue)).ToArray();

        var m = JavaModelFactory.CreateModifier(spec.Type, spec.Field, spec.Value,
            conditions: conditions, repeats: repeats);

        if (conditionGroups != null)
            foreach (var cg in conditionGroups)
                m.getConditionGroups().add(cg);

        return m;
    }

    private static net.battlescribe.model.data.ConditionGroup BuildConditionGroup(ConditionGroupSpec spec)
    {
        var conditions = spec.Conditions?.Select(c =>
            JavaModelFactory.CreateCondition(c.Type, c.Value, c.Field, c.Scope, c.ChildId,
                c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray();

        var childGroups = spec.ConditionGroups?.Select(BuildConditionGroup).ToArray();

        return JavaModelFactory.CreateConditionGroup(spec.Type, conditions, childGroups);
    }

    private static net.battlescribe.model.data.ModifierGroup BuildModifierGroup(ModifierGroupSpec spec)
    {
        var conditions = spec.Conditions?.Select(c =>
            JavaModelFactory.CreateCondition(c.Type, c.Value, c.Field, c.Scope, c.ChildId,
                c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray();

        var conditionGroups = spec.ConditionGroups?.Select(BuildConditionGroup).ToArray();

        var repeats = spec.Repeats?.Select(r =>
            JavaModelFactory.CreateRepeat(r.Value, r.Repeats, r.Field, r.Scope, r.ChildId,
                r.RoundUp, r.Shared, r.IncludeChildSelections, r.IncludeChildForces, r.PercentValue)).ToArray();

        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var group = JavaModelFactory.CreateModifierGroup(conditions, conditionGroups, repeats, modifiers);
        if (spec.ModifierGroups != null)
            foreach (var nested in spec.ModifierGroups)
                group.getModifierGroups().add(BuildModifierGroup(nested));
        return group;
    }

    private void IndexEntries(SelectionEntry entry)
    {
        _entryLookup[entry.getId()] = entry;
        var children = JavaListToList<SelectionEntry>(entry.getSelectionEntries());
        foreach (var child in children)
            IndexEntries(child);
        var groups = JavaListToList<SelectionEntryGroup>(entry.getSelectionEntryGroups());
        foreach (var group in groups)
        {
            var groupEntries = JavaListToList<SelectionEntry>(group.getSelectionEntries());
            foreach (var ge in groupEntries)
                IndexEntries(ge);
        }
    }

    /// <summary>
    /// Get a setup selection entry by index (for OracleRosterEngine).
    /// </summary>
    internal SelectionEntry GetSetupSelectionEntry(int index) => _setupSelectionEntries[index];

    /// <summary>
    /// Get a cost type by ID (for SetCostLimit).
    /// </summary>
    internal CostType? GetCostTypeById(string id) =>
        _setupCostTypes.FirstOrDefault(ct => ct.getId() == id);

    /// <summary>
    /// Find a selection entry by its ID (searches all entries including children).
    /// </summary>
    internal SelectionEntry? GetEntryById(string id) =>
        _entryLookup.TryGetValue(id, out var entry) ? entry : null;

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
