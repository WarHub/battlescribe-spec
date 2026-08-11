namespace BattleScribeSpec.Roster;

/// <summary>
/// Reads the ids BattleScribe hangs on a roster element alongside its validation errors into the
/// entry-to-candidate-constraints multimap both BattleScribe engines resolve an error's
/// <c>from</c> through.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format</b>, which nothing else in this repo writes down. <c>getValidationErrorIds()</c>
/// returns strings shaped <c>ownerId::entryId::constraintId</c>, listing what the ELEMENT knows
/// about rather than what any one error was raised by. The three halves are used as follows:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ownerId</c> — the element the ids were read off. Discarded: the map is already built per
/// element, so the segment repeats what the caller already has.
/// </description></item>
/// <item><description>
/// <c>entryId</c> — the map KEY, passed to a composite-id-aware entry lookup and reported as the
/// spec's <c>from</c> entry id. It is itself composite: per <c>docs/entry-id-construction.md</c> an
/// entryId reached through entry links reads <c>link1::link2::…::actualEntryId</c>, so it can span
/// several <c>::</c> segments of its own.
/// </description></item>
/// <item><description>
/// <c>constraintId</c> — the map VALUE, one entry's list of CANDIDATE constraint ids. The list
/// cannot say which of them fired; picking is the caller's job, and it walks the list in order, so
/// the order is part of the answer.
/// </description></item>
/// </list>
/// <para>
/// The consequence, and the reason this is not simply <c>parts[2]</c>: an id with more than three
/// segments has the middle ones belonging to the ENTRY id, not to the constraint. Only the LAST
/// segment is ever a constraint id. Constraint ids are deduped per entry with insertion order
/// preserved, because BattleScribe repeats an id once per error the element carries and a caller
/// walking candidates in order should see each one once.
/// </para>
/// <para>
/// <b>What is measured and what is inferred.</b> The three-segment case is measured: every id the
/// spec corpus has been observed to produce has exactly three segments, and for those this returns
/// byte-for-byte what the in-process adapter returned before this type existed — so centralizing it
/// is verdict-neutral. The four-or-more-segment case is INFERRED from the composite-entryId format
/// above; no observed sample exercises it. It is worth stating that the two previous copies of this
/// rule both got that case wrong and wrongly in different ways — one answered a LINK id where a
/// constraint id was wanted, the other the whole unsplit remainder — which is what a rule with
/// three copies and no test does.
/// </para>
/// <para>
/// <b>Why this is shared rather than reimplemented.</b> There were three copies: two verbatim in
/// the in-process adapter and one in the UI driver's Java agent, and they had already drifted on
/// both the split limit and the dedupe. The two BattleScribe engines read the same Java model and
/// must answer the same way, and a divergence here does not fail loudly — it renames a constraint,
/// which surfaces as one spec disagreeing about <c>from</c>. The Java agent cannot call this code
/// (it runs inside the BattleScribe JVM, not the .NET host), so <c>parseValidationErrorIds</c> in
/// <c>src/bs-ui-java-agent/src/bsspec/uiagent/EngineAccessor.java</c> is a hand-kept mirror of this
/// method rather than a caller of it; <c>tests/Features/BattleScribeErrorIdsTests.cs</c> pins the
/// cases the two are meant to agree on.
/// </para>
/// <para>
/// <b>What is dropped, and why that is accepted.</b> Nulls, and anything with fewer than three
/// segments. A shorter id cannot be read without guessing WHICH half is missing, and a guess here
/// produces a well-formed and wrong <c>from</c> — worse than none, since the caller has message-text
/// resolution to fall back on. Empty segments are kept rather than removed: an id whose middle is
/// blank still puts its constraint id last, and removing empties would change the three-segment
/// behaviour that every observed sample depends on.
/// </para>
/// </remarks>
public static class BattleScribeErrorIds
{
    /// <summary>
    /// Parses <paramref name="errorIds"/> into entry id → the constraint ids listed against it, in
    /// the order they were listed and without repeats.
    /// </summary>
    /// <param name="errorIds">
    /// The raw ids from one element's <c>getValidationErrorIds()</c>. Null entries are skipped, as
    /// is a null list.
    /// </param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(IEnumerable<string?>? errorIds)
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var errorId in errorIds ?? [])
        {
            if (errorId is null)
            {
                continue;
            }

            var parts = errorId.Split("::");
            if (parts.Length < 3)
            {
                continue;
            }

            // parts[0] is the owner id. Everything from parts[1] up to the last segment is the
            // entry id -- the links traversed to reach it are segments of the entry id, not of the
            // constraint id -- and the last segment alone is the constraint.
            var entryId = string.Join("::", parts, 1, parts.Length - 2);
            var constraintId = parts[^1];

            if (!map.TryGetValue(entryId, out var constraintIds))
            {
                constraintIds = [];
                map[entryId] = constraintIds;
            }

            if (!constraintIds.Contains(constraintId))
            {
                constraintIds.Add(constraintId);
            }
        }

        return map.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value);
    }
}
