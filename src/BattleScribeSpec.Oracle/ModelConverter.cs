using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec;

/// <summary>
/// Converts Java BattleScribe model state to wham-compatible types for comparison.
/// Used in oracle tests to compare engine output against wham data model.
/// </summary>
public static class ModelConverter
{
    /// <summary>
    /// Capture the current state of the oracle's roster as a snapshot for comparison.
    /// </summary>
    public static RosterState CaptureOracleSnapshot(BattleScribeOracle oracle)
    {
        var roster = oracle.GetRoster();
        var forces = oracle.GetForces();
        var errors = oracle.GetValidationErrors();

        var forceSnapshots = forces.Select(f =>
        {
            var selections = JavaListToList<net.battlescribe.model.roster.Selection>(f.getSelections());
            return new ForceState(
                f.getName(),
                f.getCatalogueId(),
                selections.Select(CaptureSelection).ToList());
        }).ToList();

        var costs = JavaListToList<net.battlescribe.model.data.Cost>(roster.getCosts());
        var costStates = costs.Select(c =>
            new CostState(c.getName(), c.getTypeId(), c.getValue())).ToList();

        return new RosterState(
            roster.getName(),
            roster.getGameSystemId(),
            forceSnapshots,
            costStates,
            errors);
    }

    private static SelectionState CaptureSelection(net.battlescribe.model.roster.Selection sel)
    {
        var costs = JavaListToList<net.battlescribe.model.data.Cost>(sel.getCosts());
        var children = JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections());
        return new SelectionState(
            sel.getName(),
            EntryId: null,
            sel.getType(),
            sel.getNumber(),
            Hidden: false,
            costs.Select(c => new CostState(c.getName(), c.getTypeId(), c.getValue())).ToList(),
            children.Select(CaptureSelection).ToList());
    }

    /// <summary>
    /// Create a roster state from wham RosterNode for comparison.
    /// </summary>
    public static RosterState CaptureWhamSnapshot(RosterNode roster, IReadOnlyList<string>? validationErrors = null)
    {
        var forces = roster.Forces.Select(f =>
            new ForceState(
                f.Name ?? "",
                f.CatalogueId,
                f.Selections.Select(CaptureWhamSelection).ToList())).ToList();

        var costs = roster.Costs.Select(c =>
            new CostState(c.Name ?? "", c.TypeId ?? "", (double)c.Value)).ToList();

        var errors = validationErrors?.Select(e => new ValidationErrorState(e)).ToList()
            ?? new List<ValidationErrorState>();

        return new RosterState(
            roster.Name ?? "",
            roster.GameSystemId ?? "",
            forces,
            costs,
            errors);
    }

    private static SelectionState CaptureWhamSelection(SelectionNode sel)
    {
        var costs = sel.Costs.Select(c =>
            new CostState(c.Name ?? "", c.TypeId ?? "", (double)c.Value)).ToList();
        var children = sel.Selections.Select(CaptureWhamSelection).ToList();
        return new SelectionState(
            sel.Name ?? "",
            EntryId: null,
            sel.Type.ToString(),
            sel.Number,
            Hidden: false,
            costs,
            children);
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
