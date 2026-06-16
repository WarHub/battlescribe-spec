using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// IGameDataEngine implementation that drives the NR Editor web app via Playwright.
///
/// The NR Editor is a separate app from the roster builder (newrecruit.eu).
/// It's deployed at https://giloushaker.github.io/nr-editor/ (or custom URL).
///
/// Architecture:
///   - Setup: generates BattleScribe XML, loads via NR Editor's file import
///   - Actions: drives editorStore methods (add, remove, move, edit_node)
///   - State: reads the catalogue tree from NR's reactive store
///
/// The adapter accesses Pinia stores via page.EvaluateAsync().
/// Store discovery is done dynamically on first access.
/// </summary>
public sealed class NewRecruitGameDataEngine : IGameDataEngine
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private string _specId = "";
    private bool _disposed;

    public string BaseUrl { get; }
    public bool Headless { get; }

    private NewRecruitGameDataEngine(string baseUrl, bool headless)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Headless = headless;
    }

    /// <summary>
    /// Create a live NR Editor engine pointed at the given URL.
    /// </summary>
    public static async Task<NewRecruitGameDataEngine> CreateAsync(
        string baseUrl,
        bool headless = true,
        float? slowMo = null)
    {
        var engine = new NewRecruitGameDataEngine(baseUrl, headless);
        await engine.InitializeAsync(slowMo);
        return engine;
    }

    /// <summary>
    /// Create a frozen NR Editor engine that serves static files from a local directory.
    /// The directory must contain the gh-pages deployment of the NR Editor
    /// (index.html, _nuxt/, assets/, etc.).
    /// </summary>
    public static async Task<NewRecruitGameDataEngine> CreateFrozenAsync(
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
        var engine = new NewRecruitGameDataEngine("https://nr-editor.local/nr-editor", headless);
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

    private async Task InitializeAsync(float? slowMo)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Headless,
            SlowMo = slowMo,
        });
        _page = await _browser.NewPageAsync();
        await _page.GotoAsync(BaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        // Wait for the app to initialize (Vue/Nuxt)
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
            // Block service workers to prevent them from bypassing route interception
            ServiceWorkers = ServiceWorkerPolicy.Block,
        });
        _page = await context.NewPageAsync();

        // Set up route interception that serves local static files
        await SetupStaticFileRouting(_page, staticDir);

        // Navigate to the app — all network requests will be served from local files
        await _page.GotoAsync(BaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        // Wait for the app to initialize (Vue/Nuxt)
        await WaitForAppReadyAsync();
    }

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
        [".webm"] = "video/webm",
        [".mp4"] = "video/mp4",
        [".txt"] = "text/plain",
        [".xml"] = "application/xml",
    };

    private static async Task SetupStaticFileRouting(IPage page, string staticDir)
    {
        // Normalize with trailing separator for safe containment check
        var normalizedDir = Path.GetFullPath(staticDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        await page.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var url = new Uri(request.Url);
            var path = Uri.UnescapeDataString(url.AbsolutePath);

            // Normalize backslashes that could come from %5C decoding
            path = path.Replace('\\', '/');

            // Strip the /nr-editor/ base URL prefix
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

            // Empty path maps to index.html
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                path = "index.html";
            }

            // Security: prevent path traversal (trailing separator ensures no prefix collision)
            var fullPath = Path.GetFullPath(Path.Combine(normalizedDir, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 403,
                    ContentType = "text/plain",
                    Body = "Forbidden",
                });
                return;
            }

            if (File.Exists(fullPath))
            {
                var ext = Path.GetExtension(fullPath);
                var contentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
                var body = await File.ReadAllBytesAsync(fullPath);

                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = contentType,
                    BodyBytes = body,
                });
            }
            else
            {
                // SPA fallback only for navigation requests (HTML pages), not static assets.
                // Returning index.html for a missing .js/.css would hide real 404s.
                var ext = Path.GetExtension(fullPath);
                var isStaticAsset = !string.IsNullOrEmpty(ext) && ext != ".html";

                if (!isStaticAsset)
                {
                    var indexPath = Path.Combine(normalizedDir, "index.html");
                    if (File.Exists(indexPath))
                    {
                        var body = await File.ReadAllBytesAsync(indexPath);
                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "text/html",
                            BodyBytes = body,
                        });
                        return;
                    }
                }

                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 404,
                    ContentType = "text/plain",
                    Body = "Not Found",
                });
            }
        });
    }

    private async Task WaitForAppReadyAsync()
    {
        if (_page is null)
        { return; }

        // NR Editor may use #app or #__nuxt as the Vue mount point.
        // Try both patterns for Pinia discovery.
        await _page.WaitForFunctionAsync("""
            () => {
                const nuxt = document.querySelector('#__nuxt')?.__vue_app__;
                const app = document.querySelector('#app')?.__vue_app__;
                const vueApp = nuxt || app;
                return !!vueApp?.config?.globalProperties?.$pinia;
            }
            """, null, new() { Timeout = 30_000 });
    }

    public void SetTestContext(string specId)
    {
        _specId = specId;
    }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        return SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupAsync(
        ProtocolGameSystem gameSystem,
        ProtocolCatalogue[] catalogues)
    {
        if (_page is null)
        {
            return ["NR Editor page not initialized"];
        }

        var errors = new List<string>();

        try
        {
            // Generate BattleScribe XML from protocol types
            var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
            IReadOnlyList<(string FileName, string Xml)> allCatXml = [];
            if (catalogues.Length > 0)
            {
                allCatXml = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues);
            }
            var catFiles = allCatXml.Select(c => new { name = c.FileName, data = c.Xml }).ToArray();

            // Load data into the NR Editor.
            // Strategy: load XML files into the editor's store, then open the catalogue.
            // The editor's loadSystemFromFs populates IndexedDB, after which we must
            // find and open the catalogue by its actual key (not our spec ID).
            var setupResult = await _page.EvaluateAsync<string?>("""
                async ([gstXml, catFiles, systemId, systemName, specId]) => {
                    try {
                        // Discover Pinia
                        const nuxt = document.querySelector('#__nuxt')?.__vue_app__;
                        const app = document.querySelector('#app')?.__vue_app__;
                        const vueApp = nuxt || app;
                        if (!vueApp) return 'Vue app not found (tried #__nuxt and #app)';

                        const pinia = vueApp.config?.globalProperties?.$pinia;
                        if (!pinia) return 'Pinia store not found on Vue app';

                        // Store discovery
                        const storeIds = [...pinia._s.keys()];
                        const editorStore = pinia._s.get('editor') || pinia._s.get('editorStore')
                            || pinia._s.get('catalogue') || pinia._s.get('catalogues');
                        const sysStore = pinia._s.get('systemsStore') || pinia._s.get('systems')
                            || pinia._s.get('system');

                        if (!editorStore && !sysStore) {
                            return 'No editor/system store found. Available: [' + storeIds.join(', ') + ']';
                        }

                        // Load system files
                        const loader = sysStore || editorStore;
                        const files = [
                            { name: 'system.gst', path: '/spec/system.gst', data: gstXml },
                            ...catFiles.map(c => ({ name: c.name, path: '/spec/' + c.name, data: c.data })),
                        ];

                        if (loader?.loadSystemFromFs) {
                            await loader.loadSystemFromFs(files);
                        } else if (editorStore?.create_system) {
                            await editorStore.create_system(systemName);
                        } else {
                            return 'No load method. Stores: [' + storeIds.join(', ') + ']';
                        }

                        // After loading, we need to open the catalogue for editing.
                        // IMPORTANT: Always build our own catalogue from XML to ensure
                        // all setup entries are correctly represented. NR Editor's native
                        // loadSystemFromFs + open_catalogue flow doesn't reliably preserve
                        // all entries in a way we can access via direct manipulation.

                        // Build catalogue from XML parsing (always, not just as fallback)
                        const parser = new DOMParser();

                        // Recursive entry parser shared by catalogue and game system roots
                        const parseEntry = (el) => {
                            const entry = {
                                id: el.getAttribute('id') || '',
                                name: el.getAttribute('name') || '',
                                hidden: el.getAttribute('hidden') === 'true',
                                type: el.getAttribute('type') || '',
                            };
                            // Recursively parse child containers
                            const childContainers = ['selectionEntries', 'selectionEntryGroups',
                                'rules', 'profiles', 'infoGroups', 'infoLinks', 'entryLinks',
                                'categoryLinks', 'constraints', 'modifiers', 'modifierGroups',
                                'conditions', 'conditionGroups'];
                            for (const ck of childContainers) {
                                const container = el.querySelector(':scope > ' + ck);
                                if (container && container.children.length > 0) {
                                    entry[ck] = [...container.children].map(parseEntry);
                                }
                            }
                            return entry;
                        };
                        const parseEntries = (parentEl, tag) => {
                            const container = parentEl.querySelector(':scope > ' + tag);
                            if (!container) return [];
                            return [...container.children].map(parseEntry);
                        };

                        // Build an editable root (catalogue or game system) from a root element
                        const buildRoot = (rootEl, rootId, rootName, rootGameSystemId) => {
                            const root = {
                                id: rootEl.getAttribute('id') || rootId,
                                name: rootEl.getAttribute('name') || rootName,
                                gameSystemId: rootEl.getAttribute('gameSystemId') || rootGameSystemId,
                                selectionEntries: [],
                                selectionEntryGroups: [],
                                entryLinks: [],
                                rules: [],
                                sharedSelectionEntries: [],
                                sharedSelectionEntryGroups: [],
                                sharedRules: [],
                                sharedProfiles: [],
                                forceEntries: [],
                                categoryEntries: [],
                                publications: [],
                                costTypes: [],
                                profileTypes: [],
                            };

                            root.selectionEntries = parseEntries(rootEl, 'selectionEntries');
                            root.sharedSelectionEntries = parseEntries(rootEl, 'sharedSelectionEntries');
                            root.sharedSelectionEntryGroups = parseEntries(rootEl, 'sharedSelectionEntryGroups');
                            root.selectionEntryGroups = parseEntries(rootEl, 'selectionEntryGroups');
                            root.rules = parseEntries(rootEl, 'rules');
                            root.sharedRules = parseEntries(rootEl, 'sharedRules');
                            root.sharedProfiles = parseEntries(rootEl, 'sharedProfiles');
                            root.entryLinks = parseEntries(rootEl, 'entryLinks');
                            root.forceEntries = parseEntries(rootEl, 'forceEntries');
                            root.categoryEntries = parseEntries(rootEl, 'categoryEntries');
                            root.publications = parseEntries(rootEl, 'publications');
                            root.costTypes = parseEntries(rootEl, 'costTypes');
                            root.profileTypes = parseEntries(rootEl, 'profileTypes');
                            return root;
                        };

                        let catalogue = null;
                        let gameSystem = null;
                        const catXml = catFiles[0]?.data;
                        if (catXml) {
                            const doc = parser.parseFromString(catXml, 'text/xml');
                            const catEl = doc.querySelector('catalogue');
                            if (catEl) {
                                catalogue = buildRoot(catEl, systemId, systemName, systemId);
                            }
                            if (!catalogue) {
                                return 'Setup error: could not parse catalogue from XML';
                            }
                        } else {
                            // No catalogues: author entries directly in the game system.
                            const gstDoc = parser.parseFromString(gstXml, 'text/xml');
                            const gstEl = gstDoc.querySelector('gameSystem');
                            if (gstEl) {
                                gameSystem = buildRoot(gstEl, systemId, systemName,
                                    gstEl.getAttribute('id') || systemId);
                            }
                            if (!gameSystem) {
                                return 'Setup error: could not parse game system from XML';
                            }
                        }

                        // Store references for later actions
                        window.__bsspec_editor = {
                            pinia,
                            editorStore,
                            sysStore,
                            storeIds,
                            systemId,
                            specId,
                            directMode: true,
                            catalogue,
                            gameSystem,
                        };

                        return null; // success
                    } catch(e) {
                        return 'Setup error: ' + e.message + (e.stack ? '\n' + e.stack : '');
                    }
                }
                """, new object[] { gstXml, catFiles, gameSystem.Id, gameSystem.Name, _specId });

            if (setupResult != null)
            {
                errors.Add(setupResult);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"NR Editor setup exception: {ex.Message}");
        }

        return errors;
    }

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null)
    {
        return AddEntryAsync(parentId, entryType, name).GetAwaiter().GetResult();
    }

    private async Task<GameDataActionOutputs> AddEntryAsync(string parentId, string entryType, string? name)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([parentId, entryType, name]) => {
                try {
                    const ctx = window.__bsspec_editor;
                    if (!ctx) return 'ERROR:No editor context — was Setup called?';

                    const root = ctx.catalogue || ctx.gameSystem;
                    if (!root) return 'ERROR:No catalogue available';

                    // Map entryType to container key
                    // At catalogue root, some types go to "shared" containers
                    const containerKeyMap = {
                        'selectionEntry': 'selectionEntries',
                        'selectionEntryGroup': 'selectionEntryGroups',
                        'entryLink': 'entryLinks',
                        'rule': 'rules',
                        'profile': 'profiles',
                        'infoGroup': 'infoGroups',
                        'infoLink': 'infoLinks',
                        'categoryEntry': 'categoryEntries',
                        'categoryLink': 'categoryLinks',
                        'forceEntry': 'forceEntries',
                        'constraint': 'constraints',
                        'modifier': 'modifiers',
                        'modifierGroup': 'modifierGroups',
                        'condition': 'conditions',
                        'conditionGroup': 'conditionGroups',
                        'publication': 'publications',
                        'costType': 'costTypes',
                        'profileType': 'profileTypes',
                        'repeat': 'repeats',
                    };
                    // When parent is catalogue root, some types use shared containers
                    const sharedKeyMap = {
                        'selectionEntryGroup': 'sharedSelectionEntryGroups',
                        'profile': 'sharedProfiles',
                    };
                    let childKey = containerKeyMap[entryType] || (entryType + 's');

                    // Generate a unique ID
                    const id = crypto.randomUUID ? crypto.randomUUID()
                        : 'xxxx-xxxx-xxxx-xxxx'.replace(/x/g, () => Math.floor(Math.random()*16).toString(16));

                    // Create the entry object
                    const data = { id, name: name || 'New ' + entryType, hidden: false };
                    if (entryType === 'selectionEntry') data.type = 'upgrade';
                    if (entryType === 'selectionEntryGroup') data.type = 'group';

                    // Find parent by ID (walk catalogue tree)
                    const findById = (obj, targetId) => {
                        if (!obj) return null;
                        if (obj.id === targetId) return obj;
                        for (const key of Object.keys(obj)) {
                            const val = obj[key];
                            if (Array.isArray(val)) {
                                for (const item of val) {
                                    if (item && typeof item === 'object') {
                                        const found = findById(item, targetId);
                                        if (found) return found;
                                    }
                                }
                            }
                        }
                        return null;
                    };

                    let parent = findById(root, parentId);
                    if (!parent && parentId === ctx.systemId) {
                        parent = root; // treat root (catalogue or game system) as root
                    }
                    if (!parent) return 'ERROR:Parent not found: ' + parentId;

                    // If parent is the root, use shared containers for certain types
                    if (parent === root && sharedKeyMap[entryType]) {
                        childKey = sharedKeyMap[entryType];
                    }

                    // Direct array push
                    if (!parent[childKey]) parent[childKey] = [];
                    parent[childKey].push(data);

                    return id;
                } catch(e) {
                    return 'ERROR:AddEntry: ' + e.message;
                }
            }
            """, new object[] { parentId, entryType, name ?? "" });

        if (result?.StartsWith("ERROR:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(result[6..]);
        }

        return new GameDataActionOutputs { EntryId = result };
    }

    public void RemoveEntry(string entryId)
    {
        RemoveEntryAsync(entryId).GetAwaiter().GetResult();
    }

    private async Task RemoveEntryAsync(string entryId)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            (entryId) => {
                try {
                    const ctx = window.__bsspec_editor;
                    if (!ctx) return 'No editor context';
                    const root = ctx.catalogue || ctx.gameSystem;
                    if (!root) return 'No catalogue';

                    // Find and splice from parent
                    const removeFromParent = (parent, id) => {
                        if (!parent || typeof parent !== 'object') return false;
                        for (const key of Object.keys(parent)) {
                            const val = parent[key];
                            if (Array.isArray(val)) {
                                const idx = val.findIndex(e => e?.id === id);
                                if (idx >= 0) { val.splice(idx, 1); return true; }
                                for (const item of val) {
                                    if (removeFromParent(item, id)) return true;
                                }
                            }
                        }
                        return false;
                    };
                    if (!removeFromParent(root, entryId)) {
                        return 'Could not remove entry: ' + entryId;
                    }
                    return null;
                } catch(e) {
                    return 'RemoveEntry error: ' + e.message;
                }
            }
            """, entryId);

        if (result != null)
        {
            throw new InvalidOperationException(result);
        }
    }

    public void SetField(string entryId, string field, string? value)
    {
        SetFieldAsync(entryId, field, value).GetAwaiter().GetResult();
    }

    private async Task SetFieldAsync(string entryId, string field, string? value)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([entryId, field, value]) => {
                try {
                    const ctx = window.__bsspec_editor;
                    if (!ctx) return 'No editor context';
                    const root = ctx.catalogue || ctx.gameSystem;
                    if (!root) return 'No catalogue';

                    // Find entry
                    const findById = (obj, id) => {
                        if (!obj) return null;
                        if (obj.id === id) return obj;
                        for (const key of Object.keys(obj)) {
                            const val = obj[key];
                            if (Array.isArray(val)) {
                                for (const item of val) {
                                    if (item && typeof item === 'object') {
                                        const found = findById(item, id);
                                        if (found) return found;
                                    }
                                }
                            }
                        }
                        return null;
                    };

                    let entry = findById(root, entryId);
                    if (!entry && entryId === ctx.systemId) {
                        entry = root;
                    }
                    if (!entry) return 'Entry not found: ' + entryId;

                    // Handle boolean fields
                    if (value === 'true' || value === 'false') {
                        entry[field] = value === 'true';
                    } else {
                        entry[field] = value;
                    }

                    return null;
                } catch(e) {
                    return 'SetField error: ' + e.message;
                }
            }
            """, new object[] { entryId, field, value ?? "" });

        if (result != null)
        {
            throw new InvalidOperationException(result);
        }
    }

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId)
    {
        return AddLinkAsync(parentId, linkType, targetId).GetAwaiter().GetResult();
    }

    private async Task<GameDataActionOutputs> AddLinkAsync(string parentId, string linkType, string targetId)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([parentId, linkType, targetId]) => {
                try {
                    const ctx = window.__bsspec_editor;
                    if (!ctx) return 'ERROR:No editor context';
                    const root = ctx.catalogue || ctx.gameSystem;
                    if (!root) return 'ERROR:No catalogue';

                    const containerKeyMap = {
                        'entryLink': 'entryLinks',
                        'infoLink': 'infoLinks',
                        'categoryLink': 'categoryLinks',
                    };
                    const childKey = containerKeyMap[linkType] || (linkType + 's');

                    const id = crypto.randomUUID ? crypto.randomUUID()
                        : 'xxxx-xxxx-xxxx-xxxx'.replace(/x/g, () => Math.floor(Math.random()*16).toString(16));

                    const data = { id, targetId, name: '', hidden: false };
                    if (linkType === 'entryLink') data.type = 'selectionEntry';
                    if (linkType === 'categoryLink') data.type = 'category';

                    // Find parent
                    const findById = (obj, pid) => {
                        if (!obj) return null;
                        if (obj.id === pid) return obj;
                        for (const key of Object.keys(obj)) {
                            const val = obj[key];
                            if (Array.isArray(val)) {
                                for (const item of val) {
                                    if (item && typeof item === 'object') {
                                        const found = findById(item, pid);
                                        if (found) return found;
                                    }
                                }
                            }
                        }
                        return null;
                    };
                    let parent = findById(root, parentId);
                    if (!parent && parentId === ctx.systemId) {
                        parent = root;
                    }
                    if (!parent) return 'ERROR:Parent not found: ' + parentId;

                    if (!parent[childKey]) parent[childKey] = [];
                    parent[childKey].push(data);

                    return id;
                } catch(e) {
                    return 'ERROR:AddLink: ' + e.message;
                }
            }
            """, new object[] { parentId, linkType, targetId });

        if (result?.StartsWith("ERROR:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(result[6..]);
        }

        return new GameDataActionOutputs { EntryId = result };
    }

    public GameDataState GetState()
    {
        return GetStateAsync().GetAwaiter().GetResult();
    }

    private async Task<GameDataState> GetStateAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var json = await _page.EvaluateAsync<string>("""
            () => {
                const ctx = window.__bsspec_editor;
                if (!ctx) return JSON.stringify({ error: 'No editor context' });
                const catalogue = ctx.catalogue;

                // Helper to serialize an entry node
                const serializeEntry = (entry, entryType) => {
                    if (!entry) return null;
                    const result = {
                        id: entry.id || '',
                        name: entry.name || '',
                        entryType: entryType || entry.type || '',
                        hidden: !!entry.hidden,
                        children: [],
                        fields: {},
                    };

                    // Collect type-specific fields
                    const skipKeys = new Set(['id', 'name', 'hidden', 'selectionEntries', 'selectionEntryGroups',
                        'entryLinks', 'infoLinks', 'categoryLinks', 'rules', 'profiles', 'infoGroups',
                        'constraints', 'modifiers', 'modifierGroups', 'conditions', 'conditionGroups',
                        'forceEntries', 'categoryEntries', 'publications', 'costTypes', 'profileTypes', 'repeats']);

                    for (const [key, val] of Object.entries(entry)) {
                        if (skipKeys.has(key)) continue;
                        if (typeof val === 'function') continue;
                        if (val !== null && val !== undefined && !Array.isArray(val) && typeof val !== 'object') {
                            result.fields[key] = String(val);
                        }
                    }

                    // Serialize nested children
                    const childContainers = ['selectionEntries', 'selectionEntryGroups', 'rules',
                        'profiles', 'infoGroups', 'constraints', 'modifiers', 'modifierGroups',
                        'conditions', 'conditionGroups', 'entryLinks', 'infoLinks', 'categoryLinks',
                        'forceEntries', 'categoryEntries', 'repeats'];

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

                // Serialize a catalogue/system with explicit containers
                const serializeContainer = (container) => {
                    if (!container) return null;
                    const result = {
                        id: container.id || '',
                        name: container.name || '',
                        gameSystemId: container.gameSystemId || container.id_game_system || '',
                        selectionEntries: [],
                        entryLinks: [],
                        rules: [],
                        sharedSelectionEntries: [],
                        sharedSelectionEntryGroups: [],
                        sharedRules: [],
                        sharedProfiles: [],
                        forceEntries: [],
                        categoryEntries: [],
                        publications: [],
                        costTypes: [],
                        profileTypes: [],
                    };

                    // Map from NR's property names to our state containers
                    const mappings = [
                        ['selectionEntries', 'selectionEntries', 'selectionEntry'],
                        ['entryLinks', 'entryLinks', 'entryLink'],
                        ['rules', 'rules', 'rule'],
                        ['sharedSelectionEntries', 'sharedSelectionEntries', 'selectionEntry'],
                        ['sharedSelectionEntryGroups', 'sharedSelectionEntryGroups', 'selectionEntryGroup'],
                        ['sharedRules', 'sharedRules', 'rule'],
                        ['sharedProfiles', 'sharedProfiles', 'profile'],
                        ['forceEntries', 'forceEntries', 'forceEntry'],
                        ['categoryEntries', 'categoryEntries', 'categoryEntry'],
                        ['publications', 'publications', 'publication'],
                        ['costTypes', 'costTypes', 'costType'],
                        ['profileTypes', 'profileTypes', 'profileType'],
                    ];

                    for (const [srcKey, destKey, entryType] of mappings) {
                        const items = container[srcKey];
                        if (Array.isArray(items)) {
                            result[destKey] = items.map(e => serializeEntry(e, entryType)).filter(Boolean);
                        }
                    }

                    return result;
                };

                // Build state
                const state = { catalogues: [], gameSystem: null };

                if (ctx.gameSystem) {
                    state.gameSystem = serializeContainer(ctx.gameSystem);
                }

                if (ctx.catalogue) {
                    state.catalogues = [serializeContainer(ctx.catalogue)];
                }

                return JSON.stringify(state);
            }
            """);

        return DeserializeState(json);
    }

    private static GameDataState DeserializeState(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorProp))
        {
            throw new InvalidOperationException($"GetState failed: {errorProp.GetString()}");
        }

        var catalogues = new List<CatalogueDataState>();
        if (root.TryGetProperty("catalogues", out var catsElement))
        {
            foreach (var catEl in catsElement.EnumerateArray())
            {
                catalogues.Add(DeserializeCatalogue(catEl));
            }
        }

        GameSystemDataState? gameSystem = null;
        if (root.TryGetProperty("gameSystem", out var gsEl) && gsEl.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            gameSystem = DeserializeGameSystem(gsEl);
        }

        return new GameDataState
        {
            GameSystem = gameSystem,
            Catalogues = catalogues,
        };
    }

    private static CatalogueDataState DeserializeCatalogue(System.Text.Json.JsonElement el)
    {
        return new CatalogueDataState
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Name = el.GetProperty("name").GetString() ?? "",
            GameSystemId = el.GetProperty("gameSystemId").GetString() ?? "",
            SelectionEntries = DeserializeEntryList(el, "selectionEntries"),
            EntryLinks = DeserializeEntryList(el, "entryLinks"),
            Rules = DeserializeEntryList(el, "rules"),
            SharedSelectionEntries = DeserializeEntryList(el, "sharedSelectionEntries"),
            SharedSelectionEntryGroups = DeserializeEntryList(el, "sharedSelectionEntryGroups"),
            SharedRules = DeserializeEntryList(el, "sharedRules"),
            SharedProfiles = DeserializeEntryList(el, "sharedProfiles"),
            ForceEntries = DeserializeEntryList(el, "forceEntries"),
            CategoryEntries = DeserializeEntryList(el, "categoryEntries"),
            Publications = DeserializeEntryList(el, "publications"),
            CostTypes = DeserializeEntryList(el, "costTypes"),
            ProfileTypes = DeserializeEntryList(el, "profileTypes"),
        };
    }

    private static GameSystemDataState DeserializeGameSystem(System.Text.Json.JsonElement el)
    {
        return new GameSystemDataState
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Name = el.GetProperty("name").GetString() ?? "",
            SelectionEntries = DeserializeEntryList(el, "selectionEntries"),
            EntryLinks = DeserializeEntryList(el, "entryLinks"),
            Rules = DeserializeEntryList(el, "rules"),
            SharedSelectionEntries = DeserializeEntryList(el, "sharedSelectionEntries"),
            SharedSelectionEntryGroups = DeserializeEntryList(el, "sharedSelectionEntryGroups"),
            SharedRules = DeserializeEntryList(el, "sharedRules"),
            SharedProfiles = DeserializeEntryList(el, "sharedProfiles"),
            ForceEntries = DeserializeEntryList(el, "forceEntries"),
            CategoryEntries = DeserializeEntryList(el, "categoryEntries"),
            CostTypes = DeserializeEntryList(el, "costTypes"),
            ProfileTypes = DeserializeEntryList(el, "profileTypes"),
            Publications = DeserializeEntryList(el, "publications"),
        };
    }

    private static IReadOnlyList<DataEntryState> DeserializeEntryList(
        System.Text.Json.JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<DataEntryState>();
        foreach (var el in arr.EnumerateArray())
        {
            entries.Add(DeserializeEntry(el));
        }
        return entries;
    }

    private static DataEntryState DeserializeEntry(System.Text.Json.JsonElement el)
    {
        var fields = new Dictionary<string, string?>();
        if (el.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in fieldsEl.EnumerateObject())
            {
                fields[prop.Name] = prop.Value.GetString();
            }
        }

        var children = new List<DataEntryState>();
        if (el.TryGetProperty("children", out var childrenEl) && childrenEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var childEl in childrenEl.EnumerateArray())
            {
                children.Add(DeserializeEntry(childEl));
            }
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

    public void Cleanup()
    {
        CleanupAsync().GetAwaiter().GetResult();
    }

    private async Task CleanupAsync()
    {
        if (_page is null)
        { return; }

        await _page.EvaluateAsync("""
            () => {
                const ctx = window.__bsspec_editor;
                if (ctx?.editorStore?.reset) {
                    ctx.editorStore.reset();
                }
                window.__bsspec_editor = null;
            }
            """);
    }

    public void Dispose()
    {
        if (_disposed)
        { return; }
        _disposed = true;

        DisposeAsync().GetAwaiter().GetResult();
    }

    private async Task DisposeAsync()
    {
        if (_page is not null)
        {
            try
            { await _page.CloseAsync(); }
            catch { /* best effort */ }
            _page = null;
        }
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
    }
}
