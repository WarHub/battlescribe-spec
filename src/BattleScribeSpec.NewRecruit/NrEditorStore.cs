using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Engine-agnostic helpers that drive NewRecruit Editor's <b>real</b> Pinia store through Playwright:
/// opening the editor and checking the contracts the drivers depend on, serving the frozen static
/// bundle, loading game-system + catalogue XML through NR's own file import, reading state back from
/// <c>editor.gameSystems[systemId].loadedCatalogues</c>, exporting via NR's own serializer
/// (<c>saveCatalogueInFiles</c>), reloading, loading additional files, and reading reference-validation
/// errors.
///
/// <para>
/// Both NewRecruit GameData engines share this code: the store-direct
/// <see cref="NewRecruitGameDataEngine"/> (fast direct-JS mutations) and the UI-driven
/// <c>NrGameDataUiEngine</c> (mutations via real widget clicks). The only difference between them is
/// how they mutate — setup, state, export, reload, load and validation are identical, so they live
/// here. This class is in <c>BattleScribeSpec.NewRecruit</c> (which already references Playwright and is
/// referenced by the UI driver) so both can use it without a project dependency cycle.
/// </para>
/// </summary>
public static class NrEditorStore
{
    /// <summary>
    /// Ceiling for editor navigation / URL / store-ready waits. These are reactive waits — they
    /// resolve as soon as the condition is met — so the ceiling only needs to be generous enough to
    /// absorb CPU contention when several browser contexts load the editor in parallel (see the
    /// GameData UI engine pool). Tunable via <c>NR_NAV_TIMEOUT_MS</c>; defaults to 30s.
    /// </summary>
    private static readonly int NavTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("NR_NAV_TIMEOUT_MS"), out var v) && v > 0
            ? v
            : 30_000;

    /// <summary>
    /// Ceiling for the import view's controls to render — a local view with nothing to fetch, so a
    /// miss means the control is gone, not slow. It is also the bound on how long a moved app takes
    /// to fail <see cref="OpenEditorAsync"/>.
    /// </summary>
    private const int ImportViewTimeoutMs = 10_000;

    /// <summary>
    /// The import page's "add system" menu button. The file input it holds is rendered only while the
    /// menu is open, and it is the only input on the page once any system is stored; the page's
    /// empty-state input is gone by then.
    /// </summary>
    private const string ImportMenuSelector = "button.add";

    /// <summary>
    /// How long an import gets to show up in the store before it is read as declined. Generous on
    /// purpose: a file NR rejects is normally settled in milliseconds by its console error, so this
    /// budget is only ever spent on a file it is slowly accepting — and calling one of those a
    /// refusal would put a falsehood in a spec.
    /// </summary>
    private const int ImportPollAttempts = 60;
    private const int ImportPollIntervalMs = 250;

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html",
        [".js"] = "application/javascript",
        [".mjs"] = "application/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".eot"] = "application/vnd.ms-fontobject",
        [".map"] = "application/json",
        [".webp"] = "image/webp",
        [".txt"] = "text/plain",
        [".xml"] = "application/xml",
    };

    // ===== Frozen static-file serving =====

    /// <summary>
    /// Whether a resolved request path stays inside the served root — the directory-escape guard for
    /// <see cref="SetupStaticFileRoutingAsync"/>, extracted so it is testable rather than argued about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neither casing rule is correct on both platforms, so neither is hard-coded.</b> This used to
    /// be <c>fullPath.StartsWith(root, OrdinalIgnoreCase)</c>, which is right on NTFS and
    /// over-permissive on ext4 — there <c>/tmp/STATIC/x</c> is a genuinely different directory that a
    /// guard rooted at <c>/tmp/static/</c> waves straight through (#311). Switching to
    /// <c>Ordinal</c> is the mirror-image bug: on Windows those two strings name one directory, and a
    /// legitimate request would 403.
    /// </para>
    /// <para>
    /// <see cref="Path.GetRelativePath"/> applies the running platform's own rule, and "outside" is
    /// then spelled as what it means — a relative path that has to climb out, or one that could not be
    /// made relative at all (a different Windows volume comes back rooted).
    /// </para>
    /// </remarks>
    /// <param name="root">The served directory, absolute and ending in a directory separator.</param>
    /// <param name="fullPath">The absolute path a request resolved to.</param>
    internal static bool IsInsideRoot(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sets up Playwright route interception to serve NR Editor static files from a local directory.
    /// Strips the /nr-editor/ URL prefix when mapping to file paths, handles SPA fallback.
    /// </summary>
    public static async Task SetupStaticFileRoutingAsync(IPage page, string staticDir)
    {
        var normalizedDir = Path.GetFullPath(staticDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        await page.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var url = new Uri(request.Url);
            var path = Uri.UnescapeDataString(url.AbsolutePath);
            path = path.Replace('\\', '/');

            const string basePrefix = "/nr-editor/";
            if (path.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[basePrefix.Length..];
            }
            else if (path == "/nr-editor")
            {
                path = "";
            }
            else if (path.StartsWith('/'))
            {
                path = path[1..];
            }

            if (string.IsNullOrEmpty(path) || path == "/")
            {
                path = "index.html";
            }

            var fullPath = Path.GetFullPath(Path.Combine(normalizedDir, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInsideRoot(normalizedDir, fullPath))
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 403, ContentType = "text/plain", Body = "Forbidden" });
                return;
            }

            if (File.Exists(fullPath))
            {
                var ext = Path.GetExtension(fullPath);
                var contentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
                var body = await File.ReadAllBytesAsync(fullPath);
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = contentType, BodyBytes = body });
            }
            else
            {
                var ext = Path.GetExtension(fullPath);
                var isStaticAsset = !string.IsNullOrEmpty(ext) && ext != ".html";
                if (!isStaticAsset)
                {
                    var indexPath = Path.Combine(normalizedDir, "index.html");
                    if (File.Exists(indexPath))
                    {
                        var body = await File.ReadAllBytesAsync(indexPath);
                        await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html", BodyBytes = body });
                        return;
                    }
                }
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404, ContentType = "text/plain", Body = "Not Found" });
            }
        });
    }

    // ===== Opening the editor, and the contracts the drivers depend on =====

    /// <summary>
    /// Makes the page an ordinary user's session rather than an automated one. A browser that reports
    /// <c>navigator.webdriver</c> gets the editor's WebMCP bridge switched on without asking, and the
    /// bridge then probes a range of loopback ports for a local relay — again at intervals for as long
    /// as the page lives. Each probe that finds no relay is a <c>console.error</c>, which is exactly
    /// the signal <see cref="LoadFileAsync"/> reads as the importer refusing a file; and a probe that
    /// does find one hands the page under test to whatever is listening on this machine.
    /// </summary>
    private const string OrdinarySessionScript =
        "Object.defineProperty(Navigator.prototype, 'webdriver', { get: () => false });";

    /// <summary>
    /// Opens the editor at <paramref name="baseUrl"/> and checks it still offers what both GameData
    /// engines depend on, failing in seconds — once, from the engine's creation — rather than in
    /// every spec's setup after a navigation timeout.
    /// <para>
    /// The editor is a moving target: a deployment replaces the whole app, and nothing obliges it to
    /// keep a route, a store action or a control. Each contract below is one the drivers use; a
    /// missing one is named, with the fix, instead of surfacing later as an unexplained timeout.
    /// </para>
    /// </summary>
    public static async Task OpenEditorAsync(IPage page, string baseUrl)
    {
        await page.AddInitScriptAsync(OrdinarySessionScript);
        await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });
        await WaitForAppAsync(page, baseUrl);

        var missing = await page.EvaluateAsync<string[]>(
            """
            () => {
                const gp = document.querySelector('#__nuxt').__vue_app__.config.globalProperties;
                const editor = gp.$pinia._s.get('editor');
                const routes = new Set(gp.$router.getRoutes().map(r => r.name));
                const missing = [];
                if (!editor) missing.push("a Pinia store 'editor'");
                else {
                    if (typeof editor.gameSystems !== 'object') missing.push("editor.gameSystems (the loaded files, read by every state read)");
                    if (typeof editor.goto_catalogue !== 'function') missing.push('editor.goto_catalogue (opens a file)');
                    if (typeof editor.saveCatalogueInFiles !== 'function') missing.push('editor.saveCatalogueInFiles (the export)');
                }
                for (const name of ['index', 'system', 'catalogue']) {
                    if (!routes.has(name)) missing.push(`a route named '${name}'`);
                }
                return missing;
            }
            """);
        if (missing.Length == 0)
        {
            try
            {
                await GoToImportAsync(page);
                await PushRouteAsync(page, "index");
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                missing = [$"a file import on the 'system' route — an input[type=file], or a '{ImportMenuSelector}' menu "
                    + $"holding one ({Compact(ex.Message)})"];
            }
        }

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"The NR Editor at {baseUrl} no longer offers what the GameData drivers depend on: "
                + string.Join("; ", missing) + ". The app has moved: re-pin \"nr-editor\" in testdata.json to the "
                + "deployment the drivers were ported to, or port NrEditorStore and the NR GameData UI driver to "
                + "this one (AGENTS.md, \"NR Editor frozen tests\").");
        }
    }

    /// <summary>Waits for the editor's Vue app to mount with Pinia — the first contract, and the one every read needs.</summary>
    private static async Task WaitForAppAsync(IPage page, string baseUrl)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => !!document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia",
                null,
                new PageWaitForFunctionOptions { Timeout = NavTimeoutMs });
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"The NR Editor at {baseUrl} never mounted a Vue app with Pinia on #__nuxt: {Compact(ex.Message)}", ex);
        }
    }

    /// <summary>Pushes a named route through the editor's own router, client-side, so the in-memory stores survive.</summary>
    private static Task PushRouteAsync(IPage page, string name) =>
        page.EvaluateAsync(
            "(name) => document.querySelector('#__nuxt').__vue_app__.config.globalProperties.$router.push({ name })",
            name);

    /// <summary>
    /// Puts the page on the editor's import view (the <c>system</c> route) and returns its file input.
    /// The input in the add-system menu exists only while the menu is open, and the page is kept alive
    /// between visits, so the menu may still be open from the last import — it is clicked only when
    /// no input is there, since a click on an open menu closes it.
    /// </summary>
    private static async Task<ILocator> GoToImportAsync(IPage page)
    {
        await PushRouteAsync(page, "system");
        var menu = page.Locator(ImportMenuSelector);
        await menu.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ImportViewTimeoutMs });

        var input = page.Locator("input[type=file]");
        if (await input.CountAsync() == 0)
        {
            await menu.ClickAsync(new LocatorClickOptions { Timeout = ImportViewTimeoutMs });
        }

        await input.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = ImportViewTimeoutMs });
        return input.First;
    }

    /// <summary>
    /// Feeds files through the editor's import, so its real parse (<c>convertToJson</c>) and its import
    /// handler run and populate the stores exactly as a user's import does, and returns once the
    /// handler has finished: it ends by routing away from the import view, so leaving it is the
    /// signal. Returns an error when that never happens.
    /// </summary>
    private static async Task<string?> ImportAsync(IPage page, IReadOnlyList<FilePayload> payloads)
    {
        var input = await GoToImportAsync(page);
        await input.SetInputFilesAsync(payloads);
        try
        {
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#__nuxt').__vue_app__.config.globalProperties.$router.currentRoute.value.name !== 'system'",
                null,
                new PageWaitForFunctionOptions { Timeout = NavTimeoutMs });
            return null;
        }
        catch (TimeoutException ex)
        {
            return $"NR Editor's import of {string.Join(", ", payloads.Select(p => p.Name))} never finished — "
                + $"its handler routes away from the import view when it does: {Compact(ex.Message)}";
        }
    }

    private static List<FilePayload> ToPayloads(IEnumerable<(string Name, string Xml)> files) =>
        [.. files.Select(f => new FilePayload { Name = f.Name, MimeType = "application/xml", Buffer = Encoding.UTF8.GetBytes(f.Xml) })];

    // ===== Setup / navigation (NR's real import + open) =====

    /// <summary>
    /// Loads the spec's game system and catalogues through the editor's import (<see cref="ImportAsync"/>),
    /// then opens the target file: the last catalogue, or the game system itself when there are none.
    /// Returns [] on success.
    /// </summary>
    public static async Task<IReadOnlyList<string>> LoadAndOpenCatalogueAsync(
        IPage page,
        ProtocolGameSystem gameSystem,
        ProtocolCatalogue[] catalogues)
    {
        // Generate BattleScribe XML from protocol types. GenerateAllCatalogueXml requires at
        // least one catalogue, so skip it for game-system-only specs.
        var files = new List<(string, string)> { ("system.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)) };
        if (catalogues.Length > 0)
        {
            files.AddRange(CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues));
        }

        var importError = await ImportAsync(page, ToPayloads(files));
        if (importError is not null)
        {
            return [importError];
        }

        var openError = await OpenAsync(page, gameSystem.Id, catalogues.Length > 0 ? catalogues[^1].Id : gameSystem.Id);
        return openError is null ? [] : [openError];
    }

    /// <summary>
    /// Switches the editor to a different loaded file (catalogue or game system) by id. Used by the
    /// spec <c>openFile</c> action so multi-catalogue specs can declare the active file.
    /// </summary>
    public static async Task NavigateToFileAsync(IPage page, string id)
    {
        // The system the file is filed under: the open one when it holds the file, else any that does.
        var systemId = await page.EvaluateAsync<string?>(
            """
            (id) => {
                const ed = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                const holds = (key, sys) => key === id || sys?.gameSystem?.gameSystem?.id === id
                    || Object.entries(sys?.catalogueFiles ?? {}).some(([k, f]) => k === id || (f?.catalogue ?? f)?.id === id);
                const open = new URLSearchParams(location.search).get('systemId');
                if (open && holds(open, ed?.gameSystems?.[open])) return open;
                return Object.entries(ed?.gameSystems ?? {}).find(([key, sys]) => holds(key, sys))?.[0] ?? null;
            }
            """, id);

        _ = systemId ?? throw new InvalidOperationException(
            $"NR Editor: no loaded game system holds a file with id '{id}', so it cannot be opened.");

        var error = await OpenAsync(page, systemId, id);
        if (error is not null)
        {
            throw new InvalidOperationException($"NR Editor: could not open file id '{id}'. {error}");
        }
    }

    /// <summary>
    /// Opens a loaded file through the editor's own <c>goto_catalogue</c> — the action its links use —
    /// which routes to the catalogue view and resolves once that view has loaded the file. Returns null
    /// on success, or what went wrong.
    /// <para>
    /// <c>goto_catalogue</c> decides it is already there from the current route's <c>id</c> query alone,
    /// and an import leaves <c>/?id=&lt;system&gt;</c> behind — so opening that game system straight after
    /// importing it was a silent no-op. Leaving for the plain list first keeps the comparison honest.
    /// Its promise never settles when the load throws, hence the bound.
    /// </para>
    /// </summary>
    private static async Task<string?> OpenAsync(IPage page, string systemId, string id)
    {
        var outcome = await page.EvaluateAsync<string>(
            """
            async ([systemId, id, timeoutMs]) => {
                const gp = document.querySelector('#__nuxt').__vue_app__.config.globalProperties;
                const editor = gp.$pinia._s.get('editor');
                if (gp.$router.currentRoute.value.name !== 'catalogue') await gp.$router.push({ name: 'index' });
                const opened = Promise.resolve(editor.goto_catalogue(id, systemId)).then(() => 'opened');
                const late = new Promise(r => setTimeout(() => r('timeout'), timeoutMs));
                const result = await Promise.race([opened, late]);
                const params = new URLSearchParams(location.search);
                if (params.get('id') !== id) return `${result}; the editor is at ${location.href}`;
                if (!editor.gameSystems?.[systemId]?.loadedCatalogues?.[id]) {
                    return `${result}; editor.gameSystems['${systemId}'].loadedCatalogues has no '${id}' `
                        + `(loaded: ${Object.keys(editor.gameSystems?.[systemId]?.loadedCatalogues ?? {}).join(', ') || 'none'})`;
                }
                return result;
            }
            """,
            new object[] { systemId, id, NavTimeoutMs });

        return outcome == "opened" ? null : $"Opening '{id}' (system '{systemId}') did not finish: {outcome}.";
    }

    /// <summary>First line of an exception message, length-capped — Playwright appends a long call log.</summary>
    private static string Compact(string message)
    {
        var span = message.AsSpan();
        var end = span.IndexOfAny('\r', '\n');
        var line = (end >= 0 ? span[..end] : span).Trim().ToString();
        return line.Length > 200 ? string.Concat(line.AsSpan(0, 200), "…") : line;
    }

    /// <summary>
    /// Clears what the spec left behind and reloads the editor empty. Called between specs and before
    /// a reload.
    /// <para>
    /// <b>The editor persists every import, so a reload alone brings it back.</b> Imports are written to
    /// the origin's IndexedDB and the imported systems are remembered in its settings, and the list
    /// page restores them on load — asynchronously, so a restored system from the last spec can land
    /// on top of the next spec's import of the same id. The origin's storage is cleared before the
    /// reload; the stores themselves are rebuilt by the reload.
    /// </para>
    /// </summary>
    public static async Task CleanupCatalogueAsync(IPage page, string editorBaseUrl)
    {
        await page.EvaluateAsync("""
            async () => {
                // A blocked delete completes once the page's own connection closes, which the reload
                // below does, and before the reloaded page can open the database again.
                const dbs = await indexedDB.databases();
                await Promise.all(dbs.map(d => new Promise(resolve => {
                    const request = indexedDB.deleteDatabase(d.name);
                    request.onsuccess = request.onerror = request.onblocked = () => resolve();
                })));
                localStorage.clear();
                sessionStorage.clear();
            }
            """);

        await page.GotoAsync(editorBaseUrl);
        await WaitForAppAsync(page, editorBaseUrl);
    }

    /// <summary>
    /// Reloads the editor from already-serialized BattleScribe XML — typically the editor's own export
    /// of the current, mutated state — and reopens the file <paramref name="reopenId"/>. Clears the
    /// editor, feeds the XML through the same import the initial load uses (so NR's real parse runs),
    /// then opens the file again. Used by round-trip specs.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReloadFromXmlAsync(
        IPage page,
        string editorBaseUrl,
        IReadOnlyList<(string Name, string Xml)> files,
        string reopenId)
    {
        if (files.Count == 0)
        {
            return ["Reload: no exported XML files to reload"];
        }

        await CleanupCatalogueAsync(page, editorBaseUrl);

        // GST first, then CATs — mirrors the initial upload ordering.
        var importError = await ImportAsync(page, ToPayloads(
            files.OrderByDescending(f => f.Name.EndsWith(".gst", StringComparison.OrdinalIgnoreCase))));
        if (importError is not null)
        {
            return [importError];
        }

        try
        {
            await NavigateToFileAsync(page, reopenId);
            return [];
        }
        catch (InvalidOperationException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>
    /// Load a single additional file (catalogue or game system) from XML WITHOUT resetting existing
    /// state, then open it. Used by <c>openFile</c> with a source.
    /// <para>
    /// <b>A file NR declines is reported as a refusal, in NR's own words.</b> Its importer parses
    /// each uploaded file and then keeps only what came back as a catalogue or a game system
    /// (<c>files.filter(f =&gt; f.catalogue)</c>); anything else is dropped without a dialog, a toast,
    /// or any store state — the single place it says so is <c>console.error</c>, from the one
    /// <c>catch</c> in its upload handler. So the refusal is detected structurally, by the file not
    /// arriving, and described with the console error NR emitted while it was being read.
    /// </para>
    /// <para>
    /// Detection is by <em>diffing the file set</em> rather than by looking for the id the caller
    /// expects: a payload broken enough to be refused is usually too broken to have a readable root
    /// id, and asking for one first meant the driver rejected those files before NR ever saw them.
    /// Before this, a declined file surfaced 30 seconds later as a navigation timeout describing our
    /// own driver rather than anything NR did, which is why <c>newrecruit-ui</c> could not carry a
    /// load-failure spec (#268).
    /// </para>
    /// </summary>
    public static async Task<NrImportOutcome> LoadFileAsync(IPage page, string fileName, string xml)
    {
        var errors = new List<string>();

        // Client-side, so the in-memory stores are untouched.
        var input = await GoToImportAsync(page);

        var before = await ReadImportedFilesAsync(page);

        // NR only ever writes to console.error on this path, so collect for the upload's duration.
        var consoleErrors = new List<string>();
        void OnConsole(object? sender, IConsoleMessage message)
        {
            if (message.Type == "error")
            {
                consoleErrors.Add(message.Text);
            }
        }

        page.Console += OnConsole;
        NrImportedFile? added;
        try
        {
            await input.SetInputFilesAsync(ToPayloads([(fileName, xml)]));

            added = await WaitForImportedFileAsync(page, before, consoleErrors);
        }
        finally
        {
            page.Console -= OnConsole;
        }

        if (added is null)
        {
            errors.Add(DescribeImportRefusal(fileName, consoleErrors));
            return new NrImportOutcome(null, errors);
        }

        var openError = await OpenAsync(page, added.SystemKey, added.Id);
        if (openError is not null)
        {
            errors.Add($"NR Editor could not open loaded file '{added.Id}' ({added.Name}): {openError}");
        }

        return new NrImportOutcome(added, errors);
    }

    /// <summary>What an import did: the file NR took (null when it took none) and any errors.</summary>
    public sealed record NrImportOutcome(NrImportedFile? Imported, IReadOnlyList<string> Errors);

    /// <summary>One file NR's importer has taken: which system it filed it under, its id and name.</summary>
    public sealed record NrImportedFile(string SystemKey, string Id, string Name, bool IsGameSystem);

    /// <summary>
    /// Every catalogue and game system NR currently holds, across all systems. Read as a set so an
    /// import can be detected by what it adds, without knowing what to look for.
    /// <para>
    /// Returned as delimited text rather than JSON on purpose: the fields are read positionally, so
    /// there is no property-name mapping to get wrong. It was wrong — camelCase keys against a
    /// PascalCase record deserialized to a list of empty rows, every row hashed to the same key, and
    /// no import was ever detected as new. A silent shape mismatch, and it read as "NR refused this
    /// file" for every file.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<NrImportedFile>> ReadImportedFilesAsync(IPage page)
    {
        var text = await page.EvaluateAsync<string>(
            """
            () => {
              const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
              const ed = pinia?._s?.get('editor');
              const rows = [];
              const clean = v => String(v ?? '').replace(/[\t\n\r]/g, ' ');
              for (const [key, sys] of Object.entries(ed?.gameSystems ?? {})) {
                const gs = sys?.gameSystem?.gameSystem;
                if (gs?.id) rows.push([clean(key), clean(gs.id), clean(gs.name), 'gst'].join('\t'));
                for (const [fileId, file] of Object.entries(sys?.catalogueFiles ?? {})) {
                  const cat = file?.catalogue ?? file;
                  rows.push([clean(key), clean(cat?.id ?? fileId), clean(cat?.name), 'cat'].join('\t'));
                }
              }
              return rows.join('\n');
            }
            """);

        var result = new List<NrImportedFile>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 4)
            {
                result.Add(new NrImportedFile(parts[0], parts[1], parts[2], parts[3] == "gst"));
            }
        }

        return result;
    }

    /// <summary>
    /// Waits for NR to take the uploaded file, returning what it took, or null when it declined.
    /// <para>
    /// Two signals, because one is not enough. NR writes <c>console.error</c> from the single
    /// <c>catch</c> in its upload handler the moment a file fails to parse, so an error settles the
    /// question at once and the wait ends there. Absent that, the only evidence is the file arriving
    /// in the store — and that is <b>slow</b>: the import survives a round trip through IndexedDB and
    /// Vue reactivity, and was measured arriving more than three seconds after the upload. A short
    /// budget read those late arrivals as refusals, which is the worse error of the two: it would
    /// have had a spec record that NR rejects a file it accepts.
    /// </para>
    /// </summary>
    private static async Task<NrImportedFile?> WaitForImportedFileAsync(
        IPage page, IReadOnlyList<NrImportedFile> before, IReadOnlyList<string> consoleErrors)
    {
        var seen = before.Select(Key).ToHashSet(StringComparer.Ordinal);
        for (var attempt = 0; attempt < ImportPollAttempts; attempt++)
        {
            await page.WaitForTimeoutAsync(ImportPollIntervalMs);
            if (consoleErrors.Count > 0)
            {
                return null;
            }

            var after = await ReadImportedFilesAsync(page);
            var added = after.FirstOrDefault(f => !seen.Contains(Key(f)));
            if (added is not null)
            {
                return added;
            }
        }

        return null;

        // Name is part of the key because an import may REPLACE a file rather than add one: a payload
        // carrying an id the editor already holds is a legitimate load, and a key of system+id alone
        // cannot see it arrive. Two files identical in system, id and name are indistinguishable here,
        // and a load of one is the one import this cannot detect.
        static string Key(NrImportedFile f) => $"{f.SystemKey}/{f.Id}/{f.Name}/{f.IsGameSystem}";
    }

    /// <summary>
    /// The refusal message. NR's own console error is quoted when it produced one; when it produced
    /// none — its importer drops an unparsed file by filtering, not by throwing — the message says
    /// exactly that rather than inventing a reason NR never gave.
    /// </summary>
    private static string DescribeImportRefusal(string fileName, IReadOnlyList<string> consoleErrors)
    {
        if (consoleErrors.Count == 0)
        {
            return $"NewRecruit's importer did not take '{fileName}': no catalogue or game system was "
                + "added, and it reported nothing. Its upload handler keeps only the files that parsed "
                + "into a catalogue or a game system and silently drops the rest.";
        }

        return $"NewRecruit's importer did not take '{fileName}', reporting: "
            + string.Join(" | ", consoleErrors.Select(FirstLine));

        static string FirstLine(string text)
        {
            var end = text.IndexOfAny(['\r', '\n']);
            return end < 0 ? text.Trim() : text[..end].Trim();
        }
    }

    /// <summary>Reads the id of the catalogue/game-system currently open in the editor (URL <c>id</c> param).</summary>
    public static async Task<string> GetCurrentCatalogueIdAsync(IPage page)
        => await page.EvaluateAsync<string>(
            "() => new URLSearchParams(window.location.search).get('id') ?? ''") ?? "";

    // ===== State read (from the real loadedCatalogues store) =====

    /// <summary>
    /// Reads the current <see cref="GameDataState"/> from NR Editor's Pinia editorStore
    /// (<c>editor.gameSystems[systemId].loadedCatalogues</c>). Called after each mutation to assert
    /// expected state in specs.
    /// </summary>
    public static async Task<GameDataState> ReadStateAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("""
            () => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return JSON.stringify({ error: 'Pinia not found' });

                    // Resolve the currently open catalogue and system IDs from the page URL:
                    // .../catalogue?systemId=gs-1&id=cat-1
                    const params = new URLSearchParams(window.location.search);
                    const catId = params.get('id');
                    const systemId = params.get('systemId');
                    if (!catId) {
                        return JSON.stringify({ error: 'No catalogue ID in URL — not on catalogue page? URL: ' + window.location.href });
                    }

                    // The actual catalogue data lives in editor.gameSystems[systemId].loadedCatalogues[catId].
                    // The 'catalogues' Pinia store is a dependency tracker (not the data store).
                    const editorStore = pinia._s.get('editor');
                    if (!editorStore) {
                        return JSON.stringify({ error: 'editor store not found. Available: [' + [...pinia._s.keys()].join(', ') + ']' });
                    }

                    const gsSys = editorStore.gameSystems?.[systemId];
                    if (!gsSys) {
                        return JSON.stringify({ error: `Game system '${systemId}' not found in editor.gameSystems` });
                    }

                    const loaded = gsSys.loadedCatalogues ?? {};
                    if (!loaded[catId] && !loaded[systemId]) {
                        return JSON.stringify({ error: `Catalogue '${catId}' not in loadedCatalogues for system '${systemId}'` });
                    }

                    const gameSystemData = loaded[systemId] ?? null;

                    // Format a numeric value the way BattleScribe does (trim trailing .0).
                    const formatNum = (v) => {
                        const n = Number(v);
                        return Number.isFinite(n) && Number.isInteger(n) ? String(n) : String(v);
                    };

                    // Helper to serialize an entry node
                    const serializeEntry = (entry, entryType) => {
                        if (!entry || typeof entry !== 'object') return null;
                        const result = {
                            id: entry.id || '',
                            name: entry.name || '',
                            entryType: entryType || entry.type || '',
                            hidden: !!entry.hidden,
                            children: [],
                            fields: {},
                        };

                        // Container arrays are excluded by the Array.isArray check below, so the skip
                        // set only needs identity/internal keys. (A Repeat node's scalar `repeats`
                        // count must survive even though `repeats` is also a child-container name.)
                        const skipKeys = new Set(['id', 'name', 'hidden', 'parent', 'catalogue',
                            'attributes', 'costs', 'characteristics']);

                        for (const [key, val] of Object.entries(entry)) {
                            if (skipKeys.has(key) || key.startsWith('$') || key.startsWith('__')) continue;
                            if (typeof val === 'function') continue;
                            if (val !== null && val !== undefined && !Array.isArray(val) && typeof val !== 'object') {
                                // NR stores the import flag as 'import' but the spec protocol uses 'imported'
                                const fieldKey = key === 'import' ? 'imported' : key;
                                result.fields[fieldKey] = String(val);
                            }
                        }

                        // Costs / characteristics serialize as composite "cost:<typeId>" / "char:<name>"
                        // (NR keeps the characteristic value in the $text field).
                        if (Array.isArray(entry.costs)) {
                            for (const c of entry.costs) {
                                if (c && c.typeId != null) result.fields['cost:' + c.typeId] = formatNum(c.value);
                            }
                        }
                        if (Array.isArray(entry.characteristics)) {
                            for (const ch of entry.characteristics) {
                                if (ch && ch.name) result.fields['char:' + ch.name] = String(ch.$text ?? ch.value ?? '');
                            }
                        }

                        // BattleScribe reference engine's fixed container order, so positional
                        // child assertions match the BS anchors.
                        const childContainers = ['selectionEntries', 'selectionEntryGroups', 'entryLinks',
                            'rules', 'profiles', 'infoGroups', 'infoLinks', 'categoryLinks',
                            'constraints', 'modifiers', 'modifierGroups', 'conditions', 'conditionGroups',
                            'repeats', 'forceEntries', 'categoryEntries', 'characteristicTypes',
                            'associations', 'attributeTypes', 'localConditionGroups'];

                        for (const ck of childContainers) {
                            const items = entry[ck];
                            if (Array.isArray(items)) {
                                const singularType = ck.replace(/ies$/, 'y').replace(/s$/, '');
                                for (const item of items) {
                                    const child = serializeEntry(item, singularType);
                                    if (child) result.children.push(child);
                                }
                            }
                        }

                        return result;
                    };

                    const serializeContainer = (container) => {
                        if (!container) return null;
                        const r = {
                            id: container.id || '',
                            name: container.name || '',
                            gameSystemId: container.gameSystemId || container.id_game_system || '',
                            fields: {},
                            selectionEntries: [],
                            entryLinks: [],
                            rules: [],
                            sharedSelectionEntries: [],
                            sharedSelectionEntryGroups: [],
                            sharedRules: [],
                            sharedProfiles: [],
                            sharedInfoGroups: [],
                            forceEntries: [],
                            categoryEntries: [],
                            publications: [],
                            costTypes: [],
                            profileTypes: [],
                            catalogueLinks: [],
                            sharedForceEntries: [],
                            sharedAssociations: [],
                        };

                        // Root metadata fields (revision, authorName, library, …).
                        for (const [key, val] of Object.entries(container)) {
                            if (key === 'id' || key === 'name') continue;
                            if (val !== null && val !== undefined && !Array.isArray(val) && typeof val !== 'object') {
                                r.fields[key] = String(val);
                            }
                        }

                        const mappings = [
                            ['selectionEntries', 'selectionEntries', 'selectionEntry'],
                            ['entryLinks', 'entryLinks', 'entryLink'],
                            ['rules', 'rules', 'rule'],
                            ['sharedSelectionEntries', 'sharedSelectionEntries', 'selectionEntry'],
                            ['sharedSelectionEntryGroups', 'sharedSelectionEntryGroups', 'selectionEntryGroup'],
                            ['sharedRules', 'sharedRules', 'rule'],
                            ['sharedProfiles', 'sharedProfiles', 'profile'],
                            ['sharedInfoGroups', 'sharedInfoGroups', 'infoGroup'],
                            ['forceEntries', 'forceEntries', 'forceEntry'],
                            ['categoryEntries', 'categoryEntries', 'categoryEntry'],
                            ['publications', 'publications', 'publication'],
                            ['costTypes', 'costTypes', 'costType'],
                            ['profileTypes', 'profileTypes', 'profileType'],
                            ['catalogueLinks', 'catalogueLinks', 'catalogueLink'],
                            ['sharedForceEntries', 'sharedForceEntries', 'forceEntry'],
                            ['sharedAssociations', 'sharedAssociations', 'association'],
                        ];

                        for (const [srcKey, destKey, entryType] of mappings) {
                            const items = container[srcKey];
                            if (Array.isArray(items)) {
                                r[destKey] = items.map(e => serializeEntry(e, entryType)).filter(Boolean);
                            }
                        }
                        return r;
                    };

                    // Surface every loaded catalogue (multi-catalogue specs assert on a catalogue
                    // other than the one the editor happens to have open); the system-id entry is
                    // the game system, not a catalogue.
                    const state = { catalogues: [], gameSystem: null };
                    if (gameSystemData) state.gameSystem = serializeContainer(gameSystemData);
                    state.catalogues = Object.entries(loaded)
                        .filter(([k]) => k !== systemId)
                        .map(([, c]) => serializeContainer(c))
                        .filter(Boolean);

                    return JSON.stringify(state);
                } catch (e) {
                    return JSON.stringify({ error: 'ReadState error: ' + e.message });
                }
            }
            """);

        return DeserializeState(json);
    }

    private static GameDataState DeserializeState(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorProp))
        {
            throw new InvalidOperationException($"ReadState failed: {errorProp.GetString()}");
        }

        var catalogues = new List<CatalogueDataState>();
        if (root.TryGetProperty("catalogues", out var catsEl))
        {
            foreach (var catEl in catsEl.EnumerateArray())
            { catalogues.Add(DeserializeCatalogue(catEl)); }
        }

        GameSystemDataState? gameSystem = null;
        if (root.TryGetProperty("gameSystem", out var gsEl) && gsEl.ValueKind != JsonValueKind.Null)
        { gameSystem = DeserializeGameSystem(gsEl); }

        return new GameDataState { GameSystem = gameSystem, Catalogues = catalogues };
    }

    private static CatalogueDataState DeserializeCatalogue(JsonElement el) =>
        new()
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Name = el.GetProperty("name").GetString() ?? "",
            GameSystemId = el.GetProperty("gameSystemId").GetString() ?? "",
            Fields = DeserializeFields(el),
            SelectionEntries = DeserializeEntryList(el, "selectionEntries"),
            EntryLinks = DeserializeEntryList(el, "entryLinks"),
            Rules = DeserializeEntryList(el, "rules"),
            SharedSelectionEntries = DeserializeEntryList(el, "sharedSelectionEntries"),
            SharedSelectionEntryGroups = DeserializeEntryList(el, "sharedSelectionEntryGroups"),
            SharedRules = DeserializeEntryList(el, "sharedRules"),
            SharedProfiles = DeserializeEntryList(el, "sharedProfiles"),
            SharedInfoGroups = DeserializeEntryList(el, "sharedInfoGroups"),
            SharedForceEntries = DeserializeEntryList(el, "sharedForceEntries"),
            SharedAssociations = DeserializeEntryList(el, "sharedAssociations"),
            ForceEntries = DeserializeEntryList(el, "forceEntries"),
            CategoryEntries = DeserializeEntryList(el, "categoryEntries"),
            Publications = DeserializeEntryList(el, "publications"),
            CostTypes = DeserializeEntryList(el, "costTypes"),
            ProfileTypes = DeserializeEntryList(el, "profileTypes"),
            CatalogueLinks = DeserializeEntryList(el, "catalogueLinks"),
        };

    private static GameSystemDataState DeserializeGameSystem(JsonElement el) =>
        new()
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Name = el.GetProperty("name").GetString() ?? "",
            Fields = DeserializeFields(el),
            SelectionEntries = DeserializeEntryList(el, "selectionEntries"),
            EntryLinks = DeserializeEntryList(el, "entryLinks"),
            Rules = DeserializeEntryList(el, "rules"),
            SharedSelectionEntries = DeserializeEntryList(el, "sharedSelectionEntries"),
            SharedSelectionEntryGroups = DeserializeEntryList(el, "sharedSelectionEntryGroups"),
            SharedRules = DeserializeEntryList(el, "sharedRules"),
            SharedProfiles = DeserializeEntryList(el, "sharedProfiles"),
            SharedInfoGroups = DeserializeEntryList(el, "sharedInfoGroups"),
            ForceEntries = DeserializeEntryList(el, "forceEntries"),
            CategoryEntries = DeserializeEntryList(el, "categoryEntries"),
            CostTypes = DeserializeEntryList(el, "costTypes"),
            ProfileTypes = DeserializeEntryList(el, "profileTypes"),
            Publications = DeserializeEntryList(el, "publications"),
        };

    private static IReadOnlyDictionary<string, string?>? DeserializeFields(JsonElement el)
    {
        if (!el.TryGetProperty("fields", out var fieldsEl) || fieldsEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = new Dictionary<string, string?>();
        foreach (var prop in fieldsEl.EnumerateObject())
        {
            fields[prop.Name] = prop.Value.GetString();
        }
        return fields.Count > 0 ? fields : null;
    }

    private static IReadOnlyList<DataEntryState> DeserializeEntryList(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        { return []; }

        var entries = new List<DataEntryState>();
        foreach (var el in arr.EnumerateArray())
        { entries.Add(DeserializeEntry(el)); }
        return entries;
    }

    private static DataEntryState DeserializeEntry(JsonElement el)
    {
        var fields = new Dictionary<string, string?>();
        if (el.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in fieldsEl.EnumerateObject())
            { fields[prop.Name] = prop.Value.GetString(); }
        }

        var children = new List<DataEntryState>();
        if (el.TryGetProperty("children", out var childrenEl) && childrenEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var childEl in childrenEl.EnumerateArray())
            { children.Add(DeserializeEntry(childEl)); }
        }

        return new DataEntryState
        {
            Id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            Name = el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            EntryType = el.TryGetProperty("entryType", out var typeEl) ? typeEl.GetString() ?? "" : "",
            Hidden = el.TryGetProperty("hidden", out var hiddenEl) && hiddenEl.GetBoolean(),
            Children = children,
            Fields = fields.Count > 0 ? fields : null,
        };
    }

    // ===== Export (NR's own serializer via saveCatalogueInFiles) =====

    /// <summary>
    /// Serializes every currently-loaded file (game system + catalogues) to BattleScribe XML using NR's
    /// own bundled serializer (<c>convertToXml</c>), returning JSON
    /// <c>{ "files": { "&lt;path&gt;": "&lt;xml&gt;" }, "debug": [..] }</c>.
    ///
    /// NR's <c>convertToXml</c> is module-scoped (not directly reachable), but the editor store's
    /// <c>saveCatalogueInFiles(data)</c> calls it and then hands the bytes to <c>writeFile</c>, which
    /// forwards to <c>electron.invoke("saveFile", path, content)</c>. In the browser (no Electron)
    /// <c>writeFile</c> normally no-ops; we temporarily stub <c>globalThis.electron</c> so the serialized
    /// content is captured in-page instead of written to disk. This is the same serializer the editor's
    /// "Download" button uses, so the XML is byte-for-byte what NR emits.
    /// </summary>
    public static async Task<string> ExportLoadedFilesJsonAsync(IPage page)
    {
        return await page.EvaluateAsync<string>("""
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
    /// Extracts (fileName, xml) pairs from <see cref="ExportLoadedFilesJsonAsync"/>'s
    /// <c>{ files: { path: xml }, debug: [] }</c> payload, skipping any binary-marker entries.
    /// </summary>
    public static List<(string Name, string Xml)> ParseExportedFiles(string exportJson)
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

    /// <summary>Reads the root id, name and game-system flag from a catalogue/game-system XML string.</summary>
    public static (string Id, string Name, bool IsGameSystem) ParseRoot(string xml)
    {
        // The root tag is matched by shape, not by name, and game-system-ness is decided by a
        // <gameSystem element appearing at all rather than by a complete tag — the same rule the
        // BattleScribe engines apply. A truncated game system never closes its root tag, so the
        // stricter form read one as a catalogue and named the staged upload .cat (#268).
        var rootTag = Regex.Match(xml, @"<\s*[A-Za-z_][\w.:-]*\b[^>]*>").Value;
        var isGameSystem = Regex.IsMatch(xml, @"<\s*gameSystem\b");
        var id = Regex.Match(rootTag, @"\bid=""([^""]*)""").Groups[1].Value;
        var name = Regex.Match(rootTag, @"\bname=""([^""]*)""").Groups[1].Value;
        return (id, name, isGameSystem);
    }

    // ===== Reference validation =====

    /// <summary>
    /// Reads reference-validation errors (entry/catalogue links whose targets don't resolve) directly
    /// from the NR Editor store. Uses WeakSet cycle guards since NR's reactive model has back-references.
    /// </summary>
    public static async Task<IReadOnlyList<ValidationErrorState>> GetValidationErrorsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("""
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

        using var doc = JsonDocument.Parse(json);
        var errors = new List<ValidationErrorState>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var message = el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var entryId = el.TryGetProperty("entryId", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
            errors.Add(new ValidationErrorState(message, EntryId: entryId));
        }
        return errors;
    }
}
