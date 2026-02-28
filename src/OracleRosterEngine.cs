using System.Collections.Immutable;

namespace BattleScribeSpec;

/// <summary>
/// IRosterEngine implementation backed by the BattleScribe Java engine via IKVM.
/// Serves as the reference oracle for conformance testing.
/// </summary>
public sealed class OracleRosterEngine : IRosterEngine
{
    private readonly BattleScribeOracle _oracle = new();
    private CatalogueSpec? _catalogueSpec;

    public IReadOnlyList<string> Setup(GameSystemSpec gameSystem, CatalogueSpec catalogue)
    {
        _catalogueSpec = catalogue;
        var scenario = new ScenarioSpec(gameSystem, catalogue);
        return _oracle.SetupFromSpec(scenario);
    }

    public void AddForce(int forceEntryIndex)
    {
        _oracle.AddForceByIndex(forceEntryIndex);
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
        // Child entry selection requires the parent selection and child entry reference
        // This will be expanded as spec tests require it
        throw new NotImplementedException("SelectChildEntry not yet implemented");
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
        // Need to get the selection entry and parent
        throw new NotImplementedException("SetSelectionCount requires entry reference mapping");
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
        // Find cost type from game system
        var roster = _oracle.GetRoster();
        var costTypes = JavaListToList<net.battlescribe.model.data.CostType>(
            _oracle.GetRoster().getCosts());
        // SetCostLimit needs a CostType object — simplified for now
        throw new NotImplementedException("SetCostLimit requires CostType lookup");
    }

    public RosterState GetRosterState()
    {
        var roster = _oracle.GetRoster();
        var forces = _oracle.GetForces();
        var errors = _oracle.GetValidationErrors();

        var forceStates = forces.Select(f =>
        {
            var selections = JavaListToList<net.battlescribe.model.roster.Selection>(f.getSelections());
            return new ForceState(
                f.getName() ?? "",
                f.getCatalogueId(),
                selections.Select(CaptureSelection).ToList());
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

    private static SelectionState CaptureSelection(net.battlescribe.model.roster.Selection sel)
    {
        var costs = JavaListToList<net.battlescribe.model.data.Cost>(sel.getCosts());
        var children = JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections());
        return new SelectionState(
            sel.getName() ?? "",
            sel.getEntryId(),
            sel.getType(),
            sel.getNumber(),
            false, // hidden status requires checking the entry, not the selection
            costs.Select(c => new CostState(c.getName() ?? "", c.getTypeId() ?? "", c.getValue())).ToList(),
            children.Select(CaptureSelection).ToList());
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
