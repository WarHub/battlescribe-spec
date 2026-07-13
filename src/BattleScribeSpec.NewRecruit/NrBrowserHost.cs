using BattleScribeSpec.Telemetry;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Process-scoped Chromium instance shared by every <see cref="NewRecruitBrowser"/> session.
/// <para>
/// Launching Chromium is the expensive part of a NewRecruit spec; creating a browser <b>context</b>
/// is cheap (tens of milliseconds). So one browser is launched per process and every session gets
/// its own context — which means every spec gets a pristine storage partition (cookies,
/// localStorage, IndexedDB, cache, service workers) and a fresh page, hence a fresh JS heap and
/// fresh Pinia stores.
/// </para>
/// <para>
/// That is the point: <b>isolation by construction, with no cleanup code to get wrong.</b> The
/// previous model reused a single page in the browser's implicit context and relied on hand-written
/// JS to scrub NR's state between specs — which silently leaked a leftover list and broke every spec
/// after the first roster-creating one (see docs/warm-reuse.md).
/// </para>
/// <para>
/// This mirrors <see cref="NewRecruitEnginePool"/>, which already runs N engines as N contexts of a
/// single browser in-process.
/// </para>
/// </summary>
internal static class NrBrowserHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    private static (bool Headless, float? SlowMo) _launchKey;
    private static bool _exitHookInstalled;
    /// <summary>Set once <c>ResourceMetrics.Acquired("browser")</c> has fired, so <see cref="CloseCoreAsync"/> releases exactly once.</summary>
    private static bool _acquired;

    /// <summary>
    /// Returns the shared browser, launching it on first use. Relaunches if a caller asks for
    /// different launch options (e.g. headed debugging) or if the previous browser has died.
    /// </summary>
    public static async Task<IBrowser> GetAsync(bool headless, float? slowMo)
    {
        await Gate.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true } live && _launchKey == (headless, slowMo))
            {
                return live;
            }

            await CloseCoreAsync();

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                SlowMo = slowMo,
            });
            _launchKey = (headless, slowMo);
            ResourceMetrics.Acquired("browser");
            _acquired = true;
            InstallExitHook();
            return _browser;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Closes the shared browser. Safe to call more than once.</summary>
    public static async ValueTask ShutdownAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await CloseCoreAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void InstallExitHook()
    {
        if (_exitHookInstalled)
        {
            return;
        }

        _exitHookInstalled = true;

        // The browser outlives every session, so nothing else would close it — without this the
        // Chromium child process would be orphaned when the host exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                ShutdownAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort during shutdown.
            }
        };
    }

    private static async Task CloseCoreAsync()
    {
        if (_browser is not null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch
            {
                // Best effort.
            }
            finally
            {
                // In a finally so a throwing close can't leak the counter — a counter that drifts
                // upward is worse than no counter, because it silently invents resources that don't exist.
                _browser = null;
                if (_acquired)
                {
                    _acquired = false;
                    ResourceMetrics.Released("browser");
                }
            }
        }

        _playwright?.Dispose();
        _playwright = null;
    }
}
