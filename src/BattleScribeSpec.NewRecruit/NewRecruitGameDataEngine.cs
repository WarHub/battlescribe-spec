using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Telemetry;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Store-direct <see cref="IGameDataEngine"/> for the NewRecruit Editor: drives NR's <b>real</b> Pinia
/// store via Playwright JS, mutating <c>editor.gameSystems[systemId].loadedCatalogues</c> directly.
///
/// <para>
/// Setup, state reads, export, reload, file-load and validation are the shared
/// <see cref="NrEditorStore"/> helpers — the same real-store code the UI-driven
/// <c>NrGameDataUiEngine</c> uses. The only difference is mutation: this engine pushes/splices/edits
/// plain entry objects (NR field names) directly in the loaded store (fast), where the UI driver clicks
/// rendered widgets. Because both ultimately serialize through NR's own <c>saveCatalogueInFiles</c>, the
/// exported XML is byte-for-byte identical to the UI driver's — making this a true NR <b>base</b> producer
/// for snapshot assertions.
/// </para>
/// </summary>
public sealed class NewRecruitGameDataEngine : IGameDataEngine
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private string _specId = "";
    private bool _disposed;

    // This engine always launches and owns its own browser (there is no pool-created,
    // externally-owned path for this class, unlike NrGameDataUiEngine). Flags guard the
    // Acquired/Released pairing so DisposeAsync releases exactly once even if it's called more
    // than once or after a partially-failed initialization.
    private bool _browserAcquired;
    private bool _contextAcquired;

    // Loaded-file tracking (id -> display name) and the active file id, so export/reload can pick and
    // reopen the right file — mirrors NrGameDataUiEngine.
    private readonly Dictionary<string, string> _idToName = new(StringComparer.Ordinal);
    private string? _openId;

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
        ResourceMetrics.Acquired("browser");
        _browserAcquired = true;
        _page = await _browser.NewPageAsync();
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
        ResourceMetrics.Acquired("browser");
        _browserAcquired = true;
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            // Block service workers to prevent them from bypassing route interception
            ServiceWorkers = ServiceWorkerPolicy.Block,
        });
        ResourceMetrics.Acquired("browser-context");
        _contextAcquired = true;
        _page = await context.NewPageAsync();

        // Set up route interception that serves local static files (shared with the UI driver).
        await NrEditorStore.SetupStaticFileRoutingAsync(_page, staticDir);

        // Navigate to the app — all network requests will be served from local files
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

        // NR Editor may use #app or #__nuxt as the Vue mount point.
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
        => SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<string>> SetupAsync(
        ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        if (_page is null)
        { return ["NR Editor page not initialized"]; }

        try
        {
            // Load + open through NR's real upload pipeline (populates loadedCatalogues).
            var errors = await NrEditorStore.LoadAndOpenCatalogueAsync(_page, gameSystem, catalogues);
            if (errors.Count > 0)
            { return errors; }

            // Track loaded files (id -> display name) and the active one, so export/reload pick/reopen
            // correctly. Setup opens the last catalogue, or the game system itself when there are none.
            _idToName.Clear();
            _idToName[gameSystem.Id] = gameSystem.Name;
            foreach (var cat in catalogues)
            { _idToName[cat.Id] = cat.Name; }
            _openId = catalogues.Length > 0 ? catalogues[^1].Id : gameSystem.Id;

            return [];
        }
        catch (Exception ex)
        {
            return [$"NR Editor GameData setup exception: {ex.Message}"];
        }
    }

    /// <summary>
    /// Selects/opens the given catalogue (or game system) for editing — navigates the editor to it so
    /// state reads (URL-keyed) and subsequent edits target the right file.
    /// </summary>
    public void OpenFile(string id) => OpenFileAsync(id).GetAwaiter().GetResult();

    private async Task OpenFileAsync(string id)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        await NrEditorStore.NavigateToFileAsync(_page, id);
        _openId = id;
    }

    // ===== Direct-JS mutations on the real loadedCatalogues store =====
    //
    // NR's reactive nodes carry parent/catalogue back-references, so every tree walk is cycle-guarded
    // with a WeakSet and only descends array-valued (container) properties.

    private const string FindInRootsPrelude = """
        const editor = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
        const systemId = new URLSearchParams(location.search).get('systemId');
        const loaded = editor?.gameSystems?.[systemId]?.loadedCatalogues ?? {};
        const roots = Object.values(loaded);
        const findInRoots = (id) => {
            const seen = new WeakSet();
            const rec = (obj) => {
                if (!obj || typeof obj !== 'object' || seen.has(obj)) return null;
                seen.add(obj);
                if (obj.id === id) return obj;
                for (const k of Object.keys(obj)) {
                    if (k === 'parent' || k === 'catalogue') continue;
                    const v = obj[k];
                    if (Array.isArray(v)) { for (const it of v) { const f = rec(it); if (f) return f; } }
                }
                return null;
            };
            for (const r of roots) { const f = rec(r); if (f) return f; }
            if (id === systemId && loaded[systemId]) return loaded[systemId];
            return null;
        };
        """;

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null)
        => AddEntryAsync(parentId, entryType, name, id).GetAwaiter().GetResult();

    private async Task<GameDataActionOutputs> AddEntryAsync(string parentId, string entryType, string? name, string? declaredId)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([parentId, entryType, name, declaredId]) => {
                try {
            """ + FindInRootsPrelude + """
                    // Map entryType to container key.
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
                        'characteristicType': 'characteristicTypes',
                        'repeat': 'repeats',
                        'catalogueLink': 'catalogueLinks',
                        'sharedSelectionEntry': 'sharedSelectionEntries',
                        'sharedSelectionEntryGroup': 'sharedSelectionEntryGroups',
                        'sharedRule': 'sharedRules',
                        'sharedProfile': 'sharedProfiles',
                        'sharedInfoGroup': 'sharedInfoGroups',
                    };
                    // When the parent is a catalogue/system root, these bare types use shared containers.
                    const sharedKeyMap = {
                        'selectionEntryGroup': 'sharedSelectionEntryGroups',
                        'profile': 'sharedProfiles',
                    };
                    let childKey = containerKeyMap[entryType] || (entryType + 's');

                    // Use the declared id if provided (for byte-reproducible exports), else generate one.
                    const id = declaredId || (crypto.randomUUID ? crypto.randomUUID()
                        : 'xxxx-xxxx-xxxx-xxxx'.replace(/x/g, () => Math.floor(Math.random()*16).toString(16)));

                    // Build the entry in NR's shape. NR's serializer emits attributes in object-key
                    // insertion order, so mirror the UI-created node's field order (type, import, name,
                    // hidden, id) — selectionEntry/group also get NR's type + import defaults — to make
                    // the exported XML byte-for-byte identical to the UI driver's.
                    const data = {};
                    if (entryType === 'selectionEntry') { data.type = 'upgrade'; data.import = true; }
                    else if (entryType === 'selectionEntryGroup') { data.type = 'group'; data.import = true; }
                    data.name = name || 'New ' + entryType;
                    data.hidden = false;
                    data.id = id;

                    let parent = findInRoots(parentId);
                    if (!parent) return 'ERROR:Parent not found: ' + parentId;

                    if (roots.includes(parent) && sharedKeyMap[entryType]) {
                        childKey = sharedKeyMap[entryType];
                    }

                    if (!parent[childKey]) parent[childKey] = [];
                    data.parent = parent;
                    data.catalogue = parent.catalogue || parent;
                    parent[childKey].push(data);

                    return id;
                } catch(e) {
                    return 'ERROR:AddEntry: ' + e.message;
                }
            }
            """, new object[] { parentId, entryType, name ?? "", declaredId ?? "" });

        if (result?.StartsWith("ERROR:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(result[6..]);
        }

        return new GameDataActionOutputs { EntryId = result };
    }

    public void RemoveEntry(string entryId) => RemoveEntryAsync(entryId).GetAwaiter().GetResult();

    private async Task RemoveEntryAsync(string entryId)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            (entryId) => {
                try {
                    const editor = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                    const systemId = new URLSearchParams(location.search).get('systemId');
                    const loaded = editor?.gameSystems?.[systemId]?.loadedCatalogues ?? {};
                    const roots = Object.values(loaded);

                    const removeFromParent = (parent, id, seen) => {
                        if (!parent || typeof parent !== 'object' || seen.has(parent)) return false;
                        seen.add(parent);
                        for (const key of Object.keys(parent)) {
                            if (key === 'parent' || key === 'catalogue') continue;
                            const val = parent[key];
                            if (Array.isArray(val)) {
                                const idx = val.findIndex(e => e?.id === id);
                                if (idx >= 0) { val.splice(idx, 1); return true; }
                                for (const item of val) {
                                    if (removeFromParent(item, id, seen)) return true;
                                }
                            }
                        }
                        return false;
                    };
                    if (!roots.some(r => removeFromParent(r, entryId, new WeakSet()))) {
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
        => SetFieldAsync(entryId, field, value).GetAwaiter().GetResult();

    private async Task SetFieldAsync(string entryId, string field, string? value)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([entryId, field, value]) => {
                try {
            """ + FindInRootsPrelude + """
                    const entry = findInRoots(entryId);
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

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null)
        => AddLinkAsync(parentId, linkType, targetId, id).GetAwaiter().GetResult();

    private async Task<GameDataActionOutputs> AddLinkAsync(string parentId, string linkType, string targetId, string? declaredId)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([parentId, linkType, targetId, declaredId]) => {
                try {
            """ + FindInRootsPrelude + """
                    const containerKeyMap = {
                        'entryLink': 'entryLinks',
                        'infoLink': 'infoLinks',
                        'categoryLink': 'categoryLinks',
                        'catalogueLink': 'catalogueLinks',
                    };
                    const childKey = containerKeyMap[linkType] || (linkType + 's');

                    const id = declaredId || (crypto.randomUUID ? crypto.randomUUID()
                        : 'xxxx-xxxx-xxxx-xxxx'.replace(/x/g, () => Math.floor(Math.random()*16).toString(16)));

                    const data = { id, targetId, name: '', hidden: false };
                    if (linkType === 'entryLink') { data.type = 'selectionEntry'; data.import = true; }
                    if (linkType === 'categoryLink') data.type = 'category';

                    const parent = findInRoots(parentId);
                    if (!parent) return 'ERROR:Parent not found: ' + parentId;

                    if (!parent[childKey]) parent[childKey] = [];
                    data.parent = parent;
                    data.catalogue = parent.catalogue || parent;
                    parent[childKey].push(data);

                    return id;
                } catch(e) {
                    return 'ERROR:AddLink: ' + e.message;
                }
            }
            """, new object[] { parentId, linkType, targetId, declaredId ?? "" });

        if (result?.StartsWith("ERROR:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(result[6..]);
        }

        return new GameDataActionOutputs { EntryId = result };
    }

    public void SetCost(string entryId, string costTypeId, string? value)
        => SetCostAsync(entryId, costTypeId, value).GetAwaiter().GetResult();

    /// <summary>Set/clear a cost on an entry's real <c>costs</c> array (<c>{typeId, value, name}</c>).</summary>
    private async Task SetCostAsync(string entryId, string costTypeId, string? value)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([entryId, costTypeId, value]) => {
                try {
            """ + FindInRootsPrelude + """
                    const entry = findInRoots(entryId);
                    if (!entry) return 'Entry not found: ' + entryId;

                    if (!Array.isArray(entry.costs)) entry.costs = [];
                    const idx = entry.costs.findIndex(c => c && c.typeId === costTypeId);
                    if (value === null || value === '') {
                        if (idx >= 0) entry.costs.splice(idx, 1);
                    } else {
                        const num = Number(value);
                        if (idx >= 0) {
                            entry.costs[idx].value = num;
                        } else {
                            entry.costs.push({ typeId: costTypeId, name: '', value: num });
                        }
                    }
                    return null;
                } catch (e) {
                    return 'SetCost error: ' + e.message;
                }
            }
            """, new object[] { entryId, costTypeId, value ?? "" });

        if (result != null)
        {
            throw new InvalidOperationException(result);
        }
    }

    public void SetCharacteristic(string entryId, string nameOrTypeId, string? value)
        => SetCharacteristicAsync(entryId, nameOrTypeId, value).GetAwaiter().GetResult();

    /// <summary>
    /// Set/clear a characteristic on a profile entry's real <c>characteristics</c> array, matched by
    /// name or typeId (<c>{name, typeId, $text}</c>; NR keeps the value in <c>$text</c>).
    /// </summary>
    private async Task SetCharacteristicAsync(string entryId, string nameOrTypeId, string? value)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            ([entryId, nameOrTypeId, value]) => {
                try {
            """ + FindInRootsPrelude + """
                    const entry = findInRoots(entryId);
                    if (!entry) return 'Entry not found: ' + entryId;

                    if (!Array.isArray(entry.characteristics)) entry.characteristics = [];
                    const idx = entry.characteristics.findIndex(
                        c => c && (c.name === nameOrTypeId || c.typeId === nameOrTypeId));
                    if (value === null || value === '') {
                        if (idx >= 0) entry.characteristics.splice(idx, 1);
                    } else if (idx >= 0) {
                        entry.characteristics[idx].$text = value;
                    } else {
                        entry.characteristics.push({ name: nameOrTypeId, typeId: nameOrTypeId, $text: value });
                    }
                    return null;
                } catch (e) {
                    return 'SetCharacteristic error: ' + e.message;
                }
            }
            """, new object[] { entryId, nameOrTypeId, value ?? "" });

        if (result != null)
        {
            throw new InvalidOperationException(result);
        }
    }

    // ===== Persistence / export (shared NR real-store serializer) =====

    public string ExportActiveFile() => ExportActiveFileAsync().GetAwaiter().GetResult();

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
    /// Load a catalogue/game-system from XML directly into the real store (parse in-page, inject the
    /// plain-object tree into <c>loadedCatalogues</c>). This is the store-direct counterpart to the UI
    /// driver's file-input upload: no navigation, so it's reliable mid-spec. The injected file is read
    /// by <see cref="NrEditorStore.ReadStateAsync"/> and serialized by NR's exporter like any other.
    /// </summary>
    private async Task<string> LoadFileAsync(string xml)
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        var result = await _page.EvaluateAsync<string?>("""
            (xml) => {
                try {
                    const editor = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                    const systemId = new URLSearchParams(location.search).get('systemId');
                    const gsSys = editor?.gameSystems?.[systemId];
                    if (!gsSys) return 'ERROR:LoadFile: no loaded game system ' + systemId;

                    const doc = new DOMParser().parseFromString(xml, 'text/xml');
                    const root = doc.querySelector('catalogue') || doc.querySelector('gameSystem');
                    if (!root) return 'ERROR:LoadFile: XML has no catalogue/gameSystem root';

                    // Recursively parse an element into a plain node, capturing every attribute and
                    // descending known child containers.
                    const childContainers = ['selectionEntries', 'selectionEntryGroups', 'rules',
                        'profiles', 'infoGroups', 'infoLinks', 'entryLinks', 'categoryLinks',
                        'constraints', 'modifiers', 'modifierGroups', 'conditions', 'conditionGroups',
                        'characteristicTypes', 'repeats', 'associations', 'attributeTypes',
                        'localConditionGroups', 'characteristics', 'costs'];
                    const parseEntry = (el) => {
                        const entry = {};
                        for (const a of el.attributes) entry[a.name] = a.value;
                        if ('hidden' in entry) entry.hidden = el.getAttribute('hidden') === 'true';
                        if ('import' in entry) entry.import = el.getAttribute('import') === 'true';
                        for (const ck of childContainers) {
                            const container = el.querySelector(':scope > ' + ck);
                            if (container && container.children.length > 0) {
                                entry[ck] = [...container.children].map(parseEntry);
                            }
                        }
                        return entry;
                    };
                    const rootContainers = [...childContainers, 'sharedSelectionEntries',
                        'sharedSelectionEntryGroups', 'sharedRules', 'sharedProfiles', 'sharedInfoGroups',
                        'forceEntries', 'categoryEntries', 'publications', 'costTypes', 'profileTypes',
                        'catalogueLinks', 'sharedForceEntries', 'sharedAssociations'];
                    const node = {};
                    for (const a of root.attributes) node[a.name] = a.value;
                    for (const ck of rootContainers) {
                        const container = root.querySelector(':scope > ' + ck);
                        if (container && container.children.length > 0) {
                            node[ck] = [...container.children].map(parseEntry);
                        }
                    }
                    const id = node.id || '';
                    if (!id) return 'ERROR:LoadFile: root has no id';
                    node.catalogue = node;
                    gsSys.loadedCatalogues[id] = node;
                    return id;
                } catch (e) {
                    return 'ERROR:LoadFile: ' + e.message;
                }
            }
            """, xml);

        if (result is null || result.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result?[6..] ?? "LoadFile: null result");
        }

        var (_, name, _) = NrEditorStore.ParseRoot(xml);
        _idToName[result] = name;
        _openId = result;
        return result;
    }

    public void Reload() => ReloadAsync().GetAwaiter().GetResult();

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
    }

    // ===== State / validation (shared real-store readers) =====

    public GameDataState GetState() => GetStateAsync().GetAwaiter().GetResult();

    private async Task<GameDataState> GetStateAsync()
    {
        if (_page is null)
        { throw new InvalidOperationException("Page not initialized"); }

        return await NrEditorStore.ReadStateAsync(_page);
    }

    public IReadOnlyList<Roster.ValidationErrorState> GetValidationErrors()
        => GetValidationErrorsAsync().GetAwaiter().GetResult();

    private async Task<IReadOnlyList<Roster.ValidationErrorState>> GetValidationErrorsAsync()
    {
        if (_page is null)
        { return []; }

        return await NrEditorStore.GetValidationErrorsAsync(_page);
    }

    public void Cleanup() => CleanupAsync().GetAwaiter().GetResult();

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
            // Best-effort cleanup — don't propagate failures to callers.
        }
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
            try
            {
                await _browser.CloseAsync();
            }
            finally
            {
                // In a finally so a throwing close can't leak the counter — a counter that drifts
                // upward is worse than no counter, because it silently invents resources that don't exist.
                _browser = null;
                if (_contextAcquired)
                {
                    _contextAcquired = false;
                    ResourceMetrics.Released("browser-context");
                }
                if (_browserAcquired)
                {
                    _browserAcquired = false;
                    ResourceMetrics.Released("browser");
                }
            }
        }
        _playwright?.Dispose();
        _playwright = null;
    }
}
