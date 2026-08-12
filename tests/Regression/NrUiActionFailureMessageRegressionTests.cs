using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrRosterUiDriver;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Regression;

/// <summary>
/// The action-step counterpart of <see cref="NrUiSetupFailureMessageRegressionTests"/>: a UI action
/// that times out must say which action, where the page was, and what it saw instead.
/// </summary>
/// <remarks>
/// <para>
/// Setup got this treatment first, for failures reported as <c>Setup failed:</c>. Actions kept the
/// anonymity for the same structural reason — Playwright names the target of a <em>locator</em> wait
/// but has nothing to name for a <c>WaitForFunctionAsync</c> — and the bill arrived on the v35.27
/// HAR bump (PR #338, run 31568343878), where the whole of what CI reported for
/// <c>constraint/constraint-forces-field-on-forceentry</c> was:
/// </para>
/// <code>Step 4: TimeoutException: Timeout 20000ms exceeded.</code>
/// <para>
/// A snapshot bump asks exactly one question — did NR change under the driver — and that message
/// cannot separate a changed UI from a page the driver had drifted off, which is the difference
/// between a driver fix and a re-run.
/// </para>
/// <para>
/// Driven against <c>about:blank</c> for the same reasons the setup tests are: what is under test is
/// the shape of the failure, not NR, so this keeps reporting on the day a snapshot bump breaks
/// everything else in the lane. The observation JS is NR-shaped but degrades honestly on a blank
/// page — <c>no army</c>, zero counts — which is precisely the reading a drifted page produces.
/// </para>
/// </remarks>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class NrUiActionFailureMessageRegressionTests(ITestOutputHelper output)
{
    /// <summary>Mirrors <c>FrozenNrUiRosterFixture</c> — see the setup tests' remarks for why both.</summary>
    private static (bool Headless, float? SlowMo) LaunchOptions => (
        Environment.GetEnvironmentVariable("NR_HEADLESS") != "false",
        float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null);

    [Fact]
    public async Task AnActionTimeout_NamesTheAction_ThePage_AndWhatItSawInstead()
    {
        await using var blank = await BlankPage.OpenAsync();

        var described = await NrUiDiagnostics.DescribeTimeoutAsync(
            blank.Page,
            "addForce-fe-patrol",
            new TimeoutException("Timeout 20000ms exceeded."),
            reportDir: "/artifacts/nr-ui-diagnostics/20260812-060000-spec-addForce-fe-patrol");

        output.WriteLine(described);

        // WHICH action, WHERE the page was, WHAT the editor held, and where to find the rest — the
        // four the bare Playwright message has none of.
        Assert.Contains("addForce-fe-patrol", described, StringComparison.Ordinal);
        Assert.Contains("about:blank", described, StringComparison.Ordinal);
        Assert.Contains("forces=no army", described, StringComparison.Ordinal);
        Assert.Contains("unitRows=0", described, StringComparison.Ordinal);
        Assert.Contains("nr-ui-diagnostics", described, StringComparison.Ordinal);

        // The original text is kept, not replaced: the ceiling that was hit is part of the evidence.
        Assert.Contains("Timeout 20000ms exceeded.", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture that failed leaves no report to name, and the description must not invent one.
    /// </summary>
    [Fact]
    public async Task AnActionTimeout_WithNoReportOnDisk_SaysEverythingElse()
    {
        await using var blank = await BlankPage.OpenAsync();

        var described = await NrUiDiagnostics.DescribeTimeoutAsync(
            blank.Page, "read-create-roster", new TimeoutException("Timeout 30000ms exceeded."), reportDir: null);

        output.WriteLine(described);

        Assert.Contains("read-create-roster", described, StringComparison.Ordinal);
        Assert.DoesNotContain("Report:", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A description is applied once. A setup wait that fails INSIDE an action already carries the
    /// more specific observation (which condition, and the state that condition was testing), and
    /// wrapping it again would push that behind a second, vaguer one.
    /// </summary>
    [Fact]
    public void AnAlreadyDescribedFailure_IsRecognisedAsSuch()
    {
        Assert.True(NrUiDiagnostics.IsDescribed(new TimeoutException(
            "NR UI setup: waited 30000ms for NR installed the game data for system 'x' and it did "
            + "not happen (page: https://www.newrecruit.eu/app/MyLists). Observed: localLibrary=[].")));

        Assert.False(NrUiDiagnostics.IsDescribed(new TimeoutException("Timeout 20000ms exceeded.")));
    }

    /// <summary>
    /// A blank page in its own context off the shared Chromium — see
    /// <see cref="NrUiSetupFailureMessageRegressionTests"/> for why the launch options must match the
    /// lane's.
    /// </summary>
    private sealed class BlankPage(IBrowserContext context, IPage page) : IAsyncDisposable
    {
        public IPage Page { get; } = page;

        public static async Task<BlankPage> OpenAsync()
        {
            var (headless, slowMo) = LaunchOptions;
            var browser = await NrBrowserHost.GetAsync(headless, slowMo);
            var context = await browser.NewContextAsync();
            return new BlankPage(context, await context.NewPageAsync());
        }

        public async ValueTask DisposeAsync() => await context.CloseAsync();
    }
}
