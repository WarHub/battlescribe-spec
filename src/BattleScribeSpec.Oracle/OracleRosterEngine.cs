using System.Collections.Immutable;

using BattleScribeSpec.Protocol;

namespace BattleScribeSpec;

/// <summary>
/// IRosterEngine implementation backed by the BattleScribe Java engine via IKVM.
/// Serves as the reference oracle for conformance testing.
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

    public void AddForce(int[] forcePath, int forceEntryIndex, int catalogueIndex = 0)
    {
        if (forcePath.Length == 0)
        {
            _oracle.AddForceByIndex(forceEntryIndex, catalogueIndex);
            return;
        }
        // Nested: navigate to parent force and add a child force
        var parentForce = NavigateForce(forcePath);
        _oracle.AddChildForce(parentForce, forceEntryIndex, catalogueIndex);
    }

    public void RemoveForce(int[] forcePath)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for RemoveForce.");
        if (forcePath.Length == 1)
        {
            var forceIndex = forcePath[0];
            var forces = _oracle.GetForces();
            if (forceIndex < 0 || forceIndex >= forces.Count)
                throw new ArgumentOutOfRangeException(nameof(forcePath));
            _oracle.RemoveForce(forces[forceIndex]);
            return;
        }
        // Nested: navigate to the parent, then remove child at last index
        var parentPath = forcePath[..^1];
        var childIndex = forcePath[^1];
        var parent = NavigateForce(parentPath);
        var childForces = JavaListToList<net.battlescribe.model.roster.Force>(parent.getForces());
        if (childIndex < 0 || childIndex >= childForces.Count)
            throw new ArgumentOutOfRangeException(nameof(forcePath), $"Child force index {childIndex} out of range ({childForces.Count} children).");
        _oracle.RemoveForce(childForces[childIndex]);
    }

    public void SelectEntry(int[] forcePath, int entryIndex)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for SelectEntry.");
        if (forcePath.Length == 1)
        {
            _oracle.SelectEntryByIndex(forcePath[0], entryIndex);
            return;
        }
        // Nested: navigate to the target force, get entries, select
        var force = NavigateForce(forcePath);
        var entries = _oracle.GetEntriesForForce(force);
        if (entryIndex < 0 || entryIndex >= entries.Count)
            throw new ArgumentOutOfRangeException(nameof(entryIndex),
                $"Entry index {entryIndex} out of range (have {entries.Count} entries)");
        _oracle.SelectEntry(force, entries[entryIndex]);
    }

    public void SelectChildEntry(int[] forcePath, int[] selectionPath, int childEntryIndex)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for SelectChildEntry.");
        if (selectionPath.Length == 0)
            throw new ArgumentException("selectionPath cannot be empty for SelectChildEntry.");
        var force = NavigateForce(forcePath);
        var parentSelection = NavigateSelection(force, selectionPath);
        var parentEntryId = parentSelection.getEntryId();
        var parentEntry = _oracle.GetEntryById(parentEntryId)
            ?? _oracle.GetEntryByCompositeId(parentEntryId)
            ?? throw new InvalidOperationException($"Parent entry '{parentEntryId}' not found in entry lookup.");
        var childEntries = FlattenChildEntries(parentEntry);
        if (childEntryIndex < 0 || childEntryIndex >= childEntries.Count)
            throw new ArgumentOutOfRangeException(nameof(childEntryIndex));
        _oracle.SelectEntry(parentSelection, childEntries[childEntryIndex]);
    }

    public void DeselectSelection(int[] forcePath, int[] selectionPath)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for DeselectSelection.");
        if (selectionPath.Length == 0)
            throw new ArgumentException("selectionPath cannot be empty for DeselectSelection.");
        var force = NavigateForce(forcePath);
        var selection = NavigateSelection(force, selectionPath);
        _oracle.DeselectEntry(selection);
    }

    public void SetSelectionCount(int[] forcePath, int entryIndex, int count)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for SetSelectionCount.");
        if (forcePath.Length == 1)
        {
            var forceIndex = forcePath[0];
            var forces = _oracle.GetForces();
            if (forceIndex < 0 || forceIndex >= forces.Count)
                throw new ArgumentOutOfRangeException(nameof(forcePath));
            var entry = _oracle.GetSelectionEntryForForce(forceIndex, entryIndex);
            _oracle.SetNumSelections(forces[forceIndex], entry, count);
            return;
        }
        var force = NavigateForce(forcePath);
        var entries = _oracle.GetEntriesForForce(force);
        if (entryIndex < 0 || entryIndex >= entries.Count)
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        _oracle.SetNumSelections(force, entries[entryIndex], count);
    }

    public void DuplicateSelection(int[] forcePath, int[] selectionPath)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for DuplicateSelection.");
        if (selectionPath.Length == 0)
            throw new ArgumentException("selectionPath cannot be empty for DuplicateSelection.");
        var force = NavigateForce(forcePath);
        var selection = NavigateSelection(force, selectionPath);
        _oracle.DuplicateSelection(selection);
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

    public void AddForceByName(int[] forcePath, string forceName, string? catalogueName = null, int catalogueIndex = 0)
    {
        if (forcePath.Length > 0)
        {
            // Nested force addition by name: navigate to parent, resolve force entry by name
            var parentForce = NavigateForce(forcePath);
            var feIndex = _oracle.GetChildForceEntryIndexByName(parentForce, forceName);
            if (feIndex < 0)
                throw new InvalidOperationException(
                    $"Child force entry '{forceName}' not found under parent force.");
            if (catalogueName is { Length: > 0 })
            {
                catalogueIndex = _oracle.GetCatalogueIndexByName(catalogueName);
                if (catalogueIndex < 0)
                    throw new InvalidOperationException(
                        $"Catalogue '{catalogueName}' not found. Available: {string.Join(", ", _oracle.GetLoadedCatalogueNames())}");
            }
            _oracle.AddChildForce(parentForce, feIndex, catalogueIndex);
            return;
        }
        var index = _oracle.GetForceEntryIndexByName(forceName);
        if (index < 0)
            throw new InvalidOperationException(
                $"Force entry '{forceName}' not found. Available: {string.Join(", ", _oracle.GetAvailableForceEntryNames())}");
        if (catalogueName is { Length: > 0 })
        {
            catalogueIndex = _oracle.GetCatalogueIndexByName(catalogueName);
            if (catalogueIndex < 0)
                throw new InvalidOperationException(
                    $"Catalogue '{catalogueName}' not found. Available: {string.Join(", ", _oracle.GetLoadedCatalogueNames())}");
        }
        _oracle.AddForceByIndex(index, catalogueIndex);
    }

    public void SelectEntryByName(int[] forcePath, string entryName)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for SelectEntryByName.");
        if (forcePath.Length == 1)
        {
            var result = _oracle.SelectEntryByNameOnForce(entryName, forcePath[0]);
            if (result < 0)
                throw new InvalidOperationException(
                    $"Entry '{entryName}' not found on force {forcePath[0]}. Available: {string.Join(", ", _oracle.GetAllAvailableEntryNames().Take(30))}");
            return;
        }
        var force = NavigateForce(forcePath);
        var entries = _oracle.GetEntriesForForce(force);
        var entry = entries.FirstOrDefault(
            e => string.Equals(e.getName(), entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidOperationException(
                $"Entry '{entryName}' not found on nested force. Available: {string.Join(", ", entries.Select(e => e.getName()))}");
        _oracle.SelectEntry(force, entry);
    }

    public void SelectChildEntryByName(int[] forcePath, int[] selectionPath, string childEntryName)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty for SelectChildEntryByName.");
        if (selectionPath.Length == 0)
            throw new ArgumentException("selectionPath cannot be empty for SelectChildEntryByName.");
        var force = NavigateForce(forcePath);
        var parentSelection = NavigateSelection(force, selectionPath);

        var parentEntryId = parentSelection.getEntryId();
        var parentEntry = _oracle.GetEntryById(parentEntryId)
            ?? _oracle.GetEntryByCompositeId(parentEntryId);
        if (parentEntry is null)
            throw new InvalidOperationException($"Parent entry '{parentEntryId}' not found.");

        var childEntries = FlattenChildEntries(parentEntry);
        var childEntry = childEntries.FirstOrDefault(
            ce => string.Equals(ce.getName(), childEntryName, StringComparison.OrdinalIgnoreCase));
        if (childEntry is null)
            throw new InvalidOperationException(
                $"Child entry '{childEntryName}' not found under '{parentEntry.getName()}'. " +
                $"Available: {string.Join(", ", childEntries.Select(c => c.getName()))}");

        _oracle.SelectEntry(parentSelection, childEntry);
    }

    /// <summary>
    /// Navigate the force tree using a path of indices.
    /// <c>[0]</c> = top-level force 0; <c>[0, 1]</c> = child 1 of top-level force 0; etc.
    /// </summary>
    private net.battlescribe.model.roster.Force NavigateForce(int[] forcePath)
    {
        if (forcePath.Length == 0)
            throw new ArgumentException("forcePath cannot be empty.");
        var forces = _oracle.GetForces();
        if (forcePath[0] < 0 || forcePath[0] >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forcePath),
                $"Force index {forcePath[0]} out of range ({forces.Count} top-level forces).");
        var current = forces[forcePath[0]];
        for (int i = 1; i < forcePath.Length; i++)
        {
            var childForces = JavaListToList<net.battlescribe.model.roster.Force>(current.getForces());
            if (forcePath[i] < 0 || forcePath[i] >= childForces.Count)
                throw new ArgumentOutOfRangeException(nameof(forcePath),
                    $"Force path index [{i}]={forcePath[i]} out of range ({childForces.Count} child forces at depth {i}).");
            current = childForces[forcePath[i]];
        }
        return current;
    }

    /// <summary>
    /// Navigate the selection tree within a force using a path of indices.
    /// <c>[0]</c> = selection 0 of the force; <c>[0, 2]</c> = child 2 of selection 0; etc.
    /// </summary>
    private static net.battlescribe.model.roster.Selection NavigateSelection(
        net.battlescribe.model.roster.Force force, int[] selectionPath)
    {
        if (selectionPath.Length == 0)
            throw new ArgumentException("selectionPath cannot be empty.");
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(force.getSelections());
        if (selectionPath[0] < 0 || selectionPath[0] >= selections.Count)
            throw new ArgumentOutOfRangeException(nameof(selectionPath),
                $"Selection index {selectionPath[0]} out of range ({selections.Count} selections).");
        var current = selections[selectionPath[0]];
        for (int i = 1; i < selectionPath.Length; i++)
        {
            var children = JavaListToList<net.battlescribe.model.roster.Selection>(current.getSelections());
            if (selectionPath[i] < 0 || selectionPath[i] >= children.Count)
                throw new ArgumentOutOfRangeException(nameof(selectionPath),
                    $"Selection path index [{i}]={selectionPath[i]} out of range ({children.Count} child selections at depth {i}).");
            current = children[selectionPath[i]];
        }
        return current;
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
        return new ForceState(
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
