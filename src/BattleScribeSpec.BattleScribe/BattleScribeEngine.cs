using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using net.battlescribe.model.data;
using net.battlescribe.model.roster;
using BsRoster = net.battlescribe.model.roster.Roster;
using JavaArrayList = java.util.ArrayList;
using JavaEngine = net.battlescribe.engine.a.f;
using JavaHashMap = java.util.HashMap;
using JavaList = java.util.List;
using JavaPerfMetrics = net.battlescribe.engine.b.e;

namespace BattleScribeSpec;

/// <summary>
/// Wraps the BattleScribe Java engine (via IKVM) to provide a C#-friendly API
/// for engine testing. Enables running the same operations in both the original
/// BattleScribe engine and the wham/.NET implementation, then comparing results.
/// </summary>
/// <remarks>
/// This class is NOT thread-safe. All methods must be called from a single thread.
/// The <c>threadCount</c> constructor parameter controls the Java engine's internal
/// thread pool, not the thread-safety of this wrapper.
/// </remarks>
public sealed class BattleScribeEngine : IDisposable
{
    private readonly JavaEngine _engine;
    private GameSystem? _gameSystem;
    private readonly Dictionary<string, Catalogue> _catalogues = [];
    private bool _initialized;
    private bool _autoSelectDone;

    public BattleScribeEngine(int threadCount = 1)
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
        var desktopField = fields.FirstOrDefault(f => f.Name.EndsWith('d') && f.FieldType == platformType)
            ?? fields.Where(f => f.FieldType == platformType).Skip(3).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Desktop platform field not found. Available fields: {string.Join(", ", fields.Select(f => f.Name))}");
        var platformValue = desktopField.GetValue(null)
            ?? throw new InvalidOperationException("Desktop platform field was found but had null value.");
        var enumName = platformValue.GetType().GetMethod("name")?.Invoke(platformValue, null)?.ToString();
        var isDesktop = string.Equals(enumName, "d", StringComparison.OrdinalIgnoreCase)
            || string.Equals(enumName, "DESKTOP", StringComparison.OrdinalIgnoreCase);
        if (!isDesktop)
        {
            throw new InvalidOperationException(
                $"Resolved platform enum is '{enumName ?? "<null>"}' instead of expected desktop.");
        }

