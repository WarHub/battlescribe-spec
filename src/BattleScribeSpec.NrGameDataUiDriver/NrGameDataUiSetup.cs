using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Helpers for the setup phase of NrGameDataUiEngine:
///   1. Serving the NR Editor static files locally (frozen mode).
///   2. Loading game system + catalogue XML files via the NR Editor's "Add From Folder" UI.
///   3. Navigating to the catalogue editor view.
///   4. Cleanup between tests.
///
/// File loading uses the same <c>showDirectoryPicker</c> mock as NrUiSetup for the
/// roster builder: a fake FileSystemDirectoryHandle returns our XML data when NR's
/// "Add From Folder" button is clicked, going through NR's full loading pipeline.
/// </summary>
public static class NrGameDataUiSetup
{
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
            if (!fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Loads game system + catalogue XML into the NR Editor via its "Add From Folder" UI flow,
    /// then navigates to the catalogue editor view.
    /// </summary>
    /// <returns>
    /// Empty list on success. Non-empty on setup errors (engine can skip the spec).
    /// </returns>
    public static async Task<IReadOnlyList<string>> LoadAndOpenCatalogueAsync(
        IPage page,
        ProtocolGameSystem gameSystem,
        ProtocolCatalogue[] catalogues)
    {
        var errors = new List<string>();

        // Generate BattleScribe XML from protocol types
        var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
        var allCatXml = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues);
        var files = new List<(string FileName, string Xml)> { ("system.gst", gstXml) };
        files.AddRange(allCatXml);

        // Use Pinia store's loadSystemFromFs to load the data (same pipeline as user clicking "Add From Folder")
        // This is the least fragile approach: NR's internal loading pipeline handles parsing.
        var loadResult = await page.EvaluateAsync<string?>("""
            async ([files]) => {
                try {
                    const nuxt = document.querySelector('#__nuxt')?.__vue_app__;
                    const app = document.querySelector('#app')?.__vue_app__;
                    const vueApp = nuxt || app;
                    if (!vueApp) return 'Vue app not found';
                    const pinia = vueApp.config?.globalProperties?.$pinia;
                    if (!pinia) return 'Pinia not found';

                    const sysStore = pinia._s.get('systemsStore') || pinia._s.get('systems') || pinia._s.get('system');
                    const editorStore = pinia._s.get('editor') || pinia._s.get('editorStore')
                        || pinia._s.get('catalogue') || pinia._s.get('catalogues');

                    // Trigger showDirectoryPicker (our mock will return the files)
                    if (sysStore?.loadSystemFromFs) {
                        // Call loadSystemFromFs directly (it internally calls showDirectoryPicker for the UI flow)
                        await sysStore.loadSystemFromFs(files.map(f => ({ name: f.FileName, data: f.Content })));
                    } else {
                        // Trigger via clicking the "Add From Folder" button in the UI
                        // The mock intercepts showDirectoryPicker
                        return 'no-load-method';
                    }

                    // Store reference to pinia/stores for later use
                    window.__bsspec_editor_ui = {
                        pinia,
                        sysStore,
                        editorStore,
                        storeIds: [...pinia._s.keys()],
                    };

                    return null;
                } catch (e) {
                    return 'Load error: ' + e.message;
                }
            }
            """, new object[] { files.Select(f => new { f.FileName, f.Xml }).ToArray() });

        if (loadResult == "no-load-method")
        {
            // Fall back to UI-based loading: navigate to home and click "Add From Folder"
            var uiLoadResult = await LoadViaUiAsync(page, gameSystem.Id);
            if (uiLoadResult is not null)
            {
                errors.Add(uiLoadResult);
                return errors;
            }
        }
        else if (loadResult is not null)
        {
            errors.Add(loadResult);
            return errors;
        }

        // Navigate to the catalogue editing view via UI
        var navResult = await NavigateToCatalogueAsync(page, gameSystem.Id);
        if (navResult is not null)
        {
            errors.Add(navResult);
        }

        return errors;
    }

