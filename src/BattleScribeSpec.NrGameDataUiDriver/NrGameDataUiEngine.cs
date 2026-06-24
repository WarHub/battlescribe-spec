using System.Text.Json;
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
        _page = await context.NewPageAsync();
        _diagnostics = new NrGameDataUiDiagnostics(_page);
        _ui = new NrGameDataUiDriver(_page);
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
            var errors = await NrGameDataUiSetup.LoadAndOpenCatalogueAsync(_page, gameSystem, catalogues);
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

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null)
        => Run($"addEntry-{entryType}", () => _ui.AddEntryAsync(parentId, entryType, name));

    public void RemoveEntry(string entryId)
        => Run($"removeEntry", () => NrGameDataUiActions.RemoveEntryAsync(_page!, entryId));

    public void SetField(string entryId, string field, string? value)
        => Run($"setField-{field}", () => _ui.SetFieldAsync(entryId, field, value));

    public void SetCost(string entryId, string costTypeId, string? value)
        => Run($"setCost-{costTypeId}", () => _ui.SetCostAsync(entryId, costTypeId, value));

    public void SetCharacteristic(string entryId, string nameOrTypeId, string? value)
        => Run($"setCharacteristic-{nameOrTypeId}", () => _ui.SetCharacteristicAsync(entryId, nameOrTypeId, value));

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId)
        => Run($"addLink-{linkType}", () => _ui.AddLinkAsync(parentId, linkType, targetId));

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

        var files = ParseExportedFiles(await ExportLoadedFilesJsonAsync());
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "Reload: the NR Editor export produced no text XML files to reload.");
        }

        var reopenName = _openId is not null && _idToName.TryGetValue(_openId, out var name)
            ? name
            : _idToName.Values.FirstOrDefault()
                ?? throw new InvalidOperationException("Reload: no loaded file to reopen.");

        var errors = await NrGameDataUiSetup.ReloadFromXmlAsync(_page, BaseUrl, files, reopenName);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Reload failed: " + string.Join("; ", errors));
        }

        // The reset clears our spec-context marker; restore it for diagnostics.
        await _page.EvaluateAsync(
            "(specId) => { window.__bsspec_editor_ui = window.__bsspec_editor_ui || {}; window.__bsspec_editor_ui.specId = specId; }",
            _specId);
    }

    /// <summary>
    /// Extracts (fileName, xml) pairs from <see cref="ExportLoadedFilesJsonAsync"/>'s
    /// <c>{ files: { path: xml }, debug: [] }</c> payload, skipping any binary-marker entries.
    /// </summary>
    private static List<(string Name, string Xml)> ParseExportedFiles(string exportJson)
    {
        var result = new List<(string, string)>();
        using var doc = JsonDocument.Parse(exportJson);
        if (doc.RootElement.TryGetProperty("files", out var filesEl)
            && filesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in filesEl.EnumerateObject())
            {
                var xml = prop.Value.GetString();
                if (xml is null || xml.StartsWith("[binary:", StringComparison.Ordinal))
                { continue; }
                result.Add((Path.GetFileName(prop.Name), xml));
            }
        }

        return result;
    }

    private NrGameDataUiDriver _ui = null!;

    public GameDataState GetState()
        => GetStateAsync().GetAwaiter().GetResult();

    private async Task<GameDataState> GetStateAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        return await NrGameDataUiActions.ReadStateAsync(_page);
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
        => GetValidationErrorsAsync().GetAwaiter().GetResult();

    private async Task<IReadOnlyList<ValidationErrorState>> GetValidationErrorsAsync()
    {
        if (_page is null)
        { return []; }

        // Reference validation for the link-target rules the specs assert, read directly from
        // the NR Editor store: an entry link / catalogue link whose target does not resolve.
        var json = await _page.EvaluateAsync<string>("""
            () => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return '[]';
                    const editor = pinia._s.get('editor');
                    const systemId = new URLSearchParams(window.location.search).get('systemId');
                    const gsSys = editor?.gameSystems?.[systemId];
                    if (!gsSys) return '[]';

                    const cats = Object.values(gsSys.loadedCatalogues ?? {});
                    const catIds = new Set(Object.keys(gsSys.loadedCatalogues ?? {}));

                    // NR's reactive model has back-references (parent/catalogue) and shared arrays,
                    // so a naive recursion over every array can cycle and overflow the stack (which
                    // the catch below would swallow as "no errors"). Guard every descent with a seen-set.
                    const entryIds = new Set();
                    const seenCollect = new WeakSet();
                    const collect = (obj) => {
                        if (!obj || typeof obj !== 'object' || seenCollect.has(obj)) return;
                        seenCollect.add(obj);
                        if (typeof obj.id === 'string' && obj.id) entryIds.add(obj.id);
                        for (const k of Object.keys(obj)) {
                            const v = obj[k];
                            if (Array.isArray(v)) for (const it of v) collect(it);
                        }
                    };
                    for (const c of cats) collect(c);

                    const errors = [];
                    const seenWalk = new WeakSet();
                    const walk = (obj) => {
                        if (!obj || typeof obj !== 'object' || seenWalk.has(obj)) return;
                        seenWalk.add(obj);
                        for (const k of Object.keys(obj)) {
                            const v = obj[k];
                            if (!Array.isArray(v)) continue;
                            if (k === 'entryLinks') {
                                for (const el of v) {
                                    if (el && el.targetId && !entryIds.has(el.targetId)) {
                                        errors.push({ message: 'EntryLink must have a target that exists', entryId: el.id || null });
                                    }
                                }
                            }
                            if (k === 'catalogueLinks') {
                                for (const cl of v) {
                                    if (cl && cl.targetId && !catIds.has(cl.targetId)) {
                                        errors.push({ message: 'CatalogueLink must have a target that exists', entryId: cl.id || null });
                                    }
                                }
                            }
                            for (const it of v) walk(it);
                        }
                    };
                    for (const c of cats) walk(c);

                    return JSON.stringify(errors);
                } catch (e) {
                    return '[]';
                }
            }
            """);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var errors = new List<ValidationErrorState>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var message = el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var entryId = el.TryGetProperty("entryId", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String
                ? e.GetString()
                : null;
            errors.Add(new ValidationErrorState(message, EntryId: entryId));
        }
        return errors;
    }

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

        return await _page.EvaluateAsync<string>("""
            async () => {
                const debug = [];
                const files = {};
                const orig = globalThis.electron;
                // Stub the electron bridge so writeFile() forwards the serialized bytes to us.
                globalThis.electron = {
                    invoke: async (cmd, p, d) => {
                        if (cmd === 'saveFile') {
                            files[p] = (typeof d === 'string') ? d : '[binary:' + (d?.byteLength ?? d?.length ?? '?') + ']';
                        }
                        return undefined;
                    },
                };
                try {
                    const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                    const editor = pinia?._s?.get('editor');
                    const store = globalThis.$store || editor;
                    if (!store) { debug.push('no editor store'); return JSON.stringify({ files, debug }); }
                    const systemId = new URLSearchParams(location.search).get('systemId');
                    const gsSys = editor?.gameSystems?.[systemId];
                    debug.push('systemId=' + systemId + ' gsSys=' + !!gsSys + ' $store=' + !!globalThis.$store);

                    // Enumerate candidate file objects: the game system plus every loaded catalogue.
                    const seen = new Set();
                    const candidates = [];
                    const add = (o) => { if (o && typeof o === 'object' && !seen.has(o)) { seen.add(o); candidates.push(o); } };
                    add(gsSys?.gameSystem);
                    for (const c of Object.values(gsSys?.loadedCatalogues ?? {})) add(c);
                    debug.push('candidates=' + candidates.length);

                    for (const data of candidates) {
                        try {
                            if (!data.fullFilePath) {
                                data.fullFilePath = data.gameSystemId ? (data.id + '.cat') : 'system.gst';
                            }
                            await store.saveCatalogueInFiles(data);
                            debug.push('saved ' + data.fullFilePath + ' id=' + data.id);
                        } catch (e) {
                            debug.push('err id=' + (data && data.id) + ': ' + (e && e.message));
                        }
                    }
                    // Let any not-yet-awaited writes settle.
                    await new Promise((r) => setTimeout(r, 50));
                } catch (e) {
                    debug.push('fatal: ' + (e && e.message));
                } finally {
                    globalThis.electron = orig;
                }
                return JSON.stringify({ files, debug });
            }
            """);
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
        // ValueTask from DisposeAsync: complete synchronously or wait via task
        var disposeTask = _browser?.DisposeAsync();
        if (disposeTask.HasValue && !disposeTask.Value.IsCompleted)
        {
            disposeTask.Value.AsTask().GetAwaiter().GetResult();
        }
        _playwright?.Dispose();
    }
}