        return platformValue;
    }

    /// <summary>
    /// Optional roster name set before Initialize. If null, defaults to "Test Roster".
    /// </summary>
    public string? RosterName { get; set; }

    /// <summary>
    /// Initialize the engine with a game system, catalogues, and an empty roster.
    /// </summary>
    public List<string> Initialize(GameSystem gameSystem, IReadOnlyDictionary<string, Catalogue> catalogues)
    {
        _gameSystem = gameSystem;
        _catalogues.Clear();
        _forceCatalogueMap.Clear();
        foreach (var kvp in catalogues)
        {
            _catalogues[kvp.Key] = kvp.Value;
        }

        var roster = new BsRoster();
        roster.setId(java.util.UUID.randomUUID().toString());
        roster.setName(RosterName ?? "Test Roster");
        roster.setGameSystemId(gameSystem.getId());
        roster.setGameSystemName(gameSystem.getName());
        roster.setGameSystemRevision(gameSystem.getRevision());

        var forceMap = new JavaHashMap();
        var linkedCatMap = new JavaHashMap();
        var favouritesMap = new JavaHashMap();

        var errors = _engine.a(roster, gameSystem, forceMap, linkedCatMap, favouritesMap, true);
        _initialized = true;

        // Set up cost types and cost limits from game system (after engine init)
        var ctIter = gameSystem.getCostTypes().iterator();
        while (ctIter.hasNext())
        {
            var ct = (CostType)ctIter.next();
            var dcl = ct.getDefaultCostLimit();
            // Negative values (e.g. -1) mean "no cost limit" in BattleScribe convention
            if (dcl >= 0)
            {
                var limit = new net.battlescribe.model.data.Cost();
                limit.setName(ct.getName());
                limit.setTypeId(ct.getId());
                limit.setValue(dcl);
                roster.getCostLimits().add(limit);
            }
            // Roster cost (starts at zero, engine calculates)
            var cost = new net.battlescribe.model.data.Cost();
            cost.setName(ct.getName());
            cost.setTypeId(ct.getId());
            cost.setValue(0.0);
            roster.getCosts().add(cost);
        }

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
            {
                linkedCatMap.put(kvp.Key, kvp.Value);
            }
        }

        var favourites = new JavaArrayList();
        var errors = new JavaArrayList();

        var force = _engine.b(_gameSystem, catalogue, linkedCatMap, forceEntry, favourites, errors);

        // After creating the force, auto-select default root entries (entries with min>=1).
        // The BattleScribe desktop UI bundles force creation into setRoster(bl=true),
        // which calls the engine's private x() method ("Select default root entries").
        // Our flow creates forces separately via selectRootForce, which doesn't call x().
        // We replicate the desktop behavior by calling x() once after the first force.
        // Note: x() processes ALL forces and always creates new selections, so calling
        // it more than once would create duplicates. The desktop only calls it once too.
        if (!_autoSelectDone)
        {
            _autoSelectDone = true;
            SelectDefaultRootEntries();
            // x() creates selections without calling t() (the full refresh).
            // The desktop's setRoster(z=true) calls a(true,true)+v()+d()+w() after x().
            // We call t() which performs u()+a(false,true)+v()+d()+w() — this refreshes
            // all CHANGED selections (auto-selected entries are marked changed).
            // selectRootForce already called t() before x(), so this second t() only
            // processes the newly auto-selected entries.
            Refresh();
        }

        return (force, JavaListToStringErrors(errors));
    }

    /// <summary>
    /// Select (add) an entry under the given parent, returning the created selection(s).
    /// </summary>
    public List<Selection> SelectEntry(BaseSelectionParent parent, SelectionEntry entry)
    {
        EnsureInitialized();
        try
        {
            var javaList = _engine.b(parent, entry);
            return JavaListToList<Selection>(javaList);
        }
        catch (NullReferenceException ex)
        {
            var entryId = entry?.getId() ?? "null";
            var entryName = entry?.getName() ?? "null";
            throw new InvalidOperationException(
                $"NullRef in Java SelectEntry(entry={entryId}/{entryName}). " +
                $"Parent type: {parent?.GetType().Name ?? "null"}, is Force: {parent is Force}. " +
                $"Forces: {GetForces().Count}.", ex);
        }
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
    /// Set the number of selections for an entry under a parent (atomic engine API).
    /// Note: The BattleScribe desktop UI does NOT use this method for count changes.
    /// Instead, it loops individual selectEntry/deselectEntry calls. This method exists
    /// for direct engine access but callers should prefer the loop approach via
    /// <see cref="BattleScribeRosterEngine.SetSelectionCount"/> for UI-accurate behavior.
    /// </summary>
    public void SetNumSelections(BaseSelectionParent parent, SelectionEntry entry, int count)
    {
        EnsureInitialized();
        // The engine's setNumSelections (f.java:880) calls t() internally,
        // which includes the full refresh cycle (costs + validation + cache clear).
        _engine.a(parent, entry, count);
    }

    /// <summary>
    /// Get the number of changes needed to reach a target count for an entry.
    /// Returns delta (positive = add, negative = remove, 0 = no-op/isDuplicate).
    /// The engine's getNumChanges enforces min/max constraints and returns 0 for
    /// isDuplicate entries (which cannot have their count changed).
    /// </summary>
    public int GetNumChanges(BaseSelectionParent parent, SelectionEntry entry, int targetCount)
    {
        EnsureInitialized();
        // _engine.b(parent, entry, count) = getNumChanges (f.java:895)
        return _engine.b(parent, entry, targetCount);
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
        var removed = _engine.g(force);
        if (removed)
        {
            _forceCatalogueMap.Remove(force);
        }

        return removed;
    }

    /// <summary>
    /// Set cost limit for a cost type.
    /// </summary>
    public void SetCostLimit(CostType costType, decimal value)
    {
        EnsureInitialized();
        _engine.a(costType, (double)value);
    }

    /// <summary>
    /// Get all validation errors for the current roster with structured entry links.
    /// Walks the roster tree per-element (like BattleScribe's rosterManager.d()),
    /// using validationErrorIds for shared entries and engine entry data for non-shared.
    /// </summary>
    public List<ValidationErrorState> GetValidationErrors()
    {
        EnsureInitialized();
        var result = new List<ValidationErrorState>();
        var roster = _engine.a();
        // Roster-level errors (e.g., cost limit) — resolve cost type from message
        CollectRosterErrors(roster, result);
        // Walk forces → categories → selections
        foreach (var force in JavaListToList<Force>(roster.getForces()))
        {
            CollectElementErrors(force, "force", force.getId(), force.getEntryId(), result);
            foreach (var category in JavaListToList<Category>(force.getCategories()))
            {
                CollectElementErrors(category, "category", category.getId(), category.getEntryId(), result);
            }
            foreach (var selection in JavaListToList<Selection>(force.getSelections()))
            {
                CollectSelectionErrors(selection, result);
            }
        }
        // One placement rule, shared with the UI driver so the two BattleScribe engines cannot
        // drift apart on where an error belongs -- see BattleScribeErrorPlacement.
        BattleScribeErrorPlacement.ApplyTo(
            result,
            linkId => _linkTargetMap.TryGetValue(linkId, out var targetId) ? targetId : null);
        return result;
    }

    private void CollectSelectionErrors(Selection selection, List<ValidationErrorState> result)
    {
        CollectElementErrors(selection, "selection", selection.getId(), selection.getEntryId(), result);
        foreach (var child in JavaListToList<Selection>(selection.getSelections()))
        {
            CollectSelectionErrors(child, result);
        }
    }

    private void CollectRosterErrors(BsRoster roster, List<ValidationErrorState> result)
    {
        var errors = roster.getValidationErrors();
        if (errors is null || errors.size() == 0)
        {
            return;
        }

        // Build cost limit lookup for resolving cost type IDs
        var costLimits = JavaListToList<Cost>(roster.getCostLimits());

        // Build error ID map from roster's validation error IDs (shared entries)
        // Use a multimap: an entry can have multiple constraints with different IDs.
        var errorIdMap = new Dictionary<string, List<string>>();
        var errorIds = ((BaseRosterElement)roster).getValidationErrorIds();
        if (errorIds is not null)
        {
            var idIter = errorIds.iterator();
            while (idIter.hasNext())
            {
                var errorId = idIter.next()?.ToString();
                if (errorId is null)
                {
                    continue;
                }

                var parts = errorId.Split("::");
                if (parts.Length >= 3)
                {
                    if (!errorIdMap.TryGetValue(parts[1], out var list))
                    {
                        list = [];
                        errorIdMap[parts[1]] = list;
                    }
                    if (!list.Contains(parts[2]))
                    {
                        list.Add(parts[2]);
                    }
                }
            }
        }

        var iter = errors.iterator();
        while (iter.hasNext())
        {
            var item = iter.next();
            if (item?.GetType().FullName != "net.battlescribe.engine.b.a")
            {
                result.Add(new ValidationErrorState(item?.ToString() ?? "(null error)"));
                continue;
            }
            dynamic error = item;
            var message = (string?)error.b() ?? "(null error)";

            string? entryId = null;
            string? constraintId = null;

            // 1. Try cost limit resolution (cost limit errors mention the cost name)
            foreach (var limit in costLimits)
            {
                var costName = limit.getName();
                if (costName is not null && message.Contains(costName))
                {
                    entryId = "costLimits";
                    constraintId = limit.getTypeId();
                    break;
                }
            }

            // 2. Try shared entry error IDs (roster-scoped shared constraints)
            if (entryId is null)
            {
                foreach (var kvp in errorIdMap)
                {
                    var entry = GetEntryById(kvp.Key);
                    if (entry is not null && message.Contains(entry.getName()))
                    {
                        entryId = kvp.Key;
                        constraintId = ResolveConstraintFromEntry(entry, kvp.Value, message);
                        break;
                    }
                }
            }

            // 3. Try ForceEntry constraint resolution (field=forces constraints)
            if (entryId is null)
            {
                (entryId, constraintId) = ResolveForceEntryFromMessage(message);
            }

            // 4. Fall back to SelectionEntry constraint resolution
            if (entryId is null || constraintId is null)
            {
                var (resolvedEntryId, resolvedConstraintId) = ResolveEntryFromMessage(message);
                entryId ??= resolvedEntryId;
                constraintId ??= resolvedConstraintId;
            }

            result.Add(new ValidationErrorState(message, "roster", roster.getId(), null,
                entryId, constraintId));
        }
    }

    /// <summary>
    /// Resolve entryId and constraintId for roster-level errors by matching
    /// ForceEntry names in the message and looking up their constraints.
    /// Handles field=forces constraints on ForceEntry definitions.
    /// </summary>
    private (string? entryId, string? constraintId) ResolveForceEntryFromMessage(string message)
    {
        foreach (var fe in _setupForceEntries)
        {
            var feName = fe.getName();
            if (feName is null || !message.Contains(feName))
            {
                continue;
            }

            var constraints = JavaListToList<Constraint>(fe.getConstraints());
            foreach (var c in constraints)
            {
                var type = c.getType();
                if ((type == "min" && (message.Contains("must have") || message.Contains("must spend"))) ||
                    (type == "max" && (message.Contains("too many") || message.Contains("too much"))))
                {
                    return (fe.getId(), c.getId());
                }
            }
        }
        return (null, null);
    }

    private void CollectElementErrors(
        BaseRosterElement element, string ownerType, string? ownerId, string? ownerEntryId,
        List<ValidationErrorState> result)
    {
        var errors = element.getValidationErrors();
        if (errors is null || errors.size() == 0)
        {
            return;
        }

        // Build a lookup of error IDs on this element (shared entries only)
        // Format: ownerId::entryId::constraintId
        // Use a multimap: an entry can have multiple constraints with different IDs.
        var errorIdMap = new Dictionary<string, List<string>>();
        var errorIds = element.getValidationErrorIds();
        if (errorIds is not null)
        {
            var idIter = errorIds.iterator();
            while (idIter.hasNext())
            {
                var errorId = idIter.next()?.ToString();
                if (errorId is null)
                {
                    continue;
                }

                var parts = errorId.Split("::");
                if (parts.Length >= 3)
                {
                    if (!errorIdMap.TryGetValue(parts[1], out var list))
                    {
                        list = [];
                        errorIdMap[parts[1]] = list;
                    }
                    if (!list.Contains(parts[2]))
                    {
                        list.Add(parts[2]);
                    }
                }
            }
        }

        var iter = errors.iterator();
        while (iter.hasNext())
        {
            var item = iter.next();
            if (item?.GetType().FullName != "net.battlescribe.engine.b.a")
            {
                result.Add(new ValidationErrorState(item?.ToString() ?? "(null error)"));
                continue;
            }

            dynamic error = item;
            var message = (string?)error.b() ?? "(null error)";

            string? entryId = null;
            string? constraintId = null;

            // Try shared entry error IDs first
            foreach (var kvp in errorIdMap)
            {
                var entry = GetEntryById(kvp.Key);
                if (entry is not null && message.Contains(entry.getName()))
                {
                    entryId = kvp.Key;
                    constraintId = ResolveConstraintFromEntry(entry, kvp.Value, message);
                    break;
                }
            }

            // Fall back to engine entry data when entryId or constraintId unresolved
            if (entryId is null || constraintId is null)
            {
                var (resolvedEntryId, resolvedConstraintId) = ResolveEntryFromMessage(message);
                entryId ??= resolvedEntryId;
                constraintId ??= resolvedConstraintId;
            }

            // Detect hidden entry errors: "cannot have any selections of {name} (hidden)"
            if (entryId is null && message.Contains("(hidden)"))
            {
                foreach (var (id, entry) in _entryLookup)
                {
                    var entryName = entry.getName();
                    if (entryName is not null && message.Contains(entryName))
                    {
                        entryId = id;
                        constraintId = "hidden";
                        break;
                    }
                }
            }

            result.Add(new ValidationErrorState(message, ownerType, ownerId, ownerEntryId, entryId, constraintId));
        }
    }

    /// <summary>
    /// Given a shared entry and a list of candidate constraint IDs from errorIdMap,
    /// pick the correct constraint ID by matching constraint value against the error message.
    /// Returns null when no value match is found (caller should fall through to other resolution).
    /// </summary>
    private static string? ResolveConstraintFromEntry(
        SelectionEntry entry, List<string> candidateConstraintIds, string message)
    {
        // Match by constraint value in message.
        // BS error messages include "(maximum N)" or "(minimum N)" with the constraint value.
        var constraints = JavaListToList<Constraint>(entry.getConstraints());
        foreach (var candidateId in candidateConstraintIds)
        {
            foreach (var c in constraints)
            {
                if (c.getId() != candidateId)
                {
                    continue;
                }

                var value = (int)c.getValue();
                if (message.Contains($"maximum {value}") || message.Contains($"minimum {value}"))
                {
                    return candidateId;
                }
            }
        }

        // No value match — return null so caller can try other resolution methods
        return null;
    }

    /// <summary>
    /// Resolve entryId and constraintId by matching the error message against
    /// the engine's own entry data (names and constraint types).
    /// </summary>
    private (string? entryId, string? constraintId) ResolveEntryFromMessage(string message)
    {
        foreach (var (id, entry) in _entryLookup)
        {
            var entryName = entry.getName();
            if (entryName is null || !message.Contains(entryName))
            {
                continue;
            }

            var constraints = JavaListToList<Constraint>(entry.getConstraints());
            string? firstMatch = null;
            foreach (var c in constraints)
            {
                var type = c.getType();
                if (MessageMatchesConstraintKind(message, type))
                {
                    // Prefer constraint whose value matches the message
                    var value = (int)c.getValue();
                    if (message.Contains($"maximum {value}") || message.Contains($"minimum {value}"))
                    {
                        return (id, c.getId());
                    }
                    firstMatch ??= c.getId();
                }
            }

            // A link onto this entry carries constraints of its own, and expansion merges them into
            // the same expanded copy — so both are candidates for one message. The VALUE is what
            // says which fired, and it is asked here rather than after `firstMatch` because a
            // kind-match whose value the message contradicts is not evidence: with `(maximum 2)` on
            // an entry whose own maximum is 4, the entry's constraint is the one constraint the
            // message rules OUT. Returning it named a limit that 3 selections do not exceed.
            (string linkId, string constraintId)? linkFirstMatch = null;
            if (_linkConstraintLookup.TryGetValue(id, out var linkConstraints))
            {
                foreach (var (linkId, constraintId, constraintType, constraintValue) in linkConstraints)
                {
                    if (!MessageMatchesConstraintKind(message, constraintType))
                    {
                        continue;
                    }
                    if (message.Contains($"maximum {constraintValue}") ||
                        message.Contains($"minimum {constraintValue}"))
                    {
                        return (linkId, constraintId);
                    }
                    linkFirstMatch ??= (linkId, constraintId);
                }
            }

            if (firstMatch is not null)
            {
                return (id, firstMatch);
            }
            // Entry has no matching constraint — fall back to a link's kind-match.
            if (linkFirstMatch is not null)
            {
                return linkFirstMatch.Value;
            }
        }
        // Also search SelectionEntryGroups for constraint matches
        foreach (var (id, group) in _groupLookup)
        {
            var groupName = group.getName();
            if (groupName is null || !message.Contains(groupName))
            {
                continue;
            }

            var constraints = JavaListToList<Constraint>(group.getConstraints());
            foreach (var c in constraints)
            {
                if (MessageMatchesConstraintKind(message, c.getType()))
                {
                    return (id, c.getId());
                }
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Whether the message's phrasing is the one BattleScribe renders for this constraint type.
    /// </summary>
    private static bool MessageMatchesConstraintKind(string message, string? type) => type switch
    {
        "min" => message.Contains("must have") || message.Contains("must spend"),
        "max" => message.Contains("too many") || message.Contains("too much"),
        _ => false,
    };

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
    public BsRoster GetRoster()
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
        // Use roster's force list (ArrayList, insertion order) instead of engine's
        // getAllForces() which returns HashMap.values() with non-deterministic order.
        return JavaListToList<Force>(GetRoster().getForces());
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
    private readonly List<Catalogue> _setupCatalogues = [];
    private readonly List<ForceEntry> _setupForceEntries = [];
    private readonly List<SelectionEntry> _setupSelectionEntries = [];
    private readonly List<CostType> _setupCostTypes = [];
    private readonly Dictionary<string, SelectionEntry> _entryLookup = [];
    private readonly Dictionary<string, SelectionEntryGroup> _groupLookup = [];
    // Entry link constraints indexed by target entry ID:
    // targetId → [(linkId, constraintId, constraintType, constraintValue)]. The VALUE is carried
    // because it is what tells a link's constraint apart from its target's when one message could
    // name either — see ResolveEntryFromMessage.
    private readonly Dictionary<string, List<(string linkId, string constraintId, string constraintType, int constraintValue)>> _linkConstraintLookup = [];
    // Entry link target resolution: linkId → targetId
    private readonly Dictionary<string, string> _linkTargetMap = [];
    // Per-catalogue entry lists for multi-catalogue support
    private readonly List<List<SelectionEntry>> _perCatalogueEntries = [];
    // Maps force object identity to catalogue (avoids positional corruption on removal)
    private readonly Dictionary<Force, Catalogue> _forceCatalogueMap = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Set up the engine with a Patrol force entry (no units).
    /// </summary>
    public void SetupWithPatrolForce()
    {
        var forceEntry = JavaModelFactory.CreateForceEntry("fe-patrol", "Patrol");
        var gs = JavaModelFactory.CreateGameSystem(forceEntries: [forceEntry]);
        var cat = JavaModelFactory.CreateCatalogue("cat-1", "Cat", "test-gs");
        _setupCatalogue = cat;
        _setupCatalogues.Clear();
        _setupCatalogues.Add(cat);
        _perCatalogueEntries.Clear();
        _perCatalogueEntries.Add([]);
        _forceCatalogueMap.Clear();
        _setupForceEntries.Clear();
        _setupForceEntries.Add(forceEntry);
        _setupSelectionEntries.Clear();

        Initialize(gs, new Dictionary<string, Catalogue> { ["cat-1"] = cat });
    }

    /// <summary>
    /// Set up the engine with a Patrol force and a unit entry.
    /// </summary>
    public void SetupWithPatrolAndUnit(bool withCosts = false)
    {
        var costs = withCosts
            ? new[] { JavaModelFactory.CreateCost("pts", "pts", 100.0m) }
            : null;
        var unitEntry = JavaModelFactory.CreateSelectionEntry("se-unit", "Marine Squad", "unit", costs: costs);
        var forceEntry = JavaModelFactory.CreateForceEntry("fe-patrol", "Patrol");

        var costTypes = withCosts
            ? new[] { JavaModelFactory.CreateCostType("pts", "pts", defaultCostLimit: 2000) }
            : null;
        var gs = JavaModelFactory.CreateGameSystem(forceEntries: [forceEntry], costTypes: costTypes);
        var cat = JavaModelFactory.CreateCatalogue("cat-1", "Cat", "test-gs",
            selectionEntries: [unitEntry]);
        _setupCatalogue = cat;
        _setupCatalogues.Clear();
        _setupCatalogues.Add(cat);
        _perCatalogueEntries.Clear();
        _perCatalogueEntries.Add([unitEntry]);
        _forceCatalogueMap.Clear();

        _setupForceEntries.Clear();
        _setupForceEntries.Add(forceEntry);
        _setupSelectionEntries.Clear();
        _setupSelectionEntries.Add(unitEntry);

        Initialize(gs, new Dictionary<string, Catalogue> { ["cat-1"] = cat });
    }

    /// <summary>
    /// Add a force by index (from setup force entries) using the specified catalogue.
    /// Automatically resolves linked catalogues from the active catalogue.
    /// </summary>
    public List<string> AddForceByIndex(int index, int catalogueIndex = -1)
    {
        EnsureInitialized();
        if (_setupCatalogues.Count == 0)
        {
            throw new InvalidOperationException("Call SetupWith* or SetupFromSpec before AddForceByIndex.");
        }
        // Use active catalogue when no explicit index given
        if (catalogueIndex < 0 && _setupCatalogue != null)
        {
            catalogueIndex = _setupCatalogues.IndexOf(_setupCatalogue);
            if (catalogueIndex < 0)
            {
                catalogueIndex = 0;
            }
        }
        else if (catalogueIndex < 0)
        {
            catalogueIndex = 0;
        }
        if (catalogueIndex >= _setupCatalogues.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(catalogueIndex),
                $"Catalogue index {catalogueIndex} out of range (have {_setupCatalogues.Count})");
        }

        var catalogue = _setupCatalogues[catalogueIndex];
        var linked = ResolveLinkedCatalogues(catalogue);
        var forcesBefore = new HashSet<Force>(GetForces(), ReferenceEqualityComparer.Instance);
        var (_, errors) = AddForce(catalogue, _setupForceEntries[index], linked);
        foreach (var force in GetForces())
        {
            if (!forcesBefore.Contains(force))
            {
                _forceCatalogueMap[force] = catalogue;
            }
        }
        return errors;
    }

    /// <summary>
    /// Add a child force under an existing parent force.
    /// Resolves the child ForceEntry from the parent force's ForceEntry definition
    /// and creates the child force via the Java engine.
    /// </summary>
    public void AddChildForce(Force parentForce, int childForceEntryIndex, int catalogueIndex = 0)
    {
        EnsureInitialized();
        // Get the parent force's ForceEntry to find child force entries
        var parentForceEntryId = parentForce.getEntryId();
        var parentForceEntry = FindForceEntryById(parentForceEntryId) ?? throw new InvalidOperationException(
                $"Could not find ForceEntry '{parentForceEntryId}' for parent force '{parentForce.getName()}'.");

        var childForceEntries = JavaListToList<ForceEntry>(parentForceEntry.getForceEntries());
        if (childForceEntryIndex < 0 || childForceEntryIndex >= childForceEntries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(childForceEntryIndex),
                $"Child force entry index {childForceEntryIndex} out of range ({childForceEntries.Count} available).");
        }

        var childForceEntry = childForceEntries[childForceEntryIndex];

        if (catalogueIndex < 0)
        {
            catalogueIndex = 0;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(catalogueIndex, _setupCatalogues.Count);

        var catalogue = _setupCatalogues[catalogueIndex];
        var linked = ResolveLinkedCatalogues(catalogue);

        var linkedCatMap = new JavaHashMap();
        if (linked.Count > 0)
        {
            foreach (var kvp in linked)
            {
                linkedCatMap.put(kvp.Key, kvp.Value);
            }
        }

        var favourites = new JavaArrayList();
        var errors = new JavaArrayList();
        // Use the engine's native selectForce(parentForce, ...) to properly add as child
        var childForce = _engine.b(parentForce, _gameSystem, catalogue, linkedCatMap, childForceEntry, favourites, errors) ?? throw new InvalidOperationException("Java engine returned null when creating child force.");

        _forceCatalogueMap[childForce] = catalogue;
    }

    /// <summary>
    /// Recursively search for a ForceEntry by ID in the setup force entries tree.
    /// </summary>
    internal ForceEntry? FindForceEntryById(string? id)
    {
        if (id is null)
        {
            return null;
        }

        foreach (var fe in _setupForceEntries)
        {
            var found = FindForceEntryRecursive(fe, id);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static ForceEntry? FindForceEntryRecursive(ForceEntry entry, string id)
    {
        if (entry.getId() == id)
        {
            return entry;
        }

        foreach (var child in JavaListToList<ForceEntry>(entry.getForceEntries()))
        {
            var found = FindForceEntryRecursive(child, id);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve linked catalogues for a catalogue by reading its CatalogueLink elements
    /// and looking up target catalogue IDs in the loaded catalogue dictionary.
    /// </summary>
    internal Dictionary<string, Catalogue> ResolveLinkedCatalogues(Catalogue catalogue)
    {
        var linked = new Dictionary<string, Catalogue>();
        var linkIter = catalogue.getCatalogueLinks().iterator();
        while (linkIter.hasNext())
        {
            var link = (CatalogueLink)linkIter.next();
            var targetId = link.getTargetId();
            if (targetId != null && _catalogues.TryGetValue(targetId, out var targetCat))
            {
                linked[targetId] = targetCat;
            }
        }
        return linked;
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
        {
            throw new InvalidOperationException("No forces available. Call AddForceByIndex first.");
        }

        var entries = GetEntriesForForce(0);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("No selection entries available for the force.");
        }

        var selections = SelectEntry(forces[0], entries[0]);
        return selections.Count;
    }

    /// <summary>
    /// Select a specific entry by index on a specific force by index.
    /// Entry index refers to entries from the force's catalogue.
    /// </summary>
    public List<Selection> SelectEntryByIndex(int forceIndex, int entryIndex)
    {
        EnsureInitialized();
        var forces = GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        }

        // Resolve entries from the force's catalogue
        var entries = GetEntriesForForce(forceIndex);
        if (entryIndex < 0 || entryIndex >= entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(entryIndex),
                $"Entry index {entryIndex} out of range (have {entries.Count} entries for force {forceIndex})");
        }

        var force = forces[forceIndex];
        var entry = entries[entryIndex];
        if (force is null)
        {
            throw new InvalidOperationException($"Force at index {forceIndex} is null");
        }

        if (entry is null)
        {
            throw new InvalidOperationException($"Entry at index {entryIndex} for force {forceIndex} is null");
        }

        return SelectEntry(force, entry);
    }

    /// <summary>
    /// Get the selection entries available for a given force, using the engine's
    /// catalogue manager which has already resolved entry links into expanded copies.
    /// Falls back to the pre-computed list if the catalogue manager isn't available.
    /// </summary>
    private List<SelectionEntry> GetEntriesForForce(int forceIndex)
    {
        var forces = GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        }

        return GetEntriesForForce(forces[forceIndex]);
    }

    /// <summary>
    /// Get entries for a force object (supports both root and nested forces).
    /// </summary>
    public List<SelectionEntry> GetEntriesForForce(Force force)
    {
        try
        {
            var catMgr = _engine.e(force);
            if (catMgr != null)
            {
                return JavaListToList<SelectionEntry>(catMgr.R());
            }
        }
        catch
        {
            // Fall through to legacy path
        }

        if (_forceCatalogueMap.TryGetValue(force, out var catalogue))
        {
            var catIdx = _setupCatalogues.IndexOf(catalogue);
            if (catIdx >= 0 && catIdx < _perCatalogueEntries.Count)
            {
                return _perCatalogueEntries[catIdx];
            }
        }
        throw new InvalidOperationException(
            "No catalogue mapping found for force. Force must be added via AddForceByIndex.");
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
    /// Get the number of available root selection entries for a force,
    /// as resolved by the Java engine (respects import filtering and CatalogueLink merging).
    /// </summary>
    public int GetAvailableEntryCountForForce(int forceIndex)
    {
        EnsureInitialized();
        var forces = GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        }

        var force = forces[forceIndex];
        var entryNames = new HashSet<string>();
        var categories = JavaListToList<Category>(force.getCategories());
        foreach (var category in categories)
        {
            var entries = JavaListToList<SelectionEntry>(_engine.a(category));
            foreach (var entry in entries)
            {
                entryNames.Add(entry.getName() ?? entry.getId());
            }
        }
        return entryNames.Count;
    }

    /// <summary>
    /// Find and select an entry by name on a force.
    /// Returns the index of the entry, or -1 if not found. Used by integration tests.
    /// </summary>
    public int SelectEntryByName(int forceIndex, string name)
    {
        var entries = GetEntriesForForce(forceIndex);
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].getName(), name, StringComparison.OrdinalIgnoreCase))
            {
                SelectEntryByIndex(forceIndex, i);
                return i;
            }
        }
        return -1;
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
        {
            throw new InvalidOperationException("No selections to deselect.");
        }

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
        {
            throw new InvalidOperationException("No forces to remove.");
        }

        return RemoveForce(forces[0]);
    }

    /// <summary>
    /// Get roster costs as (name, value) pairs for display.
    /// </summary>
    public List<(string Name, double Value)> GetRosterCostsSummary()
    {
        EnsureInitialized();
        return [.. GetRosterCosts().Select(c => (c.getName() ?? "?", c.getValue()))];
    }

    // ===== File-loading API (loads real XML data via DataUtils) =====

    /// <summary>
    /// Load a game system from a .gst XML file using DataUtils deserialization.
    /// DataUtils uses SimpleXML with AnnotationStrategy for full deserialization.
    /// Called via reflection because obfuscated class names create C# namespace conflicts.
    /// Pre-processes XML to fix compatibility issues with newer data formats.
    /// </summary>
    public void LoadGameSystemFile(string gstFilePath)
    {
        using var bis = CreatePreprocessedStream(gstFilePath);
        var gs = (GameSystem)InvokeDataUtils("e", typeof(java.io.InputStream), bis);
        _gameSystem = gs;
    }

    /// <summary>
    /// Load a catalogue from a .cat XML file using DataUtils deserialization.
    /// </summary>
    public void LoadCatalogueFile(string catFilePath)
    {
        using var bis = CreatePreprocessedStream(catFilePath);
        var cat = (Catalogue)InvokeDataUtils("f", typeof(java.io.InputStream), bis);
        _catalogues[cat.getId()] = cat;
    }

    /// <summary>
    /// Read XML file and pre-process to fix compatibility with BattleScribe 2.3.21's model.
    /// Adds default value="" attribute to modifier elements missing it, since the engine's
    /// Modifier class has @Attribute(required=true) on the value field, but newer data
    /// formats allow omitting it.
    /// </summary>
    private static java.io.BufferedInputStream CreatePreprocessedStream(string xmlFilePath)
    {
        var xml = File.ReadAllText(xmlFilePath);
        xml = AddMissingModifierValues(xml);
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        var bais = new java.io.ByteArrayInputStream(bytes);
        return new java.io.BufferedInputStream(bais);
    }

    /// <summary>
    /// Add value="" to modifier elements that are missing the value attribute.
    /// Uses regex to find &lt;modifier elements without a value= attribute and adds one.
    /// </summary>
    private static string AddMissingModifierValues(string xml)
    {
        // Match <modifier (or <repeat) tags that don't already have a value= attribute.
        // The pattern finds the tag up to > or /> and checks for absence of value=.
        return System.Text.RegularExpressions.Regex.Replace(
            xml,
            @"(<modifier\b(?![^>]*\bvalue\s*=))([^>]*?)(\/?>)",
            "$1 value=\"\"$2$3");
    }

    /// <summary>
    /// Invoke a static method on the DataUtils serializer class (net.battlescribe.a.c.e)
    /// via reflection. Direct call is impossible due to IKVM obfuscated name collisions
    /// (class 'c' in net.battlescribe.a conflicts with namespace net.battlescribe.a.c).
    /// </summary>
    private static object InvokeDataUtils(string methodName, Type parameterType, object arg)
    {
        var dataUtilsAssembly = System.Reflection.Assembly.Load("DataUtils");
        var serializerType = dataUtilsAssembly.GetType("net.battlescribe.a.c.e")
            ?? throw new InvalidOperationException("DataUtils serializer type 'net.battlescribe.a.c.e' not found.");
        var method = serializerType.GetMethod(methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            [parameterType])
            ?? throw new InvalidOperationException(
                $"DataUtils method '{methodName}({parameterType.Name})' not found.");
        try
        {
            return method.Invoke(null, [arg])
                ?? throw new InvalidOperationException(
                    $"DataUtils.{methodName} returned null.");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }
    }

    /// <summary>
    /// Serialize the current roster to its BattleScribe <c>.ros</c> XML via DataUtils
    /// <c>a(Roster, OutputStream)</c> — the same serializer path used for catalogue/game-system export.
    /// </summary>
    public string ExportRosterXml()
    {
        var roster = GetRoster();

        // The BattleScribe cost-recalc stores the roster's aggregated <costs>/<costLimits>
        // sorted alphabetically by cost name (e.g. "power" before "pts"). The real desktop
        // app, however, serializes these roster-level totals in cost-type DECLARATION order
        // (the order costTypes appear in the game system). Reorder the roster's own top-level
        // cost lists to declaration order before serialization so the reference engine's
        // exported .ros matches the app byte-for-byte. Scoped strictly to the roster's own
        // cost/costLimit children — selection-level costs are already emitted in declaration
        // order by the engine and are left untouched.
        ReorderCostsByDeclaration(roster.getCosts());
        ReorderCostsByDeclaration(roster.getCostLimits());

        var dataUtils = System.Reflection.Assembly.Load("DataUtils").GetType("net.battlescribe.a.c.e")
            ?? throw new InvalidOperationException("DataUtils serializer type 'net.battlescribe.a.c.e' not found.");
        var write = dataUtils.GetMethod("a",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            [typeof(BsRoster), typeof(java.io.OutputStream)])
            ?? throw new InvalidOperationException("DataUtils serialize 'a(Roster, OutputStream)' not found.");
        var baos = new java.io.ByteArrayOutputStream();
        try
        {
            write.Invoke(null, [roster, baos]);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }

        return System.Text.Encoding.UTF8.GetString(baos.toByteArray());
    }

    /// <summary>
    /// Serialize the current roster and load it straight back, replacing the in-memory roster with
    /// what a fresh load of the saved <c>.ros</c> produces. Round-trip specs place a repeated
    /// <c>expectedState</c> after this to assert that save + load preserved semantics. The byte
    /// round-trip exercises the same DataUtils serializer a disk save/open would; disk is incidental.
    /// </summary>
    public void ReloadRoster()
    {
        EnsureInitialized();
        LoadRosterXml(ExportRosterXml());
    }

    /// <summary>
    /// Deserialize a roster from its BattleScribe <c>.ros</c> XML via DataUtils <c>g(InputStream)</c>
    /// — the roster-side counterpart of <c>e</c> (game system) and <c>f</c> (catalogue) — and hand it
    /// to the Java engine as the current roster.
    /// <para>
    /// This reproduces the desktop app's "Load Roster" path. The app builds a <c>LoadDataParams</c>
    /// (<c>net.battlescribe.engine.b.d</c>) that maps every force in the loaded roster to its catalogue
    /// by <c>catalogueId</c> plus that catalogue's linked catalogues, then calls
    /// <c>RosterManager.setRoster(roster, gs, forceCats, linkedCats, favourites, !saved)</c>. Loading a
    /// SAVED roster passes <c>!saved</c> = <c>false</c>, which suppresses the engine's
    /// "select default root entries" pass — the loaded roster already carries its selections, and
    /// re-running defaults would duplicate them. We pass <c>false</c> for exactly that reason.
    /// </para>
    /// </summary>
    public void LoadRosterXml(string xml)
    {
        EnsureInitialized();
        var gameSystem = _gameSystem
            ?? throw new InvalidOperationException("LoadRosterXml: no game system (setup not run?).");

        var dataUtils = System.Reflection.Assembly.Load("DataUtils").GetType("net.battlescribe.a.c.e")
            ?? throw new InvalidOperationException("DataUtils serializer type 'net.battlescribe.a.c.e' not found.");
        var read = dataUtils.GetMethod("g",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            [typeof(java.io.InputStream)])
            ?? throw new InvalidOperationException("DataUtils deserialize 'g(InputStream)' not found.");

        BsRoster roster;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
            // g() requires a mark-supporting stream — BufferedInputStream provides it.
            var stream = new java.io.BufferedInputStream(new java.io.ByteArrayInputStream(bytes));
            roster = (BsRoster)(read.Invoke(null, [stream])
                ?? throw new InvalidOperationException("LoadRosterXml: DataUtils.g returned null."));
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }

        // Map every force (including child forces) to its catalogue and that catalogue's linked
        // catalogues — the engine resolves each force's entries through this map.
        var forceCatMap = new JavaHashMap();
        var linkedCatMap = new JavaHashMap();
        var favouritesMap = new JavaHashMap();
        _forceCatalogueMap.Clear();

        foreach (var force in FlattenForces(roster))
        {
            var catalogueId = force.getCatalogueId();
            if (catalogueId is null || !_catalogues.TryGetValue(catalogueId, out var catalogue))
            {
                throw new InvalidOperationException(
                    $"LoadRosterXml: force '{force.getId()}' references catalogue '{catalogueId ?? "<null>"}', " +
                    $"which is not loaded (have: {string.Join(", ", _catalogues.Keys)}).");
            }

            forceCatMap.put(force, catalogue);

            var linked = new JavaHashMap();
            foreach (var kvp in ResolveLinkedCatalogues(catalogue))
            {
                linked.put(kvp.Key, kvp.Value);
            }

            linkedCatMap.put(force, linked);
            favouritesMap.put(force, new JavaArrayList());
            _forceCatalogueMap[force] = catalogue;
        }

        // false = do NOT re-run "select default root entries": the loaded roster already has them.
        _engine.a(roster, gameSystem, forceCatMap, linkedCatMap, favouritesMap, false);

        // Defaults have already been applied by whoever produced this roster; a later AddForce must
        // not trigger the one-shot auto-select pass and duplicate them.
        _autoSelectDone = true;
    }

    /// <summary>All forces in a roster, depth-first, parents before their children.</summary>
    private static List<Force> FlattenForces(BsRoster roster)
    {
        var result = new List<Force>();
        void Visit(Force force)
        {
            result.Add(force);
            foreach (var child in JavaListToList<Force>(force.getForces()))
            {
                Visit(child);
            }
        }

        foreach (var force in JavaListToList<Force>(roster.getForces()))
        {
            Visit(force);
        }

        return result;
    }

    /// <summary>
    /// Reorder a roster-level cost list (costs or costLimits) in place to match the cost-type
    /// DECLARATION order captured in <see cref="_setupCostTypes"/>. Stable: cost types not found
    /// in the declaration order (e.g. file-based setups where <see cref="_setupCostTypes"/> is
    /// empty) retain their original relative order, so this degrades to a no-op when the order
    /// is unknown. Only reorders — never adds, drops, or mutates cost values.
    /// </summary>
    private void ReorderCostsByDeclaration(JavaList? costs)
    {
        if (costs is null || costs.size() < 2 || _setupCostTypes.Count == 0)
        {
            return;
        }

        var order = new Dictionary<string, int>();
        for (var i = 0; i < _setupCostTypes.Count; i++)
        {
            var id = _setupCostTypes[i].getId();
            if (id is not null && !order.ContainsKey(id))
            {
                order[id] = i;
            }
        }

        var items = JavaListToList<Cost>(costs);
        var reordered = items
            .Select((c, idx) => (Cost: c, Index: idx))
            .OrderBy(t => order.TryGetValue(t.Cost.getTypeId() ?? "", out var pos) ? pos : int.MaxValue)
            .ThenBy(t => t.Index)
            .Select(t => t.Cost)
            .ToList();

        // No-op if already in the desired order (avoids needless list churn).
        if (reordered.SequenceEqual(items))
        {
            return;
        }

        costs.clear();
        foreach (var c in reordered)
        {
            costs.add(c);
        }
    }

    /// <summary>
    /// Load a catalogue and all its linked catalogue dependencies from a data directory.
    /// Recursively discovers and loads linked catalogues by parsing CatalogueLink elements.
    /// </summary>
    public void LoadCatalogueWithDependencies(string catFilePath, string dataDir)
    {
        // Load the catalogue itself
        LoadCatalogueFile(catFilePath);
        var cat = _catalogues.Values.Last();

        // Build ID→file index for the data directory
        var catFiles = Directory.GetFiles(dataDir, "*.cat");
        var idToFile = new Dictionary<string, string>();
        foreach (var file in catFiles)
        {
            var catalogueId = ReadRootIdAttribute(file)
                ?? throw new InvalidOperationException($"Could not read root 'id' from catalogue file: {file}");
            idToFile[catalogueId] = file;
        }

        // Recursively load linked catalogues
        var loaded = new HashSet<string>(_catalogues.Keys);
        var toLoad = new Queue<Catalogue>();
        toLoad.Enqueue(cat);

        while (toLoad.Count > 0)
        {
            var current = toLoad.Dequeue();
            var linkIter = current.getCatalogueLinks().iterator();
            while (linkIter.hasNext())
            {
                var link = (CatalogueLink)linkIter.next();
                var targetId = link.getTargetId();
                if (targetId == null || loaded.Contains(targetId))
                {
                    continue;
                }

                if (idToFile.TryGetValue(targetId, out var linkedFile))
                {
                    LoadCatalogueFile(linkedFile);
                    loaded.Add(targetId);
                    var linkedCat = _catalogues[targetId];
                    toLoad.Enqueue(linkedCat);
                }
            }
        }
    }

    private static string? ReadRootIdAttribute(string filePath)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using var reader = System.Xml.XmlReader.Create(filePath, settings);
        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element)
            {
                continue;
            }

            return reader.GetAttribute("id");
        }
        return null;
    }

    /// <summary>
    /// Initialize the engine after loading game system and catalogues from files.
    /// </summary>
    public List<string> InitializeFromLoadedData()
    {
        if (_gameSystem is null)
        {
            throw new InvalidOperationException("Load a game system file first.");
        }

        // Populate force entries from loaded data
        _setupForceEntries.Clear();
        var feIter = _gameSystem.getForceEntries().iterator();
        while (feIter.hasNext())
        {
            _setupForceEntries.Add((ForceEntry)feIter.next());
        }

        // Set up catalogue references for AddForceByIndex
        _setupCatalogues.Clear();
        _perCatalogueEntries.Clear();
        _forceCatalogueMap.Clear();
        if (_catalogues.Count > 0)
        {
            _setupCatalogue = _catalogues.Values.First();
            foreach (var cat in _catalogues.Values)
            {
                _setupCatalogues.Add(cat);
                var entries = new List<SelectionEntry>();
                var seIter = cat.getSelectionEntries().iterator();
                while (seIter.hasNext())
                {
                    entries.Add((SelectionEntry)seIter.next());
                }

                _perCatalogueEntries.Add(entries);
            }
        }

        return Initialize(_gameSystem, new Dictionary<string, Catalogue>(_catalogues));
    }

    /// <summary>
    /// Get the names of available force entries from the loaded game system.
    /// </summary>
    public List<string> GetAvailableForceEntryNames()
    {
        return [.. _setupForceEntries.Select(fe => fe.getName() ?? "?")];
    }

    /// <summary>
    /// Set the active catalogue for AddForceByIndex (when multiple catalogues are loaded).
    /// </summary>
    public void SetActiveCatalogue(string catalogueId)
    {
        if (_catalogues.TryGetValue(catalogueId, out var cat))
        {
            _setupCatalogue = cat;
        }
        else
        {
            throw new InvalidOperationException($"Catalogue '{catalogueId}' not loaded.");
        }
    }

    /// <summary>
    /// Get all loaded catalogue IDs and names.
    /// </summary>
    public List<(string Id, string Name)> GetLoadedCatalogues()
    {
        return [.. _catalogues.Select(kvp => (kvp.Key, kvp.Value.getName() ?? "?"))];
    }

    /// <summary>
    /// Find a selection entry by ID across all catalogues (for entry link resolution).
    /// </summary>
    private SelectionEntry? FindSelectionEntryById(string id)
    {
        foreach (var cat in _catalogues.Values)
        {
            // Check shared selection entries first (common target for entry links)
            var sharedIter = cat.getSharedSelectionEntries().iterator();
            while (sharedIter.hasNext())
            {
                var se = (SelectionEntry)sharedIter.next();
                if (se.getId() == id)
                {
                    return se;
                }
            }

            // Direct entries
            var seIter = cat.getSelectionEntries().iterator();
            while (seIter.hasNext())
            {
                var se = (SelectionEntry)seIter.next();
                if (se.getId() == id)
                {
                    return se;
                }
            }
        }

        // Also check game system shared entries
        if (_gameSystem != null)
        {
            var gsSharedIter = _gameSystem.getSharedSelectionEntries().iterator();
            while (gsSharedIter.hasNext())
            {
                var se = (SelectionEntry)gsSharedIter.next();
                if (se.getId() == id)
                {
                    return se;
                }
            }
        }

        return null;
    }

    // ===== Protocol-based API (primary setup path) =====

    /// <summary>
    /// Set up the engine from Protocol types. Returns initialization errors.
    /// </summary>
    public List<string> SetupFromProtocol(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var costTypes = gameSystem.CostTypes?.Select(ct =>
            JavaModelFactory.CreateCostType(ct.Id, ct.Name, ct.DefaultCostLimit, ct.Hidden, ct.Limit)).ToArray();

        var forceEntries = gameSystem.ForceEntries?.Select(BuildForceEntry).ToArray();

        var categoryEntries = gameSystem.CategoryEntries?.Select(ce =>
            JavaModelFactory.CreateCategoryEntry(ce.Id, ce.Name, ce.Hidden,
                ce.Constraints?.Select(BuildConstraint).ToArray(),
                ce.Modifiers?.Select(BuildModifier).ToArray())).ToArray();

        var profileTypes = gameSystem.ProfileTypes?.Select(pt =>
            JavaModelFactory.CreateProfileType(pt.Id, pt.Name,
                pt.CharacteristicTypes?.Select(ct =>
                    JavaModelFactory.CreateCharacteristicType(ct.Id, ct.Name)))).ToArray();

        var gsPublications = gameSystem.Publications?.Select(p =>
            JavaModelFactory.CreatePublication(p.Id, p.Name, p.ShortName ?? "", p.Publisher ?? "",
                p.PublicationDate ?? "", p.PublisherUrl ?? "")).ToArray();
        var gsSelectionEntries = gameSystem.SelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var gsEntryLinks = gameSystem.EntryLinks?.Select(BuildEntryLink).ToArray();
        var gsRules = gameSystem.Rules?.Select(BuildRule).ToArray();
        var gsInfoLinks = gameSystem.InfoLinks?.Select(BuildInfoLink).ToArray();
        var gsSharedSelectionEntries = gameSystem.SharedSelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var gsSharedSelectionEntryGroups = gameSystem.SharedSelectionEntryGroups?.Select(BuildSelectionEntryGroup).ToArray();
        var gsSharedRules = gameSystem.SharedRules?.Select(BuildRule).ToArray();
        var gsSharedProfiles = gameSystem.SharedProfiles?.Select(BuildProfile).ToArray();
        var gsSharedInfoGroups = gameSystem.SharedInfoGroups?.Select(BuildInfoGroup).ToArray();

        var gs = JavaModelFactory.CreateGameSystem(
            id: gameSystem.Id,
            name: gameSystem.Name,
            costTypes: costTypes,
            forceEntries: forceEntries,
            categoryEntries: categoryEntries,
            profileTypes: profileTypes,
            publications: gsPublications,
            selectionEntries: gsSelectionEntries,
            entryLinks: gsEntryLinks,
            rules: gsRules,
            infoLinks: gsInfoLinks,
            sharedSelectionEntries: gsSharedSelectionEntries,
            sharedSelectionEntryGroups: gsSharedSelectionEntryGroups,
            sharedRules: gsSharedRules,
            sharedProfiles: gsSharedProfiles,
            sharedInfoGroups: gsSharedInfoGroups);

        // Build all catalogues
        var catalogueDict = new Dictionary<string, Catalogue>();
        _setupCatalogues.Clear();
        _perCatalogueEntries.Clear();
        _forceCatalogueMap.Clear();
        _setupSelectionEntries.Clear();
        _entryLookup.Clear();
        _groupLookup.Clear();
        _linkConstraintLookup.Clear();
        _linkTargetMap.Clear();

        foreach (var catSpec in catalogues)
        {
            var selectionEntries = catSpec.SelectionEntries?
                .Select(BuildSelectionEntry).ToArray();
            var entryLinks = catSpec.EntryLinks?.Select(BuildEntryLink).ToArray();
            var sharedSelectionEntries = catSpec.SharedSelectionEntries?
                .Select(BuildSelectionEntry).ToArray();
            var sharedSelectionEntryGroups = catSpec.SharedSelectionEntryGroups?
                .Select(BuildSelectionEntryGroup).ToArray();
            var sharedRules = catSpec.SharedRules?.Select(BuildRule).ToArray();
            var sharedProfiles = catSpec.SharedProfiles?.Select(BuildProfile).ToArray();
            var sharedInfoGroups = catSpec.SharedInfoGroups?.Select(BuildInfoGroup).ToArray();
            var catRules = catSpec.Rules?.Select(BuildRule).ToArray();

            var cat = JavaModelFactory.CreateCatalogue(
                catSpec.Id, catSpec.Name, catSpec.GameSystemId,
                library: catSpec.Library,
                selectionEntries: selectionEntries,
                entryLinks: entryLinks,
                sharedSelectionEntries: sharedSelectionEntries,
                sharedSelectionEntryGroups: sharedSelectionEntryGroups,
                sharedRules: sharedRules,
                sharedProfiles: sharedProfiles,
                sharedInfoGroups: sharedInfoGroups,
                rules: catRules,
                costTypes: catSpec.CostTypes?.Select(ct => JavaModelFactory.CreateCostType(ct.Id, ct.Name, ct.DefaultCostLimit ?? -1)).ToArray(),
                profileTypes: catSpec.ProfileTypes?.Select(pt =>
                {
                    var charTypes = pt.CharacteristicTypes?.Select(ct => JavaModelFactory.CreateCharacteristicType(ct.Id, ct.Name)).ToArray();
                    return JavaModelFactory.CreateProfileType(pt.Id, pt.Name, charTypes);
                }).ToArray(),
                categoryEntries: catSpec.CategoryEntries?.Select(ce =>
                {
                    var ceConstraints = ce.Constraints?.Select(BuildConstraint).ToArray();
                    var ceModifiers = ce.Modifiers?.Select(BuildModifier).ToArray();
                    var ceModifierGroups = ce.ModifierGroups?.Select(BuildModifierGroup).ToArray();
                    var ceProfiles = ce.Profiles?.Select(BuildProfile).ToArray();
                    var ceRules = ce.Rules?.Select(BuildRule).ToArray();
                    var ceInfoGroups = ce.InfoGroups?.Select(BuildInfoGroup).ToArray();
                    var ceInfoLinks = ce.InfoLinks?.Select(BuildInfoLink).ToArray();
                    var cePubId = string.IsNullOrEmpty(ce.PublicationId) ? null : ce.PublicationId;
                    var cePage = string.IsNullOrEmpty(ce.Page) ? null : ce.Page;
                    return JavaModelFactory.CreateCategoryEntry(ce.Id, ce.Name, ce.Hidden,
                        ceConstraints, ceModifiers, ceModifierGroups, ceProfiles, ceRules,
                        ceInfoGroups, ceInfoLinks, cePubId, cePage);
                }).ToArray(),
                forceEntries: catSpec.ForceEntries?.Select(BuildForceEntry).ToArray());

            if (catSpec.InfoLinks != null)
            {
                foreach (var il in catSpec.InfoLinks)
                {
                    cat.getInfoLinks().add(BuildInfoLink(il));
                }
            }

            if (catSpec.CatalogueLinks != null)
            {
                foreach (var clSpec in catSpec.CatalogueLinks)
                {
                    cat.getCatalogueLinks().add(
                        JavaModelFactory.CreateCatalogueLink(clSpec.Id, clSpec.Name, clSpec.TargetId, clSpec.ImportRootEntries, clSpec.Type));
                }
            }

            if (catSpec.Publications != null)
            {
                foreach (var pubSpec in catSpec.Publications)
                {
                    cat.getPublications().add(
                        JavaModelFactory.CreatePublication(pubSpec.Id, pubSpec.Name, pubSpec.ShortName ?? "",
                            pubSpec.Publisher ?? "", pubSpec.PublicationDate ?? "", pubSpec.PublisherUrl ?? ""));
                }
            }

            catalogueDict[catSpec.Id] = cat;
            _setupCatalogues.Add(cat);

            // Build shared entry lookup for resolving entry links
            var sharedEntryLookup = new Dictionary<string, SelectionEntry>();
            if (sharedSelectionEntries != null)
            {
                foreach (var se in sharedSelectionEntries)
                {
                    sharedEntryLookup[se.getId()] = se;
                }
            }

            // Track per-catalogue entries (direct entries + entry link targets)
            var catEntries = new List<SelectionEntry>();
            if (selectionEntries != null)
            {
                catEntries.AddRange(selectionEntries);
            }

            if (entryLinks != null)
            {
                foreach (var el in entryLinks)
                {
                    var targetId = el.getTargetId();
                    if (targetId != null && sharedEntryLookup.TryGetValue(targetId, out var target)
                        && !catEntries.Contains(target))
                    {
                        catEntries.Add(target);
                    }
                }
            }

            _perCatalogueEntries.Add(catEntries);

            // Index direct entries and shared entries for lookup by ID.
            if (selectionEntries != null)
            {
                foreach (var se in selectionEntries)
                {
                    IndexEntries(se);
                }
            }
            if (sharedSelectionEntries != null)
            {
                foreach (var se in sharedSelectionEntries)
                {
                    IndexEntries(se);
                }
            }

            // Index entry link constraints and targets for error resolution.
            if (catSpec.EntryLinks != null)
            {
                foreach (var elSpec in catSpec.EntryLinks)
                {
                    if (elSpec.TargetId is not null)
                    {
                        _linkTargetMap[elSpec.Id] = elSpec.TargetId;
                    }

                    if (elSpec.Constraints is { Count: > 0 } && elSpec.TargetId is not null)
                    {
                        if (!_linkConstraintLookup.TryGetValue(elSpec.TargetId, out var list))
                        {
                            list = [];
                            _linkConstraintLookup[elSpec.TargetId] = list;
                        }
                        foreach (var cSpec in elSpec.Constraints)
                        {
                            // Truncated to int to match what the entry-side path does with
                            // `getValue()`, because both are compared against the same rendered
                            // "maximum N" — the two must round the same way or they disagree about
                            // which constraint a message names.
                            list.Add((elSpec.Id, cSpec.Id, cSpec.Type ?? "max", (int)cSpec.Value));
                        }
                    }
                }
            }
        }

        // Default active catalogue is the first loaded catalogue.
        _setupCatalogue = _setupCatalogues.Count > 0 ? _setupCatalogues[0] : null;

        _setupForceEntries.Clear();
        if (forceEntries != null)
        {
            _setupForceEntries.AddRange(forceEntries);
        }
        // Include force entries defined in catalogues
        foreach (var catSpec in catalogues)
        {
            if (catSpec.ForceEntries is { Count: > 0 })
            {
                _setupForceEntries.AddRange(catSpec.ForceEntries.Select(BuildForceEntry));
            }
        }
        _setupCostTypes.Clear();
        if (costTypes != null)
        {
            _setupCostTypes.AddRange(costTypes);
        }

        var initErrors = Initialize(gs, catalogueDict);
        return initErrors;
    }

    private static ForceEntry BuildForceEntry(ProtocolForceEntry feSpec)
    {
        var categoryLinks = feSpec.CategoryLinks?.Select(BuildCategoryLink).ToArray();
        var childForceEntries = feSpec.ForceEntries?.Select(BuildForceEntry).ToArray();
        var constraints = feSpec.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = feSpec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = feSpec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var profiles = feSpec.Profiles?.Select(BuildProfile).ToArray();
        var rules = feSpec.Rules?.Select(BuildRule).ToArray();
        var infoGroups = feSpec.InfoGroups?.Select(BuildInfoGroup).ToArray();
        var infoLinks = feSpec.InfoLinks?.Select(BuildInfoLink).ToArray();
        var pubId = string.IsNullOrEmpty(feSpec.PublicationId) ? null : feSpec.PublicationId;
        var page = string.IsNullOrEmpty(feSpec.Page) ? null : feSpec.Page;
        return JavaModelFactory.CreateForceEntry(feSpec.Id, feSpec.Name,
            hidden: feSpec.Hidden,
            categoryLinks: categoryLinks, forceEntries: childForceEntries,
            constraints: constraints, modifiers: modifiers, modifierGroups: modifierGroups,
            profiles: profiles, rules: rules, infoGroups: infoGroups, infoLinks: infoLinks,
            publicationId: pubId, page: page);
    }

    private static SelectionEntry BuildSelectionEntry(ProtocolSelectionEntry spec)
    {
        var costs = spec.Costs?.Select(c => JavaModelFactory.CreateCost(c.Name, c.TypeId, c.Value)).ToArray();
        var constraints = spec.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var childEntries = spec.SelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var categoryLinks = spec.CategoryLinks?.Select(BuildCategoryLink).ToArray();

        var entry = JavaModelFactory.CreateSelectionEntry(
            spec.Id, spec.Name, spec.Type,
            hidden: spec.Hidden,
            costs: costs,
            constraints: constraints,
            modifiers: modifiers,
            selectionEntries: childEntries,
            categoryLinks: categoryLinks,
            collective: spec.Collective,
            import: spec.Import,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId);

        if (spec.ModifierGroups != null)
        {
            foreach (var mg in spec.ModifierGroups)
            {
                entry.getModifierGroups().add(BuildModifierGroup(mg));
            }
        }

        if (spec.SelectionEntryGroups != null)
        {
            foreach (var seg in spec.SelectionEntryGroups)
            {
                entry.getSelectionEntryGroups().add(BuildSelectionEntryGroup(seg));
            }
        }

        if (spec.Rules != null)
        {
            foreach (var ruleSpec in spec.Rules)
            {
                entry.getRules().add(BuildRule(ruleSpec));
            }
        }

        if (spec.Profiles != null)
        {
            foreach (var profileSpec in spec.Profiles)
            {
                entry.getProfiles().add(BuildProfile(profileSpec));
            }
        }

        if (spec.InfoGroups != null)
        {
            foreach (var igSpec in spec.InfoGroups)
            {
                entry.getInfoGroups().add(BuildInfoGroup(igSpec));
            }
        }

        if (spec.EntryLinks != null)
        {
            foreach (var el in spec.EntryLinks)
            {
                entry.getEntryLinks().add(BuildEntryLink(el));
            }
        }

        if (spec.InfoLinks != null)
        {
            foreach (var il in spec.InfoLinks)
            {
                entry.getInfoLinks().add(BuildInfoLink(il));
            }
        }

        if (!string.IsNullOrEmpty(spec.Page))
        {
            entry.setPage(spec.Page);
        }

        return entry;
    }

    private static SelectionEntryGroup BuildSelectionEntryGroup(ProtocolSelectionEntryGroup spec)
    {
        var constraints = spec.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var childEntries = spec.SelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var childGroups = spec.SelectionEntryGroups?.Select(BuildSelectionEntryGroup).ToArray();
        var entryLinks = spec.EntryLinks?.Select(BuildEntryLink).ToArray();
        var categoryLinks = spec.CategoryLinks?.Select(BuildCategoryLink).ToArray();
        var costs = spec.Costs?.Select(c => JavaModelFactory.CreateCost(c.Name, c.TypeId, c.Value)).ToArray();
        var profiles = spec.Profiles?.Select(BuildProfile).ToArray();
        var rules = spec.Rules?.Select(BuildRule).ToArray();
        var infoGroups = spec.InfoGroups?.Select(BuildInfoGroup).ToArray();
        var infoLinks = spec.InfoLinks?.Select(BuildInfoLink).ToArray();

        return JavaModelFactory.CreateSelectionEntryGroup(
            spec.Id, spec.Name,
            hidden: spec.Hidden,
            defaultSelectionEntryId: spec.DefaultSelectionEntryId,
            collective: spec.Collective,
            selectionEntries: childEntries,
            selectionEntryGroups: childGroups,
            entryLinks: entryLinks,
            categoryLinks: categoryLinks,
            costs: costs,
            constraints: constraints,
            modifiers: modifiers,
            modifierGroups: modifierGroups,
            profiles: profiles,
            rules: rules,
            infoGroups: infoGroups,
            infoLinks: infoLinks,
            page: string.IsNullOrEmpty(spec.Page) ? null : spec.Page,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId,
            import: spec.Import);
    }

    private static Rule BuildRule(ProtocolRule spec)
    {
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        return JavaModelFactory.CreateRule(spec.Id, spec.Name, spec.Description,
            spec.Hidden, spec.Page ?? "", modifiers,
            string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId,
            modifierGroups);
    }

    private static Profile BuildProfile(ProtocolProfile spec)
    {
        var chars = spec.Characteristics?.Select(c =>
            JavaModelFactory.CreateCharacteristic(c.Name, c.TypeId, c.Value)).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var page = string.IsNullOrEmpty(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId;
        return JavaModelFactory.CreateProfile(spec.Id, spec.Name,
            spec.TypeId, spec.TypeName, spec.Hidden, chars, modifiers, page, pubId,
            modifierGroups);
    }

    private static InfoGroup BuildInfoGroup(ProtocolInfoGroup spec)
    {
        var profiles = spec.Profiles?.Select(BuildProfile).ToArray();
        var rules = spec.Rules?.Select(BuildRule).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var childInfoGroups = spec.InfoGroups?.Select(BuildInfoGroup).ToArray();
        var pubId = string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId;
        var page = string.IsNullOrEmpty(spec.Page) ? null : spec.Page;
        var ig = JavaModelFactory.CreateInfoGroup(spec.Id, spec.Name, spec.Hidden, profiles, rules, modifiers, pubId, page,
            modifierGroups, childInfoGroups);
        if (spec.InfoLinks != null)
        {
            foreach (var il in spec.InfoLinks)
            {
                ig.getInfoLinks().add(BuildInfoLink(il));
            }
        }

        return ig;
    }

    private static InfoLink BuildInfoLink(ProtocolInfoLink spec)
    {
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var pubId = string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId;
        var page = string.IsNullOrEmpty(spec.Page) ? null : spec.Page;
        return JavaModelFactory.CreateInfoLink(spec.Id, spec.Name, spec.TargetId, spec.Type,
            spec.Hidden, modifiers, pubId, page, modifierGroups);
    }

    private static EntryLink BuildEntryLink(ProtocolEntryLink spec)
    {
        var costs = spec.Costs?.Select(c => JavaModelFactory.CreateCost(c.Name, c.TypeId, c.Value)).ToArray();
        var constraints = spec.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var categoryLinks = spec.CategoryLinks?.Select(BuildCategoryLink).ToArray();
        var selectionEntries = spec.SelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var selectionEntryGroups = spec.SelectionEntryGroups?.Select(BuildSelectionEntryGroup).ToArray();
        var entryLinks = spec.EntryLinks?.Select(BuildEntryLink).ToArray();
        var profiles = spec.Profiles?.Select(BuildProfile).ToArray();
        var rules = spec.Rules?.Select(BuildRule).ToArray();
        var infoGroups = spec.InfoGroups?.Select(BuildInfoGroup).ToArray();
        var infoLinks = spec.InfoLinks?.Select(BuildInfoLink).ToArray();

        return JavaModelFactory.CreateEntryLink(
            spec.Id, spec.Name, spec.TargetId, spec.Type, spec.Hidden,
            collective: spec.Collective,
            costs: costs, constraints: constraints, modifiers: modifiers,
            modifierGroups: modifierGroups, categoryLinks: categoryLinks,
            selectionEntries: selectionEntries, selectionEntryGroups: selectionEntryGroups,
            entryLinks: entryLinks, profiles: profiles, rules: rules,
            infoGroups: infoGroups, infoLinks: infoLinks,
            import: spec.Import,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId,
            page: string.IsNullOrEmpty(spec.Page) ? null : spec.Page);
    }

    private static CategoryLink BuildCategoryLink(ProtocolCategoryLink cl)
    {
        var constraints = cl.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = cl.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = cl.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var profiles = cl.Profiles?.Select(BuildProfile).ToArray();
        var rules = cl.Rules?.Select(BuildRule).ToArray();
        var infoGroups = cl.InfoGroups?.Select(BuildInfoGroup).ToArray();
        var infoLinks = cl.InfoLinks?.Select(BuildInfoLink).ToArray();
        var pubId = string.IsNullOrEmpty(cl.PublicationId) ? null : cl.PublicationId;
        var page = string.IsNullOrEmpty(cl.Page) ? null : cl.Page;
        return JavaModelFactory.CreateCategoryLink(cl.Id, cl.Name, cl.TargetId, cl.Primary, cl.Hidden,
            constraints, modifiers, modifierGroups, profiles, rules, infoGroups, infoLinks, pubId, page);
    }

    private static Constraint BuildConstraint(ProtocolConstraint c) =>
        JavaModelFactory.CreateConstraint(c.Id, c.Type, c.Value, c.Field, c.Scope,
            c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue);

    private static Modifier BuildModifier(ProtocolModifier spec)
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
        {
            foreach (var cg in conditionGroups)
            {
                m.getConditionGroups().add(cg);
            }
        }

        return m;
    }

    private static net.battlescribe.model.data.ConditionGroup BuildConditionGroup(ProtocolConditionGroup spec)
    {
        var conditions = spec.Conditions?.Select(c =>
            JavaModelFactory.CreateCondition(c.Type, c.Value, c.Field, c.Scope, c.ChildId,
                c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray();

        var childGroups = spec.ConditionGroups?.Select(BuildConditionGroup).ToArray();

        return JavaModelFactory.CreateConditionGroup(spec.Type, conditions, childGroups);
    }

    private static net.battlescribe.model.data.ModifierGroup BuildModifierGroup(ProtocolModifierGroup spec)
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
        {
            foreach (var nested in spec.ModifierGroups)
            {
                group.getModifierGroups().add(BuildModifierGroup(nested));
            }
        }

        return group;
    }

    private void IndexEntries(SelectionEntry entry)
    {
        _entryLookup[entry.getId()] = entry;
        var children = JavaListToList<SelectionEntry>(entry.getSelectionEntries());
        foreach (var child in children)
        {
            IndexEntries(child);
        }

        var groups = JavaListToList<SelectionEntryGroup>(entry.getSelectionEntryGroups());
        foreach (var group in groups)
        {
            _groupLookup[group.getId()] = group;
            var groupEntries = JavaListToList<SelectionEntry>(group.getSelectionEntries());
            foreach (var ge in groupEntries)
            {
                IndexEntries(ge);
            }
            var subGroups = JavaListToList<SelectionEntryGroup>(group.getSelectionEntryGroups());
            foreach (var sg in subGroups)
            {
                IndexGroupRecursive(sg);
            }
        }
    }

    private void IndexGroupRecursive(SelectionEntryGroup group)
    {
        _groupLookup[group.getId()] = group;
        var entries = JavaListToList<SelectionEntry>(group.getSelectionEntries());
        foreach (var e in entries)
        {
            IndexEntries(e);
        }
        var subGroups = JavaListToList<SelectionEntryGroup>(group.getSelectionEntryGroups());
        foreach (var sg in subGroups)
        {
            IndexGroupRecursive(sg);
        }
    }

    /// <summary>
    /// Get a setup selection entry by index (for BattleScribeRosterEngine).
    /// </summary>
    internal SelectionEntry GetSetupSelectionEntry(int index) => _setupSelectionEntries[index];

    internal SelectionEntry GetSelectionEntryForForce(int forceIndex, int entryIndex)
    {
        var entries = GetEntriesForForce(forceIndex);
        if (entryIndex < 0 || entryIndex >= entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(entryIndex),
                $"Entry index {entryIndex} out of range (have {entries.Count} entries for force {forceIndex})");
        }

        return entries[entryIndex];
    }

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
    /// Find a selection entry by a composite ID like "linkId::targetId".
    /// EntryLinks create composite IDs at runtime; try both parts.
    /// </summary>
    internal SelectionEntry? GetEntryByCompositeId(string compositeId)
    {
        if (compositeId.Contains("::"))
        {
            var parts = compositeId.Split("::");
            foreach (var part in parts)
            {
                if (_entryLookup.TryGetValue(part, out var entry))
                {
                    return entry;
                }
            }
        }
        return null;
    }

    internal GameSystem? GetGameSystem() => _gameSystem;

    /// <summary>
    /// Resolve a SelectionEntry by applying modifiers within the force context.
    /// The engine's <c>q</c> map stores ORIGINAL entries (modifiers not applied).
    /// We call the engine's public <c>c.a(d, BaseSelectable, T, bool)</c> method
    /// which creates a copy and applies all conditional modifiers to it.
    /// </summary>
    internal SelectionEntry? GetResolvedEntry(Force force, Selection selection)
    {
        try
        {
            var forceContext = _engine.e(force);
            if (forceContext is null)
            {
                return null;
            }

            var originalEntry = forceContext.i(selection.getEntryId());
            if (originalEntry is null)
            {
                return null;
            }
            // c.a(d, BaseSelectable, T extends BaseModifyableData, bool) creates a copy with modifiers applied
            return (SelectionEntry)_engine.a(forceContext, selection, originalEntry, true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve a ForceEntry by applying modifiers within the force context.
    /// Same principle as GetResolvedEntry — the static entry is copied and modifiers applied.
    /// </summary>
    internal ForceEntry? GetResolvedForceEntry(Force force)
    {
        try
        {
            var forceContext = _engine.e(force);
            if (forceContext is null)
            {
                return null;
            }

            var originalEntry = forceContext.e(force.getEntryId());
            if (originalEntry is null)
            {
                return null;
            }

            return (ForceEntry)_engine.a(forceContext, force, originalEntry, true);
        }
        catch
        {
            return null;
        }
    }

    internal string? GetPublicationName(string? publicationId)
    {
        if (string.IsNullOrEmpty(publicationId) || _gameSystem is null)
        {
            return null;
        }
        // Search game system publications
        var iter = _gameSystem.getPublications().iterator();
        while (iter.hasNext())
        {
            var pub = (Publication)iter.next();
            if (pub.getId() == publicationId)
            {
                return pub.getName();
            }
        }
        // Search catalogue publications
        foreach (var cat in _catalogues.Values)
        {
            var catIter = cat.getPublications().iterator();
            while (catIter.hasNext())
            {
                var pub = (Publication)catIter.next();
                if (pub.getId() == publicationId)
                {
                    return pub.getName();
                }
            }
        }
        return null;
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
    /// Resolve a catalogue by ID, or return the default catalogue if null.
    /// </summary>
    internal Catalogue ResolveCatalogue(string? catalogueId)
    {
        if (catalogueId != null)
        {
            for (var i = 0; i < _setupCatalogues.Count; i++)
            {
                if (_setupCatalogues[i].getId() == catalogueId)
                {
                    return _setupCatalogues[i];
                }
            }
            throw new InvalidOperationException($"Catalogue '{catalogueId}' not found.");
        }
        if (_setupCatalogue != null)
        {
            var idx = _setupCatalogues.IndexOf(_setupCatalogue);
            return idx >= 0 ? _setupCatalogues[idx] : _setupCatalogues[0];
        }
        return _setupCatalogues[0];
    }

    /// <summary>
    /// Track the catalogue for a newly created force.
    /// </summary>
    internal void TrackForceCatalogue(Force force, Catalogue catalogue) =>
        _forceCatalogueMap[force] = catalogue;

    /// <summary>
    /// Get the tracked catalogue for a force.
    /// </summary>
    internal Catalogue GetForceCatalogue(Force force) =>
        _forceCatalogueMap.TryGetValue(force, out var cat) ? cat : _setupCatalogues[0];

    /// <summary>
    /// Create a child force under a parent using a ForceEntry object.
    /// </summary>
    internal Force CreateChildForce(Force parentForce, ForceEntry childForceEntry, Catalogue catalogue)
    {
        EnsureInitialized();
        var linked = ResolveLinkedCatalogues(catalogue);

        var linkedCatMap = new JavaHashMap();
        foreach (var kvp in linked)
        {
            linkedCatMap.put(kvp.Key, kvp.Value);
        }

        var favourites = new JavaArrayList();
        var errors = new JavaArrayList();
        var childForce = _engine.b(parentForce, _gameSystem, catalogue, linkedCatMap, childForceEntry, favourites, errors) ?? throw new InvalidOperationException("Java engine returned null when creating child force.");

        _forceCatalogueMap[childForce] = catalogue;
        // selectForce (f.java:868) calls t() internally — no need for explicit Validate().
        return childForce;
    }

    public void Dispose()
    {
        _initialized = false;
        _gameSystem = null;
        _catalogues.Clear();
        _forceCatalogueMap.Clear();
        _setupCatalogues.Clear();
        _perCatalogueEntries.Clear();
        _setupForceEntries.Clear();
        _setupSelectionEntries.Clear();
        _entryLookup.Clear();
        _groupLookup.Clear();
        _linkConstraintLookup.Clear();
        _linkTargetMap.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Calls the engine's private x() method ("Select default root entries").
    /// In the BattleScribe desktop UI, this is triggered during setRoster(bl=true)
    /// which runs when loading or creating a new roster. It auto-selects entries
    /// that have a minimum constraint >= 1 on the force's root entries.
    /// Since our engine creates forces via selectRootForce (which doesn't call x()),
    /// we must invoke it separately to match the desktop behavior.
    /// </summary>
    /// <remarks>
    /// WARNING: This uses reflection on an obfuscated private method name "x".
    /// This WILL break if the BattleScribe engine JAR is re-obfuscated or updated.
    /// The method signature is: private void x() in net.battlescribe.engine.a.f
    /// (decompiled source reference: BattleScribeEngine line 978-987).
    /// </remarks>
    private void SelectDefaultRootEntries()
    {
        // x() is a private method on the engine (net.battlescribe.engine.a.f)
        var method = _engine.GetType().GetMethod("x",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: Type.EmptyTypes, modifiers: null) ?? throw new InvalidOperationException(
                "Could not find engine method x() for auto-selecting default root entries.");

        method.Invoke(_engine, null);
    }

    /// <summary>
    /// Calls the engine's private v() method (main validation pass).
    /// The BattleScribe engine uses a dirty-flag pattern: mutations mark elements as
    /// changed, and v() processes only dirty elements. Some mutation methods (like
    /// selectEntry/b()) trigger v() internally, but others (like x() / auto-select
    /// default entries) do not. We call v() explicitly after operations that skip it
    /// to ensure validation state is always current.
    /// </summary>
    /// <remarks>
    /// WARNING: This uses reflection on an obfuscated private method name "v".
    /// The method signature is: private void v() in net.battlescribe.engine.a.f
    /// (decompiled source reference: BattleScribeEngine line 356).
    /// </remarks>
    private void Validate()
    {
        _validateMethod ??= _engine.GetType().GetMethod("v",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: Type.EmptyTypes, modifiers: null) ?? throw new InvalidOperationException(
                "Could not find engine method v() for validation.");

        _validateMethod.Invoke(_engine, null);
    }

    private MethodInfo? _validateMethod;

    /// <summary>
    /// Calls the engine's private synchronized t() method (full refresh).
    /// This is the main refresh cycle that the engine calls after every mutation:
    ///   t() { u(); a(false,true); v(); d(); w(); }
    /// Where:
    ///   u() = mark dependent entries as changed via dependency graph
    ///   a(false,true) = cost refresh (single-pass over CHANGED selections)
    ///   v() = validate constraints
    ///   d() = clear query cache
    ///   w() = clear all 'changed' flags
    /// Most public engine methods (selectEntry, deselectEntry, setNumSelections,
    /// selectRootForce, selectForce, deselectForce) call t() internally.
    /// We only need to call it explicitly after x() (auto-select defaults) which
    /// creates selections without triggering a refresh.
    /// </summary>
    /// <remarks>
    /// WARNING: This uses reflection on an obfuscated private method name "t".
    /// The method signature is: private synchronized void t() in net.battlescribe.engine.a.f
    /// (decompiled source reference: BattleScribeEngine f.java line 150).
    /// </remarks>
    private void Refresh()
    {
        _refreshMethod ??= _engine.GetType().GetMethod("t",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: Type.EmptyTypes, modifiers: null) ?? throw new InvalidOperationException(
                "Could not find engine method t() for refresh.");

        _refreshMethod.Invoke(_engine, null);
    }

    private MethodInfo? _refreshMethod;

    [MemberNotNull(nameof(_gameSystem))]
    private void EnsureInitialized()
    {
        if (!_initialized || _gameSystem is null)
        {
            throw new InvalidOperationException("Engine not initialized. Call Initialize() first.");
        }
    }

    private static List<T> JavaListToList<T>(JavaList? javaList)
    {
        if (javaList is null)
        {
            return [];
        }

        var result = new List<T>(javaList.size());
        var iter = javaList.iterator();
        while (iter.hasNext())
        {
            var next = iter.next();
            if (next is T typed)
            {
                result.Add(typed);
            }
            else
            {
                throw new InvalidCastException(
                    $"Java list element is {next?.GetType().Name ?? "null"}, expected {typeof(T).Name}");
            }
        }
        return result;
    }


    private static List<string> JavaListToStringErrors(JavaList? javaList)
    {
        if (javaList is null)
        {
            return [];
        }

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
