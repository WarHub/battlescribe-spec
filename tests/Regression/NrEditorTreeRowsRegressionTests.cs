using BattleScribeSpec.NrGameDataUiDriver;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Regression;

/// <summary>
/// An NR Editor tree row for an entry with no name — a constraint — is found by the entry it renders
/// (#579). The lookup used to search the store for a NAME and match it in the tree, so a constraint the
/// setup declared was "not found in any catalogue collection", and with the collection added it would
/// have matched nothing: a constraint's title is prose NR composes, not a name it stores.
/// </summary>
/// <remarks>
/// <para>
/// The markup is the Editor's own shape — an <c>EditorCollapsibleBox</c> (<c>collapsible-box</c> plus the
/// entry's <c>parentKey</c> and depth) around the title row <see cref="NrEditorExactNameRegressionTests"/>
/// copies from the frozen snapshot — and the stamp the inline script sets is the one the box's
/// <c>mounted()</c> sets: <c>this.$el.vnode = this</c>, whose <c>payload</c> is the entry.
/// </para>
/// <para>
/// Its own headless Chromium, as in <see cref="NrEditorExactNameRegressionTests"/>.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrGameDataUi")]
public sealed class NrEditorTreeRowsRegressionTests
{
    /// <summary>A constraint's tree row: no name, only the prose NR builds from its fields.</summary>
    private static string ConstraintRow(string boxId, string prose = "max 3 selections in force") => $"""
        <div id="{boxId}" class="nobox nocollapse collapsible-box constraints depth-2"><div class="title normalTitle"><span class="title-before"></span><div class="arrow-wrap"><img class="hide arrow icon" src="x.png"></div>
          <span class=""><span class="typeIcon-wrapper"><img class="typeIcon" src="assets/bsicons/constraint.png"></span><span class="">{prose}</span></span>
        </div></div>
        """;

    /// <summary>The stamp <c>EditorCollapsibleBox.mounted()</c> leaves on its element.</summary>
    private static string Stamp(string boxId, string entryId)
        => $"document.getElementById('{boxId}').vnode = {{ payload: {{ id: '{entryId}' }} }};";

    [Fact]
    public async Task OfTwoConstraintsThatReadTheSame_TheRowRenderingTheIdIsPicked()
    {
        await using var page = await EditorPage.OpenAsync(
            ConstraintRow("other") + ConstraintRow("wanted"),
            Stamp("other", "con-other") + Stamp("wanted", "con-squad-max"));

        var row = await NrEditorTreeRows.RenderingAsync(page.Page, "con-squad-max");

        Assert.Equal("wanted", await row.EvaluateAsync<string>("t => t.parentElement.id"));
        Assert.Contains("title", await row.GetAttributeAsync("class") ?? "", StringComparison.Ordinal);
    }

    /// <summary>A row Vue renders after the lookup starts — an ancestor just expanded — is waited for.</summary>
    [Fact]
    public async Task ARowRenderedAfterTheLookupStarts_IsWaitedFor()
    {
        await using var page = await EditorPage.OpenAsync(
            ConstraintRow("other"),
            Stamp("other", "con-other") + $$"""
                setTimeout(() => {
                    document.getElementById('editor-entries').insertAdjacentHTML('beforeend', `{{ConstraintRow("wanted")}}`);
                    {{Stamp("wanted", "con-squad-max")}}
                }, 300);
                """);

        var row = await NrEditorTreeRows.RenderingAsync(page.Page, "con-squad-max");

        Assert.Equal("wanted", await row.EvaluateAsync<string>("t => t.parentElement.id"));
    }

    /// <summary>Rows that carry no entry fail naming the stamp the lookup relies on, not a missing row.</summary>
    [Fact]
    public async Task RowsThatCarryNoEntry_FailNamingTheStamp()
    {
        await using var page = await EditorPage.OpenAsync(ConstraintRow("unstamped"), script: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NrEditorTreeRows.RenderingAsync(page.Page, "con-squad-max", timeoutMs: 300));

        Assert.Contains("el.vnode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NrEditorTreeRows", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A tree that does carry its entries, none of them this one, says the row is not rendered.</summary>
    [Fact]
    public async Task AStampedTreeWithoutTheEntry_FailsSayingItsRowIsNotRendered()
    {
        await using var page = await EditorPage.OpenAsync(ConstraintRow("other"), Stamp("other", "con-other"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NrEditorTreeRows.RenderingAsync(page.Page, "con-squad-max", timeoutMs: 300));

        Assert.Contains("not rendered", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("el.vnode", ex.Message, StringComparison.Ordinal);
    }

    private sealed class EditorPage(IPlaywright playwright, IBrowser browser, IPage page) : IAsyncDisposable
    {
        public IPage Page => page;

        public static async Task<EditorPage> OpenAsync(string rows, string script)
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(
                $"<!doctype html><html><body><div id=\"editor-entries\">{rows}</div><script>{script}</script></body></html>");
            return new EditorPage(playwright, browser, page);
        }

        public async ValueTask DisposeAsync()
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }
}