    /// <summary>
    /// UI-based fallback loading: navigate to home page → "Add More Games" → "Add From Folder".
    /// The showDirectoryPicker mock must already be injected before calling this.
    /// </summary>
    private static async Task<string?> LoadViaUiAsync(IPage page, string systemId)
    {
        try
        {
            // Navigate to the systems list (home page)
            var homeLink = page.Locator("a[href*='MySystems'], a[href='/'], a[href*='home']").First;
            if (await homeLink.IsVisibleAsync())
            {
                await homeLink.ClickAsync();
                await page.WaitForTimeoutAsync(500);
            }

            // Click "Add More Games" or "Add System" button
            var addBtn = page.GetByText("Add more games")
                .Or(page.GetByText("Add System"))
                .Or(page.GetByText("Add From Folder"));
            await addBtn.First.ClickAsync(new() { Timeout = 5_000 });
            await page.WaitForTimeoutAsync(500);

            // If we opened a popup, click "Add From Folder" within it
            var addFromFolder = page.GetByText("Add From Folder");
            if (await addFromFolder.IsVisibleAsync())
            {
                await addFromFolder.ClickAsync();
            }

            // Wait for the system to appear in the local library
            await page.WaitForFunctionAsync(
                """
                (systemId) => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const sysStore = pinia?._s?.get('systemsStore');
                    if (systemId) return !!sysStore?.localLibrary?.[systemId];
                    return Object.keys(sysStore?.localLibrary || {}).length > 0;
                }
                """,
                systemId,
                new() { Timeout = 10_000 });

            // Close any popup
            var closeBtn = page.Locator(".xCross, button[aria-label='Close'], [class*='close']").First;
            if (await closeBtn.IsVisibleAsync())
            {
                await closeBtn.ClickAsync();
                await page.WaitForTimeoutAsync(300);
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"UI load error: {ex.Message}";
        }
    }

    /// <summary>
    /// Navigate to the catalogue editing view for the loaded system.
    /// The NR Editor shows a list of available catalogues; we click to open one for editing.
    /// </summary>
#pragma warning disable IDE0060
    private static async Task<string?> NavigateToCatalogueAsync(IPage page, string systemId)
#pragma warning restore IDE0060
    {
        try
        {
            // Look for a catalogue link or card in the systems list, then open the editor.
            // The NR Editor URL for editing is typically /nr-editor/[catalogue] or similar.
            // Try clicking on the system/catalogue card.
            var catalogueCard = page
                .Locator("[data-id], [class*='system'], [class*='catalogue'], [class*='book']")
                .Filter(new LocatorFilterOptions { HasNotText = "Add" })
                .First;

            if (await catalogueCard.IsVisibleAsync())
            {
                await catalogueCard.ClickAsync();
                await page.WaitForTimeoutAsync(1_000);
            }

            // Wait for the editor store to have a loaded catalogue
            await page.WaitForFunctionAsync("""
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return false;
                    const editorStore = pinia._s.get('editor') || pinia._s.get('editorStore')
                        || pinia._s.get('catalogue') || pinia._s.get('catalogues');
                    return !!(editorStore?.catalogue || editorStore?.currentCatalogue
                        || editorStore?.rootCatalogue || editorStore?.rootEntry);
                }
                """,
                null,
                new() { Timeout = 10_000 });

            // Store reference in window for state reads
            await page.EvaluateAsync("""
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const editorStore = pinia?._s?.get('editor') || pinia?._s?.get('editorStore')
                        || pinia?._s?.get('catalogue') || pinia?._s?.get('catalogues');
                    const sysStore = pinia?._s?.get('systemsStore') || pinia?._s?.get('systems');
                    if (!window.__bsspec_editor_ui) window.__bsspec_editor_ui = {};
                    window.__bsspec_editor_ui.editorStore = editorStore;
                    window.__bsspec_editor_ui.sysStore = sysStore;
                    window.__bsspec_editor_ui.pinia = pinia;
                    window.__bsspec_editor_ui.storeIds = pinia ? [...pinia._s.keys()] : [];
                }
                """);

            return null;
        }
        catch (Exception ex)
        {
            return $"Navigation to catalogue failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Injects a mock for <c>window.showDirectoryPicker</c> that returns a fake
    /// <c>FileSystemDirectoryHandle</c> containing the provided XML file data.
    /// The mock is one-shot: it restores the original after the first call.
    /// </summary>
    public static async Task InjectDirectoryPickerMockAsync(
        IPage page,
        IReadOnlyList<(string FileName, string Xml)> files)
    {
        var fileData = files.Select(f => new { name = f.FileName, content = f.Xml }).ToArray();

        await page.EvaluateAsync("""
            (fileData) => {
                const originalPicker = window.showDirectoryPicker;

                window.showDirectoryPicker = async () => {
                    if (originalPicker) {
                        window.showDirectoryPicker = originalPicker;
                    } else {
                        delete window.showDirectoryPicker;
                    }

                    const fakeFiles = fileData.map(f => {
                        const blob = new Blob([f.content], { type: 'application/xml' });
                        return new File([blob], f.name, { type: 'application/xml' });
                    });

                    const fileHandles = fakeFiles.map(file => ({
                        kind: 'file',
                        name: file.name,
                        getFile: async () => file,
                    }));

                    return {
                        kind: 'directory',
                        name: 'spec-data',
                        values: async function* () { for (const h of fileHandles) yield h; },
                        entries: async function* () { for (const h of fileHandles) yield [h.name, h]; },
                        keys: async function* () { for (const h of fileHandles) yield h.name; },
                        getFileHandle: async (name) => {
                            const h = fileHandles.find(fh => fh.name === name);
                            if (!h) throw new DOMException('File not found: ' + name, 'NotFoundError');
                            return h;
                        },
                    };
                };
            }
            """, fileData);
    }

    /// <summary>
    /// Clears the NR Editor's loaded state for this spec.
    /// Called by <see cref="NrGameDataUiEngine.Cleanup"/> between test runs.
    /// </summary>
    public static async Task CleanupCatalogueAsync(IPage page)
    {
        await page.EvaluateAsync("""
            () => {
                const ctx = window.__bsspec_editor_ui;
                if (ctx?.editorStore?.reset) {
                    try { ctx.editorStore.reset(); } catch { /* best-effort */ }
                }
                if (ctx?.sysStore?.clear) {
                    try { ctx.sysStore.clear(); } catch { /* best-effort */ }
                }
                window.__bsspec_editor_ui = null;
            }
            """);
    }
}
