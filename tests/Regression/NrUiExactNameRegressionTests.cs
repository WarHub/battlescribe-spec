using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrRosterUiDriver;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Regression;

/// <summary>
/// #567: the NR UI roster driver picks a catalogue row by NAME only when the name is the row's whole
/// name — "Intercessor Squad" is not "Assault Intercessor Squad".
/// </summary>
/// <remarks>
/// <para>
/// On real-world/wh40k-10e-space-marines-army, selecting "Intercessor Squad" added "Assault Intercessor
/// Squad": the spec names the shared entry, the panel's row is the entryLink to it, so the id tag
/// missed and <c>SelectEntryByNameAsync</c> fell back to a <c>HasText</c> filter — a substring match —
/// whose <c>.First</c> was the Assault squad, listed ahead of it. It surfaced as a 5-point cost
/// mismatch two steps later.
/// </para>
/// <para>
/// Driven against a page holding NR's own catalogue-panel markup (copied from the HAR replay), not
/// against the HAR: what is under test is the lookup rule, and a synthetic page states the trap
/// exactly — a row whose name CONTAINS the wanted one, rendered first — where the real panel holds it
/// only for as long as this dataset and this NR ordering last. It also runs the rule the way
/// production does, through Playwright into the browser's own regex engine, which a .NET-only test of
/// <see cref="NrExactText.Pattern"/> could not show.
/// </para>
/// <para>
/// Each case was checked red against the substring filter this replaced.
/// </para>
/// <para>
/// In the <c>FrozenNrUiRoster</c> collection, for the reason <see cref="NrUiSetupFailureMessageRegressionTests"/>
/// gives: <see cref="NrBrowserHost"/> keeps one Chromium per process and relaunches it on different
/// launch options, so this asks for exactly what the lane's fixture asks for and runs serialised with it.
/// </para>
/// </remarks>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class NrUiExactNameRegressionTests
{
    private const string ForceUid = "5ttp79g";

    /// <summary>Mirrors <c>FrozenNrUiRosterFixture</c> — see the class remarks.</summary>
    private static (bool Headless, float? SlowMo) LaunchOptions => (
        Environment.GetEnvironmentVariable("NR_HEADLESS") != "false",
        float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null);

    /// <summary>
    /// One catalogue-panel row as NR renders it: cost before name, both inside the top row — so the
    /// row's text carries the cost too, which is why the match is made on <c>.name</c>.
    /// </summary>
    private static string Row(string name, string cost = "80 pts", string? taggedId = null) => $"""
        <div class="unselectable hover-darken clickable unit-wrap newui"{(taggedId is null ? "" : $" data-spec-entry-id=\"{taggedId}\"")}>
          <div class="units"><div class="unit-top-row">
            <span class="costs desktop"><span class="costList"><span class="cost">{cost}</span></span></span>
            <span class="name">{name}</span>
            <span class="addButton">+</span>
          </div></div>
        </div>
        """;

    /// <summary>
    /// A force's <c>.bookForce</c>: the child-force picker first (same markup, marked by the tagger),
    /// then the entry list — the layout <c>FindEntryRowAsync</c> scopes itself to.
    /// </summary>
    private static string Panel(params string[] rows) => $"""
        <div class="bookForce" id="{ForceUid}">
          <div class="childForces"><div class="unitList">
            <div class="unit-wrap" data-nrui-force-row="1"><span class="name">Intercessor Squad</span><span class="addButton">+</span></div>
          </div></div>
          <div class="unitList">{string.Join("\n", rows)}</div>
        </div>
        """;

    [Fact]
    public async Task ANameContainedInAnEarlierRow_PicksTheRowWithThatWholeName()
    {
        await using var page = await PanelPage.OpenAsync(Panel(
            Row("Assault Intercessor Squad", "75 pts"),
            Row("Heavy Intercessor Squad", "100 pts"),
            Row("Intercessor Squad", "80 pts")));

        var row = await NrUiActions.FindEntryRowAsync(page.Page, ForceUid, "8da0-4570-c3c-819f", "Intercessor Squad");

        Assert.NotNull(row);
        Assert.Equal("Intercessor Squad", (await row.Locator(".name").TextContentAsync())?.Trim());
        Assert.Equal("80 pts", (await row.Locator(".cost").TextContentAsync())?.Trim());
    }

    /// <summary>
    /// Names carrying regex syntax reach the browser as a pattern, so they must arrive escaped —
    /// brackets, slashes and dots included — and still match only themselves.
    /// </summary>
    [Theory]
    [InlineData("Captain")]
    [InlineData("Librarian with Jump Pack [Legends]")]
    [InlineData("Show/Hide Options")]
    [InlineData("Lt. (Phobos)")]
    public async Task ANameWithPatternSyntax_MatchesOnlyItself(string name)
    {
        await using var page = await PanelPage.OpenAsync(Panel(
            Row("Captain in Gravis Armour"),
            Row("Captain on Bike [Legends]"),
            Row("Librarian with Jump Pack"),
            Row("Show/Hide Options (Legacy)"),
            Row("Lt. (Phobos) Squad"),
            Row(name)));

        var row = await NrUiActions.FindEntryRowAsync(page.Page, ForceUid, "unused-id", name);

        Assert.NotNull(row);
        Assert.Equal(name, (await row.Locator(".name").TextContentAsync())?.Trim());
    }

    /// <summary>
    /// A name no row carries whole finds nothing, rather than the first row that contains it — the
    /// caller then reports what the panel offered.
    /// </summary>
    [Fact]
    public async Task ANameOnlyContainedInRows_FindsNoRow()
    {
        await using var page = await PanelPage.OpenAsync(Panel(
            Row("Assault Intercessor Squad"),
            Row("Heavy Intercessor Squad")));

        Assert.Null(await NrUiActions.FindEntryRowAsync(page.Page, ForceUid, "8da0-4570-c3c-819f", "Intercessor Squad"));
    }

    /// <summary>The id the tagger stamped wins over any name, the requested one included.</summary>
    [Fact]
    public async Task ATaggedRow_IsPickedByIdWhateverItIsCalled()
    {
        await using var page = await PanelPage.OpenAsync(Panel(
            Row("Intercessor Squad"),
            Row("Renamed By A Modifier", taggedId: "se-wanted")));

        var row = await NrUiActions.FindEntryRowAsync(page.Page, ForceUid, "se-wanted", "Intercessor Squad");

        Assert.NotNull(row);
        Assert.Equal("Renamed By A Modifier", (await row.Locator(".name").TextContentAsync())?.Trim());
    }

    /// <summary>
    /// Two rows with the same whole name and no id to separate them are refused, as a harness fault
    /// naming the fix — not resolved by order, which is the guess #567 was.
    /// </summary>
    [Fact]
    public async Task TwoRowsWithTheSameWholeName_AreRefusedNotGuessed()
    {
        await using var page = await PanelPage.OpenAsync(Panel(
            Row("Squad", "10 pts"),
            Row("Squad", "20 pts")));

        var ex = await Assert.ThrowsAsync<HarnessFaultException>(
            () => NrUiActions.FindEntryRowAsync(page.Page, ForceUid, "link-b", "Squad"));

        Assert.Contains("2 catalogue rows", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TagUnitListEntriesAsync", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A page holding a panel's markup, in its own context off the lane's shared Chromium.</summary>
    private sealed class PanelPage(IBrowserContext context, IPage page) : IAsyncDisposable
    {
        public IPage Page { get; } = page;

        public static async Task<PanelPage> OpenAsync(string html)
        {
            var (headless, slowMo) = LaunchOptions;
            var browser = await NrBrowserHost.GetAsync(headless, slowMo);
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.SetContentAsync($"<!doctype html><html><body>{html}</body></html>");
            return new PanelPage(context, page);
        }

        public async ValueTask DisposeAsync() => await context.CloseAsync();
    }
}
