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
        int concurrency,
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
        IBrowser? browser = null;
        var contexts = new List<IBrowserContext>();
        var engines = new List<NewRecruitRosterEngine>();

        // Everything from here down is guarded: if any step throws partway through the loop (say
        // context 3 of 5), the browser/contexts already created above would otherwise never be
        // disposed — no pool object is ever returned, so the caller has nothing to dispose — which
        // leaks a real OS-level Chromium process AND permanently inflates harness.resource.count
        // (the Acquired() calls already fired; the matching Released() never would).
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                SlowMo = slowMo,
            });
            ResourceMetrics.Acquired("browser");

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
        catch
        {
            await DisposePartialConstructionAsync(playwright, browser, contexts, engines);
            throw;
        }
    }

    /// <summary>
    /// Create a live engine pool pointed at the real NR website.
    /// </summary>
    public static async Task<NewRecruitEnginePool> CreateLiveAsync(
        int concurrency,
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true,
        bool visual = false,
        float? slowMo = null)
    {
        var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        var contexts = new List<IBrowserContext>();
        var engines = new List<NewRecruitRosterEngine>();

        // See CreateFrozenAsync above for why this must be exception-safe: a mid-loop throw must
        // not leak the browser/contexts already acquired, nor leave their ResourceMetrics counters
        // permanently inflated.
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                SlowMo = slowMo,
            });
            ResourceMetrics.Acquired("browser");

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
        catch
        {
            await DisposePartialConstructionAsync(playwright, browser, contexts, engines);
            throw;
        }
    }

    /// <summary>
    /// Tears down whatever was already created before a construction-time exception, so a partial
    /// failure (e.g. context 3 of 5 throwing) can never leak a real Chromium process or leave
    /// <see cref="ResourceMetrics"/> counters permanently inflated with resources that no longer
    /// exist. Mirrors <see cref="DisposeAsync"/>'s teardown order (engines, then contexts, then
    /// browser, then Playwright) but only releases what was actually acquired.
    /// </summary>
    private static async Task DisposePartialConstructionAsync(
        IPlaywright playwright,
        IBrowser? browser,
        List<IBrowserContext> contexts,
        List<NewRecruitRosterEngine> engines)
    {
        foreach (var engine in engines)
        {
            try
            { engine.Dispose(); }
            catch { /* best effort */ }
        }

        foreach (var ctx in contexts)
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

        if (browser is not null)
        {
            try
            { await browser.CloseAsync(); }
            catch { /* best effort */ }
            finally
            {
                ResourceMetrics.Released("browser");
            }
        }

        playwright.Dispose();
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
