namespace BattleScribeSpec.Roster;

/// <summary>
/// Moves BattleScribe's validation errors onto the node the spec corpus reports them on.
/// </summary>
/// <remarks>
/// <para>
/// BattleScribe's Java engine hangs an over-limit violation on the CATEGORY, FORCE or ROSTER node
/// that noticed it. NewRecruit — and the canonical spec form — attribute it to the selection that
/// violated the constraint. Min violations are the exception: both engines place those on the
/// category, so they are left alone.
/// </para>
/// <para>
/// <b>Why this is shared rather than reimplemented.</b> Both BattleScribe engines read the same
/// Java model and must answer the same way; the in-process adapter is what every spec's expected
/// placement was written against. When the UI driver grew its own error reading, it produced the
/// right <c>from</c> on the wrong <c>on</c> — the two engines disagreeing by accident. One rule in
/// one place is what makes them agree by construction.
/// </para>
/// <para>
/// <b>This reads English error text</b>, because the placement BattleScribe intends is not exposed
/// any other way — the model attaches no constraint to a validation error. That is accepted here on
/// the same grounds the in-process adapter accepted it: the BS engine is EOL at v2.3.21, so its
/// message strings are fixed.
/// </para>
/// </remarks>
public static class BattleScribeErrorPlacement
{
    /// <summary>
    /// Rewrites <paramref name="errors"/> in place, moving over-limit and hidden violations off
    /// their container node and onto the selection responsible.
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
            if (e.EntryId is null)
            {
                continue;
            }

            var moved = e.OwnerType switch
            {
                // Hidden-entry errors move too: the category is where BattleScribe noticed the
                // hidden selection, not what was hidden.
                "category" when e.ConstraintId == "hidden" || IsOverLimit(e.Message)
                    => e with { OwnerType = "selection", OwnerId = null, OwnerEntryId = e.EntryId },

                // A cost-limit violation genuinely belongs to the roster, so `costLimits` stays.
                // " forces from " is a force-COUNT constraint: its subject is the roster, not a
                // selection, and moving it would invent an owner that does not exist.
                "roster" when e.EntryId != "costLimits" && IsOverLimit(e.Message) && !IsForceCount(e.Message)
                    => e with { OwnerType = "selection", OwnerId = null, OwnerEntryId = e.EntryId },

                "force" when IsOverLimit(e.Message) && !IsForceCount(e.Message)
                    => e with
                    {
                        OwnerType = "selection",
                        OwnerId = null,
                        OwnerEntryId = resolveLinkTarget?.Invoke(e.EntryId) ?? e.EntryId,
                    },

                _ => e,
            };

            errors[i] = moved;
        }
    }

    private static bool IsOverLimit(string message)
        => message.Contains("too many") || message.Contains("too much");

    private static bool IsForceCount(string message)
        => message.Contains(" forces from ");
}
