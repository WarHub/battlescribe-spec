using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using net.battlescribe.model.data;
using JavaList = java.util.List;

namespace BattleScribeSpec;

/// <summary>
/// IGameDataEngine implementation backed by direct Java model manipulation via IKVM.
///
/// Unlike the roster engine (which needs the full engine controller for cost calculations,
/// validation, etc.), the data editor adapter only needs to manipulate the Java model objects
/// directly — creating, removing, moving entries in their parent lists.
///
/// This is the BattleScribe-native data model, so it serves as the reference implementation
/// for how entries should be structured, named, and nested.
/// </summary>
public sealed class BattleScribeGameDataEngine : IGameDataEngine
{
    private GameSystem? _gameSystem;
    private Catalogue? _catalogue;
    private readonly List<Catalogue> _catalogues = [];
    private string _specId = "";

    // Lookup maps for finding entries by ID
    private readonly Dictionary<string, object> _entriesById = [];

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var errors = new List<string>();
        try
        {
            _entriesById.Clear();
            _catalogues.Clear();

            // Build game system from protocol types (reuse BattleScribeEngine patterns)
            _gameSystem = BuildGameSystem(gameSystem);

            // Build every catalogue (a catalogueLink target lives in a second, library
            // catalogue, so all catalogues must be present and indexed). The first is the
            // primary edited catalogue for root-level operations.
            foreach (var catSpec in catalogues)
            {
                var built = BuildCatalogue(catSpec);
                _catalogues.Add(built);
                IndexAllEntries(built);
            }

            _catalogue = _catalogues.FirstOrDefault();

            // Also index game system entries
            if (_gameSystem != null)
            {
                IndexGameSystemEntries(_gameSystem);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Setup error: {ex.Message}");
        }

        return errors;
    }

