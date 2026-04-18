using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Pool of NewRecruit engine instances backed by multiple browser contexts
/// sharing a single Playwright browser. Each context has its own page, HAR
/// replay, and Pinia stores — fully isolated for parallel test execution.
///
/// Architecture:
///   IPlaywright (singleton)
///     └── IBrowser (one shared instance)
///          ├── IBrowserContext #1 → IPage #1 → NewRecruitRosterEngine #1
///          ├── IBrowserContext #2 → IPage #2 → NewRecruitRosterEngine #2
///          └── IBrowserContext #N → IPage #N → NewRecruitRosterEngine #N
/// </summary>
public sealed class NewRecruitEnginePool : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly List<IBrowserContext> _contexts;
    private readonly EnginePool<NewRecruitRosterEngine> _pool;
    private bool _disposed;

    private NewRecruitEnginePool(
        IPlaywright playwright,
        IBrowser browser,
        List<IBrowserContext> contexts,
        EnginePool<NewRecruitRosterEngine> pool)
    {
        _playwright = playwright;
        _browser = browser;
        _contexts = contexts;
        _pool = pool;
    }

    /// <summary>
    /// Number of parallel engine instances in the pool.
    /// </summary>
    public int Size => _pool.Size;

    /// <summary>
    /// The underlying generic engine pool. Use for acquiring/releasing engines.
    /// </summary>
    public EnginePool<NewRecruitRosterEngine> Pool => _pool;

    /// <summary>
    /// Create a frozen (HAR replay) engine pool with the specified concurrency.
    /// Each engine gets its own browser context with HAR replay.
    /// </summary>
    public static async Task<NewRecruitEnginePool> CreateFrozenAsync(
        string harFilePath,
        int concurrency = 5,
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        if (!File.Exists(harFilePath))
            throw new FileNotFoundException($"HAR file not found: {harFilePath}", harFilePath);

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });

        var contexts = new List<IBrowserContext>();
        var engines = new List<NewRecruitRosterEngine>();

        for (int i = 0; i < concurrency; i++)
        {
            var context = await browser.NewContextAsync();
            contexts.Add(context);

            var page = await context.NewPageAsync();

            // HAR replay at page level (context-level RouteFromHAR not available)
            await page.RouteFromHARAsync(harFilePath, new PageRouteFromHAROptions
            {
                Url = "**",
                NotFound = HarNotFound.Abort,
            });

            // Navigate to /app and wait for load
            await page.GotoAsync($"{baseUrl}/app", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 60_000,
            });

            var nrBrowser = NewRecruitBrowser.CreateFromContext(page, baseUrl, isFrozen: true);

            // Wait for Pinia to initialize
            await nrBrowser.WaitForPiniaAsync();
            // Pre-inject JS helpers
            await nrBrowser.InjectHelpersAsync();

            var engine = NewRecruitRosterEngine.CreateFromBrowser(nrBrowser);
            engines.Add(engine);
        }

        var pool = EnginePool<NewRecruitRosterEngine>.Create(engines);
        return new NewRecruitEnginePool(playwright, browser, contexts, pool);
    }

    /// <summary>
    /// Create a live engine pool pointed at the real NR website.
    /// </summary>
    public static async Task<NewRecruitEnginePool> CreateLiveAsync(
        int concurrency = 2,
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });

        var contexts = new List<IBrowserContext>();
        var engines = new List<NewRecruitRosterEngine>();

        for (int i = 0; i < concurrency; i++)
        {
            var context = await browser.NewContextAsync();
            contexts.Add(context);
            var page = await context.NewPageAsync();
            await page.GotoAsync(baseUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 60_000,
            });

            var nrBrowser = NewRecruitBrowser.CreateFromContext(page, baseUrl, isFrozen: false);
            var engine = NewRecruitRosterEngine.CreateFromBrowser(nrBrowser);
            engines.Add(engine);
        }

        var pool = EnginePool<NewRecruitRosterEngine>.Create(engines);
        return new NewRecruitEnginePool(playwright, browser, contexts, pool);
    }

    /// <summary>
    /// Acquire an engine from the pool. Dispose the returned handle to release it.
    /// </summary>
    public ValueTask<PooledEngine<NewRecruitRosterEngine>> AcquireAsync(CancellationToken ct = default)
        => _pool.AcquireAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _pool.DisposeAsync();

        foreach (var ctx in _contexts)
        {
            try { await ctx.CloseAsync(); } catch { /* best effort */ }
        }

        await _browser.CloseAsync();
        _playwright.Dispose();
    }
}
