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

    public void AddForce(int forceEntryIndex, int catalogueIndex = 0)
    {
        _oracle.AddForceByIndex(forceEntryIndex, catalogueIndex);
    }

    public void RemoveForce(int forceIndex)
    {
        var forces = _oracle.GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        _oracle.RemoveForce(forces[forceIndex]);
    }

    public void SelectEntry(int forceIndex, int entryIndex)
    {
        _oracle.SelectEntryByIndex(forceIndex, entryIndex);
    }

    public void SelectChildEntry(int forceIndex, int selectionIndex, int childEntryIndex)
    {
        var forces = _oracle.GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(forces[forceIndex].getSelections());
        if (selectionIndex < 0 || selectionIndex >= selections.Count)
            throw new ArgumentOutOfRangeException(nameof(selectionIndex));
        var parentSelection = selections[selectionIndex];
        var parentEntryId = parentSelection.getEntryId();
        var parentEntry = _oracle.GetEntryById(parentEntryId)
            ?? _oracle.GetEntryByCompositeId(parentEntryId)
            ?? throw new InvalidOperationException($"Parent entry '{parentEntryId}' not found in entry lookup.");
        var childEntries = FlattenChildEntries(parentEntry);
        if (childEntryIndex < 0 || childEntryIndex >= childEntries.Count)
            throw new ArgumentOutOfRangeException(nameof(childEntryIndex));
        _oracle.SelectEntry(parentSelection, childEntries[childEntryIndex]);
    }

    public void DeselectSelection(int forceIndex, int selectionIndex)
    {
        var forces = _oracle.GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(forces[forceIndex].getSelections());
        if (selectionIndex < 0 || selectionIndex >= selections.Count)
            throw new ArgumentOutOfRangeException(nameof(selectionIndex));
        _oracle.DeselectEntry(selections[selectionIndex]);
    }

    public void SetSelectionCount(int forceIndex, int entryIndex, int count)
    {
        var forces = _oracle.GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        var entry = _oracle.GetSelectionEntryForForce(forceIndex, entryIndex);
        _oracle.SetNumSelections(forces[forceIndex], entry, count);
    }

    public void DuplicateSelection(int forceIndex, int selectionIndex)
    {
        var forces = _oracle.GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(forces[forceIndex].getSelections());
        if (selectionIndex < 0 || selectionIndex >= selections.Count)
            throw new ArgumentOutOfRangeException(nameof(selectionIndex));
        _oracle.DuplicateSelection(selections[selectionIndex]);
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

        var forceStates = forces.Select((f, i) =>
        {
            var selections = JavaListToList<net.battlescribe.model.roster.Selection>(f.getSelections());
            var forceProfiles = JavaListToList<net.battlescribe.model.data.Profile>(f.getProfiles());
            var forceRules = JavaListToList<net.battlescribe.model.data.Rule>(f.getRules());
            var pubId = f.getPublicationId();
            return new ForceState(
                f.getName() ?? "",
                f.getCatalogueId(),
                selections.Select(CaptureSelection).ToList(),
                _oracle.GetAvailableEntryCountForForce(i),
                Profiles: forceProfiles.Select(CaptureProfile).ToList(),
                Rules: forceRules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden(), r.getPage(),
                    string.IsNullOrEmpty(r.getPublicationId()) ? null : r.getPublicationId())).ToList(),
                PublicationId: string.IsNullOrEmpty(pubId) ? null : pubId,
                Page: f.getPage());
        }).ToList();

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

    public void AddForceByName(string forceName, string? catalogueName = null, int catalogueIndex = 0)
    {
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

    public void SelectEntryByName(int forceIndex, string entryName)
    {
        var result = _oracle.SelectEntryByNameOnForce(entryName, forceIndex);
        if (result < 0)
            throw new InvalidOperationException(
                $"Entry '{entryName}' not found on force {forceIndex}. Available: {string.Join(", ", _oracle.GetAllAvailableEntryNames().Take(30))}");
    }

    public void SelectChildEntryByName(int forceIndex, int selectionIndex, string childEntryName)
    {
        var forces = _oracle.GetForces();
        if (forceIndex < 0 || forceIndex >= forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        var selections = JavaListToList<net.battlescribe.model.roster.Selection>(forces[forceIndex].getSelections());
        if (selectionIndex < 0 || selectionIndex >= selections.Count)
            throw new ArgumentOutOfRangeException(nameof(selectionIndex));
        var parentSelection = selections[selectionIndex];

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
