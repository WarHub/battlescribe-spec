using System.Collections.Immutable;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec;

/// <summary>
/// Converts Java BattleScribe model state to wham-compatible types for comparison.
/// Used in oracle tests to compare engine output against wham data model.
/// </summary>
public static class ModelConverter
{
    /// <summary>
    /// Represents a simplified selection for cross-engine comparison.
    /// </summary>
    public record SelectionSnapshot(
        string? Name,
        string? Type,
        int Number,
        ImmutableArray<CostSnapshot> Costs,
        ImmutableArray<SelectionSnapshot> Children);

    /// <summary>
    /// Represents a simplified cost entry for comparison.
    /// </summary>
    public record CostSnapshot(string? Name, string? TypeId, double Value);

    /// <summary>
    /// Represents a simplified force for comparison.
    /// </summary>
    public record ForceSnapshot(
        string? Name,
        string? CatalogueId,
        ImmutableArray<SelectionSnapshot> Selections);

    /// <summary>
    /// Represents a simplified roster state for comparison.
    /// </summary>
    public record RosterSnapshot(
        string? Name,
        string? GameSystemId,
        ImmutableArray<ForceSnapshot> Forces,
        ImmutableArray<CostSnapshot> Costs,
        ImmutableArray<string> ValidationErrors);

    /// <summary>
    /// Capture the current state of the oracle's roster as a snapshot for comparison.
    /// </summary>
    public static RosterSnapshot CaptureOracleSnapshot(BattleScribeOracle oracle)
    {
        var roster = oracle.GetRoster();
        var forces = oracle.GetForces();
        var errors = oracle.GetValidationErrors();

        var forceSnapshots = forces.Select(f =>
        {
            var selections = JavaListToList<net.battlescribe.model.roster.Selection>(f.getSelections());
            return new ForceSnapshot(
                f.getName(),
                f.getCatalogueId(),
                selections.Select(CaptureSelection).ToImmutableArray());
        }).ToImmutableArray();

        var costs = JavaListToList<net.battlescribe.model.data.Cost>(roster.getCosts());
        var costSnapshots = costs.Select(c =>
            new CostSnapshot(c.getName(), c.getTypeId(), c.getValue())).ToImmutableArray();

        return new RosterSnapshot(
            roster.getName(),
            roster.getGameSystemId(),
            forceSnapshots,
            costSnapshots,
            [.. errors]);
    }

    private static SelectionSnapshot CaptureSelection(net.battlescribe.model.roster.Selection sel)
    {
        var costs = JavaListToList<net.battlescribe.model.data.Cost>(sel.getCosts());
        var children = JavaListToList<net.battlescribe.model.roster.Selection>(sel.getSelections());
        return new SelectionSnapshot(
            sel.getName(),
            sel.getType(),
            sel.getNumber(),
            costs.Select(c => new CostSnapshot(c.getName(), c.getTypeId(), c.getValue())).ToImmutableArray(),
            children.Select(CaptureSelection).ToImmutableArray());
    }

    /// <summary>
    /// Create a roster snapshot from wham RosterNode for comparison.
    /// </summary>
    public static RosterSnapshot CaptureWhamSnapshot(RosterNode roster, IReadOnlyList<string>? validationErrors = null)
    {
        var forces = roster.Forces.Select(f =>
            new ForceSnapshot(
                f.Name,
                f.CatalogueId,
                f.Selections.Select(CaptureWhamSelection).ToImmutableArray())).ToImmutableArray();

        var costs = roster.Costs.Select(c =>
            new CostSnapshot(c.Name, c.TypeId, (double)c.Value)).ToImmutableArray();

        return new RosterSnapshot(
            roster.Name,
            roster.GameSystemId,
            forces,
            costs,
            validationErrors?.ToImmutableArray() ?? []);
    }

    private static SelectionSnapshot CaptureWhamSelection(SelectionNode sel)
    {
        var costs = sel.Costs.Select(c =>
            new CostSnapshot(c.Name, c.TypeId, (double)c.Value)).ToImmutableArray();
        var children = sel.Selections.Select(CaptureWhamSelection).ToImmutableArray();
        return new SelectionSnapshot(
            sel.Name,
            sel.Type.ToString(),
            sel.Number,
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
