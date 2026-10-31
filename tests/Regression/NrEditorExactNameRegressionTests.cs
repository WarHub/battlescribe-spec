using BattleScribeSpec.NrGameDataUiDriver;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Regression;

/// <summary>
/// The NR Editor counterpart of <see cref="NrUiExactNameRegressionTests"/> (#567): a tree node or a
/// suggestion is picked by its WHOLE name, read without the annotations the Editor prints beside it.
/// </summary>
/// <remarks>
/// <para>
/// The Editor renders no element holding a name alone — "Bolter" is a bare text node followed by
/// <c>[Test Catalogue]</c> in its own span — so its lookups were substring filters, and a substring
/// filter picks by order. The markup below is the Editor's own, copied from the frozen snapshot; each
/// case puts the trap first, where the substring filters' <c>.First</c> took it, and each was checked
/// red against them.
/// </para>
/// <para>
/// Its own headless Chromium, not <c>NrBrowserHost</c>'s: the GameData UI driver launches its own
/// browsers too, so this cannot relaunch one out from under the lane.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrGameDataUi")]
public sealed class NrEditorExactNameRegressionTests
{
    /// <summary>An autocomplete suggestion: name as a bare text node, then the Editor's annotation spans.</summary>
    private static string Suggestion(string id, string name, string annotations = "") => $"""
        <div id="{id}"><div style="white-space: nowrap;"><img class="mr-1 align-middle" src="x.png"> {name} <span class="gray"></span>{annotations}</div></div>
        """;

    /// <summary>A tree title: name in an unclassed span, then its type in <c>.gray</c> and its costs.</summary>
    private static string Title(string id, string name, string type, string? cost = null) => $"""
        <div id="{id}" class="title normalTitle"><span class="title-before"></span><div class="arrow-wrap"><img class="hide arrow icon" src="x.png"></div>
          <span class=""><span class="typeIcon-wrapper"><img class="typeIcon" src="x.png"></span><span class="">{name}</span><span class="gray">&nbsp; {type}</span>{(cost is null ? "" : $"<span class=\"ml-10px\"><span class=\"costList\"><span class=\"cost\">{cost}</span></span></span>")}</span>
        </div>
        """;

    [Fact]
    public async Task ASuggestionWhoseNameContainsTheWantedOne_IsNotPicked()
    {
        await using var page = await EditorPage.OpenAsync(
            Suggestion("template", "Bolter Template", "<span class=\"catalogueName\"> [Test Catalogue]</span>"),
            Suggestion("wanted", "Bolter", "<span class=\"catalogueName\"> [Test Catalogue]</span>"),
            Suggestion("heavy", "Heavy Bolter", "<span class=\"shared\"> (shared) </span>"));

        var pick = await NrEditorNames.PickAsync(page.Candidates("body > div"), "Bolter", "suggestion");

        Assert.Equal("wanted", await pick.GetAttributeAsync("id"));
    }

    [Fact]
    public async Task ATreeTitleWhoseNameContainsTheWantedOne_IsNotPicked()
    {
        await using var page = await EditorPage.OpenAsync(
            Title("sub", "Sub-detachment", "upgrade"),
            Title("wanted", "Detachment", "upgrade", "0 pts"));

        var pick = await NrEditorNames.PickAsync(page.Candidates(".title"), "Detachment", "tree node");

        Assert.Equal("wanted", await pick.GetAttributeAsync("id"));
    }

    /// <summary>A case-sensitive match beats a case-insensitive one listed ahead of it.</summary>
    [Fact]
    public async Task AnExactCaseMatch_BeatsAnEarlierCaseInsensitiveOne()
    {
        await using var page = await EditorPage.OpenAsync(
            Suggestion("upper", "Category"),
            Suggestion("wanted", "category"));

        var pick = await NrEditorNames.PickAsync(page.Candidates("body > div"), "category", "field option");

        Assert.Equal("wanted", await pick.GetAttributeAsync("id"));
    }

    /// <summary>With no case-sensitive match a case-insensitive one is taken — a scope label "force" is "Force".</summary>
    [Fact]
    public async Task WithNoExactCaseMatch_ACaseInsensitiveOneIsTaken()
    {
        await using var page = await EditorPage.OpenAsync(
            Suggestion("entry", "Force Entry"),
            Suggestion("wanted", "Force"));

        var pick = await NrEditorNames.PickAsync(page.Candidates("body > div"), "force", "suggestion");

        Assert.Equal("wanted", await pick.GetAttributeAsync("id"));
    }

    /// <summary>No whole-name match fails, naming what was offered and where the fix goes.</summary>
    [Fact]
    public async Task ANameOnlyContainedInCandidates_FailsNamingThem()
    {
        await using var page = await EditorPage.OpenAsync(
            Suggestion("template", "Bolter Template", "<span class=\"catalogueName\"> [Test Catalogue]</span>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NrEditorNames.PickAsync(page.Candidates("body > div"), "Bolter", "'Target:' suggestion"));

        Assert.Contains("'Bolter Template'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AnnotationSelector", ex.Message, StringComparison.Ordinal);
    }

    private sealed class EditorPage(IPlaywright playwright, IBrowser browser, IPage page) : IAsyncDisposable
    {
        public ILocator Candidates(string selector) => page.Locator(selector);

        public static async Task<EditorPage> OpenAsync(params string[] candidates)
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync($"<!doctype html><html><body>{string.Join("\n", candidates)}</body></html>");
            return new EditorPage(playwright, browser, page);
        }

        public async ValueTask DisposeAsync()
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }
}

/// <summary>
/// <see cref="NrEditorNames.IndexOf"/> on its own: the comparison the Editor picks are made with,
/// once the page has read each candidate's name.
/// </summary>
public sealed class NrEditorNamesIndexOfTests
{
    [Fact]
    public void AContainingName_IsNotAMatch()
        => Assert.Equal(-1, NrEditorNames.IndexOf(["Assault Intercessor Squad", "Heavy Intercessor Squad"], "Intercessor Squad"));

    [Fact]
    public void TheWholeName_IsAMatch_WhereverItIsListed()
        => Assert.Equal(2, NrEditorNames.IndexOf(["Assault Intercessor Squad", "Heavy Intercessor Squad", "Intercessor Squad"], "Intercessor Squad"));

    [Fact]
    public void ExactCase_BeatsAnEarlierCaseInsensitiveMatch()
        => Assert.Equal(1, NrEditorNames.IndexOf(["Category", "category"], "category"));

    [Fact]
    public void CaseInsensitive_IsTheFallback()
        => Assert.Equal(0, NrEditorNames.IndexOf(["Force", "Force Entry"], "force"));

    [Fact]
    public void Duplicates_ResolveToTheFirst()
        => Assert.Equal(0, NrEditorNames.IndexOf(["Target Upgrade", "Target Upgrade"], "Target Upgrade"));

    [Fact]
    public void TheWantedName_IsWhitespaceNormalisedLikeTheCandidates()
        => Assert.Equal(0, NrEditorNames.IndexOf(["Tactical Squad"], " Tactical  Squad "));
}
