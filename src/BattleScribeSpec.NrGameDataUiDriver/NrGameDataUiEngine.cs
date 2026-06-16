using BattleScribeSpec.GameData;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// IGameDataEngine implementation that drives the NR Editor web app through
/// real Playwright UI interactions rather than direct store/JS manipulation.
///
/// Architecture:
///   - Setup: loads XML files via NR Editor's "Add From Folder" file picker UI
///   - Actions: drives the editor's catalogue tree, context menus, and property panels
///   - State: reads from NR Editor's Pinia editorStore after each mutation
///
/// This is the UI-driver counterpart to <see cref="NewRecruitGameDataEngine"/>,
/// which injects JS directly into the store. The UI driver validates that NR Editor's
/// actual rendered interface correctly implements BattleScribe data model mutations.
///
/// Frozen mode: serves NR Editor static files locally (same gh-pages snapshot
/// as <see cref="NewRecruitGameDataEngine"/>, no additional HAR required).
/// Live mode: connects to a running NR Editor deployment.
/// </summary>
public sealed class NrGameDataUiEngine : IGameDataEngine
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private NrGameDataUiDiagnostics? _diagnostics;
    private string _specId = "";
    private bool _disposed;

    /// <summary>Base URL of the NR Editor being tested.</summary>
    public string BaseUrl { get; }

    /// <summary>Whether the browser is running in headless mode.</summary>
    public bool Headless { get; }

    private NrGameDataUiEngine(string baseUrl, bool headless)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Headless = headless;
    }

    /// <summary>
    /// Create a live NR Editor engine pointed at the given URL.
    /// </summary>
    public static async Task<NrGameDataUiEngine> CreateAsync(
        string baseUrl = "https://giloushaker.github.io/nr-editor",
        bool headless = true,
        float? slowMo = null)
    {
        var engine = new NrGameDataUiEngine(baseUrl, headless);
        await engine.InitializeAsync(slowMo);
        return engine;
    }

    /// <summary>
    /// Create a frozen NR Editor engine that serves static files from a local directory.
    /// The directory must contain the gh-pages deployment of the NR Editor
    /// (index.html, _nuxt/, assets/, etc.).
    /// </summary>
    public static async Task<NrGameDataUiEngine> CreateFrozenAsync(
        string staticDir,
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

        // Use a synthetic base URL — all requests are intercepted locally
        var engine = new NrGameDataUiEngine("https://nr-editor.local/nr-editor", headless);
        await engine.InitializeFrozenAsync(staticDir, slowMo);
        return engine;
    }

    /// <summary>
    /// Locates the NR Editor static files directory by walking up from startDir
    /// looking for .testdata/nr-editor/index.html.
    /// </summary>
    public static string? FindFrozenStaticDir(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".testdata", "nr-editor");
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Exposes the Playwright page for probe and diagnostics access.</summary>
    public IPage Page => _page ?? throw new InvalidOperationException("Engine not initialized.");

    private async Task InitializeAsync(float? slowMo)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Headless,
            SlowMo = slowMo,
        });
        _page = await _browser.NewPageAsync();
        _diagnostics = new NrGameDataUiDiagnostics(_page);
        await _page.GotoAsync(BaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        await WaitForAppReadyAsync();
    }

    private async Task InitializeFrozenAsync(string staticDir, float? slowMo)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Headless,
            SlowMo = slowMo,
        });
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ServiceWorkers = ServiceWorkerPolicy.Block,
        });
        _page = await context.NewPageAsync();
        _diagnostics = new NrGameDataUiDiagnostics(_page);
        await NrGameDataUiSetup.SetupStaticFileRoutingAsync(_page, staticDir);
        await _page.GotoAsync(BaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        await WaitForAppReadyAsync();
    }

    private async Task WaitForAppReadyAsync()
    {
        if (_page is null)
        { return; }

        await _page.WaitForFunctionAsync(
            """
            () => {
                const nuxt = document.querySelector('#__nuxt')?.__vue_app__;
                const app = document.querySelector('#app')?.__vue_app__;
                const vueApp = nuxt || app;
                return !!vueApp?.config?.globalProperties?.$pinia;
            }
            """,
            null,
            new() { Timeout = 30_000 });
    }

    // ===== IGameDataEngine =====

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        => SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<string>> SetupAsync(
        ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        if (_page is null)
        { return ["NR Editor page not initialized"]; }

        try
        {
            var errors = await NrGameDataUiSetup.LoadAndOpenCatalogueAsync(_page, gameSystem, catalogues);
            if (errors.Count > 0)
            { return errors; }

            // Store spec context for diagnostics
            await _page.EvaluateAsync(
                "(specId) => { window.__bsspec_editor_ui = window.__bsspec_editor_ui || {}; window.__bsspec_editor_ui.specId = specId; }",
                _specId);

            return [];
        }
        catch (Exception ex)
        {
            return [$"NR Editor GameData UI setup exception: {ex.Message}"];
        }
    }

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null)
        => NrGameDataUiActions.AddEntryAsync(_page!, parentId, entryType, name).GetAwaiter().GetResult();

    public void RemoveEntry(string entryId)
        => NrGameDataUiActions.RemoveEntryAsync(_page!, entryId).GetAwaiter().GetResult();

    public void SetField(string entryId, string field, string? value)
        => NrGameDataUiActions.SetFieldAsync(_page!, entryId, field, value).GetAwaiter().GetResult();

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId)
        => NrGameDataUiActions.AddLinkAsync(_page!, parentId, linkType, targetId).GetAwaiter().GetResult();

    public GameDataState GetState()
        => GetStateAsync().GetAwaiter().GetResult();

    private async Task<GameDataState> GetStateAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        return await NrGameDataUiActions.ReadStateAsync(_page);
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

    public void Cleanup()
        => CleanupAsync().GetAwaiter().GetResult();

    private async Task CleanupAsync()
    {
        if (_page is null)
        { return; }

        try
        {
            await NrGameDataUiSetup.CleanupCatalogueAsync(_page, BaseUrl);
        }
        catch
        {
            // Best-effort cleanup — don't propagate failures to callers
        }
    }

    /// <summary>
    /// Captures a screenshot of the current page state (for diagnostics).
    /// </summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
        => _diagnostics is not null ? await _diagnostics.CaptureScreenshotAsync() : null;

    /// <summary>
    /// Captures the full diagnostic bundle (screenshot + console + DOM + Pinia state).
    /// </summary>
    public async Task<NrGameDataDiagnosticReport?> CaptureDiagnosticsAsync()
        => _diagnostics is not null ? await _diagnostics.CaptureFullReportAsync() : null;

    /// <summary>
    /// Evaluates arbitrary JavaScript in the NR Editor page context.
    /// Used by probe and diagnostic tooling.
    /// </summary>
    public async Task<T?> EvaluateAsync<T>(string expression)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        return await _page.EvaluateAsync<T>(expression);
    }

    public void Dispose()
    {
        if (_disposed)
        { return; }

        _disposed = true;
        // ValueTask from DisposeAsync: complete synchronously or wait via task
        var disposeTask = _browser?.DisposeAsync();
        if (disposeTask.HasValue && !disposeTask.Value.IsCompleted)
        {
            disposeTask.Value.AsTask().GetAwaiter().GetResult();
        }
        _playwright?.Dispose();
    }
}
