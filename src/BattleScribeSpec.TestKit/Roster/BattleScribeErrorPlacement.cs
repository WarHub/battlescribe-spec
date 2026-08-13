namespace BattleScribeSpec.Roster;

/// <summary>
/// Moves BattleScribe's validation errors onto the node the spec corpus reports them on.
/// </summary>
/// <remarks>
/// <para>
/// BattleScribe's Java engine hangs an over-limit violation on the CATEGORY, FORCE or ROSTER node
/// that noticed it, and can hang a violation raised inside a link-reached selection on the PARENT
/// selection. NewRecruit — and the canonical spec form — attribute it to the selection that
/// violated the constraint. Min violations are the exception: both engines place those on the
/// category, so they are left alone.
/// </para>
/// <para>
/// <b>Structural, not textual.</b> This used to read the message ("too many"/"too much" for
/// over-limit, " forces from " for a force-count) — which mis-scoped the force-count case the engine
/// renders as " forces of " for a SelectionEntry, and coupled placement to the exact prose. It now
/// decides from the constraint's captured <see cref="ValidationErrorState.ConstraintType"/> and
/// <see cref="ValidationErrorState.ConstraintField"/>: a <c>max</c> violation is over-limit, and a
/// <c>forces</c>-field violation is a count whose subject is the roster/force, not a selection.
/// </para>
/// <para>
/// <b>What it must not touch.</b> <see cref="ValidationErrorState.RaisedOnType"/> and
/// <see cref="ValidationErrorState.RaisedOnId"/> record the node the engine actually raised the
/// error on, written at capture. Placement moves the ATTRIBUTION and leaves that record alone — a
/// <c>with</c> expression carries unlisted members through, so every moved error still names its
/// raising node. It used to null the raising node's id on every move instead, which is why a
/// failure could report an over-limit violation without naming any node at all (issue #421).
/// </para>
/// <para>
/// <b>Why this is shared rather than reimplemented.</b> Both BattleScribe engines read the same
/// Java model and must answer the same way; the in-process adapter is what every spec's expected
/// placement was written against. When the UI driver grew its own error reading, it produced the
/// right <c>from</c> on the wrong <c>on</c> — the two engines disagreeing by accident. One rule in
/// one place is what makes them agree by construction.
/// </para>
/// </remarks>
public static class BattleScribeErrorPlacement
{
    /// <summary>
    /// Rewrites <paramref name="errors"/> in place, moving over-limit and hidden violations off
    /// their container node and onto the selection responsible, and reducing a link-reached owner to
    /// the target entry a spec names it by (issue #400).
    /// </summary>
    /// <param name="errors">The collected errors, rewritten in place.</param>
    /// <param name="resolveLinkTarget">
    /// Maps an entry-link id to the selection entry it targets, for force-level errors raised
    /// through a link. Return null (or pass null) to use the entry id as-is.
    /// </param>
    public static void ApplyTo(
        IList<ValidationErrorState> errors,
        Func<string, string?>? resolveLinkTarget = null)
    {
        for (var i = 0; i < errors.Count; i++)
        {
            var e = errors[i];

            // THE owner reduction, for both BattleScribe lanes (#400): an element reached through an
            // entry link reports its entryId as the composite route (linkId::…::targetId), and specs
            // address the owner by the target entry. Both engines feed their captured errors through
            // this method with the RAW owner id — the in-process adapter from the live element, the
            // UI driver from the agent's payload — so the two lanes agree by construction, not by
            // keeping two implementations in step. Plain ids pass through unchanged.
            //
            // This reduction is for ENTRY ids only. RaisedOnId is a runtime node id, which has no
            // link-composite form, so it is never fed through here.
            if (e.OwnerEntryId is not null)
            {
                e = e with { OwnerEntryId = BattleScribeErrorIds.ReduceToTargetEntry(e.OwnerEntryId) };
                errors[i] = e;
            }

            if (e.EntryId is null)
            {
                continue;
            }

            // The selection a spec attributes the error to is the constraint's own entry, addressed
            // by target — not the link route taken to reach it (#400). Both lanes apply this one
            // reduction so they agree on ownerEntryId for link-reached selections.
            var ownerEntry = BattleScribeErrorIds.ReduceToTargetEntry(e.EntryId);

            var moved = e.OwnerType switch
            {
                // Hidden-entry errors move too: the category is where BattleScribe noticed the
                // hidden selection, not what was hidden.
                "category" when e.ConstraintId == "hidden" || IsOverLimit(e)
                    => e with { OwnerType = "selection", OwnerEntryId = ownerEntry },

                // A cost-limit violation genuinely belongs to the roster, so `costLimits` stays. A
                // force-COUNT constraint's subject is the roster, not a selection, and moving it
                // would invent an owner that does not exist.
                "roster" when e.EntryId != "costLimits" && IsOverLimit(e) && !IsForceCount(e)
                    => e with { OwnerType = "selection", OwnerEntryId = ownerEntry },

                "force" when IsOverLimit(e) && !IsForceCount(e)
                    => e with
                    {
                        OwnerType = "selection",
                        OwnerEntryId = resolveLinkTarget?.Invoke(e.EntryId) ?? ownerEntry,
                    },

                _ => e,
            };

            errors[i] = moved;
        }
    }

    // A max violation is the over-limit ("too many"/"too much") case; min stays on its container.
    private static bool IsOverLimit(ValidationErrorState e) => e.ConstraintType == "max";

    // A forces-field count's subject is the roster/force itself, not a selection.
    private static bool IsForceCount(ValidationErrorState e) => e.ConstraintField == "forces";
}
