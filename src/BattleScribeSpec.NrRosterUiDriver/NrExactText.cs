using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// The one rule every by-name lookup in the NR roster UI driver matches with: an element is called
/// <c>name</c> when its text is the WHOLE of <c>name</c>, surrounding whitespace aside — never when
/// its text merely contains it.
/// </summary>
/// <remarks>
/// <para>
/// Playwright's <c>HasText</c> and <c>HasTextString</c> are case-insensitive SUBSTRING matches. A row
/// filter for "Intercessor Squad" therefore also takes "Assault Intercessor Squad" and "Heavy
/// Intercessor Squad", and <c>.First</c> then clicks whichever of them NR happens to render first.
/// That is #567: on real-world/wh40k-10e-space-marines-army the catalogue panel lists the Assault
/// squad ahead of the plain one, so selecting "Intercessor Squad" added a 75-point "Assault Intercessor
/// Squad" and the roster came out 5 points short — reported two steps later as a cost mismatch, not as
/// the wrong click it was. "Captain" had been picking the right row out of seven matches by luck of
/// ordering alone.
/// </para>
/// <para>
/// The pattern is built here, once, so no lookup can drift back to a substring. It is evaluated by
/// the page, not by .NET: Playwright hands the browser this regex's source, which is why it is kept
/// to syntax both engines read the same way.
/// </para>
/// <para>
/// The NR Editor needs a different shape of the same rule, because it renders no element that holds
/// a name alone — see <c>NrEditorNames</c> in the GameData UI driver.
/// </para>
/// </remarks>
internal static class NrExactText
{
    /// <summary>
    /// A pattern that matches <paramref name="text"/> exactly, give or take whitespace around it.
    /// </summary>
    public static Regex Pattern(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Regex($@"^\s*{Regex.Escape(text)}\s*$");
    }

    /// <summary>
    /// The elements of <paramref name="rows"/> holding a <paramref name="nameSelector"/> element whose
    /// text is exactly <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// Matches on the row's NAME element rather than on the row: a row's text also carries its cost,
    /// its count and its buttons' labels, so even an anchored match on the whole row would depend on
    /// how NR lays those out.
    /// </remarks>
    public static ILocator WhereNameIs(this ILocator rows, string nameSelector, string name)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Filter(new() { Has = rows.Page.Locator(nameSelector, new() { HasTextRegex = Pattern(name) }) });
    }
}
