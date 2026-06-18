using System.Text;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Helpers for the setup phase of NrGameDataUiEngine:
///   1. Serving the NR Editor static files locally (frozen mode).
///   2. Loading game system + catalogue XML files via the NR Editor's file-upload UI.
///   3. Navigating to the catalogue editor view.
///   4. Cleanup between tests.
///
/// File loading uses Playwright's <c>SetInputFilesAsync</c> on the hidden
/// <c>&lt;input type="file"&gt;</c> that is already on the NR Editor system page.
/// When files are set, NR's <c>onChange</c> handler fires, parses the XML via
/// <c>BSXmlToJson</c>, calls <c>uploaded()</c>, populates the Pinia stores, and
/// navigates to <c>/?id=&lt;systemId&gt;</c> — going through NR's full loading
/// pipeline without any mocking.
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
    /// Loads game system + catalogue XML into the NR Editor via its file-upload UI,
    /// then navigates to the catalogue editor view.
    ///
    /// The approach mirrors the NR Roster UI Driver's directory-picker mock:
    /// rather than calling internal JS functions directly, we feed data through
    /// the UI path that real users take. Here that means calling Playwright's
    /// <c>SetInputFilesAsync</c> on the hidden <c>&lt;input type="file"&gt;</c> element.
    /// NR's Vue <c>onChange</c> handler fires, parses the XML via <c>BSXmlToJson</c>,
    /// stores the data in Pinia, and navigates to <c>/?id=&lt;systemId&gt;</c>.
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

        // Generate BattleScribe XML from protocol types. GenerateAllCatalogueXml requires at
        // least one catalogue, so skip it for game-system-only specs.
        var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
        IReadOnlyList<(string FileName, string Xml)> allCatXml = [];
        if (catalogues.Length > 0)
        {
            allCatXml = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues);
        }

        // Build file payloads: GST first, then all CATs
        var payloads = new List<FilePayload>
        {
            new() { Name = "system.gst", MimeType = "application/xml", Buffer = Encoding.UTF8.GetBytes(gstXml) }
        };
        foreach (var (fileName, xml) in allCatXml)
        {
            payloads.Add(new() { Name = fileName, MimeType = "application/xml", Buffer = Encoding.UTF8.GetBytes(xml) });
        }

        // Set files on the hidden input — triggers onChange → uploaded() pipeline.
        // Playwright's SetInputFilesAsync on a Locator sets files regardless of visibility.
        await page.Locator("input[type=file]").SetInputFilesAsync(payloads);

        // Wait for the catalogues Pinia store to be populated.
        // uploaded() calls updateCatalogue() for each file, which populates catalogues.dict.
        try
        {
            await page.WaitForFunctionAsync(
                """
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const cs = pinia?._s?.get('catalogues');
                    return cs?.dict && Object.keys(cs.dict).length > 0;
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 15_000 });
        }
        catch (TimeoutException ex)
        {
            errors.Add($"NR Editor did not populate catalogues store after file upload: {ex.Message}");
            return errors;
        }

        // Game-system-only spec: open the game system itself for editing. NR Editor edits a
        // game system through the same catalogue-editor route, keyed by the system id
        // (loadedCatalogues[systemId]); ReadStateAsync already surfaces it as state.gameSystem.
        if (catalogues.Length == 0)
        {
            var gsNav = await NavigateToEditableAsync(page, gameSystem.Name);
            if (gsNav is not null)
            {
                errors.Add(gsNav);
            }

            return errors;
        }

        // Navigate to the target catalogue (last in the list is the spec's target)
        var navResult = await NavigateToEditableAsync(page, catalogues[^1].Name);
        if (navResult is not null)
        {
            errors.Add(navResult);
        }

        return errors;
    }

    /// <summary>
    /// Navigates to the editor view for a loaded item (a catalogue, or the game system itself).
    ///
    /// After <c>uploaded()</c> runs, the NR Editor is on the system list page showing the
    /// uploaded files (game system and catalogues) as <c>.item.unselectable</c> elements.
    /// Double-clicking an item navigates to the editor at <c>/catalogue?systemId=X&amp;id=Y</c>
    /// — the game system is edited through the same route, keyed by the system id. This method
    /// finds the item by name and double-clicks it, then waits for the editor route and the
    /// item to appear in <c>loadedCatalogues</c>.
    /// </summary>
    /// <summary>
    /// Switches the editor to a different loaded file (catalogue or game system) by id, driven
    /// through the UI: returns to the system list and double-clicks the matching item. Used by
    /// the spec <c>openCatalogue</c> action so multi-catalogue specs can declare the active file.
    /// </summary>
    internal static async Task NavigateToFileAsync(IPage page, string id)
    {
        // Already open?
        var currentId = await page.EvaluateAsync<string?>(
            "() => new URLSearchParams(location.search).get('id')");
        if (currentId == id)
        {
            return;
        }

        // Resolve the file's display name (read-only) from the loaded catalogues or systems store.
        var name = await page.EvaluateAsync<string?>(
            """
            (id) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const ed = pinia?._s?.get('editor');
                const sId = new URLSearchParams(location.search).get('systemId');
                const loaded = ed?.gameSystems?.[sId]?.loadedCatalogues ?? {};
                if (loaded[id]?.name) return loaded[id].name;
                // Fall back to scanning all systems' catalogue indexes.
                for (const gs of Object.values(ed?.gameSystems ?? {})) {
                    for (const c of Object.values(gs?.cataloguesById ?? gs?.catalogues ?? {})) {
                        if (c?.id === id && c?.name) return c.name;
                    }
                }
                return null;
            }
            """, id);

        _ = name ?? throw new InvalidOperationException(
            $"NR Editor UI: cannot resolve a name for file id '{id}' to open it.");

        // Return to the system list and double-click the target file.
        await page.GoBackAsync(new PageGoBackOptions { Timeout = 15_000 });
        await page.WaitForSelectorAsync(".item.unselectable:not(.add)", new() { Timeout = 10_000 });
        var item = page.Locator(".item.unselectable:not(.add)", new PageLocatorOptions { HasText = name });
        await item.First.DblClickAsync();
        await page.WaitForURLAsync("**/catalogue**", new() { Timeout = 15_000 });
        await page.WaitForFunctionAsync(
            """
            (id) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const sId = new URLSearchParams(location.search).get('systemId');
                return !!pinia?._s?.get('editor')?.gameSystems?.[sId]?.loadedCatalogues?.[id];
            }
            """, id, new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task<string?> NavigateToEditableAsync(
        IPage page,
        string itemName)
    {
        try
        {
            // After file upload, the system list page shows .item.unselectable elements —
            // one per uploaded file. Wait for them to appear.
            await page.WaitForSelectorAsync(".item.unselectable:not(.add)",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            // Find the item matching the name and double-click it.
            // Double-click (not single click) navigates to the editor.
            var item = page.Locator(".item.unselectable:not(.add)",
                new PageLocatorOptions { HasText = itemName });
            await item.First.DblClickAsync();

            // Wait for URL to change to the catalogue editor route.
            await page.WaitForURLAsync("**/catalogue**",
                new PageWaitForURLOptions { Timeout = 15_000 });

            // Wait for the editor store to have the catalogue fully loaded in
            // loadedCatalogues. The URL change fires before Vue finishes
            // populating editor.gameSystems[systemId].loadedCatalogues[catId].
            await page.WaitForFunctionAsync(
                """
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const params = new URLSearchParams(window.location.search);
                    const systemId = params.get('systemId');
                    const catId = params.get('id');
                    const editor = pinia?._s?.get('editor');
                    return !!editor?.gameSystems?.[systemId]?.loadedCatalogues?.[catId];
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 15_000 });

            // Persist Pinia store references for action methods to use later.
            await page.EvaluateAsync("""
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    window.__bsspec_editor_ui = {
                        pinia,
                        storeIds: pinia ? [...pinia._s.keys()] : [],
                        cataloguesStore: pinia?._s?.get('catalogues'),
                        editorStore: pinia?._s?.get('editor'),
                    };
                }
                """);

            return null;
        }
        catch (Exception ex)
        {
            return $"Navigation to editor failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Clears the NR Editor's loaded state for this spec.
    /// Called by <see cref="NrGameDataUiEngine.Cleanup"/> between test runs.
    /// Resets the Pinia stores and navigates back to the home page.
    /// </summary>
    public static async Task CleanupCatalogueAsync(IPage page, string editorBaseUrl)
    {
        // Reset Pinia store state
        await page.EvaluateAsync("""
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                // Reset catalogues and editor stores to clear loaded data
                try { pinia?._s?.get('catalogues')?.$reset(); } catch { /* best-effort */ }
                try { pinia?._s?.get('editor')?.$reset(); } catch { /* best-effort */ }
                window.__bsspec_editor_ui = null;
            }
            """);

        // Navigate back to home page for the next test
        await page.GotoAsync(editorBaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
