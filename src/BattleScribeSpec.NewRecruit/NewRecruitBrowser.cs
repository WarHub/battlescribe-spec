using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Manages Playwright browser lifecycle for New Recruit testing.
/// Handles launching, navigating, and disposing the browser.
/// </summary>
public sealed class NewRecruitBrowser : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _isFrozen;
    private bool _frozenReady;

    public IPage Page { get; private set; } = null!;
    public string BaseUrl { get; }
    public bool IsFrozen => _isFrozen;

    /// <summary>
    /// True after the first successful frozen-mode setup. When set,
    /// <see cref="NavigateToAppAsync"/> and <see cref="WaitForPiniaAsync"/>
    /// can be skipped because we're already at /app with Pinia initialized
    /// and the setup JS blob handles cleanup of previous state.
    /// </summary>
    public bool FrozenReady
    {
        get => _frozenReady;
        set => _frozenReady = value;
    }

    private NewRecruitBrowser(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Create and initialize a browser session pointed at the NR web app.
    /// </summary>
    public static async Task<NewRecruitBrowser> CreateAsync(
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        var browser = new NewRecruitBrowser(baseUrl);
        await browser.InitializeAsync(headless, harFilePath: null);
        return browser;
    }

    /// <summary>
    /// Create a browser session that replays from a HAR file (frozen/offline mode).
    /// All network requests matching the HAR are served from the file — no internet required.
    /// </summary>
    public static async Task<NewRecruitBrowser> CreateFrozenAsync(
        string harFilePath,
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        if (!File.Exists(harFilePath))
            throw new FileNotFoundException($"HAR file not found: {harFilePath}", harFilePath);
        var browser = new NewRecruitBrowser(baseUrl);
        await browser.InitializeAsync(headless, harFilePath);
        return browser;
    }

    /// <summary>
    /// Create a browser wrapper from an existing context and page.
    /// Used by <see cref="NewRecruitEnginePool"/> to create multiple engines
    /// from a shared browser instance with individual contexts.
    /// The caller retains ownership of the context — disposing this browser
    /// only closes the page, not the context or playwright instance.
    /// </summary>
    internal static NewRecruitBrowser CreateFromContext(
        IPage page, string baseUrl, bool isFrozen)
    {
        var browser = new NewRecruitBrowser(baseUrl)
        {
            Page = page,
            _isFrozen = isFrozen,
            // No _playwright or _browser — lifecycle owned by the pool
        };
        return browser;
    }

    private async Task InitializeAsync(bool headless, string? harFilePath)
    {
        _isFrozen = harFilePath is not null;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });
        Page = await _browser.NewPageAsync();
        // Register JS helpers as an init script — automatically re-injected
        // on every full page navigation (GotoAsync). No manual tracking needed.
        await RegisterHelpersOnPageAsync(Page);
        if (harFilePath is not null)
        {
            await Page.RouteFromHARAsync(harFilePath, new PageRouteFromHAROptions
            {
                Url = "**",
                NotFound = HarNotFound.Abort,
            });
        }
        // In frozen mode, go directly to /app since HAR replay has issues
        // re-serving JS resources across full page navigations to different URLs.
        var initialUrl = _isFrozen ? $"{BaseUrl}/app" : BaseUrl;
        // Use 'Load' for all modes — NetworkIdle can hang on persistent
        // connections (analytics, ads, WebSockets) in the live site.
        await Page.GotoAsync(initialUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        if (!_isFrozen)
            await WaitForNetworkSettledAsync();
        // Dismiss cookie/consent dialogs if present
        await DismissDialogsAsync();
    }

    /// <summary>
    /// Navigate to the NR app page where systems can be selected/loaded.
    /// Uses Vue Router client-side navigation — avoids a full page reload
    /// (which is slower in live mode and breaks HAR replay in frozen mode).
    /// </summary>
    public async Task NavigateToAppAsync()
    {
        await VueRouterPushAsync("/app");
        await DismissDialogsAsync();
    }

    /// <summary>
    /// Navigate to the roster editor for a specific list.
    /// Uses Vue Router client-side navigation — avoids a full page reload
    /// (which is slower in live mode and breaks HAR replay in frozen mode).
    /// </summary>
    public async Task NavigateToEditorAsync(string? listId = null)
    {
        var route = listId != null ? $"/app/Lists/{listId}" : "/app";
        await VueRouterPushAsync(route);
        await DismissDialogsAsync();
    }

    /// <summary>
    /// Perform a Vue Router client-side navigation and wait for the route to resolve.
    /// Avoids full page reloads — faster and preserves JS state (Pinia stores,
    /// init scripts, window globals).
    /// </summary>
    private async Task VueRouterPushAsync(string route)
    {
        await Page.EvaluateAsync("""
            (route) => {
                const router = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$router;
                if (router) router.push(route);
            }
            """, route);
        await WaitForVueRouteAsync(route);
    }

    private static readonly Regex SafeStoreIdPattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Access the Pinia store by ID from within the page context.
    /// Returns the JS expression to access a given store.
    /// </summary>
    public static string PiniaStoreAccess(string storeId)
    {
        if (!SafeStoreIdPattern.IsMatch(storeId))
            throw new ArgumentException(
                $"Invalid Pinia store ID '{storeId}'. Must match [a-zA-Z0-9_-]+.", nameof(storeId));
        return $"document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('{storeId}')";
    }
    /// <summary>
    /// Try to dismiss any consent/cookie dialogs that might block interaction.
    /// </summary>
    private async Task DismissDialogsAsync()
    {
        try
        {
            // NR shows a consent dialog — look for "Do not consent" or similar buttons
            var consentButton = Page.GetByRole(AriaRole.Button, new() { Name = "Do not consent" });
            if (await consentButton.IsVisibleAsync())
            {
                await consentButton.ClickAsync();
                await consentButton.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 2_000 });
            }
        }
        catch
        {
            // Consent dialog may not be present — that's fine
        }
    }

    /// <summary>
    /// Best-effort wait for network to settle. Catches timeout so persistent
    /// connections (analytics, WebSockets) don't cause hard failures.
    /// </summary>
    private async Task WaitForNetworkSettledAsync(int timeoutMs = 15_000)
    {
        try
        {
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            // Expected when the site has persistent connections.
        }
    }

    /// <summary>
    /// Wait for Vue Router to complete a client-side navigation to the expected route.
    /// Polls the router's current path — avoids crude Task.Delay after router.push().
    /// </summary>
    private async Task WaitForVueRouteAsync(string expectedPath, int timeoutMs = 5_000)
    {
        await Page.WaitForFunctionAsync(
            "(expected) => document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$router?.currentRoute?.value?.path === expected",
            expectedPath,
            new() { Timeout = timeoutMs });
    }

    /// <summary>
    /// Wait for Pinia stores to be available in the Vue/Nuxt app.
    /// The page's load event may fire before Vue has fully initialized.
    /// </summary>
    public async Task WaitForPiniaAsync(int timeoutMs = 10_000)
    {
        try
        {
            await Page.WaitForFunctionAsync(
                "() => !!document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia",
                null,
                new() { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            // Let the caller's JS check produce the diagnostic error
        }
    }

    /// <summary>
    /// Register JS helpers as a page init script. The script runs automatically
    /// on every full page navigation (GotoAsync), eliminating the need to manually
    /// track and re-inject after navigations. For client-side navigations (Vue Router),
    /// window globals persist naturally — no re-injection needed.
    /// Call once per page, before the first navigation.
    /// </summary>
    public static async Task RegisterHelpersOnPageAsync(IPage page)
    {
        await page.AddInitScriptAsync(JsHelpers.InjectionScript);
    }

    public async ValueTask DisposeAsync()
    {
        if (Page is not null)
        {
            try { await Page.CloseAsync(); } catch { /* best effort */ }
            Page = null!;
        }
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
    }
}