    /// <summary>
    /// Select the active file. Switching catalogue makes it the <i>primary</i> for validation
    /// (the others become imported link targets) and the profile-type context for adds. Opening
    /// the game system is a no-op (it is always the root context). Throws on an unknown id so a
    /// mistyped <c>openCatalogue</c> fails loudly rather than silently editing the wrong file.
    /// </summary>
    public void OpenFile(string id)
    {
        if (_gameSystem is not null && _gameSystem.getId() == id)
        {
            return;
        }

        _catalogue = _catalogues.FirstOrDefault(c => c.getId() == id)
            ?? throw new InvalidOperationException(
                $"openCatalogue: no loaded catalogue or game system with id '{id}'");
    }

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null)
    {
        var parent = FindById(parentId)
            ?? throw new InvalidOperationException($"Parent not found: {parentId}");

        // Fail with a clear error (rather than silently producing an entry the Data Editor
        // would reject) for entry types the editor only adds under a precondition. Keeps the
        // in-process reference engine consistent with the battlescribe-ui anchor.
        ValidateAddPreconditions(parent, entryType);

        var id = Guid.NewGuid().ToString();
        var entryName = name ?? $"New {entryType}";

        // Create the Java model object for this entry type and add to parent
        var created = CreateAndAddEntry(parent, entryType, id, entryName);
        _entriesById[id] = created;

        return new GameDataActionOutputs { EntryId = id };
    }

    /// <summary>
    /// Throw a clear error when an AddEntry would violate a Data Editor precondition:
    /// a category link only attaches to a force entry, and a profile needs a profile type
    /// to assign. Mirrors the guards in the BattleScribe Data Editor controller so both
    /// anchors agree on what is and isn't a valid add.
    /// </summary>
    private void ValidateAddPreconditions(object parent, string entryType)
    {
        if (entryType == "categoryLink" && parent is not ForceEntry)
        {
            throw new InvalidOperationException(
                $"categoryLink can only be added to a ForceEntry; parent is a {parent.GetType().Name}");
        }

        if ((entryType == "profile" || entryType == "sharedProfile") && !ProfileTypeExists())
        {
            throw new InvalidOperationException(
                "profile requires at least one profileType in the game system or catalogue");
        }
    }

    private bool ProfileTypeExists() =>
        HasProfileType(_gameSystem) || HasProfileType(_catalogue);

    private static bool HasProfileType(object? root)
    {
        if (root is null)
        {
            return false;
        }

        var list = GetList(root, "getProfileTypes");
        return list is not null && list.size() > 0;
    }

    public void RemoveEntry(string entryId)
    {
        // Walk all container lists across every catalogue and the game system.
        var removed = _catalogues.Any(cat => RemoveFromParent(cat, entryId))
            || RemoveFromParent(_gameSystem, entryId);
        if (!removed)
        {
            throw new InvalidOperationException($"Could not remove entry: {entryId}");
        }

        _entriesById.Remove(entryId);
    }

    public void SetField(string entryId, string field, string? value)
    {
        var entry = FindById(entryId)
            ?? throw new InvalidOperationException($"Entry not found: {entryId}");

        SetFieldOnObject(entry, field, value);
    }

    public void SetCost(string entryId, string costTypeId, string? value)
    {
        var entry = FindById(entryId)
            ?? throw new InvalidOperationException($"Entry not found: {entryId}");

        var costs = GetList(entry, "getCosts")
            ?? throw new InvalidOperationException($"Entry {entryId} has no costs container");

        var amount = double.TryParse(value, out var d) ? d : 0.0;

        // Find an existing cost for this type, else create one.
        for (var i = 0; i < costs.size(); i++)
        {
            var cost = costs.get(i);
            if (cost.GetType().GetMethod("getTypeId")?.Invoke(cost, null)?.ToString() == costTypeId)
            {
                ((Cost)cost).setValue(amount);
                return;
            }
        }

        costs.add(JavaModelFactory.CreateCost(costTypeId, costTypeId, (decimal)amount));
    }

    public void SetCharacteristic(string entryId, string nameOrTypeId, string? value)
    {
        var entry = FindById(entryId)
            ?? throw new InvalidOperationException($"Entry not found: {entryId}");

        var chars = GetList(entry, "getCharacteristics")
            ?? throw new InvalidOperationException($"Entry {entryId} has no characteristics container (not a profile?)");

        // Match by characteristic name or typeId, else create a new one keyed by name.
        for (var i = 0; i < chars.size(); i++)
        {
            var ch = chars.get(i);
            var chName = ch.GetType().GetMethod("getName")?.Invoke(ch, null)?.ToString();
            var chTypeId = ch.GetType().GetMethod("getTypeId")?.Invoke(ch, null)?.ToString();
            if (chName == nameOrTypeId || chTypeId == nameOrTypeId)
            {
                ((Characteristic)ch).setValue(value ?? "");
                return;
            }
        }

        chars.add(JavaModelFactory.CreateCharacteristic(nameOrTypeId, "", value ?? ""));
    }

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId)
    {
        var parent = FindById(parentId)
            ?? throw new InvalidOperationException($"Parent not found: {parentId}");

        var id = Guid.NewGuid().ToString();

        object link = linkType switch
        {
            "entryLink" => CreateEntryLink(id, targetId),
            "infoLink" => CreateInfoLink(id, targetId),
            "categoryLink" => CreateCategoryLink(id, targetId),
            _ => throw new InvalidOperationException($"Unknown link type: {linkType}"),
        };

        var container = GetContainerList(parent, linkType)
            ?? throw new InvalidOperationException(
                $"No suitable container for {linkType} in parent {parentId}");

        container.add(link);
        _entriesById[id] = link;

        return new GameDataActionOutputs { EntryId = id };
    }

    public GameDataState GetState()
    {
        var catalogues = _catalogues.Select(ReadCatalogueState).ToList();

        GameSystemDataState? gsState = null;
        if (_gameSystem != null)
        {
            gsState = ReadGameSystemState(_gameSystem);
        }

        return new GameDataState
        {
            GameSystem = gsState,
            Catalogues = catalogues,
        };
    }

    public IReadOnlyList<Roster.ValidationErrorState> GetValidationErrors()
    {
        // Validation lives in the BattleScribe data manager — the same class the Data Editor
        // builds. Construct it reflectively over the live model, load, and read its error list
        // via a(true) (full validation, matching the editor). Two concrete managers exist:
        //   - engine.a.d : catalogue editing — a(GameSystem, Catalogue, Map, Collection)
        //   - engine.a.e : game-system-only  — b(GameSystem, Collection, boolean)
        // Both share the ctor (platform=DESKTOP, logger, perf tracker). Defensive: any failure
        // (obfuscation drift) yields no errors.
        try
        {
            if (_gameSystem is null)
            {
                return [];
            }

            // Shared constructor dependencies. The platform enum is a nested type
            // (enum e inside class net.battlescribe.engine.constants.a), so its binary
            // name uses '$'; field "d" is the DESKTOP constant the Data Editor uses.
            var platform = java.lang.Class.forName("net.battlescribe.engine.constants.a$e").getField("d").get(null);
            // The Data Editor's logger lives in DataUtils (not in the engine IKVM assembly),
            // so supply our own no-op implementation of the engine logger interface.
            var logger = new SilentEngineLogger();
            var perf = java.lang.Class.forName("net.battlescribe.engine.b.e").getDeclaredConstructor().newInstance();

            java.lang.Class dmType;
            object dm;

            if (_catalogues.Count > 0)
            {
                // Catalogue editing: index the primary catalogue plus all others (link targets).
                dmType = java.lang.Class.forName("net.battlescribe.engine.a.d");
                dm = NewDataManager(dmType, platform, logger, perf);

                var primary = _catalogue ?? _catalogues[0];
                var imported = new java.util.HashMap();
                var allCats = new java.util.ArrayList();
                foreach (var cat in _catalogues)
                {
                    allCats.add(cat);
                    if (!ReferenceEquals(cat, primary))
                    {
                        imported.put(cat.getId(), cat);
                    }
                }

                // a(GameSystem, Catalogue, Map<String,Catalogue>, Collection<Catalogue>)
                var init = dmType.getMethod("a",
                    java.lang.Class.forName("net.battlescribe.model.data.GameSystem"),
                    java.lang.Class.forName("net.battlescribe.model.data.Catalogue"),
                    java.lang.Class.forName("java.util.Map"),
                    java.lang.Class.forName("java.util.Collection"));
                init.invoke(dm, [_gameSystem, primary, imported, allCats]);
            }
            else
            {
                // Game-system-only editing.
                dmType = java.lang.Class.forName("net.battlescribe.engine.a.e");
                dm = NewDataManager(dmType, platform, logger, perf);

                // b(GameSystem, Collection<Catalogue>, boolean)
                var init = dmType.getMethod("b",
                    java.lang.Class.forName("net.battlescribe.model.data.GameSystem"),
                    java.lang.Class.forName("java.util.Collection"),
                    java.lang.Boolean.TYPE);
                init.invoke(dm, [_gameSystem, new java.util.ArrayList(), java.lang.Boolean.FALSE]);
            }

            // a(boolean) → List of error objects; each is INamed (getName() = message).
            var errMethod = dmType.getMethod("a", java.lang.Boolean.TYPE);
            var raw = errMethod.invoke(dm, [java.lang.Boolean.TRUE]);
            if (raw is not JavaList list)
            {
                return [];
            }

            var result = new List<Roster.ValidationErrorState>();
            var iter = list.iterator();
            while (iter.hasNext())
            {
                var err = iter.next();
                var msg = err?.GetType().GetMethod("getName")?.Invoke(err, null)?.ToString()
                    ?? err?.ToString() ?? "";
                result.Add(new Roster.ValidationErrorState(msg));
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-gamedata] validation unavailable: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// No-op implementation of the BattleScribe engine logger interface
    /// (<c>net.battlescribe.engine.b.c</c>). The data manager logs verbosely during load /
    /// validation; we discard it. Implemented in C# (IKVM lets a managed type satisfy a Java
    /// interface) because the engine ships no usable logger in its own assembly.
    /// </summary>
    private sealed class SilentEngineLogger : net.battlescribe.engine.b.c
    {
        public void a(string str) { }

        public void b(string str) { }
    }

    private static object NewDataManager(java.lang.Class dmType, object platform, object logger, object perf)
    {
        var ctor = dmType.getConstructors().FirstOrDefault(c => c.getParameterCount() == 3)
            ?? throw new InvalidOperationException($"No 3-arg constructor on {dmType.getName()}");
        return ctor.newInstance([platform, logger, perf]);
    }

    public void Dispose()
    {
        _entriesById.Clear();
        _catalogues.Clear();
        _gameSystem = null;
        _catalogue = null;
    }

    // ===== Setup helpers =====

    private static GameSystem BuildGameSystem(ProtocolGameSystem spec)
    {
        var costTypes = spec.CostTypes?.Select(ct =>
            JavaModelFactory.CreateCostType(ct.Id, ct.Name, ct.DefaultCostLimit, ct.Hidden, ct.Limit)).ToArray();

        var forceEntries = spec.ForceEntries?.Select(BuildForceEntry).ToArray();

        var categoryEntries = spec.CategoryEntries?.Select(ce =>
            JavaModelFactory.CreateCategoryEntry(ce.Id, ce.Name, ce.Hidden,
                ce.Constraints?.Select(BuildConstraint).ToArray(),
                ce.Modifiers?.Select(BuildModifier).ToArray())).ToArray();

        var profileTypes = spec.ProfileTypes?.Select(pt =>
            JavaModelFactory.CreateProfileType(pt.Id, pt.Name,
                pt.CharacteristicTypes?.Select(ct =>
                    JavaModelFactory.CreateCharacteristicType(ct.Id, ct.Name)))).ToArray();

        var publications = spec.Publications?.Select(p =>
            JavaModelFactory.CreatePublication(p.Id, p.Name, p.ShortName ?? "", p.Publisher ?? "",
                p.PublicationDate ?? "", p.PublisherUrl ?? "")).ToArray();

        var selectionEntries = spec.SelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var entryLinks = spec.EntryLinks?.Select(BuildEntryLink).ToArray();
        var rules = spec.Rules?.Select(BuildRule).ToArray();
        var infoLinks = spec.InfoLinks?.Select(BuildInfoLink).ToArray();
        var sharedSelectionEntries = spec.SharedSelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var sharedSelectionEntryGroups = spec.SharedSelectionEntryGroups?.Select(BuildSelectionEntryGroup).ToArray();
        var sharedRules = spec.SharedRules?.Select(BuildRule).ToArray();
        var sharedProfiles = spec.SharedProfiles?.Select(BuildProfile).ToArray();
        var sharedInfoGroups = spec.SharedInfoGroups?.Select(BuildInfoGroup).ToArray();

        return JavaModelFactory.CreateGameSystem(
            id: spec.Id,
            name: spec.Name,
            costTypes: costTypes,
            forceEntries: forceEntries,
            categoryEntries: categoryEntries,
            profileTypes: profileTypes,
            publications: publications,
            selectionEntries: selectionEntries,
            entryLinks: entryLinks,
            rules: rules,
            infoLinks: infoLinks,
            sharedSelectionEntries: sharedSelectionEntries,
            sharedSelectionEntryGroups: sharedSelectionEntryGroups,
            sharedRules: sharedRules,
            sharedProfiles: sharedProfiles,
            sharedInfoGroups: sharedInfoGroups);
    }

    private static Catalogue BuildCatalogue(ProtocolCatalogue spec)
    {
        var selectionEntries = spec.SelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var entryLinks = spec.EntryLinks?.Select(BuildEntryLink).ToArray();
        var sharedSelectionEntries = spec.SharedSelectionEntries?.Select(BuildSelectionEntry).ToArray();
        var sharedSelectionEntryGroups = spec.SharedSelectionEntryGroups?.Select(BuildSelectionEntryGroup).ToArray();
        var sharedRules = spec.SharedRules?.Select(BuildRule).ToArray();
        var sharedProfiles = spec.SharedProfiles?.Select(BuildProfile).ToArray();
        var sharedInfoGroups = spec.SharedInfoGroups?.Select(BuildInfoGroup).ToArray();
        var rules = spec.Rules?.Select(BuildRule).ToArray();

        var cat = JavaModelFactory.CreateCatalogue(
            spec.Id, spec.Name, spec.GameSystemId,
            library: spec.Library,
            selectionEntries: selectionEntries,
            entryLinks: entryLinks,
            sharedSelectionEntries: sharedSelectionEntries,
            sharedSelectionEntryGroups: sharedSelectionEntryGroups,
            sharedRules: sharedRules,
            sharedProfiles: sharedProfiles,
            sharedInfoGroups: sharedInfoGroups,
            rules: rules,
            costTypes: spec.CostTypes?.Select(ct =>
                JavaModelFactory.CreateCostType(ct.Id, ct.Name, ct.DefaultCostLimit ?? -1)).ToArray(),
            profileTypes: spec.ProfileTypes?.Select(pt =>
                JavaModelFactory.CreateProfileType(pt.Id, pt.Name,
                    pt.CharacteristicTypes?.Select(ct =>
                        JavaModelFactory.CreateCharacteristicType(ct.Id, ct.Name)))).ToArray(),
            categoryEntries: spec.CategoryEntries?.Select(ce =>
                JavaModelFactory.CreateCategoryEntry(ce.Id, ce.Name, ce.Hidden,
                    ce.Constraints?.Select(BuildConstraint).ToArray(),
                    ce.Modifiers?.Select(BuildModifier).ToArray())).ToArray(),
            forceEntries: spec.ForceEntries?.Select(BuildForceEntry).ToArray());

        if (spec.Publications != null)
        {
            foreach (var pubSpec in spec.Publications)
            {
                cat.getPublications().add(
                    JavaModelFactory.CreatePublication(pubSpec.Id, pubSpec.Name, pubSpec.ShortName ?? "",
                        pubSpec.Publisher ?? "", pubSpec.PublicationDate ?? "", pubSpec.PublisherUrl ?? ""));
            }
        }

        if (spec.InfoLinks != null)
        {
            foreach (var il in spec.InfoLinks)
            {
                cat.getInfoLinks().add(BuildInfoLink(il));
            }
        }

        return cat;
    }

    // ===== Protocol → Java model builders =====

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
            foreach (var r in spec.Rules)
            {
                entry.getRules().add(BuildRule(r));
            }
        }

        if (spec.Profiles != null)
        {
            foreach (var p in spec.Profiles)
            {
                entry.getProfiles().add(BuildProfile(p));
            }
        }

        if (spec.InfoGroups != null)
        {
            foreach (var ig in spec.InfoGroups)
            {
                entry.getInfoGroups().add(BuildInfoGroup(ig));
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

        return JavaModelFactory.CreateSelectionEntryGroup(
            spec.Id, spec.Name,
            hidden: spec.Hidden,
            defaultSelectionEntryId: spec.DefaultSelectionEntryId,
            constraints: constraints,
            modifiers: modifiers,
            modifierGroups: modifierGroups,
            selectionEntries: childEntries,
            selectionEntryGroups: childGroups,
            entryLinks: entryLinks,
            categoryLinks: categoryLinks,
            collective: spec.Collective,
            import: spec.Import,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId);
    }

    private static EntryLink BuildEntryLink(ProtocolEntryLink spec)
    {
        var constraints = spec.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var modifierGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();
        var categoryLinks = spec.CategoryLinks?.Select(BuildCategoryLink).ToArray();

        return JavaModelFactory.CreateEntryLink(
            spec.Id, spec.Name ?? "", spec.TargetId ?? "", spec.Type,
            hidden: spec.Hidden,
            collective: spec.Collective,
            import: spec.Import,
            constraints: constraints,
            modifiers: modifiers,
            modifierGroups: modifierGroups,
            categoryLinks: categoryLinks,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId);
    }

    private static ForceEntry BuildForceEntry(ProtocolForceEntry spec)
    {
        var categoryLinks = spec.CategoryLinks?.Select(BuildCategoryLink).ToArray();
        var constraints = spec.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var childForces = spec.ForceEntries?.Select(BuildForceEntry).ToArray();

        return JavaModelFactory.CreateForceEntry(
            spec.Id, spec.Name,
            hidden: spec.Hidden,
            categoryLinks: categoryLinks,
            forceEntries: childForces,
            constraints: constraints,
            modifiers: modifiers);
    }

    private static Rule BuildRule(ProtocolRule spec)
    {
        return JavaModelFactory.CreateRule(
            spec.Id, spec.Name, spec.Description ?? "",
            hidden: spec.Hidden,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId,
            page: spec.Page ?? "");
    }

    private static Profile BuildProfile(ProtocolProfile spec)
    {
        var characteristics = spec.Characteristics?.Select(c =>
            JavaModelFactory.CreateCharacteristic(c.Name, c.TypeId, c.Value ?? "")).ToArray();

        return JavaModelFactory.CreateProfile(
            spec.Id, spec.Name, spec.TypeId, spec.TypeName,
            hidden: spec.Hidden,
            characteristics: characteristics,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId,
            page: string.IsNullOrEmpty(spec.Page) ? null : spec.Page);
    }

    private static InfoGroup BuildInfoGroup(ProtocolInfoGroup spec)
    {
        var profiles = spec.Profiles?.Select(BuildProfile).ToArray();
        var rules = spec.Rules?.Select(BuildRule).ToArray();
        var infoLinks = spec.InfoLinks?.Select(BuildInfoLink).ToArray();
        var childGroups = spec.InfoGroups?.Select(BuildInfoGroup).ToArray();

        var ig = JavaModelFactory.CreateInfoGroup(
            spec.Id, spec.Name,
            hidden: spec.Hidden,
            profiles: profiles,
            rules: rules,
            infoGroups: childGroups);

        if (infoLinks != null)
        {
            foreach (var il in infoLinks)
            {
                ig.getInfoLinks().add(il);
            }
        }

        return ig;
    }

    private static InfoLink BuildInfoLink(ProtocolInfoLink spec)
    {
        return JavaModelFactory.CreateInfoLink(
            spec.Id, spec.Name ?? "", spec.TargetId ?? "", spec.Type,
            hidden: spec.Hidden,
            publicationId: string.IsNullOrEmpty(spec.PublicationId) ? null : spec.PublicationId,
            page: string.IsNullOrEmpty(spec.Page) ? null : spec.Page);
    }

    private static Constraint BuildConstraint(ProtocolConstraint spec)
    {
        return JavaModelFactory.CreateConstraint(
            spec.Id, spec.Type, spec.Value, spec.Field, spec.Scope,
            shared: spec.Shared,
            percentValue: spec.PercentValue,
            includeChildSelections: spec.IncludeChildSelections,
            includeChildForces: spec.IncludeChildForces);
    }

    private static Modifier BuildModifier(ProtocolModifier spec)
    {
        var conditions = spec.Conditions?.Select(BuildCondition).ToArray();
        var conditionGroups = spec.ConditionGroups?.Select(BuildConditionGroup).ToArray();
        var repeats = spec.Repeats?.Select(BuildRepeat).ToArray();

        var m = JavaModelFactory.CreateModifier(
            spec.Type, spec.Field, spec.Value,
            conditions: conditions,
            repeats: repeats);

        if (conditionGroups != null)
        {
            foreach (var cg in conditionGroups)
            {
                m.getConditionGroups().add(cg);
            }
        }

        return m;
    }

    private static ModifierGroup BuildModifierGroup(ProtocolModifierGroup spec)
    {
        var conditions = spec.Conditions?.Select(BuildCondition).ToArray();
        var conditionGroups = spec.ConditionGroups?.Select(BuildConditionGroup).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();
        var repeats = spec.Repeats?.Select(BuildRepeat).ToArray();
        var childGroups = spec.ModifierGroups?.Select(BuildModifierGroup).ToArray();

        var mg = JavaModelFactory.CreateModifierGroup(
            conditions: conditions,
            conditionGroups: conditionGroups,
            modifiers: modifiers);

        if (repeats != null)
        {
            foreach (var r in repeats)
            {
                mg.getRepeats().add(r);
            }
        }

        if (childGroups != null)
        {
            foreach (var child in childGroups)
            {
                mg.getModifierGroups().add(child);
            }
        }

        return mg;
    }

    private static Condition BuildCondition(ProtocolCondition spec)
    {
        return JavaModelFactory.CreateCondition(
            spec.Type, spec.Value, spec.Field, spec.Scope,
            childId: spec.ChildId,
            shared: spec.Shared,
            percentValue: spec.PercentValue,
            includeChildSelections: spec.IncludeChildSelections,
            includeChildForces: spec.IncludeChildForces);
    }

    private static ConditionGroup BuildConditionGroup(ProtocolConditionGroup spec)
    {
        var conditions = spec.Conditions?.Select(BuildCondition).ToArray();
        var childGroups = spec.ConditionGroups?.Select(BuildConditionGroup).ToArray();

        return JavaModelFactory.CreateConditionGroup(
            spec.Type,
            conditions: conditions,
            conditionGroups: childGroups);
    }

    private static Repeat BuildRepeat(ProtocolRepeat spec)
    {
        return JavaModelFactory.CreateRepeat(
            spec.Value, spec.Repeats, spec.Field, spec.Scope,
            childId: spec.ChildId,
            shared: spec.Shared,
            percentValue: spec.PercentValue,
            includeChildSelections: spec.IncludeChildSelections,
            includeChildForces: spec.IncludeChildForces,
            roundUp: spec.RoundUp);
    }

    private static CategoryLink BuildCategoryLink(ProtocolCategoryLink spec)
    {
        var constraints = spec.Constraints?.Select(BuildConstraint).ToArray();
        var modifiers = spec.Modifiers?.Select(BuildModifier).ToArray();

        return JavaModelFactory.CreateCategoryLink(
            spec.Id, spec.Name ?? "", spec.TargetId,
            primary: spec.Primary,
            hidden: spec.Hidden,
            constraints: constraints,
            modifiers: modifiers);
    }

    // ===== Entry creation =====

    private static object CreateAndAddEntry(object parent, string entryType, string id, string name)
    {
        // Determine the correct container based on entry type and parent
        var isRootParent = parent is Catalogue || parent is GameSystem;

        return entryType switch
        {
            "selectionEntry" => AddNewSelectionEntry(parent, id, name),
            "selectionEntryGroup" => AddNewSelectionEntryGroup(parent, id, name, isRootParent),
            "rule" => AddNewRule(parent, id, name),
            "profile" => AddNewProfile(parent, id, name, isRootParent),
            "entryLink" => AddNewEntryLink(parent, id, name),
            "forceEntry" => AddNewForceEntry(parent, id, name),
            "categoryEntry" => AddNewCategoryEntry(parent, id, name),
            // ── Shared root containers ──────────────────────────────────────
            "sharedSelectionEntry" => AddTo(parent, "sharedSelectionEntry", JavaModelFactory.CreateSelectionEntry(id, name, "upgrade")),
            "sharedSelectionEntryGroup" => AddTo(parent, "sharedSelectionEntryGroup", JavaModelFactory.CreateSelectionEntryGroup(id, name)),
            "sharedRule" => AddTo(parent, "sharedRule", JavaModelFactory.CreateRule(id, name, "")),
            "sharedProfile" => AddTo(parent, "sharedProfile", JavaModelFactory.CreateProfile(id, name, "", "")),
            "sharedInfoGroup" => AddTo(parent, "sharedInfoGroup", JavaModelFactory.CreateInfoGroup(id, name)),
            // ── Info content ────────────────────────────────────────────────
            "infoGroup" => AddTo(parent, "infoGroup", JavaModelFactory.CreateInfoGroup(id, name)),
            "infoLink" => AddTo(parent, "infoLink", JavaModelFactory.CreateInfoLink(id, name, "", "profile")),
            "categoryLink" => AddTo(parent, "categoryLink", JavaModelFactory.CreateCategoryLink(id, "", name)),
            "catalogueLink" => AddTo(parent, "catalogueLink", JavaModelFactory.CreateCatalogueLink(id, name, "")),
            // ── Constraints / modifiers / queries (id-less except constraint) ─
            "constraint" => AddTo(parent, "constraint", JavaModelFactory.CreateConstraint(id, "min", 0m, "selections", "parent")),
            "modifier" => AddTo(parent, "modifier", JavaModelFactory.CreateModifier("set", "name", "")),
            "modifierGroup" => AddTo(parent, "modifierGroup", JavaModelFactory.CreateModifierGroup()),
            "condition" => AddTo(parent, "condition", JavaModelFactory.CreateCondition("atLeast", 0m, "selections", "parent")),
            "conditionGroup" => AddTo(parent, "conditionGroup", JavaModelFactory.CreateConditionGroup("and")),
            "repeat" => AddTo(parent, "repeat", JavaModelFactory.CreateRepeat()),
            // ── Type definitions (root only) ────────────────────────────────
            "costType" => AddTo(parent, "costType", JavaModelFactory.CreateCostType(id, name)),
            "profileType" => AddTo(parent, "profileType", JavaModelFactory.CreateProfileType(id, name)),
            "characteristicType" => AddTo(parent, "characteristicType", JavaModelFactory.CreateCharacteristicType(id, name)),
            "publication" => AddTo(parent, "publication", JavaModelFactory.CreatePublication(id, name)),
            _ => throw new InvalidOperationException($"Unsupported entry type for AddEntry: {entryType}"),
        };
    }

    /// <summary>
    /// Add a freshly-created Java model object to the container resolved for
    /// <paramref name="containerKey"/> on <paramref name="parent"/>, returning the object.
    /// </summary>
    private static object AddTo(object parent, string containerKey, object created)
    {
        var container = GetContainerList(parent, containerKey)
            ?? throw new InvalidOperationException(
                $"No suitable container for '{containerKey}' on parent {parent.GetType().Name}");
        container.add(created);
        return created;
    }

    private static SelectionEntry AddNewSelectionEntry(object parent, string id, string name)
    {
        var entry = JavaModelFactory.CreateSelectionEntry(id, name, "upgrade");
        GetContainerList(parent, "selectionEntry")!.add(entry);
        return entry;
    }

    private static SelectionEntryGroup AddNewSelectionEntryGroup(
        object parent, string id, string name, bool isRootParent)
    {
        var group = JavaModelFactory.CreateSelectionEntryGroup(id, name);
        // At catalogue/system root, groups go to sharedSelectionEntryGroups
        var containerName = isRootParent ? "sharedSelectionEntryGroup" : "selectionEntryGroup";
        GetContainerList(parent, containerName)!.add(group);
        return group;
    }

    private static Rule AddNewRule(object parent, string id, string name)
    {
        var rule = JavaModelFactory.CreateRule(id, name, "");
        GetContainerList(parent, "rule")!.add(rule);
        return rule;
    }

    private static Profile AddNewProfile(object parent, string id, string name, bool isRootParent)
    {
        var profile = JavaModelFactory.CreateProfile(id, name, "", "");
        var containerName = isRootParent ? "sharedProfile" : "profile";
        GetContainerList(parent, containerName)!.add(profile);
        return profile;
    }

    private static EntryLink AddNewEntryLink(object parent, string id, string name)
    {
        var link = JavaModelFactory.CreateEntryLink(id, name, "", "selectionEntry");
        GetContainerList(parent, "entryLink")!.add(link);
        return link;
    }

    private static ForceEntry AddNewForceEntry(object parent, string id, string name)
    {
        var fe = JavaModelFactory.CreateForceEntry(id, name);
        GetContainerList(parent, "forceEntry")!.add(fe);
        return fe;
    }

    private static CategoryEntry AddNewCategoryEntry(object parent, string id, string name)
    {
        var ce = JavaModelFactory.CreateCategoryEntry(id, name);
        GetContainerList(parent, "categoryEntry")!.add(ce);
        return ce;
    }

    // ===== Link creation =====

    private static EntryLink CreateEntryLink(string id, string targetId)
    {
        return JavaModelFactory.CreateEntryLink(id, "", targetId, "selectionEntry");
    }

    private static InfoLink CreateInfoLink(string id, string targetId)
    {
        return JavaModelFactory.CreateInfoLink(id, "", targetId, "profile");
    }

    private static CategoryLink CreateCategoryLink(string id, string targetId)
    {
        return JavaModelFactory.CreateCategoryLink(id, "", targetId);
    }

    // ===== Container resolution =====

    /// <summary>
    /// Gets the Java List for the appropriate container on the parent object.
    /// </summary>
    private static JavaList? GetContainerList(object parent, string entryType)
    {
        return entryType switch
        {
            "selectionEntry" => GetList(parent, "getSelectionEntries"),
            "selectionEntryGroup" => GetList(parent, "getSelectionEntryGroups"),
            "sharedSelectionEntry" => GetList(parent, "getSharedSelectionEntries"),
            "sharedSelectionEntryGroup" => GetList(parent, "getSharedSelectionEntryGroups"),
            "entryLink" => GetList(parent, "getEntryLinks"),
            "rule" => GetList(parent, "getRules"),
            "sharedRule" => GetList(parent, "getSharedRules"),
            "profile" => GetList(parent, "getProfiles"),
            "sharedProfile" => GetList(parent, "getSharedProfiles"),
            "infoLink" => GetList(parent, "getInfoLinks"),
            "infoGroup" => GetList(parent, "getInfoGroups"),
            "sharedInfoGroup" => GetList(parent, "getSharedInfoGroups"),
            "categoryLink" => GetList(parent, "getCategoryLinks"),
            "catalogueLink" => GetList(parent, "getCatalogueLinks"),
            "forceEntry" => GetList(parent, "getForceEntries"),
            "categoryEntry" => GetList(parent, "getCategoryEntries"),
            "constraint" => GetList(parent, "getConstraints"),
            "modifier" => GetList(parent, "getModifiers"),
            "modifierGroup" => GetList(parent, "getModifierGroups"),
            "condition" => GetList(parent, "getConditions"),
            "conditionGroup" => GetList(parent, "getConditionGroups"),
            "repeat" => GetList(parent, "getRepeats"),
            "costType" => GetList(parent, "getCostTypes"),
            "profileType" => GetList(parent, "getProfileTypes"),
            "characteristicType" => GetList(parent, "getCharacteristicTypes"),
            "publication" => GetList(parent, "getPublications"),
            _ => null,
        };
    }

    private static JavaList? GetList(object obj, string methodName)
    {
        var method = obj.GetType().GetMethod(methodName);
        return method?.Invoke(obj, null) as JavaList;
    }

    // ===== Field setting =====

    private static void SetFieldOnObject(object entry, string field, string? value)
    {
        // Map field name to setter method
        var setterName = "set" + char.ToUpperInvariant(field[0]) + field[1..];
        var type = entry.GetType();
        var setter = type.GetMethod(setterName)
            ?? throw new InvalidOperationException(
                $"No setter '{setterName}' found on {type.Name}");

        var paramType = setter.GetParameters()[0].ParameterType;

        // Convert value to the expected type
        object? converted;
        if (paramType == typeof(bool) || paramType == typeof(java.lang.Boolean))
        {
            converted = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        else if (paramType == typeof(int) || paramType == typeof(java.lang.Integer))
        {
            converted = int.TryParse(value, out var i) ? i : 0;
        }
        else if (paramType == typeof(double) || paramType == typeof(java.lang.Double))
        {
            converted = double.TryParse(value, out var d) ? d : 0.0;
        }
        else
        {
            converted = value;
        }

        setter.Invoke(entry, [converted]);
    }

    // ===== Entry lookup and indexing =====

    private object? FindById(string id)
    {
        foreach (var cat in _catalogues)
        {
            if (cat.getId() == id)
            {
                return cat;
            }
        }

        if (_gameSystem != null && _gameSystem.getId() == id)
        {
            return _gameSystem;
        }

        return _entriesById.GetValueOrDefault(id);
    }

    private void IndexAllEntries(Catalogue cat)
    {
        IndexList(cat.getSelectionEntries());
        IndexList(cat.getEntryLinks());
        IndexList(cat.getSharedSelectionEntries());
        IndexList(cat.getSharedSelectionEntryGroups());
        IndexList(cat.getSharedRules());
        IndexList(cat.getSharedProfiles());
        IndexList(cat.getRules());
        IndexList(cat.getForceEntries());
        IndexList(cat.getCategoryEntries());
        IndexList(cat.getInfoLinks());
    }

    private void IndexGameSystemEntries(GameSystem gs)
    {
        IndexList(gs.getSelectionEntries());
        IndexList(gs.getEntryLinks());
        IndexList(gs.getSharedSelectionEntries());
        IndexList(gs.getSharedSelectionEntryGroups());
        IndexList(gs.getSharedRules());
        IndexList(gs.getSharedProfiles());
        IndexList(gs.getRules());
        IndexList(gs.getForceEntries());
        IndexList(gs.getCategoryEntries());
        IndexList(gs.getInfoLinks());
    }

    private void IndexList(JavaList? list)
    {
        if (list == null)
        {
            return;
        }

        var iter = list.iterator();
        while (iter.hasNext())
        {
            var item = iter.next();
            var id = GetId(item);
            if (!string.IsNullOrEmpty(id))
            {
                _entriesById[id] = item;
            }
            // Recursively index children
            IndexChildren(item);
        }
    }

    private void IndexChildren(object entry)
    {
        // Index nested entries for each known container
        string[] containers = [
            "getSelectionEntries", "getSelectionEntryGroups", "getEntryLinks",
            "getRules", "getProfiles", "getInfoGroups", "getInfoLinks",
            "getCategoryLinks", "getConstraints", "getModifiers", "getModifierGroups",
            "getConditions", "getConditionGroups", "getRepeats",
            "getForceEntries", "getCategoryEntries",
        ];

        foreach (var getter in containers)
        {
            var list = GetList(entry, getter);
            if (list != null && list.size() > 0)
            {
                IndexList(list);
            }
        }
    }

    // ===== Removal =====

    private static bool RemoveFromParent(object? root, string entryId)
    {
        if (root == null)
        {
            return false;
        }

        // Try removing from all container lists on this object
        string[] containers = [
            "getSelectionEntries", "getSelectionEntryGroups", "getEntryLinks",
            "getRules", "getProfiles", "getInfoGroups", "getInfoLinks",
            "getCategoryLinks", "getConstraints", "getModifiers", "getModifierGroups",
            "getConditions", "getConditionGroups", "getRepeats",
            "getForceEntries", "getCategoryEntries",
            "getSharedSelectionEntries", "getSharedSelectionEntryGroups",
            "getSharedRules", "getSharedProfiles",
        ];

        foreach (var getter in containers)
        {
            var list = GetList(root, getter);
            if (list == null)
            {
                continue;
            }

            for (var i = 0; i < list.size(); i++)
            {
                var item = list.get(i);
                if (GetId(item) == entryId)
                {
                    list.remove(i);
                    return true;
                }
                // Recurse into children
                if (RemoveFromParent(item, entryId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ===== State extraction =====

    private static CatalogueDataState ReadCatalogueState(Catalogue cat)
    {
        return new CatalogueDataState
        {
            Id = cat.getId() ?? "",
            Name = cat.getName() ?? "",
            GameSystemId = cat.getGameSystemId() ?? "",
            Fields = ReadRootFields(cat, isCatalogue: true),
            SelectionEntries = ReadEntryList(cat.getSelectionEntries(), "selectionEntry"),
            EntryLinks = ReadEntryList(cat.getEntryLinks(), "entryLink"),
            Rules = ReadEntryList(cat.getRules(), "rule"),
            SharedSelectionEntries = ReadEntryList(cat.getSharedSelectionEntries(), "selectionEntry"),
            SharedSelectionEntryGroups = ReadEntryList(cat.getSharedSelectionEntryGroups(), "selectionEntryGroup"),
            SharedRules = ReadEntryList(cat.getSharedRules(), "rule"),
            SharedProfiles = ReadEntryList(cat.getSharedProfiles(), "profile"),
            ForceEntries = ReadEntryList(cat.getForceEntries(), "forceEntry"),
            CategoryEntries = ReadEntryList(cat.getCategoryEntries(), "categoryEntry"),
            Publications = ReadEntryList(cat.getPublications(), "publication"),
            CostTypes = ReadEntryList(cat.getCostTypes(), "costType"),
            ProfileTypes = ReadEntryList(cat.getProfileTypes(), "profileType"),
            SharedInfoGroups = ReadEntryList(cat.getSharedInfoGroups(), "infoGroup"),
            CatalogueLinks = ReadEntryList(cat.getCatalogueLinks(), "catalogueLink"),
        };
    }

    private static GameSystemDataState ReadGameSystemState(GameSystem gs)
    {
        return new GameSystemDataState
        {
            Id = gs.getId() ?? "",
            Name = gs.getName() ?? "",
            Fields = ReadRootFields(gs, isCatalogue: false),
            SelectionEntries = ReadEntryList(gs.getSelectionEntries(), "selectionEntry"),
            EntryLinks = ReadEntryList(gs.getEntryLinks(), "entryLink"),
            Rules = ReadEntryList(gs.getRules(), "rule"),
            SharedSelectionEntries = ReadEntryList(gs.getSharedSelectionEntries(), "selectionEntry"),
            SharedSelectionEntryGroups = ReadEntryList(gs.getSharedSelectionEntryGroups(), "selectionEntryGroup"),
            SharedRules = ReadEntryList(gs.getSharedRules(), "rule"),
            SharedProfiles = ReadEntryList(gs.getSharedProfiles(), "profile"),
            ForceEntries = ReadEntryList(gs.getForceEntries(), "forceEntry"),
            CategoryEntries = ReadEntryList(gs.getCategoryEntries(), "categoryEntry"),
            Publications = ReadEntryList(gs.getPublications(), "publication"),
            CostTypes = ReadEntryList(gs.getCostTypes(), "costType"),
            ProfileTypes = ReadEntryList(gs.getProfileTypes(), "profileType"),
            SharedInfoGroups = ReadEntryList(gs.getSharedInfoGroups(), "infoGroup"),
        };
    }

    /// <summary>
    /// Read root-level metadata fields (author info, revision, version, library) from a
    /// game system or catalogue into a generic field dictionary.
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? ReadRootFields(object root, bool isCatalogue)
    {
        var fields = new Dictionary<string, string?>();
        TryAddField(fields, root, "getAuthorName", "authorName");
        TryAddField(fields, root, "getAuthorContact", "authorContact");
        TryAddField(fields, root, "getAuthorUrl", "authorUrl");
        TryAddField(fields, root, "getReadme", "readme");
        TryAddNumField(fields, root, "getRevision", "revision");
        TryAddField(fields, root, "getBattleScribeVersion", "battleScribeVersion");
        if (isCatalogue)
        {
            TryAddBoolField(fields, root, "library");
            TryAddNumField(fields, root, "getGameSystemRevision", "gameSystemRevision");
        }

        return fields.Count > 0 ? fields : null;
    }

    private static IReadOnlyList<DataEntryState> ReadEntryList(JavaList? list, string entryType)
    {
        if (list == null || list.size() == 0)
        {
            return [];
        }

        var result = new List<DataEntryState>();
        var iter = list.iterator();
        while (iter.hasNext())
        {
            var item = iter.next();
            result.Add(ReadEntry(item, entryType));
        }
        return result;
    }

    private static DataEntryState ReadEntry(object entry, string entryType)
    {
        var id = GetId(entry) ?? "";
        var name = GetName(entry) ?? "";
        var hidden = GetHidden(entry);

        // Collect children
        var children = new List<DataEntryState>();
        AddChildren(children, entry, "getSelectionEntries", "selectionEntry");
        AddChildren(children, entry, "getSelectionEntryGroups", "selectionEntryGroup");
        AddChildren(children, entry, "getEntryLinks", "entryLink");
        AddChildren(children, entry, "getRules", "rule");
        AddChildren(children, entry, "getProfiles", "profile");
        AddChildren(children, entry, "getInfoGroups", "infoGroup");
        AddChildren(children, entry, "getInfoLinks", "infoLink");
        AddChildren(children, entry, "getCategoryLinks", "categoryLink");
        AddChildren(children, entry, "getConstraints", "constraint");
        AddChildren(children, entry, "getModifiers", "modifier");
        AddChildren(children, entry, "getModifierGroups", "modifierGroup");
        AddChildren(children, entry, "getConditions", "condition");
        AddChildren(children, entry, "getConditionGroups", "conditionGroup");
        AddChildren(children, entry, "getRepeats", "repeat");
        AddChildren(children, entry, "getForceEntries", "forceEntry");
        AddChildren(children, entry, "getCategoryEntries", "categoryEntry");
        AddChildren(children, entry, "getCharacteristicTypes", "characteristicType");

        // Collect type-specific fields. Only non-empty values are emitted, so a given
        // entry surfaces just the fields meaningful to its type.
        var fields = new Dictionary<string, string?>();
        TryAddField(fields, entry, "getType", "type");
        TryAddField(fields, entry, "getComment", "comment");
        TryAddField(fields, entry, "getTargetId", "targetId");
        TryAddField(fields, entry, "getPublicationId", "publicationId");
        TryAddField(fields, entry, "getPage", "page");
        TryAddBoolField(fields, entry, "collective");
        TryAddBoolField(fields, entry, "imported");
        TryAddField(fields, entry, "getDefaultSelectionEntryId", "defaultSelectionEntryId");

        // Query / modifier / repeat fields (constraint, modifier, condition, repeat, group)
        TryAddNumField(fields, entry, "getValue", "value");
        TryAddField(fields, entry, "getField", "field");
        TryAddField(fields, entry, "getScope", "scope");
        TryAddField(fields, entry, "getChildId", "childId");
        TryAddBoolField(fields, entry, "shared");
        TryAddBoolField(fields, entry, "percentValue");
        TryAddBoolField(fields, entry, "includeChildSelections");
        TryAddBoolField(fields, entry, "includeChildForces");
        TryAddNumField(fields, entry, "getRepeats", "repeats");
        TryAddBoolField(fields, entry, "roundUp");

        // Type/description/publication metadata
        TryAddField(fields, entry, "getTypeId", "typeId");
        TryAddField(fields, entry, "getTypeName", "typeName");
        TryAddField(fields, entry, "getDescription", "description");
        TryAddNumField(fields, entry, "getDefaultCostLimit", "defaultCostLimit");
        TryAddBoolField(fields, entry, "primary");
        TryAddBoolField(fields, entry, "importRootEntries");
        TryAddField(fields, entry, "getShortName", "shortName");
        TryAddField(fields, entry, "getPublisher", "publisher");
        TryAddField(fields, entry, "getPublicationDate", "publicationDate");
        TryAddField(fields, entry, "getPublisherUrl", "publisherUrl");

        // Costs and characteristics are values keyed by type — surface them as
        // composite fields ("cost:<typeId>", "char:<name>") so they can be asserted
        // without polluting the children list / child counts.
        AddCostFields(fields, entry);
        AddCharacteristicFields(fields, entry);

        return new DataEntryState
        {
            Id = id,
            Name = name,
            EntryType = entryType,
            Hidden = hidden,
            Children = children,
            Fields = fields.Count > 0 ? fields : null,
        };
    }

    private static void AddChildren(
        List<DataEntryState> children, object parent, string getter, string entryType)
    {
        var list = GetList(parent, getter);
        if (list == null || list.size() == 0)
        {
            return;
        }

        var iter = list.iterator();
        while (iter.hasNext())
        {
            children.Add(ReadEntry(iter.next(), entryType));
        }
    }

    private static void TryAddField(Dictionary<string, string?> fields, object entry, string getter, string key)
    {
        var method = entry.GetType().GetMethod(getter);
        if (method == null)
        {
            return;
        }

        var value = method.Invoke(entry, null)?.ToString();
        if (!string.IsNullOrEmpty(value))
        {
            fields[key] = value;
        }
    }

    /// <summary>
    /// Add a boolean field, probing both the <c>is*</c> and <c>get*</c> getter forms
    /// derived from <paramref name="key"/> (BattleScribe is inconsistent across types).
    /// Only emits the field when a getter exists and returns a Boolean.
    /// </summary>
    private static void TryAddBoolField(Dictionary<string, string?> fields, object entry, string key)
    {
        var cap = char.ToUpperInvariant(key[0]) + key[1..];
        foreach (var getter in new[] { "is" + cap, "get" + cap })
        {
            var method = entry.GetType().GetMethod(getter);
            if (method == null)
            {
                continue;
            }

            var result = method.Invoke(entry, null);
            if (result is true)
            {
                fields[key] = "true";
                return;
            }

            if (result is false)
            {
                fields[key] = "false";
                return;
            }
        }
    }

    /// <summary>
    /// Add a numeric field, formatting whole doubles without a trailing ".0"
    /// (BattleScribe stores values as doubles, but specs read "2" not "2.0").
    /// </summary>
    private static void TryAddNumField(Dictionary<string, string?> fields, object entry, string getter, string key)
    {
        var method = entry.GetType().GetMethod(getter);
        var raw = method?.Invoke(entry, null);
        if (raw is null)
        {
            return;
        }

        // Skip non-numeric returns (e.g. Modifier.getValue() returns a String — handled by getValue caller order).
        if (raw is double d)
        {
            fields[key] = FormatNum(d);
        }
        else if (raw is int i)
        {
            fields[key] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            var s = raw.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                fields[key] = s;
            }
        }
    }

    private static string FormatNum(double d) =>
        d == Math.Floor(d) && !double.IsInfinity(d)
            ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void AddCostFields(Dictionary<string, string?> fields, object entry)
    {
        var costs = GetList(entry, "getCosts");
        if (costs == null)
        {
            return;
        }

        for (var i = 0; i < costs.size(); i++)
        {
            var cost = costs.get(i);
            var typeId = cost.GetType().GetMethod("getTypeId")?.Invoke(cost, null)?.ToString();
            var value = cost.GetType().GetMethod("getValue")?.Invoke(cost, null);
            if (!string.IsNullOrEmpty(typeId) && value is double dv)
            {
                fields[$"cost:{typeId}"] = FormatNum(dv);
            }
        }
    }

    private static void AddCharacteristicFields(Dictionary<string, string?> fields, object entry)
    {
        var chars = GetList(entry, "getCharacteristics");
        if (chars == null)
        {
            return;
        }

        for (var i = 0; i < chars.size(); i++)
        {
            var ch = chars.get(i);
            var name = ch.GetType().GetMethod("getName")?.Invoke(ch, null)?.ToString();
            var value = ch.GetType().GetMethod("getValue")?.Invoke(ch, null)?.ToString();
            if (!string.IsNullOrEmpty(name))
            {
                fields[$"char:{name}"] = value ?? "";
            }
        }
    }

    // ===== Reflection helpers =====

    private static string? GetId(object entry)
    {
        return entry.GetType().GetMethod("getId")?.Invoke(entry, null)?.ToString();
    }

    private static string? GetName(object entry)
    {
        return entry.GetType().GetMethod("getName")?.Invoke(entry, null)?.ToString();
    }

    private static bool GetHidden(object entry)
    {
        var method = entry.GetType().GetMethod("getHidden")
            ?? entry.GetType().GetMethod("isHidden");
        if (method == null)
        {
            return false;
        }

        var result = method.Invoke(entry, null);
        return result is true;
    }
}
