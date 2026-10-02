using Microsoft.Playwright;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Tells "the Playwright browsers are not installed" apart from every other way a browser bring-up can
/// fail — the one failure a frozen UI lane's fixture may turn into a skip.
/// </summary>
/// <remarks>
/// <para>
/// The frozen UI fixtures used to catch every <see cref="PlaywrightException"/> as "browsers not installed",
/// which made a snapshot that breaks bring-up indistinguishable from an unprovisioned machine: a document
/// request the HAR or the static routes failed (<c>net::ERR_FAILED</c> and the rest of <c>net::ERR_*</c>), a
/// route handler's error, a page closed under the driver — each a <see cref="PlaywrightException"/> — skipped
/// the whole lane, green for a lane that could not start. Now only a missing browser skips, and the
/// engine-composition check (<c>LaneComposition</c>) still fails a profile that claims the lane, naming
/// <c>setup.ps1</c>; anything else fails the lane's tests with the real error.
/// </para>
/// <para>
/// A navigation that times out was never in that catch: Playwright for .NET has no timeout exception of its own
/// and raises <see cref="System.TimeoutException"/>, which is not a <see cref="PlaywrightException"/>, so a
/// bring-up timeout failed the lane before this narrowing too. Checked when written, against
/// Microsoft.Playwright 1.63 by reflection (its one exported exception type is <see cref="PlaywrightException"/>)
/// and by aborting the frozen driver's <c>/app</c> document during bring-up: the lane fails with
/// <c>net::ERR_FAILED</c>, where the old catch-all skipped it.
/// </para>
/// <para>
/// <b>What this assumes.</b> Playwright's wording for a browser it has not downloaded: "Executable doesn't
/// exist at &lt;path&gt;", followed by the install command — the message of every Playwright release since the
/// browsers moved out of the package. If a release rewords it, a machine without browsers fails its frozen UI
/// lanes instead of skipping them: loud, and the message Playwright prints names the fix. Checked when
/// written with <c>PLAYWRIGHT_BROWSERS_PATH</c> pointed at an empty directory.
/// </para>
/// </remarks>
internal static class PlaywrightBrowsers
{
    /// <summary>Whether <paramref name="ex"/> is Playwright reporting that the browser it was asked to launch is not installed.</summary>
    public static bool AreMissing(PlaywrightException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal);
    }
}
