using System.Collections.Immutable;

using BattleScribeSpec.Protocol;

namespace BattleScribeSpec;

/// <summary>
/// IRosterEngine implementation backed by the BattleScribe Java engine via IKVM.
/// Serves as the reference oracle for conformance testing.
/// All addressing is ID-based: definition IDs for data references,
/// instance IDs (from previous action outputs) for roster element references.
/// </summary>
public sealed class OracleRosterEngine : IRosterEngine
{
    private readonly BattleScribeOracle _oracle = new();
    private string? _specId;

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        _oracle.RosterName = _specId;
        return _oracle.SetupFromProtocol(gameSystem, catalogues);
    }

    public ActionOutputs AddForce(string forceEntryId, string? catalogueId = null)
    {
        var forceEntry = _oracle.FindForceEntryById(forceEntryId)
            ?? throw new InvalidOperationException($"ForceEntry '{forceEntryId}' not found.");

        var catalogue = _oracle.ResolveCatalogue(catalogueId);
        var linked = _oracle.ResolveLinkedCatalogues(catalogue);
        var forcesBefore = new HashSet<net.battlescribe.model.roster.Force>(
            _oracle.GetForces(), ReferenceEqualityComparer.Instance);

        var (force, _) = _oracle.AddForce(catalogue, forceEntry, linked);

        if (force is null)
            throw new InvalidOperationException("Java engine returned null force for AddForce.");

        foreach (var f in _oracle.GetForces())
        {
            if (!forcesBefore.Contains(f))
                _oracle.TrackForceCatalogue(f, catalogue);
        }

        // Re-read force from roster to capture auto-selected entries (from constraints)
        var rosterForce = FindForceById(force.getId());
        var selections = CollectForceSelectionIds(rosterForce);
        return new ActionOutputs { ForceId = force.getId(), Selections = selections };
    }

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string? catalogueId = null)
    {
        var parentForce = FindForceById(parentForceId);
        var forceEntry = _oracle.FindForceEntryById(forceEntryId)
            ?? throw new InvalidOperationException($"ForceEntry '{forceEntryId}' not found.");

        var catalogue = catalogueId is not null
            ? _oracle.ResolveCatalogue(catalogueId)
            : _oracle.GetForceCatalogue(parentForce);

        var childForce = _oracle.CreateChildForce(parentForce, forceEntry, catalogue);
        return new ActionOutputs { ForceId = childForce.getId() };
    }

    public void RemoveForce(string forceId)
    {
        var force = FindForceById(forceId);
        _oracle.RemoveForce(force);
    }

    public ActionOutputs SelectEntry(string forceId, string entryId)
    {
        var force = FindForceById(forceId);
        var entries = _oracle.GetEntriesForForce(force);
        var entry = FindEntryById(entries, entryId)
            ?? throw new InvalidOperationException(
                $"Entry '{entryId}' not found in force '{forceId}' " +
                $"(have {entries.Count} entries: [{string.Join(", ", entries.Select(e => $"{e.getId()}/{e.getName()}"))}]).");

        var createdSelections = _oracle.SelectEntry(force, entry);

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
        var parentEntry = _oracle.GetEntryById(parentEntryId)
            ?? _oracle.GetEntryByCompositeId(parentEntryId)
            ?? throw new InvalidOperationException($"Parent entry '{parentEntryId}' not found in entry lookup.");

        var childEntries = FlattenChildEntries(parentEntry);
        var childEntry = FindEntryById(childEntries, entryId)
            ?? throw new InvalidOperationException(
                $"Child entry '{entryId}' not found under parent selection '{parentSelectionId}'.");

        var createdSelections = _oracle.SelectEntry(parentSelection, childEntry);
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
        _oracle.DeselectEntry(selection);
    }

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        var force = FindForceById(forceId);
        var selection = FindSelectionById(force, selectionId);
        var entryId = selection.getEntryId();
        var dataEntry = _oracle.GetEntryById(entryId)
            ?? _oracle.GetEntryByCompositeId(entryId)
            ?? throw new InvalidOperationException(
                $"Entry '{entryId}' not found in entry lookup for SetSelectionCount.");
        // Find the parent of this selection (the container that holds it)
        var parent = FindSelectionParent(force, selectionId);
        _oracle.SetNumSelections(parent, dataEntry, count);
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
    {
        var force = FindForceById(forceId);
        var selection = FindSelectionById(force, selectionId);
        var duplicated = _oracle.DuplicateSelection(selection);
        return new ActionOutputs
        {
            SelectionId = duplicated?.getId()
        };
    }

    public void SetCostLimit(string costTypeId, double value)
    {
        var costType = _oracle.GetCostTypeById(costTypeId)
            ?? throw new InvalidOperationException($"Cost type '{costTypeId}' not found.");
        _oracle.SetCostLimit(costType, value);
    }

    public RosterState GetRosterState()
    {
        var roster = _oracle.GetRoster();
        var forces = _oracle.GetForces();
        var errors = _oracle.GetValidationErrors();

        var forceStates = forces.Select((f, i) => CaptureForce(f, i)).ToList();

        var costs = JavaListToList<net.battlescribe.model.data.Cost>(roster.getCosts());
        var costStates = costs.Select(c =>
            new CostState(c.getName() ?? "", c.getTypeId() ?? "", c.getValue())).ToList();

        return new RosterState(
            roster.getName() ?? "",
            roster.getGameSystemId() ?? "",
            forceStates,
            costStates,
            errors);
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() => _oracle.GetValidationErrors();

    // ===== DataSource support (file-based setup + name-based actions) =====

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
    {
        _oracle.RosterName = _specId;
        // Write files to a temp directory so the oracle can load them via SimpleXML
        var tempDir = Path.Combine(Path.GetTempPath(), "bsspec-oracle-" + Guid.NewGuid().ToString("N")[..8]);
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
                return ["No .gst (game system) file found in data source files."];
            if (gstFiles.Length > 1)
                return [$"Expected exactly one .gst file, found {gstFiles.Length}."];
            _oracle.LoadGameSystemFile(gstFiles[0]);

            // Load all .cat files with dependency resolution
            var catFiles = Directory.GetFiles(tempDir, "*.cat");
            foreach (var catFile in catFiles)
            {
                _oracle.LoadCatalogueWithDependencies(catFile, tempDir);
            }

            return _oracle.InitializeFromLoadedData();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OracleRosterEngine] Failed to clean up temp dir '{tempDir}': {ex.Message}");
            }
        }
    }

    // ===== ID-based navigation helpers =====

    /// <summary>
    /// Find a force by its instance ID, searching all forces recursively.
    /// </summary>
    private net.battlescribe.model.roster.Force FindForceById(string forceId)
    {
        foreach (var force in _oracle.GetForces())
        {
            var found = FindForceByIdRecursive(force, forceId);
            if (found is not null) return found;
        }
        throw new InvalidOperationException(
            $"Force with ID '{forceId}' not found in roster " +
            $"({_oracle.GetForces().Count} top-level forces).");
    }

    private static net.battlescribe.model.roster.Force? FindForceByIdRecursive(
        net.battlescribe.model.roster.Force force, string forceId)
    {
        if (force.getId() == forceId) return force;
        foreach (var child in JavaListToList<net.battlescribe.model.roster.Force>(force.getForces()))
        {
            var found = FindForceByIdRecursive(child, forceId);
            if (found is not null) return found;
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
            if (found is not null) return found;
        }
        throw new InvalidOperationException(
            $"Selection with ID '{selectionId}' not found in force '{force.getId()}'.");
    }

    private static net.battlescribe.model.roster.Selection? FindSelectionByIdRecursive(
        net.battlescribe.model.roster.Selection sel, string selectionId)
    {
        if (sel.getId() == selectionId) return sel;
        foreach (var child in JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections()))
        {
            var found = FindSelectionByIdRecursive(child, selectionId);
            if (found is not null) return found;
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
            if (sel.getId() == selectionId) return force;
            var parent = FindSelectionParentRecursive(sel, selectionId);
            if (parent is not null) return parent;
        }
        throw new InvalidOperationException(
            $"Selection '{selectionId}' not found when looking for parent in force '{force.getId()}'.");
    }

    private static net.battlescribe.model.roster.BaseSelectionParent? FindSelectionParentRecursive(
        net.battlescribe.model.roster.Selection sel, string selectionId)
    {
        foreach (var child in JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections()))
        {
            if (child.getId() == selectionId) return sel;
            var parent = FindSelectionParentRecursive(child, selectionId);
            if (parent is not null) return parent;
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
        if (exact != null) return exact;
        // Composite ID match: entry links create IDs like "linkId::targetId"
        return entries.FirstOrDefault(e =>
        {
            var id = e.getId();
            if (id is null) return false;
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
        if (children.Count == 0) return null;
        var map = new Dictionary<string, string>();
        foreach (var child in children)
            CollectSelectionIdsRecursive(child, map);
        return map.Count > 0 ? map : null;
    }

    private static void CollectSelectionIdsRecursive(
        net.battlescribe.model.roster.Selection sel, Dictionary<string, string> map)
    {
        var entryId = sel.getEntryId();
        if (entryId is not null)
            map[entryId] = sel.getId();
        foreach (var child in JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections()))
            CollectSelectionIdsRecursive(child, map);
    }

    /// <summary>
    /// Collect entryId → selectionId map for all selections in a force (top-level + nested).
    /// Used to expose auto-selected entries after AddForce.
    /// </summary>
    private static Dictionary<string, string>? CollectForceSelectionIds(
        net.battlescribe.model.roster.Force force)
    {
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(force.getSelections());
        if (selections.Count == 0) return null;
        var map = new Dictionary<string, string>();
        foreach (var sel in selections)
            CollectSelectionIdsRecursive(sel, map);
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
            FlattenGroupEntries(group, result);
        return result;
    }

    private static void FlattenGroupEntries(
        net.battlescribe.model.data.SelectionEntryGroup group,
        List<net.battlescribe.model.data.SelectionEntry> result)
    {
        result.AddRange(JavaListToList<net.battlescribe.model.data.SelectionEntry>(group.getSelectionEntries()));
        foreach (var nested in JavaListToList<net.battlescribe.model.data.SelectionEntryGroup>(group.getSelectionEntryGroups()))
            FlattenGroupEntries(nested, result);
        foreach (var link in JavaListToList<net.battlescribe.model.data.EntryLink>(group.getEntryLinks()))
        {
            var resolved = JavaListToList<net.battlescribe.model.data.SelectionEntry>(link.getSelectionEntries());
            result.AddRange(resolved);
        }
    }

    public void Dispose() => _oracle.Dispose();

    // Expose oracle for advanced operations in existing tests
    internal BattleScribeOracle Oracle => _oracle;

    private ForceState CaptureForce(net.battlescribe.model.roster.Force f, int? rootForceIndex = null)
    {
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(f.getSelections());
        var forceProfiles = JavaListToList<net.battlescribe.model.data.Profile>(f.getProfiles());
        var forceRules = JavaListToList<net.battlescribe.model.data.Rule>(f.getRules());
        var childForces = JavaListToList<net.battlescribe.model.roster.Force>(f.getForces());
        var pubId = f.getPublicationId();
        var forceEntry = _oracle.FindForceEntryById(f.getEntryId());
        var hidden = forceEntry?.isHidden() ?? false;
        return new ForceState(
            f.getId(),
            f.getName() ?? "",
            f.getCatalogueId(),
            selections.Select(CaptureSelection).ToList(),
            rootForceIndex is { } rfi ? _oracle.GetAvailableEntryCountForForce(rfi) : null,
            ChildForces: childForces.Count > 0
                ? childForces.Select(cf => CaptureForce(cf)).ToList()
                : null,
            Profiles: forceProfiles.Select(CaptureProfile).ToList(),
            Rules: forceRules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden(), r.getPage(),
                string.IsNullOrEmpty(r.getPublicationId()) ? null : r.getPublicationId())).ToList(),
            Hidden: hidden,
            PublicationId: string.IsNullOrEmpty(pubId) ? null : pubId,
            Page: f.getPage());
    }

    private SelectionState CaptureSelection(net.battlescribe.model.roster.Selection sel)
    {
        var costs = JavaListToList<net.battlescribe.model.data.Cost>(sel.getCosts());
        var children = JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections());
        var profiles = JavaListToList<net.battlescribe.model.data.Profile>(sel.getProfiles());
        var rules = JavaListToList<net.battlescribe.model.data.Rule>(sel.getRules());
        var categories = JavaListToList<net.battlescribe.model.roster.Category>(sel.getCategories());
        var hidden = _oracle.GetEntryById(sel.getEntryId())?.isHidden() ?? false;
        var pubId = sel.getPublicationId();
        return new SelectionState(
            sel.getId(),
            sel.getName() ?? "",
            sel.getEntryId(),
            sel.getType(),
            sel.getNumber(),
            hidden,
            costs.Select(c => new CostState(c.getName() ?? "", c.getTypeId() ?? "", c.getValue())).ToList(),
            children.Select(CaptureSelection).ToList(),
            Profiles: profiles.Select(CaptureProfile).ToList(),
            Rules: rules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden(), r.getPage(),
                string.IsNullOrEmpty(r.getPublicationId()) ? null : r.getPublicationId())).ToList(),
            Categories: categories.Select(c =>
            {
                var catProfiles = JavaListToList<net.battlescribe.model.data.Profile>(c.getProfiles());
                var catRules = JavaListToList<net.battlescribe.model.data.Rule>(c.getRules());
                var catPubId = c.getPublicationId();
                return new CategoryState(
                    c.getName() ?? "", c.getEntryId(), c.isPrimary(),
                    Profiles: catProfiles.Select(CaptureProfile).ToList(),
                    Rules: catRules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden(), r.getPage(),
                        string.IsNullOrEmpty(r.getPublicationId()) ? null : r.getPublicationId())).ToList(),
                    PublicationId: string.IsNullOrEmpty(catPubId) ? null : catPubId,
                    Page: c.getPage());
            }).ToList(),
            Page: sel.getPage(),
            PublicationId: string.IsNullOrEmpty(pubId) ? null : pubId,
            PublicationName: _oracle.GetPublicationName(pubId));
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
            chars.Select(c => new CharacteristicState(c.getName() ?? "", c.getTypeId(), c.getValue() ?? "")).ToList(),
            prof.getPage(),
            string.IsNullOrEmpty(pubId) ? null : pubId);
    }

    private static List<T> JavaListToList<T>(java.util.List? javaList)
    {
        if (javaList is null) return [];
        var result = new List<T>(javaList.size());
        var iter = javaList.iterator();
        while (iter.hasNext())
            result.Add((T)iter.next());
        return result;
    }
}
