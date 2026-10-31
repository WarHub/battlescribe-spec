using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Finds the NR Editor tree row that renders a given entry, by the entry it renders rather than the
/// text it shows — for an entry with no name, whose title NR composes instead of reading.
/// </summary>
/// <remarks>
/// <para>
/// A constraint has no name. Its tree title is prose NR builds from the constraint's fields and its
/// parent (<c>max 3 selections in force</c>), so a name pick (<see cref="NrEditorNames"/>) has
/// nothing to match, and rebuilding that prose here would copy NR's wording into the driver — the
/// least stable thing on the page. Two constraints that read the same would be indistinguishable
/// anyway.
/// </para>
/// <para>
/// What does identify the row is the entry object. Every <c>EditorCollapsibleBox</c> stamps its own
/// element with itself (<c>mounted(){ this.$el.vnode = this }</c>), and its <c>payload</c> prop is
/// the entry it renders. That element → component → entry path is NR's own: the store resolves a
/// shift-click range selection through <c>el.vnode</c>, and <c>get_selected()</c> resolves the
/// selected row's entry from the same component. So the row is the box whose
/// <c>vnode.payload.id</c> is the id, and a click on its title is the click a user makes.
/// </para>
/// <para>
/// The assumption is named in the failure: when no row carries a payload, NR has stopped stamping
/// its boxes, and the message says so rather than reporting a missing row.
/// </para>
/// </remarks>
internal static class NrEditorTreeRows
{
    /// <summary>Every tree box in the catalogue tree, in document order — the order <c>Nth</c> addresses.</summary>
    internal const string Boxes = "#editor-entries .collapsible-box";

    /// <summary>
    /// The title row of the tree box rendering the entry <paramref name="entryId"/>, once one has
    /// rendered. The caller expands the tree first; a row Vue has yet to render is waited for.
    /// </summary>
    /// <remarks>
    /// The first in document order when several render it — a shared entry's constraint also shows
    /// under each link to it — and every one of them selects the same entry.
    /// </remarks>
    internal static async Task<ILocator> RenderingAsync(IPage page, string entryId, int timeoutMs = 10_000)
    {
        ArgumentNullException.ThrowIfNull(page);
        int index;
        try
        {
            // An array, not a bare index: the wait resolves on a truthy value, and row 0 is not one.
            var match = await page.WaitForFunctionAsync(
                """
                ([boxes, entryId]) => {
                    const i = [...document.querySelectorAll(boxes)]
                        .findIndex(b => b.vnode?.payload?.id === entryId);
                    return i >= 0 ? [i] : null;
                }
                """,
                new object[] { Boxes, entryId },
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
            index = (await match.JsonValueAsync<int[]>())[0];
        }
        catch (TimeoutException)
        {
            var counts = await page.EvaluateAsync<int[]>(
                """
                (boxes) => {
                    const all = [...document.querySelectorAll(boxes)];
                    return [all.length, all.filter(b => b.vnode?.payload).length];
                }
                """,
                Boxes);
            var (rows, stamped) = (counts[0], counts[1]);
            throw new InvalidOperationException(rows > 0 && stamped == 0
                ? $"NR Editor UI: no tree row renders entry '{entryId}', and none of the {rows} rows under "
                    + $"'{Boxes}' carries the entry it renders. NrEditorTreeRows finds a row for an entry "
                    + "with no name (a constraint) through the component NR stamps on each box "
                    + "(`el.vnode`, whose `payload` is the entry); NR Editor no longer stamps it. Find "
                    + "where the tree now exposes a row's entry and update NrEditorTreeRows."
                : $"NR Editor UI: no tree row renders entry '{entryId}' within {timeoutMs}ms ({stamped} of "
                    + $"{rows} rows under '{Boxes}' checked). The entry is in the store, so its row is "
                    + "not rendered: an ancestor is still collapsed, or the tree filter hides it.");
        }

        return page.Locator(Boxes).Nth(index).Locator("> .title");
    }
}
