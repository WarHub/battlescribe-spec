using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec;

/// <summary>
/// IRosterEngine implementation backed by the BattleScribe Java engine via IKVM.
/// One of the conformance engines under test (alongside NewRecruit).
/// All addressing is ID-based: definition IDs for data references,
/// instance IDs (from previous action outputs) for roster element references.
/// </summary>
public sealed class BattleScribeRosterEngine : IRosterEngine
{
    private string? _specId;

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        Engine.RosterName = _specId;
        return Engine.SetupFromProtocol(gameSystem, catalogues);
    }

    public ActionOutputs AddForce(string forceEntryId, string catalogueId)
    {
        var forceEntry = Engine.FindForceEntryById(forceEntryId)
            ?? throw new InvalidOperationException($"ForceEntry '{forceEntryId}' not found.");

        var catalogue = Engine.ResolveCatalogue(catalogueId);
        var linked = Engine.ResolveLinkedCatalogues(catalogue);
        var forcesBefore = new HashSet<net.battlescribe.model.roster.Force>(
            Engine.GetForces(), ReferenceEqualityComparer.Instance);

        var (force, _) = Engine.AddForce(catalogue, forceEntry, linked);

        if (force is null)
        {
            throw new InvalidOperationException("Java engine returned null force for AddForce.");
        }

        foreach (var f in Engine.GetForces())
        {
            if (!forcesBefore.Contains(f))
            {
                Engine.TrackForceCatalogue(f, catalogue);
            }
        }

        // Re-read force from roster to capture auto-selected entries (from constraints)
        var rosterForce = FindForceById(force.getId());
        var selections = CollectForceSelectionIds(rosterForce);
        return new ActionOutputs { ForceId = force.getId(), Selections = selections };
    }

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
    {
        var parentForce = FindForceById(parentForceId);
        var forceEntry = Engine.FindForceEntryById(forceEntryId)
            ?? throw new InvalidOperationException($"ForceEntry '{forceEntryId}' not found.");

        var catalogue = Engine.ResolveCatalogue(catalogueId);

        var childForce = Engine.CreateChildForce(parentForce, forceEntry, catalogue);
        return new ActionOutputs { ForceId = childForce.getId() };
    }

    public void RemoveForce(string forceId)
    {
        var force = FindForceById(forceId);
        Engine.RemoveForce(force);
    }

    public ActionOutputs SelectEntry(string forceId, string entryId)
    {
        var force = FindForceById(forceId);
        var entries = Engine.GetEntriesForForce(force);
        var entry = FindEntryById(entries, entryId)
            ?? throw new InvalidOperationException(
                $"Entry '{entryId}' not found in force '{forceId}' " +
                $"(have {entries.Count} entries: [{string.Join(", ", entries.Select(e => $"{e.getId()}/{e.getName()}"))}]).");

        var createdSelections = Engine.SelectEntry(force, entry);

        // The primary created selection
        var primarySelection = createdSelections.Count > 0 ? createdSelections[0] : null;

        // Build the output
        var outputs = new ActionOutputs
        {
            SelectionId = primarySelection?.getId()
        };

        // Populate Selections map with all child selections (auto-selected defaults)
        if (primarySelection != null)
        {
            outputs.Selections = CollectChildSelectionIds(primarySelection);
        }

        return outputs;
    }

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
    {
        var force = FindForceById(forceId);
        var parentSelection = FindSelectionById(force, parentSelectionId);
        var parentEntryId = parentSelection.getEntryId();
        var parentEntry = Engine.GetEntryById(parentEntryId)
            ?? Engine.GetEntryByCompositeId(parentEntryId)
            ?? throw new InvalidOperationException($"Parent entry '{parentEntryId}' not found in entry lookup.");

        var childEntries = FlattenChildEntries(parentEntry);
        var childEntry = FindEntryById(childEntries, entryId)
            ?? throw new InvalidOperationException(
                $"Child entry '{entryId}' not found under parent selection '{parentSelectionId}'.");

        var createdSelections = Engine.SelectEntry(parentSelection, childEntry);
        var primarySelection = createdSelections.Count > 0 ? createdSelections[0] : null;

        var outputs = new ActionOutputs
        {
            SelectionId = primarySelection?.getId()
        };

        if (primarySelection != null)
        {
            outputs.Selections = CollectChildSelectionIds(primarySelection);
        }

        return outputs;
    }

    public void DeselectSelection(string forceId, string selectionId)
    {
        var force = FindForceById(forceId);
        var selection = FindSelectionById(force, selectionId);
        Engine.DeselectEntry(selection);
    }

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        var force = FindForceById(forceId);
        var selection = FindSelectionById(force, selectionId);
        var entryId = selection.getEntryId();
        var dataEntry = Engine.GetEntryById(entryId)
            ?? Engine.GetEntryByCompositeId(entryId)
            ?? throw new InvalidOperationException(
                $"Entry '{entryId}' not found in entry lookup for SetSelectionCount.");
        // Find the parent of this selection (the container that holds it)
        var parent = FindSelectionParent(force, selectionId);

        // Match the BattleScribe desktop UI behavior exactly:
        // The UI's count spinner calls getNumChanges to compute delta, then loops
        // individual selectEntry (for increase) or deselectEntry (for decrease) calls.
        // Each call triggers a full t() refresh cycle, producing intermediate cost states
        // visible to self-referencing repeat modifiers.
        // This differs from the engine's atomic setNumSelections which does all changes
        // in one shot with a single t() refresh — we intentionally avoid that API here.
        var delta = Engine.GetNumChanges(parent, dataEntry, count);
        if (delta > 0)
        {
            for (var i = 0; i < delta; i++)
            {
                Engine.SelectEntry(parent, dataEntry);
            }
        }
        else if (delta < 0)
        {
            for (var i = 0; i < -delta; i++)
            {
                Engine.DeselectEntry(selection);
            }
        }
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
    {
        var force = FindForceById(forceId);
        var selection = FindSelectionById(force, selectionId);
        var duplicated = Engine.DuplicateSelection(selection);
        return new ActionOutputs
        {
            SelectionId = duplicated?.getId()
        };
    }

    public ActionOutputs DuplicateForce(string forceId)
    {
        throw new NotSupportedException(
            "DuplicateForce is not supported by the BattleScribe Java engine (no public API).");
    }

    public void SetCostLimit(string costTypeId, decimal value)
    {
        var costType = Engine.GetCostTypeById(costTypeId)
            ?? throw new InvalidOperationException($"Cost type '{costTypeId}' not found.");
        Engine.SetCostLimit(costType, value);
    }

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
    {
        var force = FindForceById(forceId);
        if (categoryEntryId is not null)
        {
            var categories = JavaListToList<net.battlescribe.model.roster.Category>(force.getCategories());
            var category = categories.FirstOrDefault(c => c.getEntryId() == categoryEntryId)
                ?? throw new InvalidOperationException($"Category '{categoryEntryId}' not found in force '{forceId}'.");
            if (customName is not null)
            {
                category.setCustomName(customName);
            }

            if (customNotes is not null)
            {
                category.setCustomNotes(customNotes);
            }
        }
        else if (selectionId is not null)
        {
            var selection = FindSelectionById(force, selectionId);
            if (customName is not null)
            {
                selection.setCustomName(customName);
            }

            if (customNotes is not null)
            {
                selection.setCustomNotes(customNotes);
            }
        }
        else
        {
            if (customName is not null)
            {
                force.setCustomName(customName);
            }

            if (customNotes is not null)
            {
                force.setCustomNotes(customNotes);
            }
        }
    }

    public RosterState GetRosterState()
    {
        var roster = Engine.GetRoster();
        var forces = Engine.GetForces();
        var errors = Engine.GetValidationErrors();

        // Sort forces alphabetically by name to match BS render layer behavior.
        // BS's own RenderRoster sorts forces via Collections.sort(new f()) which uses
        // case-insensitive alphabetical order. This matches BattleScribe's rendered order
        // and usually matches NR for simple names, but NR may differ in documented cases
        // such as numeric-aware comparisons, category grouping, or explicit sort indices.
        forces = [.. forces.OrderBy(f => f.getName(), StringComparer.OrdinalIgnoreCase)];
        var forceStates = forces.Select((f, i) => CaptureForce(f, i)).ToList();

        var costs = JavaListToList<net.battlescribe.model.data.Cost>(roster.getCosts());
        var costStates = costs.Select(c =>
            new CostState(c.getName() ?? "", c.getTypeId() ?? "", (decimal)c.getValue())).ToList();

        var rawCostLimits = JavaListToList<net.battlescribe.model.data.Cost>(roster.getCostLimits());
        var costLimitStates = rawCostLimits.Count > 0
            ? rawCostLimits.Select(c =>
                new CostState(c.getName() ?? "", c.getTypeId() ?? "", (decimal)c.getValue())).ToList()
            : null;

        return new RosterState(
            roster.getName() ?? "",
            roster.getGameSystemId() ?? "",
            forceStates,
            costStates,
            errors,
            CostLimits: costLimitStates,
            GameSystemName: string.IsNullOrEmpty(roster.getGameSystemName()) ? null : roster.getGameSystemName());
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() => Engine.GetValidationErrors();

    // ===== DataSource support (file-based setup + name-based actions) =====

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
    {
        Engine.RosterName = _specId;
        // Write files to a temp directory so the engine can load them via SimpleXML
        var tempDir = Path.Combine(Path.GetTempPath(), "bsspec-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (fileName, content) in files)
            {
                var filePath = Path.Combine(tempDir, fileName);
                File.WriteAllText(filePath, content);
            }

            // Load .gst file (game system)
            var gstFiles = Directory.GetFiles(tempDir, "*.gst");
            if (gstFiles.Length == 0)
            {
                return ["No .gst (game system) file found in data source files."];
            }

            if (gstFiles.Length > 1)
            {
                return [$"Expected exactly one .gst file, found {gstFiles.Length}."];
            }

            Engine.LoadGameSystemFile(gstFiles[0]);

            // Load all .cat files with dependency resolution
            var catFiles = Directory.GetFiles(tempDir, "*.cat");
            foreach (var catFile in catFiles)
            {
                Engine.LoadCatalogueWithDependencies(catFile, tempDir);
            }

            return Engine.InitializeFromLoadedData();
        }
        finally
        {
            try
            { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BattleScribeRosterEngine] Failed to clean up temp dir '{tempDir}': {ex.Message}");
            }
        }
    }

    // ===== ID-based navigation helpers =====

    /// <summary>
    /// Find a force by its instance ID, searching all forces recursively.
    /// </summary>
    private net.battlescribe.model.roster.Force FindForceById(string forceId)
    {
        foreach (var force in Engine.GetForces())
        {
            var found = FindForceByIdRecursive(force, forceId);
            if (found is not null)
            {
                return found;
            }
        }
        throw new InvalidOperationException(
            $"Force with ID '{forceId}' not found in roster " +
            $"({Engine.GetForces().Count} top-level forces).");
    }

    private static net.battlescribe.model.roster.Force? FindForceByIdRecursive(
        net.battlescribe.model.roster.Force force, string forceId)
    {
        if (force.getId() == forceId)
        {
            return force;
        }

        foreach (var child in JavaListToList<net.battlescribe.model.roster.Force>(force.getForces()))
        {
            var found = FindForceByIdRecursive(child, forceId);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Find a selection by its instance ID within a force, searching recursively.
    /// </summary>
    private static net.battlescribe.model.roster.Selection FindSelectionById(
        net.battlescribe.model.roster.Force force, string selectionId)
    {
        foreach (var sel in JavaListToList<net.battlescribe.model.roster.Selection>(force.getSelections()))
        {
            var found = FindSelectionByIdRecursive(sel, selectionId);
            if (found is not null)
            {
                return found;
            }
        }
        throw new InvalidOperationException(
            $"Selection with ID '{selectionId}' not found in force '{force.getId()}'.");
    }

    private static net.battlescribe.model.roster.Selection? FindSelectionByIdRecursive(
        net.battlescribe.model.roster.Selection sel, string selectionId)
    {
        if (sel.getId() == selectionId)
        {
            return sel;
        }

        foreach (var child in JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections()))
        {
            var found = FindSelectionByIdRecursive(child, selectionId);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Find the parent (Force or Selection) that directly contains the selection with the given ID.
    /// Needed for SetSelectionCount which operates on parent + entry.
    /// </summary>
    private static net.battlescribe.model.roster.BaseSelectionParent FindSelectionParent(
        net.battlescribe.model.roster.Force force, string selectionId)
    {
        foreach (var sel in JavaListToList<net.battlescribe.model.roster.Selection>(force.getSelections()))
        {
            if (sel.getId() == selectionId)
            {
                return force;
            }

            var parent = FindSelectionParentRecursive(sel, selectionId);
            if (parent is not null)
            {
                return parent;
            }
        }
        throw new InvalidOperationException(
            $"Selection '{selectionId}' not found when looking for parent in force '{force.getId()}'.");
    }

    private static net.battlescribe.model.roster.BaseSelectionParent? FindSelectionParentRecursive(
        net.battlescribe.model.roster.Selection sel, string selectionId)
    {
        foreach (var child in JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections()))
        {
            if (child.getId() == selectionId)
            {
                return sel;
            }

            var parent = FindSelectionParentRecursive(child, selectionId);
            if (parent is not null)
            {
                return parent;
            }
        }
        return null;
    }

    /// <summary>
    /// Find a SelectionEntry by its ID in a list of entries.
    /// Handles composite IDs created by entry links (e.g., "link-1::shared-unit").
    /// </summary>
    private static net.battlescribe.model.data.SelectionEntry? FindEntryById(
        List<net.battlescribe.model.data.SelectionEntry> entries, string entryId)
    {
        // Exact match first
        var exact = entries.FirstOrDefault(e => e.getId() == entryId);
        if (exact != null)
        {
            return exact;
        }
        // Composite ID match: entry links create IDs like "linkId::targetId"
        return entries.FirstOrDefault(e =>
        {
            var id = e.getId();
            if (id is null)
            {
                return false;
            }

            if (id.Contains("::"))
            {
                var parts = id.Split("::");
                return parts.Any(p => p == entryId);
            }
            return false;
        });
    }

    /// <summary>
    /// Collect entryId → selectionId map for all child selections (auto-selected defaults).
    /// </summary>
    private static Dictionary<string, string>? CollectChildSelectionIds(
        net.battlescribe.model.roster.Selection selection)
    {
        var children = JavaListToList<net.battlescribe.model.roster.Selection>(selection.getSelections());
        if (children.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>();
        foreach (var child in children)
        {
            CollectSelectionIdsRecursive(child, map);
        }

        return map.Count > 0 ? map : null;
    }

    private static void CollectSelectionIdsRecursive(
        net.battlescribe.model.roster.Selection sel, Dictionary<string, string> map)
    {
        var entryId = sel.getEntryId();
        if (entryId is not null)
        {
            map[entryId] = sel.getId();
        }

        foreach (var child in JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections()))
        {
            CollectSelectionIdsRecursive(child, map);
        }
    }

    /// <summary>
    /// Collect entryId → selectionId map for all selections in a force (top-level + nested).
    /// Used to expose auto-selected entries after AddForce.
    /// </summary>
    private static Dictionary<string, string>? CollectForceSelectionIds(
        net.battlescribe.model.roster.Force force)
    {
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(force.getSelections());
        if (selections.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>();
        foreach (var sel in selections)
        {
            CollectSelectionIdsRecursive(sel, map);
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Flatten child entries including those inside selectionEntryGroups recursively.
    /// </summary>
    private static List<net.battlescribe.model.data.SelectionEntry> FlattenChildEntries(
        net.battlescribe.model.data.SelectionEntry entry)
    {
        var result = new List<net.battlescribe.model.data.SelectionEntry>();
        result.AddRange(JavaListToList<net.battlescribe.model.data.SelectionEntry>(entry.getSelectionEntries()));
        foreach (var group in JavaListToList<net.battlescribe.model.data.SelectionEntryGroup>(entry.getSelectionEntryGroups()))
        {
            FlattenGroupEntries(group, result);
        }

        return result;
    }

    private static void FlattenGroupEntries(
        net.battlescribe.model.data.SelectionEntryGroup group,
        List<net.battlescribe.model.data.SelectionEntry> result)
    {
        result.AddRange(JavaListToList<net.battlescribe.model.data.SelectionEntry>(group.getSelectionEntries()));
        foreach (var nested in JavaListToList<net.battlescribe.model.data.SelectionEntryGroup>(group.getSelectionEntryGroups()))
        {
            FlattenGroupEntries(nested, result);
        }

        foreach (var link in JavaListToList<net.battlescribe.model.data.EntryLink>(group.getEntryLinks()))
        {
            var resolved = JavaListToList<net.battlescribe.model.data.SelectionEntry>(link.getSelectionEntries());
            result.AddRange(resolved);
        }
    }

    public void Dispose() => Engine.Dispose();

    // Expose engine for advanced operations in existing tests
    internal BattleScribeEngine Engine { get; } = new();

    private ForceState CaptureForce(net.battlescribe.model.roster.Force f, int? rootForceIndex = null)
    {
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(f.getSelections());
        var forceProfiles = JavaListToList<net.battlescribe.model.data.Profile>(f.getProfiles());
        var forceRules = JavaListToList<net.battlescribe.model.data.Rule>(f.getRules());
        var childForces = JavaListToList<net.battlescribe.model.roster.Force>(f.getForces());
        var forceCategories = JavaListToList<net.battlescribe.model.roster.Category>(f.getCategories());
        var forcePublications = JavaListToList<net.battlescribe.model.data.Publication>(f.getPublications());
        var pubId = f.getPublicationId();
        // Use engine-resolved ForceEntry (modifiers applied) for hidden state
        var resolvedForceEntry = Engine.GetResolvedForceEntry(f);
        var hidden = resolvedForceEntry?.isHidden()
            ?? Engine.FindForceEntryById(f.getEntryId())?.isHidden()
            ?? false;
        // Sort selections and child forces alphabetically to match BattleScribe render-layer ordering.
        selections = [.. selections.OrderBy(s => s.getName(), StringComparer.OrdinalIgnoreCase)];
        childForces = [.. childForces.OrderBy(cf => cf.getName(), StringComparer.OrdinalIgnoreCase)];
        var customName = f.getCustomName();
        var customNotes = f.getCustomNotes();
        return new ForceState(
            f.getId(),
            f.getName() ?? "",
            f.getCatalogueId(),
            [.. selections.Select(s => CaptureSelection(s, f))],
            rootForceIndex is { } rfi ? Engine.GetAvailableEntryCountForForce(rfi) : null,
            ChildForces: childForces.Count > 0
                ? childForces.Select(cf => CaptureForce(cf)).ToList()
                : null,
            Profiles: [.. forceProfiles.Select(CaptureProfile)],
            Rules: [.. forceRules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden(), r.getPage(),
                string.IsNullOrEmpty(r.getPublicationId()) ? null : r.getPublicationId()))],
            Hidden: hidden,
            PublicationId: string.IsNullOrEmpty(pubId) ? null : pubId,
            Page: f.getPage(),
            EntryId: string.IsNullOrEmpty(f.getEntryId()) ? null : f.getEntryId(),
            Categories: [.. forceCategories.Select(c =>
            {
                var catPubId = c.getPublicationId();
                var catCustomNotes = c.getCustomNotes();
                return new CategoryState(
                    c.getName() ?? "", c.getEntryId(), c.isPrimary(),
                    PublicationId: string.IsNullOrEmpty(catPubId) ? null : catPubId,
                    Page: c.getPage(),
                    CustomNotes: string.IsNullOrEmpty(catCustomNotes) ? null : catCustomNotes);
            })],
            Publications: forcePublications.Count > 0
                ? forcePublications.Select(p => new PublicationState(p.getId() ?? "", p.getName() ?? "")).ToList()
                : null,
            CatalogueName: string.IsNullOrEmpty(f.getCatalogueName()) ? null : f.getCatalogueName(),
            CustomName: string.IsNullOrEmpty(customName) ? null : customName,
            CustomNotes: string.IsNullOrEmpty(customNotes) ? null : customNotes);
    }

    private SelectionState CaptureSelection(net.battlescribe.model.roster.Selection sel, net.battlescribe.model.roster.Force force)
    {
        var costs = JavaListToList<net.battlescribe.model.data.Cost>(sel.getCosts());
        var children = JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections());
        // Sort children alphabetically to match BattleScribe render-layer ordering.
        children = [.. children.OrderBy(c => c.getName(), StringComparer.OrdinalIgnoreCase)];
        var profiles = JavaListToList<net.battlescribe.model.data.Profile>(sel.getProfiles());
        var rules = JavaListToList<net.battlescribe.model.data.Rule>(sel.getRules());
        var categories = JavaListToList<net.battlescribe.model.roster.Category>(sel.getCategories());
        // Use engine's modifier-application to get resolved hidden state
        var resolvedEntry = Engine.GetResolvedEntry(force, sel);
        var hidden = resolvedEntry?.isHidden()
            ?? Engine.GetEntryById(sel.getEntryId())?.isHidden()
            ?? false;
        var pubId = sel.getPublicationId();
        var selCustomName = sel.getCustomName();
        var selCustomNotes = sel.getCustomNotes();
        return new SelectionState(
            sel.getId(),
            sel.getName() ?? "",
            sel.getEntryId(),
            sel.getType(),
            sel.getNumber(),
            hidden,
            [.. costs.Select(c => new CostState(c.getName() ?? "", c.getTypeId() ?? "", (decimal)c.getValue()))],
            [.. children.Select(c => CaptureSelection(c, force))],
            Profiles: [.. profiles.Select(CaptureProfile)],
            Rules: [.. rules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden(), r.getPage(),
                string.IsNullOrEmpty(r.getPublicationId()) ? null : r.getPublicationId()))],
            Categories: [.. categories.Select(c =>
            {
                var catProfiles = JavaListToList<net.battlescribe.model.data.Profile>(c.getProfiles());
                var catRules = JavaListToList<net.battlescribe.model.data.Rule>(c.getRules());
                var catPubId = c.getPublicationId();
                var catCustomNotes = c.getCustomNotes();
                return new CategoryState(
                    c.getName() ?? "", c.getEntryId(), c.isPrimary(),
                    Profiles: [.. catProfiles.Select(CaptureProfile)],
                    Rules: [.. catRules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden(), r.getPage(),
                        string.IsNullOrEmpty(r.getPublicationId()) ? null : r.getPublicationId()))],
                    PublicationId: string.IsNullOrEmpty(catPubId) ? null : catPubId,
                    Page: c.getPage(),
                    CustomNotes: string.IsNullOrEmpty(catCustomNotes) ? null : catCustomNotes);
            })],
            Page: sel.getPage(),
            PublicationId: string.IsNullOrEmpty(pubId) ? null : pubId,
            PublicationName: Engine.GetPublicationName(pubId),
            EntryGroupId: string.IsNullOrEmpty(sel.getEntryGroupId()) ? null : sel.getEntryGroupId(),
            CustomName: string.IsNullOrEmpty(selCustomName) ? null : selCustomName,
            CustomNotes: string.IsNullOrEmpty(selCustomNotes) ? null : selCustomNotes);
    }

    private static ProfileState CaptureProfile(net.battlescribe.model.data.Profile prof)
    {
        var chars = JavaListToList<net.battlescribe.model.data.Characteristic>(prof.getCharacteristics());
        var pubId = prof.getPublicationId();
        return new ProfileState(
            prof.getName() ?? "",
            prof.getTypeId(),
            prof.getTypeName(),
            prof.isHidden(),
            [.. chars.Select(c => new CharacteristicState(c.getName() ?? "", c.getTypeId(), c.getValue() ?? ""))],
            prof.getPage(),
            string.IsNullOrEmpty(pubId) ? null : pubId);
    }

    private static List<T> JavaListToList<T>(java.util.List? javaList)
    {
        if (javaList is null)
        {
            return [];
        }

        var result = new List<T>(javaList.size());
        var iter = javaList.iterator();
        while (iter.hasNext())
        {
            result.Add((T)iter.next());
        }

        return result;
    }
}
