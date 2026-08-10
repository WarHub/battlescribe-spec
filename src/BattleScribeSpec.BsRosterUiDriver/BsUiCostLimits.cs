using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// What BattleScribe's cost-limit spinners can be asked to carry, and which limit the New Roster
/// dialog's single spinner is for.
/// </summary>
/// <remarks>
/// <para>
/// Every route this driver has into a roster's cost limits ends at a <c>Spinner&lt;Integer&gt;</c>:
/// the New Roster dialog's, and the Edit Roster dialog's that <c>rosterSetCostLimitAction</c>
/// drives. A limit that is not a whole number cannot be entered through either, and the only two
/// honest answers are the value and none. <b>Truncating is the third answer and it is a wrong
/// one</b>: 0.25 entered as 0 puts every selection over a limit the game system never declared,
/// so "cannot express this" arrives as a real-looking violation several steps from its cause.
/// </para>
/// <para>
/// The rule lives here rather than at its two call sites because it was applied at one of them and
/// not the other, which is the defect this file exists to make unrepeatable. A driver that later
/// finds a route able to carry a fractional or per-type limit — the Edit Roster dialog has not been
/// examined for per-cost-type fields — changes this file and its tests, and the call sites do not
/// move.
/// </para>
/// </remarks>
public static class BsUiCostLimits
{
    /// <summary>
    /// The integer a cost-limit spinner can carry for <paramref name="limit"/>, or null when no
    /// spinner can express it and the control must be left alone.
    /// </summary>
    /// <remarks>
    /// A negative limit is how the format spells "no limit", and an untouched spinner already means
    /// that — so it is refused rather than entered as a negative number the spinner would clamp.
    /// </remarks>
    public static int? SpinnerValueFor(decimal limit)
        => limit >= 0 && limit <= int.MaxValue && decimal.Truncate(limit) == limit
            ? (int)limit
            : null;

    /// <summary>
    /// The value to put in the New Roster dialog's cost-limit spinner, or null to leave it alone.
    /// </summary>
    /// <param name="requested">Limits the spec asked for with <c>setCostLimit</c>, by cost type id.</param>
    /// <param name="costTypes">The game system's cost types, or null on a path that has none.</param>
    /// <remarks>
    /// <para>
    /// A spec's own request wins. Failing that the game system's <c>defaultCostLimit</c> is used,
    /// and it has to be, because <b>BattleScribe applies that default only to a roster created
    /// through the engine, not to one created through this dialog</b>. Leaving the spinner untouched
    /// produced a roster with no cost limit at all (<c>costLimits: []</c> read straight back off the
    /// model), so a spec whose whole subject is a default limit saw no violation to report and
    /// failed asking where its error went.
    /// </para>
    /// <para>
    /// One cost type only, in both cases: the dialog has a single spinner, so a per-type limit
    /// cannot be expressed here and guessing which type it meant would be worse than leaving it
    /// unset. A multi-type system falls through to whatever BattleScribe does on its own.
    /// </para>
    /// </remarks>
    public static int? ForNewRoster(
        IReadOnlyDictionary<string, decimal> requested,
        IEnumerable<ProtocolCostType>? costTypes)
    {
        if (requested.Count == 1)
        {
            return SpinnerValueFor(requested.Values.First());
        }

        if (requested.Count > 1)
        {
            return null;
        }

        var defaults = costTypes?.Where(c => c.DefaultCostLimit is >= 0).ToList() ?? [];
        return defaults.Count == 1 ? SpinnerValueFor(defaults[0].DefaultCostLimit!.Value) : null;
    }
}
