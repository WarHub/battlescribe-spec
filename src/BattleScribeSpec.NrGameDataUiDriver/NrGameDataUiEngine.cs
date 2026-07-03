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

    // When the engine is created against an externally-supplied context (the pool), it does NOT own
    // the Playwright/browser and must not dispose them — the pool owns and tears down the shared
    // browser. Set definitively in InitializeInContextAsync based on whether this engine launched
    // its own browser.
    private bool _ownsBrowser = true;

    // Open-file tracking, so Reload can reopen whatever file was active. Maps each loaded file's
    // id to its display name (NavigateToEditableAsync matches on the name shown in the file list).
    private readonly Dictionary<string, string> _idToName = new(StringComparer.Ordinal);
    private string? _openId;

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
    /// Create a frozen NR Editor engine that runs inside an externally-owned
    /// <see cref="IBrowserContext"/> (supplied by <see cref="NrGameDataUiEnginePool"/>), so many
    /// engines can share a single Chromium browser for parallel execution. The caller owns the
    /// context and its browser; this engine will not dispose them. The context is expected to
    /// already block service workers and have its own static-file routing set up per page here.
    /// </summary>
    public static async Task<NrGameDataUiEngine> CreateFrozenInContextAsync(
        IBrowserContext context,
        string staticDir,
        bool headless = true)
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

        var engine = new NrGameDataUiEngine("https://nr-editor.local/nr-editor", headless);
        await engine.InitializeInContextAsync(context, staticDir);
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
        _ui = new NrGameDataUiDriver(_page);
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
        await InitializeInContextAsync(context, staticDir);
    }

    /// <summary>
    /// Wires up the page (routing, navigation, app-ready wait) inside the given context. Shared by
    /// the self-owned frozen path (<see cref="InitializeFrozenAsync"/>) and the pool path
    /// (<see cref="CreateFrozenInContextAsync"/>). Ownership of the browser is inferred from whether
    /// this engine launched one (<c>_browser</c> non-null) so Dispose only tears down what it owns.
    /// </summary>
    private async Task InitializeInContextAsync(IBrowserContext context, string staticDir)
    {
        _ownsBrowser = _browser is not null;
        _page = await context.NewPageAsync();
        _diagnostics = new NrGameDataUiDiagnostics(_page);
        _ui = new NrGameDataUiDriver(_page);
        await NrEditorStore.SetupStaticFileRoutingAsync(_page, staticDir);
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

    public void SetTestContext(string specId)
    {
        _specId = specId;
        _ui?.Reset();
    }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        => SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<string>> SetupAsync(
        ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        if (_page is null)
        { return ["NR Editor page not initialized"]; }

        try
        {
            var errors = await NrEditorStore.LoadAndOpenCatalogueAsync(_page, gameSystem, catalogues);
            if (errors.Count > 0)
            { return errors; }

            // Remember the loaded files (id -> display name) and which one is open, so Reload can
            // round-trip through the editor's export and reopen the same file. Setup opens the last
            // catalogue, or the game system itself when there are none.
            _idToName.Clear();
            _idToName[gameSystem.Id] = gameSystem.Name;
            foreach (var cat in catalogues)
            { _idToName[cat.Id] = cat.Name; }
            _openId = catalogues.Length > 0 ? catalogues[^1].Id : gameSystem.Id;

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

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null)
        => Run($"addEntry-{entryType}", () => _ui.AddEntryAsync(parentId, entryType, name, id));

    public void RemoveEntry(string entryId)
        => Run($"removeEntry", () => NrGameDataUiActions.RemoveEntryAsync(_page!, entryId));

    public void SetField(string entryId, string field, string? value)
        => Run($"setField-{field}", () => _ui.SetFieldAsync(entryId, field, value));

    public void SetCost(string entryId, string costTypeId, string? value)
        => Run($"setCost-{costTypeId}", () => _ui.SetCostAsync(entryId, costTypeId, value));

    public void SetCharacteristic(string entryId, string nameOrTypeId, string? value)
        => Run($"setCharacteristic-{nameOrTypeId}", () => _ui.SetCharacteristicAsync(entryId, nameOrTypeId, value));

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null)
        => Run($"addLink-{linkType}", () => _ui.AddLinkAsync(parentId, linkType, targetId, id));

    /// <summary>
    /// Runs an action; on failure, captures a diagnostics bundle <b>at the failure point</b>
    /// (before the runner's Cleanup navigates away) when NR_GAMEDATA_UI_DIAGNOSTICS is set, then
    /// rethrows. Gated by env var so normal runs aren't slowed; <c>bs-spec verify --diagnostics</c>
    /// sets it.
    /// </summary>
    private T Run<T>(string label, Func<Task<T>> action)
    {
        try
        {
            return action().GetAwaiter().GetResult();
        }
        catch
        {
            CaptureFailureDiagnostics(label);
            throw;
        }
    }

    private void Run(string label, Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch
        {
            CaptureFailureDiagnostics(label);
            throw;
        }
    }

    private void CaptureFailureDiagnostics(string label)
    {
        if (Environment.GetEnvironmentVariable("NR_GAMEDATA_UI_DIAGNOSTICS") is null || _diagnostics is null)
        {
            return;
        }

        try
        {
            var report = _diagnostics.CaptureFullReportAsync().GetAwaiter().GetResult();
            NrGameDataUiDiagnostics.SaveReportAsync(report, $"{_specId}-{label}").GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort — diagnostics must never mask the original failure.
        }
    }

    /// <summary>Selects/opens the given catalogue (or game system) for editing in the NR Editor.</summary>
    public void OpenFile(string id)
    {
        _ui.OpenFileAsync(id).GetAwaiter().GetResult();
        _openId = id;
    }

    public string ExportActiveFile() => ExportActiveFileAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Export the active file's XML — NR's own serialization (via <see cref="ExportLoadedFilesJsonAsync"/>),
    /// selecting the loaded file matching the open id (catalogue <c>{id}.cat</c>, else the game system
    /// <c>system.gst</c>). This is the NR base producer for snapshot assertions.
    /// </summary>
    private async Task<string> ExportActiveFileAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var files = NrEditorStore.ParseExportedFiles(await NrEditorStore.ExportLoadedFilesJsonAsync(_page));
        if (files.Count == 0)
        {
            throw new InvalidOperationException("ExportActiveFile: NR Editor export produced no text XML files.");
        }

        var catName = _openId + ".cat";
        var pick = files.FirstOrDefault(f => f.Name == catName);
        if (pick.Xml is null)
        {
            pick = files.FirstOrDefault(f => f.Name.EndsWith(".gst", StringComparison.OrdinalIgnoreCase));
        }
        if (pick.Xml is null)
        {
            pick = files[0];
        }

        return pick.Xml;
    }

    public string LoadFile(string xml) => LoadFileAsync(xml).GetAwaiter().GetResult();

    /// <summary>
    /// Load a catalogue/game system from XML into the editor (single-file upload, no reset) and open
    /// it for editing. Returns the loaded file's root id, tracked for export/reopen.
    /// </summary>
    private async Task<string> LoadFileAsync(string xml)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var (id, name, isGameSystem) = NrEditorStore.ParseRoot(xml);
        if (id.Length == 0)
        {
            throw new InvalidOperationException("LoadFile: could not read a root id from the XML.");
        }

        var fileName = isGameSystem ? "system.gst" : id + ".cat";
        var errors = await NrEditorStore.LoadFileAsync(_page, fileName, xml, id, name);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("LoadFile failed: " + string.Join("; ", errors));
        }

        _idToName[id] = name;
        _openId = id;
        return id;
    }

    public void Reload() => ReloadAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Round-trips the edited state through the real NR Editor: export the loaded files as
    /// BattleScribe XML (the editor's own serialization of the mutated stores), then reload that XML
    /// through the file-input pipeline so NR's <c>BSXmlToJson</c> parse runs, and reopen the file
    /// that was active. A round-trip spec re-asserts its <c>expectedState</c> after this.
    /// </summary>
    private async Task ReloadAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var files = NrEditorStore.ParseExportedFiles(await NrEditorStore.ExportLoadedFilesJsonAsync(_page));
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "Reload: the NR Editor export produced no text XML files to reload.");
        }

        var reopenName = _openId is not null && _idToName.TryGetValue(_openId, out var name)
            ? name
            : _idToName.Values.FirstOrDefault()
                ?? throw new InvalidOperationException("Reload: no loaded file to reopen.");

        var errors = await NrEditorStore.ReloadFromXmlAsync(_page, BaseUrl, files, reopenName);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Reload failed: " + string.Join("; ", errors));
        }

        // The reset clears our spec-context marker; restore it for diagnostics.
        await _page.EvaluateAsync(
            "(specId) => { window.__bsspec_editor_ui = window.__bsspec_editor_ui || {}; window.__bsspec_editor_ui.specId = specId; }",
            _specId);
    }

    private NrGameDataUiDriver _ui = null!;

    public GameDataState GetState()
        => GetStateAsync().GetAwaiter().GetResult();

    private async Task<GameDataState> GetStateAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        return await NrEditorStore.ReadStateAsync(_page);
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
        => GetValidationErrorsAsync().GetAwaiter().GetResult();

    private async Task<IReadOnlyList<ValidationErrorState>> GetValidationErrorsAsync()
    {
        if (_page is null)
        { return []; }

        return await NrEditorStore.GetValidationErrorsAsync(_page);
    }

    public void Cleanup()
        => CleanupAsync().GetAwaiter().GetResult();

    private async Task CleanupAsync()
    {
        if (_page is null)
        { return; }

        try
        {
            await NrEditorStore.CleanupCatalogueAsync(_page, BaseUrl);
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

    /// <summary>
    /// Serializes every currently-loaded file (game system + catalogues) to BattleScribe XML
    /// using NR's own bundled serializer (<c>convertToXml</c>), and returns the result as JSON
    /// <c>{ "files": { "&lt;path&gt;": "&lt;xml&gt;" }, "debug": [..] }</c>.
    ///
    /// NR's <c>convertToXml</c> is module-scoped (not directly reachable), but the editor store's
    /// <c>saveCatalogueInFiles(data)</c> calls it and then hands the bytes to <c>writeFile</c>,
    /// which forwards to <c>electron.invoke("saveFile", path, content)</c>. In the browser
    /// (no Electron) <c>writeFile</c> normally no-ops; we temporarily stub <c>globalThis.electron</c>
    /// so the serialized content is captured in-page instead of written to disk. This is the same
    /// serializer the editor's "Download" button uses, so the XML is byte-for-byte what NR emits.
    /// </summary>
    public async Task<string> ExportLoadedFilesJsonAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        return await NrEditorStore.ExportLoadedFilesJsonAsync(_page);
    }

    /// <summary>
    /// Dumps every <c>&lt;select&gt;</c> currently rendered in the right-hand property panel,
    /// with each option's value + visible text and the nearest label/legend for context.
    /// Used by <c>discover enums</c> to enumerate NR's dropdown vocabularies (modifier/condition/
    /// constraint types, link types, entry types, etc.) without guessing from source.
    /// </summary>
    public async Task<string> DumpSelectsJsonAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        return await _page.EvaluateAsync<string>("""
            () => {
                const out = [];
                const panel = document.querySelector('.rightPanel') || document.body;
                for (const sel of panel.querySelectorAll('select')) {
                    // Nearest contextual label: the row's first cell, a wrapping fieldset legend,
                    // or a preceding label.
                    let context = '';
                    const tr = sel.closest('tr');
                    if (tr) { const td = tr.querySelector('td'); if (td) context = td.innerText.trim(); }
                    if (!context) {
                        const fs = sel.closest('fieldset');
                        const lg = fs?.querySelector('legend');
                        if (lg) context = lg.innerText.trim();
                    }
                    if (!context) {
                        const cls = sel.closest('.constraint,.modifier,.condition,.repeat,.query');
                        if (cls) context = cls.className;
                    }
                    out.push({
                        context,
                        options: [...sel.options].map(o => ({ value: o.value, text: (o.textContent || '').trim() })),
                    });
                }
                return JSON.stringify(out);
            }
            """);
    }

    /// <summary>
    /// Opens every icon-select and autocomplete widget in the right panel in turn and reads its
    /// suggestion list, returning <c>{ "&lt;widget-context&gt;": ["opt", ...] }</c>. These widgets back
    /// NR's <c>field</c> (modType icon-select) and <c>scope</c> (autocomplete) vocabularies, which a
    /// plain <c>&lt;select&gt;</c> dump can't see. Best-effort: failures per widget are skipped.
    /// </summary>
    public async Task<string> DumpOpenableWidgetsJsonAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = new Dictionary<string, string[]>();
        var inputs = _page.Locator(".rightPanel .iconselect-input, .rightPanel .autocomplete-input");
        var count = await inputs.CountAsync();
        for (var i = 0; i < count; i++)
        {
            try
            {
                var input = inputs.Nth(i);
                if (!await input.IsVisibleAsync())
                { continue; }
                var context = await input.EvaluateAsync<string>(
                    "el => { const c = el.closest('.constraint,.modifier,.condition,.repeat,.query,tr,fieldset'); " +
                    "const tr = el.closest('tr'); const td = tr && tr.querySelector('td'); " +
                    "return (td && td.innerText.trim()) || (c && (c.className||'').toString()) || ''; }");
                await input.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                await _page.WaitForTimeoutAsync(250);
                var suggestions = _page.Locator(".suggestions:not(.hidden) > div");
                var opts = await suggestions.AllTextContentsAsync();
                var key = $"[{i}] {context}";
                result[key] = [.. opts.Select(o => o.Trim()).Where(o => o.Length > 0)];
                // Close the popup before moving on.
                await _page.Keyboard.PressAsync("Escape");
                await _page.WaitForTimeoutAsync(100);
            }
            catch
            {
                // Best-effort enumeration — skip widgets that don't open cleanly.
            }
        }
        return System.Text.Json.JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Right-clicks the given tree node (Playwright selector) and returns the resulting context-menu
    /// item labels, then hovers any submenu trigger (label containing "❯") and captures its sub-items.
    /// Used by <c>discover nodes</c> to enumerate which node types the editor can create where.
    /// Returns JSON <c>{ "menu": ["..."], "submenus": { "Link": ["..."] } }</c>.
    /// </summary>
    public async Task<string> RightClickAndDumpMenuJsonAsync(string nodeSelector)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var node = _page.Locator(nodeSelector).First;
        await node.ScrollIntoViewIfNeededAsync();
        await node.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await _page.WaitForTimeoutAsync(300);

        var menuItems = await _page.Locator(".context-menu:visible > div").AllTextContentsAsync();
        var trimmed = menuItems.Select(m => m.Trim().Replace("\n", " ")).Where(m => m.Length > 0).ToArray();

        var submenus = new Dictionary<string, string[]>();
        foreach (var item in trimmed)
        {
            if (!item.Contains('❯') && !item.Contains('►') && !item.Contains('▶'))
            { continue; }
            var label = item.Split('❯', '►', '▶')[0].Trim();
            try
            {
                var trigger = _page.Locator(".context-menu:visible > div")
                    .Filter(new LocatorFilterOptions { HasText = label });
                await trigger.First.HoverAsync(new LocatorHoverOptions { Timeout = 2_000 });
                await _page.WaitForTimeoutAsync(350);
                var subItems = await _page.Locator(".context-menu:visible")
                    .Filter(new LocatorFilterOptions { HasNotText = "Remove" })
                    .First.Locator("> div").AllTextContentsAsync();
                submenus[label] = [.. subItems.Select(s => s.Trim()).Where(s => s.Length > 0)];
            }
            catch
            {
                // Submenu didn't open — skip.
            }
        }

        await _page.Keyboard.PressAsync("Escape");
        await _page.WaitForTimeoutAsync(100);
        return System.Text.Json.JsonSerializer.Serialize(new { menu = trimmed, submenus });
    }

    public void Dispose()
    {
        if (_disposed)
        { return; }

        _disposed = true;

        // Only tear down the browser/Playwright when this engine owns them. When created against an
        // externally-supplied context (the pool), the pool owns and disposes the shared browser —
        // closing it here would break sibling engines still using it.
        if (!_ownsBrowser)
        {
            return;
        }

        // ValueTask from DisposeAsync: complete synchronously or wait via task
        var disposeTask = _browser?.DisposeAsync();
        if (disposeTask.HasValue && !disposeTask.Value.IsCompleted)
        {
            disposeTask.Value.AsTask().GetAwaiter().GetResult();
        }
        _playwright?.Dispose();
    }
}
