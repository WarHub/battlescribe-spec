using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Engine-agnostic helpers that drive NewRecruit Editor's <b>real</b> Pinia store through Playwright:
/// serving the frozen static bundle, loading game-system + catalogue XML through NR's own file-upload
/// pipeline, reading state back from <c>editor.gameSystems[systemId].loadedCatalogues</c>, exporting via
/// NR's own serializer (<c>saveCatalogueInFiles</c>), reloading, loading additional files, and reading
/// reference-validation errors.
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

    // ===== Setup / navigation (NR's real upload + open pipeline) =====

    /// <summary>
    /// Loads game system + catalogue XML into the NR Editor via its file-upload UI, then navigates to
    /// the catalogue editor view. Feeds data through the hidden <c>&lt;input type="file"&gt;</c> so NR's
    /// real <c>onChange</c> → <c>BSXmlToJson</c> → <c>uploaded()</c> pipeline runs and populates the
    /// Pinia stores, exactly as a user's "Add From Folder" does. Returns [] on success.
    /// </summary>
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
    /// Switches the editor to a different loaded file (catalogue or game system) by id, driven through
    /// the UI: returns to the system list and double-clicks the matching item. Used by the spec
    /// <c>openFile</c> action so multi-catalogue specs can declare the active file.
    /// </summary>
    public static async Task NavigateToFileAsync(IPage page, string id)
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

    private static async Task<string?> NavigateToEditableAsync(IPage page, string itemName)
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
    /// Clears the NR Editor's loaded state for this spec: resets the Pinia stores and navigates back to
    /// the home page. Called between test runs and before a reload.
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

    /// <summary>
    /// Reloads the editor from already-serialized BattleScribe XML — typically the editor's own export
    /// of the current, mutated state — and reopens the file named <paramref name="reopenName"/>. Resets
    /// the stores, feeds the XML through the same hidden file input the initial load uses (so NR's real
    /// <c>BSXmlToJson</c> parse runs), waits for the catalogues store, then navigates back into the
    /// editor. Used by round-trip specs.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReloadFromXmlAsync(
        IPage page,
        string editorBaseUrl,
        IReadOnlyList<(string Name, string Xml)> files,
        string reopenName)
    {
        var errors = new List<string>();
        if (files.Count == 0)
        {
            errors.Add("Reload: no exported XML files to reload");
            return errors;
        }

        // Reset stores and return home, exactly as between test runs.
        await CleanupCatalogueAsync(page, editorBaseUrl);

        // GST first, then CATs — mirrors the initial upload ordering.
        var payloads = files
            .OrderByDescending(f => f.Name.EndsWith(".gst", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FilePayload
            {
                Name = f.Name,
                MimeType = "application/xml",
                Buffer = Encoding.UTF8.GetBytes(f.Xml),
            })
            .ToList();

        await page.Locator("input[type=file]").SetInputFilesAsync(payloads);

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
            errors.Add($"NR Editor did not populate catalogues store after reload upload: {ex.Message}");
            return errors;
        }

        var navResult = await NavigateToEditableAsync(page, reopenName);
        if (navResult is not null)
        {
            errors.Add(navResult);
        }

        return errors;
    }

    /// <summary>
    /// Load a single additional file (catalogue or game system) from XML WITHOUT resetting existing
    /// state, then open it. The hidden file input is only actionable on the file-list view, so this
    /// returns there client-side (preserving the in-memory store) before uploading. Used by
    /// <c>openFile</c> with a source.
    /// </summary>
    public static async Task<IReadOnlyList<string>> LoadFileAsync(IPage page, string fileName, string xml, string newId, string newName)
    {
        var errors = new List<string>();

        // Return to the file-list view (client-side back — does NOT reset the Pinia store), where the
        // hidden file input lives.
        try
        {
            await page.GoBackAsync(new PageGoBackOptions { Timeout = 10_000 });
            await page.WaitForSelectorAsync(".item.unselectable:not(.add)", new() { Timeout = 10_000 });
        }
        catch
        {
            // Possibly already on the list view; the upload below will fail clearly if not.
        }

        var payload = new FilePayload
        {
            Name = fileName,
            MimeType = "application/xml",
            Buffer = Encoding.UTF8.GetBytes(xml),
        };
        await page.Locator("input[type=file]").SetInputFilesAsync([payload]);

        var navResult = await NavigateToEditableAsync(page, newName);
        if (navResult is not null)
        {
            errors.Add($"NR Editor could not open loaded file '{newId}' ({newName}): {navResult}");
        }

        return errors;
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
        var rootTag = Regex.Match(xml, @"<\s*(catalogue|gameSystem)\b[^>]*>").Value;
        var isGameSystem = rootTag.Contains("<gameSystem", StringComparison.Ordinal)
            || Regex.IsMatch(rootTag, @"<\s*gameSystem\b");
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
