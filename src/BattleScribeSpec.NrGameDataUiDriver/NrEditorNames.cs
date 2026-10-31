using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// How the GameData UI driver picks a tree node or a suggestion by NAME: the candidate whose name,
/// read without the annotations NR Editor prints beside it, is exactly the name asked for — never one
/// whose text merely contains it.
/// </summary>
/// <remarks>
/// <para>
/// The roster driver's rule (<c>NrExactText</c>, #567) cannot be used as is, because the Editor
/// renders no element that holds a name alone. A suggestion is a bare text node followed by spans —
/// <c>Bolter</c> then <c>[Test Catalogue]</c> in <c>.catalogueName</c>, <c>(shared)</c> in
/// <c>.shared</c>, <c>(1 ref)</c> in <c>.grey</c> — and a tree title puts the name in an unclassed
/// span followed by the entry type in <c>.gray</c> and its costs in <c>.costList</c>, so the title
/// of "Tactical Squad" reads <c>Tactical Squad  unit1 pts</c>. An anchored match on the whole text
/// therefore misses every one of them, which is why these lookups were left as substring filters —
/// and a substring filter picks by ORDER. Measured on the frozen nr-editor-ui lane: the tree lookup
/// for "Detachment" also matched "Sub-detachment", and the field picker's case-insensitive "category"
/// also matched "Category"; both landed on the right candidate only because NR lists it first.
/// </para>
/// <para>
/// So the annotations are removed by what they are — the classed spans listed in
/// <see cref="AnnotationSelector"/> — and what remains must equal the name. A new annotation NR adds
/// makes the match fail loudly with every candidate's name in the message, and the fix is to add its
/// class here; loosening back to a substring is not.
/// </para>
/// <para>
/// A case-sensitive match wins over a case-insensitive one, so "category" can never take "Category".
/// Two candidates with the SAME name are resolved in document order, as before: an id is turned into
/// a name before it reaches here, and the tree does show one name twice — a shared entry,
/// "Target Upgrade (1 ref)", ahead of a link to it — where the first is the node the id names.
/// </para>
/// </remarks>
internal static class NrEditorNames
{
    /// <summary>
    /// The spans NR Editor prints beside a name: the entry type and reference count (<c>.gray</c>,
    /// <c>.grey</c>), the owning catalogue (<c>.catalogueName</c>), the shared marker
    /// (<c>.shared</c>) and the costs (<c>.costList</c>).
    /// </summary>
    internal const string AnnotationSelector = ".gray, .grey, .catalogueName, .shared, .costList";

    /// <summary>
    /// Each candidate's name: its text without the <see cref="AnnotationSelector"/> spans,
    /// whitespace collapsed. Read in one evaluation, in document order — the order
    /// <c>Nth</c> addresses. (An array, not a list interface: Playwright deserializes into a type it
    /// can construct.)
    /// </summary>
    internal static Task<string[]> ReadNamesAsync(ILocator candidates)
        => candidates.EvaluateAllAsync<string[]>("""
            (els, annotations) => els.map(e => {
                const c = e.cloneNode(true);
                c.querySelectorAll(annotations).forEach(a => a.remove());
                return (c.textContent || '').replace(/ /g, ' ').replace(/\s+/g, ' ').trim();
            })
            """, AnnotationSelector);

    /// <summary>
    /// The index of the name that is exactly <paramref name="name"/> — case-sensitive first, then
    /// ignoring case — or -1. The first in document order when several are.
    /// </summary>
    internal static int IndexOf(IReadOnlyList<string> names, string name)
    {
        ArgumentNullException.ThrowIfNull(names);
        var wanted = Normalize(name);
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], wanted, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The candidate named exactly <paramref name="name"/>; throws naming every candidate when none is.
    /// </summary>
    /// <param name="candidates">The rows to choose from, already rendered (the caller waits for them).</param>
    /// <param name="name">The name to pick.</param>
    /// <param name="what">What is being picked, for the message ("'Target:' suggestion").</param>
    internal static async Task<ILocator> PickAsync(ILocator candidates, string name, string what)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var names = await ReadNamesAsync(candidates);
        var index = IndexOf(names, name);
        return index >= 0
            ? candidates.Nth(index)
            : throw new InvalidOperationException(
                $"NR Editor UI: no {what} is named exactly '{name}'. Offered (annotations removed): "
                + $"[{string.Join(", ", names.Select(n => $"'{n}'"))}]. A name is matched whole, without "
                + $"the spans NR Editor prints beside it ({AnnotationSelector}); if one of the names above "
                + "still carries an annotation, add that span's class to NrEditorNames.AnnotationSelector. "
                + "Do not loosen this to a substring match: that picks whichever candidate NR lists first.");
    }

    private static string Normalize(string text)
        => string.Join(' ', text.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
