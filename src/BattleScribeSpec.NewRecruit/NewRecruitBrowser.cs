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
        var waitUntil = NavigationWaitStrategy;
        await Page.GotoAsync(BaseUrl, new PageGotoOptions
        {
            WaitUntil = waitUntil,
            Timeout = 60_000,
        });
        // Dismiss cookie/consent dialogs if present
        await DismissDialogsAsync();
    }

    private WaitUntilState NavigationWaitStrategy =>
        _isFrozen ? WaitUntilState.Load : WaitUntilState.NetworkIdle;

    /// <summary>
    /// Navigate to the NR app page where systems can be selected/loaded.
    /// </summary>
    public async Task NavigateToAppAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/app", new PageGotoOptions
        {
            WaitUntil = NavigationWaitStrategy,
            Timeout = 30_000,
        });
        await DismissDialogsAsync();
    }

    /// <summary>
    /// Navigate to the roster editor for a specific list.
    /// </summary>
    public async Task NavigateToEditorAsync(string? listId = null)
    {
        if (listId != null)
        {
            await Page.GotoAsync($"{BaseUrl}/app/Lists/{listId}", new PageGotoOptions
            {
                WaitUntil = NavigationWaitStrategy,
                Timeout = 30_000,
            });
        }
        else
        {
            await Page.GotoAsync($"{BaseUrl}/app", new PageGotoOptions
            {
                WaitUntil = NavigationWaitStrategy,
                Timeout = 30_000,
            });
        }
        await DismissDialogsAsync();
    }

    /// <summary>
    /// Access the Pinia store by ID from within the page context.
    /// Returns the JS expression to access a given store.
    /// </summary>
    public static string PiniaStoreAccess(string storeId) =>
        $"document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('{storeId}')";

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

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
    }
}
