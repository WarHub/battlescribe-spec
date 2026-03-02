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
        await browser.InitializeAsync(headless);
        return browser;
    }

    private async Task InitializeAsync(bool headless)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });
        Page = await _browser.NewPageAsync();
        await Page.GotoAsync(BaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
        });
    }

    /// <summary>
    /// Navigate to the roster editor for a fresh roster.
    /// </summary>
    public async Task NavigateToEditorAsync()
    {
        await Page.GotoAsync($"{BaseUrl}/roster", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
        });
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
