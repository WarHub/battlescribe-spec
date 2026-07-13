using System.Threading.Channels;
using BattleScribeSpec.Telemetry;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Pool of frozen NR Editor GameData UI engines backed by multiple browser contexts sharing a
/// single Playwright browser. Each context has its own page, static-file routing and NR Editor
/// Pinia stores — fully isolated so specs can run in parallel, one engine per spec at a time.
///
/// Mirrors <c>NewRecruitEnginePool</c> (the frozen NR roster pool) but for the GameData UI engine,
/// which is not an <c>IRosterEngine</c> and so can't use the generic <c>EnginePool&lt;T&gt;</c>;
/// the acquire/release channel is inlined here instead.
///
/// Architecture:
///   IPlaywright (singleton)
///     └── IBrowser (one shared instance)
///          ├── IBrowserContext #1 → IPage #1 → NrGameDataUiEngine #1
///          ├── IBrowserContext #2 → IPage #2 → NrGameDataUiEngine #2
///          └── IBrowserContext #N → IPage #N → NrGameDataUiEngine #N
/// </summary>
public sealed class NrGameDataUiEnginePool : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly List<IBrowserContext> _contexts;
    private readonly List<NrGameDataUiEngine> _engines;
    private readonly Channel<NrGameDataUiEngine> _available;
    private bool _disposed;

    private NrGameDataUiEnginePool(
        IPlaywright playwright,
        IBrowser browser,
        List<IBrowserContext> contexts,
        List<NrGameDataUiEngine> engines)
    {
        _playwright = playwright;
        _browser = browser;
        _contexts = contexts;
        _engines = engines;
        _available = Channel.CreateBounded<NrGameDataUiEngine>(engines.Count);
        foreach (var engine in engines)
        {
            _available.Writer.TryWrite(engine);
        }
    }

    /// <summary>
    /// Number of parallel engine instances (browser contexts) in the pool.
    /// </summary>
    public int Size => _engines.Count;

    /// <summary>
    /// Create a frozen (static-file) engine pool with the specified concurrency. Launches ONE
    /// Chromium browser and gives each engine its own service-worker-blocking context with its own
    /// static-file routing over <paramref name="staticDir"/>.
    /// </summary>
    public static async Task<NrGameDataUiEnginePool> CreateFrozenAsync(
        string staticDir,
        int concurrency = 5,
        bool headless = true,
        float? slowMo = null)
    {
        if (!Directory.Exists(staticDir))
        {
            throw new DirectoryNotFoundException($"NR Editor static directory not found: {staticDir}");
        }

        if (!File.Exists(Path.Combine(staticDir, "index.html")))
        {
            throw new FileNotFoundException(
                $"NR Editor static directory doesn't contain index.html: {staticDir}");
        }

        if (concurrency < 1)
        {
            concurrency = 1;
        }

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            SlowMo = slowMo,
        });
        ResourceMetrics.Acquired("browser");

        var contexts = new List<IBrowserContext>();
        var engines = new List<NrGameDataUiEngine>();

        for (var i = 0; i < concurrency; i++)
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ServiceWorkers = ServiceWorkerPolicy.Block,
            });
            contexts.Add(context);
            ResourceMetrics.Acquired("browser-context");

            var engine = await NrGameDataUiEngine.CreateFrozenInContextAsync(context, staticDir, headless);
            engines.Add(engine);
        }

        return new NrGameDataUiEnginePool(playwright, browser, contexts, engines);
    }

    /// <summary>
    /// Acquire an engine from the pool. Dispose the returned handle to release it back to the pool.
    /// Blocks (asynchronously) until an engine is free.
    /// </summary>
    public async ValueTask<PooledGameDataUiEngine> AcquireAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = await _available.Reader.ReadAsync(ct);
        return new PooledGameDataUiEngine(engine, this);
    }

    internal void Release(NrGameDataUiEngine engine)
    {
        if (!_disposed)
        {
            _available.Writer.TryWrite(engine);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The whole teardown body is guarded by this outer try/finally — mirroring
        // AdapterProcess.Dispose — so a throw while completing the channel or disposing an
        // engine can't skip the context/browser release below and leak their counters.
        try
        {
            _available.Writer.Complete();

            foreach (var engine in _engines)
            {
                engine.Dispose();
            }
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

/// <summary>
/// A pooled NR Editor GameData UI engine wrapper. Disposing returns the engine to its pool.
/// </summary>
public readonly struct PooledGameDataUiEngine : IDisposable
{
    private readonly NrGameDataUiEnginePool _pool;

    internal PooledGameDataUiEngine(NrGameDataUiEngine engine, NrGameDataUiEnginePool pool)
    {
        Engine = engine;
        _pool = pool;
    }

    /// <summary>The engine instance. Valid until this struct is disposed.</summary>
    public NrGameDataUiEngine Engine { get; }

    public void Dispose() => _pool.Release(Engine);
}
