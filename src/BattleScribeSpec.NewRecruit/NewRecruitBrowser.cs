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

    public IPage Page { get; private set; } = null!;
    public string BaseUrl { get; }

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

    private async Task InitializeAsync(bool headless, string? harFilePath)
    {
        _isFrozen = harFilePath is not null;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });
        Page = await _browser.NewPageAsync();
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
    /// </summary>
    public async Task NavigateToAppAsync()
    {
        if (_isFrozen)
        {
            // In frozen (HAR replay) mode, use Vue Router to navigate back to /app.
            // A full GotoAsync to a different URL breaks HAR replay (Playwright
            // fails to re-serve JS resources with correct MIME types).
            // Since initial load goes to /app, client-side nav keeps state clean.
            await Page.EvaluateAsync("""
                () => {
                    const router = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$router;
                    if (router) router.push('/app');
                }
                """);
            await Task.Delay(300);
        }
        else
        {
            await Page.GotoAsync($"{BaseUrl}/app", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 30_000,
            });
            await WaitForNetworkSettledAsync();
        }
        await DismissDialogsAsync();
    }

    /// <summary>
    /// Navigate to the roster editor for a specific list.
    /// </summary>
    public async Task NavigateToEditorAsync(string? listId = null)
    {
        var url = listId != null ? $"{BaseUrl}/app/Lists/{listId}" : $"{BaseUrl}/app";
        await Page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 30_000,
        });
        if (!_isFrozen)
            await WaitForNetworkSettledAsync();
        await DismissDialogsAsync();
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
                await Page.WaitForTimeoutAsync(500);
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
