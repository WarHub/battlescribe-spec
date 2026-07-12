using BattleScribeSpec.Telemetry;
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
        Pool = pool;
    }

    /// <summary>
    /// Number of parallel engine instances in the pool.
    /// </summary>
    public int Size => Pool.Size;

    /// <summary>
    /// The underlying generic engine pool. Use for acquiring/releasing engines.
    /// </summary>
    public EnginePool<NewRecruitRosterEngine> Pool { get; }

    /// <summary>
    /// Create a frozen (HAR replay) engine pool with the specified concurrency.
    /// Each engine gets its own browser context with HAR replay.
    /// </summary>
    public static async Task<NewRecruitEnginePool> CreateFrozenAsync(
        string harFilePath,
        int concurrency = 5,
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true,
        bool visual = false,
        float? slowMo = null)
    {
        if (!File.Exists(harFilePath))
        {
            throw new FileNotFoundException($"HAR file not found: {harFilePath}", harFilePath);
        }

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            SlowMo = slowMo,
        });
        ResourceMetrics.Acquired("browser");

        var contexts = new List<IBrowserContext>();
        var engines = new List<NewRecruitRosterEngine>();

        for (var i = 0; i < concurrency; i++)
        {
            var context = await browser.NewContextAsync();
            contexts.Add(context);
            ResourceMetrics.Acquired("browser-context");

            var page = await context.NewPageAsync();

            // Register JS helpers as init script — auto-injected on every navigation
            await NewRecruitBrowser.RegisterHelpersOnPageAsync(page);

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

            var engine = NewRecruitRosterEngine.CreateFromBrowser(nrBrowser);
            engine.Visual = visual;
            engines.Add(engine);
        }

        var pool = EnginePool<NewRecruitRosterEngine>.Create(engines);
        return new NewRecruitEnginePool(playwright, browser, contexts, pool);
    }

    /// <summary>
    /// Create a live engine pool pointed at the real NR website.
    /// </summary>
    public static async Task<NewRecruitEnginePool> CreateLiveAsync(
        int concurrency = 10,
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true,
        bool visual = false,
        float? slowMo = null)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            SlowMo = slowMo,
        });
        ResourceMetrics.Acquired("browser");

        var contexts = new List<IBrowserContext>();
        var engines = new List<NewRecruitRosterEngine>();

        for (var i = 0; i < concurrency; i++)
        {
            var context = await browser.NewContextAsync();
            contexts.Add(context);
            ResourceMetrics.Acquired("browser-context");
            var page = await context.NewPageAsync();
            // Register JS helpers as init script — auto-injected on every navigation
            await NewRecruitBrowser.RegisterHelpersOnPageAsync(page);
            await page.GotoAsync(baseUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 60_000,
            });

            var nrBrowser = NewRecruitBrowser.CreateFromContext(page, baseUrl, isFrozen: false);
            var engine = NewRecruitRosterEngine.CreateFromBrowser(nrBrowser);
            engine.Visual = visual;
            engines.Add(engine);
        }

        var pool = EnginePool<NewRecruitRosterEngine>.Create(engines);
        return new NewRecruitEnginePool(playwright, browser, contexts, pool);
    }

    /// <summary>
    /// Acquire an engine from the pool. Dispose the returned handle to release it.
    /// </summary>
    public ValueTask<PooledEngine<NewRecruitRosterEngine>> AcquireAsync(CancellationToken ct = default)
        => Pool.AcquireAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The whole teardown body is guarded by this outer try/finally — mirroring
        // AdapterProcess.Dispose — so a throw from Pool.DisposeAsync() can't skip the
        // context/browser release below and leak their counters.
        try
        {
            await Pool.DisposeAsync();
        }
        finally
        {
            foreach (var ctx in _contexts)
            {
                try
                { await ctx.CloseAsync(); }
                catch { /* best effort */ }
                finally
                {
                    // In a finally so a throwing close can't leak the counter — a counter that drifts
                    // upward is worse than no counter, because it silently invents resources that don't exist.
                    ResourceMetrics.Released("browser-context");
                }
            }

            try
            { await _browser.CloseAsync(); }
            catch { /* best effort */ }
            finally
            {
                ResourceMetrics.Released("browser");
            }

            _playwright.Dispose();
        }
    }
}
