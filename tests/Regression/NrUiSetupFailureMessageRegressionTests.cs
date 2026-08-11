using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrRosterUiDriver;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Regression;

/// <summary>
/// Guards the one property a frozen NR-UI setup failure has to have: it says which wait failed and
/// what the page looked like instead.
/// </summary>
/// <remarks>
/// <para>
/// The frozen NR-UI roster lane spent two CI runs (31409213032, 31415790894) failing one spec out of
/// 363 with the complete text <c>Setup failed: TimeoutException: Timeout 30000ms exceeded.</c> — a
/// different spec each time, on PRs that touched neither this driver nor NR. That message is
/// consistent with two entirely different faults in <see cref="NrUiSetup.LoadGameDataAsync"/>: the
/// MySystems route never arriving, or NR never installing the game data once it did. One of those is
/// a lost-page race that a re-run clears; the other is a real driver or NR regression. Nothing in the
/// output distinguished them, so nobody could tell whether to re-run or investigate — which is
/// exactly the defect <c>docs/nr-ui-roster-coverage.md</c> records for the <c>library catalogues</c>
/// and <c>child force not visible</c> messages.
/// </para>
/// <para>
/// The cause is structural rather than incidental, so a test is worth having: Playwright names the
/// target of a <em>locator</em> wait ("waiting for Locator(…)") but has nothing to name for a
/// <c>WaitForFunctionAsync</c>, so every predicate wait in the driver fails with the same seven
/// words. Any new one added to setup inherits the anonymity unless it goes through
/// <see cref="NrUiSetup.WaitForSetupConditionAsync"/>.
/// </para>
/// <para>
/// Deliberately driven against <c>about:blank</c> rather than the frozen HAR. What is under test is
/// the shape of the failure, not NR: a blank page makes the condition fail for a reason the test
/// controls, in about a second, with no HAR and no dependence on NR's markup — so this keeps
/// reporting on the day an NR snapshot bump breaks everything else in the lane.
/// </para>
/// <para>
/// <c>Category=Conformance</c> because it launches a real Chromium, following
/// <c>NrListCleanupRegressionTests</c>: that trait is what keeps browser tests out of CI's offline
/// <c>core</c> step, and the <c>Engine</c> trait places it in the lanes that carry this driver.
/// </para>
/// </remarks>
/// <remarks>
/// In the <c>FrozenNrUiRoster</c> collection despite needing none of its fixture.
/// <see cref="NrBrowserHost"/> keeps ONE Chromium per process and relaunches it whenever a caller
/// asks for different launch options — so a headless request arriving while the lane is running
/// headed (<c>NR_HEADLESS=false</c>, the visible profiles, local debugging) would close the browser
/// out from under 363 in-flight specs. Sharing the collection serialises this against the lane, and
/// <see cref="LaunchOptions"/> asks for what the fixtures asked for; either alone would do, and a
/// test that can wreck the suite it guards deserves both.
/// </remarks>
[Collection("FrozenNrUiRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrUiRoster")]
public sealed class NrUiSetupFailureMessageRegressionTests(ITestOutputHelper output)
{
    /// <summary>
    /// Mirrors <c>FrozenNrUiRosterFixture</c> exactly — see the class remarks. Both knobs matter:
    /// <see cref="NrBrowserHost"/>'s launch key is the (headless, slowMo) pair, so differing on
    /// either one is what triggers the relaunch.
    /// </summary>
    private static (bool Headless, float? SlowMo) LaunchOptions => (
        Environment.GetEnvironmentVariable("NR_HEADLESS") != "false",
        float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null);

    /// <summary>
    /// Short on purpose. This test is about what the message says when the ceiling is reached, not
    /// about the ceiling — so it pays a second rather than <see cref="NrUiTimeouts"/>' 30.
    /// </summary>
    private const int TinyTimeoutMs = 1_000;

    [Fact]
    public async Task ASetupWaitThatTimesOut_NamesTheConditionAndWhatItSawInstead()
    {
        await using var blank = await BlankPage.OpenAsync();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            NrUiSetup.WaitForSetupConditionAsync(
                blank.Page,
                "NR installed the game data for system 'spec-under-test'",
                "() => false",
                null,
                TinyTimeoutMs,
                "() => 'localLibrary=[] pathname=' + location.pathname"));

        output.WriteLine(ex.Message);

        // The three facts a reader needs, and the three the bare Playwright message has none of:
        // WHICH wait, WHERE the page was, and WHAT the condition actually saw.
        Assert.Contains("NR installed the game data for system 'spec-under-test'", ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("about:blank", ex.Message, StringComparison.Ordinal);
        Assert.Contains("localLibrary=[]", ex.Message, StringComparison.Ordinal);

        // And it must still be recognisable as a timeout: the retry loop in LoadGameDataAsync
        // discriminates on this type, so a friendlier exception here would silently opt these waits
        // out of the guard that makes the lost-page race survivable.
        Assert.Contains(TinyTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A condition that holds must cost nothing and say nothing — the wrapper is a reporting path,
    /// not a new gate.
    /// </summary>
    [Fact]
    public async Task ASetupWaitWhoseConditionHolds_Passes()
    {
        await using var blank = await BlankPage.OpenAsync();

        await NrUiSetup.WaitForSetupConditionAsync(
            blank.Page,
            "a condition that is already true",
            "() => true",
            null,
            TinyTimeoutMs,
            "() => 'never evaluated'");
    }

    /// <summary>
    /// The observation is best-effort: a page that cannot be read is itself worth reporting, and must
    /// not replace the timeout with a confusing secondary failure.
    /// </summary>
    [Fact]
    public async Task AnObservationThatItselfFails_StillReportsTheTimeout()
    {
        await using var blank = await BlankPage.OpenAsync();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            NrUiSetup.WaitForSetupConditionAsync(
                blank.Page,
                "the MySystems route arrived",
                "() => false",
                null,
                TinyTimeoutMs,
                "() => { throw new Error('observation blew up'); }"));

        output.WriteLine(ex.Message);

        Assert.Contains("the MySystems route arrived", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank page in its own context off the shared Chromium, disposed with the context so these
    /// tests do not leak the browser contexts <c>ResourceMetrics</c> accounts for.
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
