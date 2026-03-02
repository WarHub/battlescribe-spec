using System.Collections.Immutable;

namespace BattleScribeSpec;

/// <summary>
/// IRosterEngine implementation backed by the BattleScribe Java engine via IKVM.
/// Serves as the reference oracle for conformance testing.
/// </summary>
public sealed class OracleRosterEngine : IRosterEngine
{
    private readonly BattleScribeOracle _oracle = new();
    private CatalogueSpec[]? _catalogueSpecs;

    public IReadOnlyList<string> Setup(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        _catalogueSpecs = catalogues;
        var scenario = new ScenarioSpec(gameSystem, catalogues);
        return _oracle.SetupFromSpec(scenario);
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
            ?? throw new InvalidOperationException($"Parent entry '{parentEntryId}' not found in entry lookup.");
        var childEntries = JavaListToList<net.battlescribe.model.data.SelectionEntry>(parentEntry.getSelectionEntries());
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
            return new ForceState(
                f.getName() ?? "",
                f.getCatalogueId(),
                selections.Select(CaptureSelection).ToList(),
                _oracle.GetAvailableEntryCountForForce(i));
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

    public IReadOnlyList<string> GetValidationErrors() => _oracle.GetValidationErrors();

    public bool HasValidationErrors() => _oracle.HasValidationErrors();

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
        return new SelectionState(
            sel.getName() ?? "",
            sel.getEntryId(),
            sel.getType(),
            sel.getNumber(),
            hidden,
            costs.Select(c => new CostState(c.getName() ?? "", c.getTypeId() ?? "", c.getValue())).ToList(),
            children.Select(CaptureSelection).ToList(),
            Profiles: profiles.Select(CaptureProfile).ToList(),
            Rules: rules.Select(r => new RuleState(r.getName() ?? "", r.getDescription() ?? "", r.isHidden())).ToList(),
            Categories: categories.Select(c => new CategoryState(c.getName() ?? "", c.getEntryId(), c.isPrimary())).ToList(),
            Page: sel.getPage());
    }

    private static ProfileState CaptureProfile(net.battlescribe.model.data.Profile prof)
    {
        var chars = JavaListToList<net.battlescribe.model.data.Characteristic>(prof.getCharacteristics());
        return new ProfileState(
            prof.getName() ?? "",
            prof.getTypeId(),
            prof.getTypeName(),
            prof.isHidden(),
            chars.Select(c => new CharacteristicState(c.getName() ?? "", c.getTypeId(), c.getValue() ?? "")).ToList());
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
