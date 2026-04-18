using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// IRosterEngine implementation that wraps the New Recruit web app via Playwright.
///
/// Supports two data loading modes:
/// 1. Synthetic (inline) data: Generate BattleScribe XML via CatXmlGenerator,
///    load into NR via loadSystemFromFs Pinia store API.
/// 2. Real-world data: Select from NR's remote library via UI click.
///
/// State is read from the roster tree via getCurrentList().army using
/// NR's internal reactive object API (getChildren, getName, getCosts, etc.)
/// </summary>
public sealed class NewRecruitRosterEngine : IRosterEngine
{
    private readonly NewRecruitBrowser _browser;

    /// <summary>
    /// Exposes the underlying browser for advanced probing in integration tests.
    /// </summary>
    internal NewRecruitBrowser Browser => _browser;
    private bool _disposed;
    private ProtocolGameSystem? _gameSystem;
    private ProtocolCatalogue[]? _catalogues;
    // Maps force index → catalogue index (tracked as forces are added)
    private readonly List<int> _forceCatalogueMap = [];

    /// <summary>
    /// Performance timing collector. Populated during test execution.
    /// Access after tests to get a timing report.
    /// </summary>
    public NrPerfTimings Timings { get; } = new();

    private NewRecruitRosterEngine(NewRecruitBrowser browser)
    {
        _browser = browser;
    }

    /// <summary>
    /// Create and initialize a NewRecruitRosterEngine with a browser session.
    /// </summary>
    public static async Task<NewRecruitRosterEngine> CreateAsync(
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        var browser = await NewRecruitBrowser.CreateAsync(baseUrl, headless);
        return new NewRecruitRosterEngine(browser);
    }

    /// <summary>
    /// Create a NewRecruitRosterEngine in frozen (HAR replay) mode.
    /// All network requests are served from a pre-recorded HAR file (no internet required).
    /// </summary>
    public static async Task<NewRecruitRosterEngine> CreateFrozenAsync(
        string harFilePath,
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        var browser = await NewRecruitBrowser.CreateFrozenAsync(harFilePath, baseUrl, headless);
        return new NewRecruitRosterEngine(browser);
    }

    /// <summary>
    /// Create a NewRecruitRosterEngine wrapping an existing browser context.
    /// Used by <see cref="NewRecruitEnginePool"/> for pooled engines.
    /// </summary>
    internal static NewRecruitRosterEngine CreateFromBrowser(NewRecruitBrowser browser)
    {
        return new NewRecruitRosterEngine(browser);
    }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        _gameSystem = gameSystem;
        _catalogues = catalogues;
        _forceCatalogueMap.Clear();
        return SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupAsync(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var errors = new List<string>();

        try
        {
            // In frozen mode after the first setup, the page is already at /app
            // with Pinia initialized. The JS cleanup block handles state reset,
            // so we can skip the expensive navigation + Pinia polling.
            if (_browser.FrozenReady)
            {
                Timings.RecordSkip("NavigateToApp");
                Timings.RecordSkip("WaitForPinia");
            }
            else
            {
                await Timings.TimeAsync("NavigateToApp", () => _browser.NavigateToAppAsync());
                await Timings.TimeAsync("WaitForPinia", () => _browser.WaitForPiniaAsync());
            }

            // Pre-inject JS helpers and state reader (once per page lifetime)
            await Timings.TimeAsync("InjectHelpers", () => _browser.InjectHelpersAsync());

            // Generate BattleScribe XML from spec data
            string gstXml = null!;
            IReadOnlyList<(string FileName, string Xml)> allCatXml = null!;
            Timings.Time("XmlGeneration", () =>
            {
                gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
                allCatXml = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues);
            });

            // Build entry order from catalogue-defined selection entries
            List<string>? entryOrder = null;
            if (catalogues.Length > 0)
            {
                entryOrder = new List<string>();
                foreach (var cat in catalogues)
                    CollectEntryIds(cat.SelectionEntries, entryOrder);
            }

            // Build cost limit config from game system cost types
            Dictionary<string, double?>? costLimitConfig = null;
            if (gameSystem.CostTypes is { Count: > 0 })
            {
                costLimitConfig = new Dictionary<string, double?>();
                foreach (var ct in gameSystem.CostTypes)
                    costLimitConfig[ct.Name] = ct.DefaultCostLimit;
            }

            // Build files array and catalogue name list for multi-catalogue support
            var catFiles = allCatXml.Select(c => new { name = c.FileName, path = $"/spec/{c.FileName}", data = c.Xml }).ToArray();
            var catNames = catalogues.Select(c => c.Name).ToArray();

            // Single consolidated EvaluateAsync: setup + entryOrder + costLimitConfig
            var setupResult = await Timings.TimeAsync("SetupJsEval", () => _browser.Page.EvaluateAsync<string?>("""
                async ([gstXml, catFiles, systemId, catNames, entryOrder, costLimitConfig]) => {
                    try {
                        const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return 'Pinia store not found';

                        const sysStore = pinia._s.get('systemsStore');
                        const listsStore = pinia._s.get('lists');
                        if (!sysStore || !listsStore) return 'Required stores not found';

                        // Load synthetic data into NR's local library
                        const files = [
                            { name: 'system.gst', path: '/spec/system.gst', data: gstXml },
                            ...catFiles.map(c => ({ name: c.name, path: c.path, data: c.data })),
                        ];
                        await sysStore.loadSystemFromFs(files);

                        // Select the locally loaded system
                        const localSys = sysStore.localLibrary[systemId];
                        if (!localSys) return 'System not found in localLibrary after load: ' + systemId;
                        sysStore.selectSystem(localSys);

                        const sys = sysStore._selectedSystem;
                        if (!sys) return 'No selected system after selectSystem()';

                        // Find playable books (catalogues)
                        const playableBooks = sys.books?.array?.filter(b => b.playable) || [];
                        if (!playableBooks.length) return 'No playable books for system: ' + sys.name;

                        // Load ALL book data for multi-catalogue support
                        const allBooks = [];
                        for (const catName of catNames) {
                            let pb = playableBooks.find(b => b.name === catName);
                            if (!pb) pb = playableBooks.find(b => b.name.includes(catName) || catName.includes(b.name));
                            if (!pb && allBooks.length === 0) pb = playableBooks[0];
                            if (pb) {
                                const bd = await sys.getBook(pb.id);
                                if (bd) {
                                    const gs = bd.catalogue.gameSystem;
                                    bd.catalogue.costIndex = {};
                                    if (gs?.costTypes) {
                                        for (const ct of gs.costTypes) {
                                            bd.catalogue.costIndex[ct.id] = ct;
                                        }
                                    }
                                    allBooks.push({ name: catName, bookRef: pb, bookData: bd });
                                }
                            }
                        }
                        if (!allBooks.length) return 'No book data loaded for any catalogue';

                        // Create roster from first book, then remove auto-created forces
                        const primaryBook = allBooks[0].bookData;
                        const costs = primaryBook.getCosts();
                        const roster = primaryBook.createRoster(costs);
                        if (!roster) return 'Failed to create roster';
                        roster.setCustomName('Spec Test');

                        const autoForces = roster.getForces?.() || [];
                        for (const f of [...autoForces]) {
                            if (typeof f.delete === 'function') f.delete();
                        }

                        // Build row metadata and add list
                        const selectedBook = allBooks[0].bookRef;
                        const row = {
                            list_key: 'spec_' + Date.now(),
                            name: 'Spec Test',
                            id_game_system: selectedBook.id_game_system || sys.id,
                            id_system: selectedBook.id || sys.id,
                            nrversion: selectedBook.nrversion,
                            date_mod: new Date(),
                            date_create: new Date(),
                            synced: false,
                            uid: null,
                            bsid_book: selectedBook.bsid,
                            bsid_system: sys.bsid
                        };

                        await listsStore.addList({row, army: roster, book: primaryBook});

                        // Save references globally — books array for multi-catalogue AddForce
                        // Also inject entryOrder and costLimitConfig inline
                        window.__bsspec = {
                            army: roster,
                            book: primaryBook,
                            books: allBooks.map(b => b.bookData),
                            row,
                            entryOrder: entryOrder || null,
                            costLimitConfig: costLimitConfig || null
                        };

                        return null; // success
                    } catch(e) {
                        return 'Setup error: ' + e.message + '\n' + e.stack;
                    }
                }
                """, new object[] { gstXml, catFiles, gameSystem.Id, catNames,
                    entryOrder?.ToArray() ?? (object)Array.Empty<string>(),
                    costLimitConfig ?? (object)new Dictionary<string, double?>() }));

            if (setupResult != null)
                errors.Add(setupResult);

            // Mark frozen mode as ready to skip navigation on subsequent setups
            if (setupResult == null && _browser.IsFrozen)
                _browser.FrozenReady = true;
        }
        catch (Exception ex)
        {
            errors.Add($"Setup exception: {ex.Message}");
        }

        return errors;
    }

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
    {
        _gameSystem = null;
        _catalogues = null;
        _forceCatalogueMap.Clear();
        return SetupFromFilesAsync(files).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupFromFilesAsync(IReadOnlyList<(string FileName, string Content)> files)
    {
        var errors = new List<string>();
        try
        {
            await _browser.NavigateToAppAsync();
            await _browser.WaitForPiniaAsync();
            await _browser.InjectHelpersAsync();

            // Build files array for loadSystemFromFs
            var fileData = files.Select(f => new { name = f.FileName, path = $"/spec/{f.FileName}", data = f.Content }).ToArray();

            var setupResult = await _browser.Page.EvaluateAsync<string?>("""
                async ([fileData]) => {
                    try {
                        const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return 'Pinia store not found';

                        const sysStore = pinia._s.get('systemsStore');
                        const listsStore = pinia._s.get('lists');
                        if (!sysStore || !listsStore) return 'Required stores not found';

                        // Load real data files into NR's local library
                        const files = fileData.map(f => ({ name: f.name, path: f.path, data: f.data }));
                        await sysStore.loadSystemFromFs(files);

                        // Find the loaded game system in localLibrary
                        const systemIds = Object.keys(sysStore.localLibrary);
                        if (!systemIds.length) return 'No systems found in localLibrary after loading files';
                        const systemId = systemIds[systemIds.length - 1]; // most recently added
                        const localSys = sysStore.localLibrary[systemId];
                        sysStore.selectSystem(localSys);

                        const sys = sysStore._selectedSystem;
                        if (!sys) return 'No selected system after selectSystem()';

                        // Find playable books (catalogues)
                        const playableBooks = sys.books?.array?.filter(b => b.playable) || [];
                        if (!playableBooks.length) return 'No playable books for system: ' + sys.name;

                        // Load ALL playable book data
                        const allBooks = [];
                        for (const pb of playableBooks) {
                            const bd = await sys.getBook(pb.id);
                            if (bd) {
                                const gs = bd.catalogue.gameSystem;
                                bd.catalogue.costIndex = {};
                                if (gs?.costTypes) {
                                    for (const ct of gs.costTypes) {
                                        bd.catalogue.costIndex[ct.id] = ct;
                                    }
                                }
                                allBooks.push({ name: pb.name, bookRef: pb, bookData: bd });
                            }
                        }
                        if (!allBooks.length) return 'No book data loaded';

                        // Create roster from first book, remove auto-forces
                        const primaryBook = allBooks[0].bookData;
                        const costs = primaryBook.getCosts();
                        const roster = primaryBook.createRoster(costs);
                        if (!roster) return 'Failed to create roster';
                        roster.setCustomName('Spec Test');

                        const autoForces = roster.getForces?.() || [];
                        for (const f of [...autoForces]) {
                            if (typeof f.delete === 'function') f.delete();
                        }

                        const selectedBook = allBooks[0].bookRef;
                        const row = {
                            list_key: 'spec_' + Date.now(),
                            name: 'Spec Test',
                            id_game_system: selectedBook.id_game_system || sys.id,
                            id_system: selectedBook.id || sys.id,
                            nrversion: selectedBook.nrversion,
                            date_mod: new Date(),
                            date_create: new Date(),
                            synced: false,
                            uid: null,
                            bsid_book: selectedBook.bsid,
                            bsid_system: sys.bsid
                        };

                        await listsStore.addList({row, army: roster, book: primaryBook});

                        window.__bsspec = {
                            army: roster,
                            book: primaryBook,
                            books: allBooks.map(b => b.bookData),
                            row
                        };

                        // Populate entryOrder from catalogues so the state
                        // reader can sort selections to match BS ordering.
                        const entryOrder = [];
                        function collectEntryIds(entries) {
                            if (!entries) return;
                            for (const e of entries) {
                                if (e.id) entryOrder.push(e.id);
                                collectEntryIds(e.selectionEntries);
                                collectEntryIds(e.selectionEntryGroups);
                            }
                        }
                        for (const b of allBooks) {
                            const cat = b.bookData?.catalogue;
                            if (cat) {
                                collectEntryIds(cat.selectionEntries);
                                collectEntryIds(cat.sharedSelectionEntries);
                            }
                        }
                        window.__bsspec.entryOrder = entryOrder;

                        // Populate costLimitConfig from game system cost types
                        // so the reader can distinguish "no limit" from "limit=0".
                        const costLimitConfig = {};
                        const gs = primaryBook?.catalogue?.gameSystem;
                        if (gs?.costTypes) {
                            for (const ct of gs.costTypes) {
                                costLimitConfig[ct.name] = ct.defaultCostLimit ?? -1;
                            }
                        }
                        window.__bsspec.costLimitConfig = costLimitConfig;

                        return null; // success
                    } catch(e) {
                        return 'Setup error: ' + e.message + '\n' + e.stack;
                    }
                }
                """, new object[] { fileData });

            if (setupResult != null)
                errors.Add(setupResult);
        }
        catch (Exception ex)
        {
            errors.Add($"Setup exception: {ex.Message}");
        }

        return errors;
    }

    public void AddForceByName(string forceName, string? catalogueName = null, int catalogueIndex = 0)
    {
        NewRecruitActions.AddForceByNameAsync(_browser.Page, forceName, catalogueIndex)
            .GetAwaiter().GetResult();
        _forceCatalogueMap.Add(catalogueIndex);
    }

    public void SelectEntryByName(int forceIndex, string entryName)
    {
        NewRecruitActions.SelectEntryByNameAsync(_browser.Page, forceIndex, entryName)
            .GetAwaiter().GetResult();
    }

    public void SelectChildEntryByName(int forceIndex, int selectionIndex, string childEntryName)
    {
        NewRecruitActions.SelectChildEntryByNameAsync(_browser.Page, forceIndex, selectionIndex, childEntryName)
            .GetAwaiter().GetResult();
    }

    public void AddForce(int forceEntryIndex, int catalogueIndex = 0)
    {
        // Build combined list: GS force entries + catalogue force entries
        var allForceEntries = new List<ProtocolForceEntry>();
        if (_gameSystem?.ForceEntries != null)
            allForceEntries.AddRange(_gameSystem.ForceEntries);
        if (_catalogues != null)
            foreach (var cat in _catalogues)
                if (cat.ForceEntries != null)
                    allForceEntries.AddRange(cat.ForceEntries);
        var forceEntry = allForceEntries.ElementAtOrDefault(forceEntryIndex);
        var forceId = forceEntry?.Id;
        if (forceId is null)
            throw new ArgumentOutOfRangeException(nameof(forceEntryIndex),
                $"Force entry index {forceEntryIndex} out of range ({allForceEntries.Count} available)");
        NewRecruitActions.AddForceByIdAsync(_browser.Page, forceId, catalogueIndex)
            .GetAwaiter().GetResult();
        _forceCatalogueMap.Add(catalogueIndex);
    }

    public void RemoveForce(int forceIndex)
    {
        NewRecruitActions.RemoveForceAsync(_browser.Page, forceIndex)
            .GetAwaiter().GetResult();
        if (forceIndex < _forceCatalogueMap.Count)
            _forceCatalogueMap.RemoveAt(forceIndex);
    }

    public void SelectEntry(int forceIndex, int entryIndex)
    {
        // Determine which catalogue this force belongs to
        var catIdx = forceIndex < _forceCatalogueMap.Count ? _forceCatalogueMap[forceIndex] : 0;
        var cat = _catalogues?.ElementAtOrDefault(catIdx) ?? _catalogues?.FirstOrDefault();
        // Build ordered list: catalogue entries, then GameSystem-level entries
        var entryIds = (cat?.SelectionEntries ?? []).Select(e => e.Id)
            .Concat((cat?.EntryLinks ?? []).Select(el => el.TargetId))
            .Concat((_gameSystem?.SelectionEntries ?? []).Select(e => e.Id))
            .Concat((_gameSystem?.EntryLinks ?? []).Select(el => el.TargetId))
            .ToList();
        if (entryIndex >= entryIds.Count)
            throw new ArgumentOutOfRangeException(nameof(entryIndex),
                $"Entry index {entryIndex} out of range (catalogue has {entryIds.Count} entries)");
        NewRecruitActions.SelectEntryByIdAsync(_browser.Page, forceIndex, entryIds[entryIndex])
            .GetAwaiter().GetResult();
    }

    public void SelectChildEntry(int forceIndex, int selectionIndex, int childEntryIndex)
    {
        // Resolve child entry ID: find the parent selection's entry,
        // then flatten all children (direct, groups, links) by index
        var state = GetRosterState();
        if (forceIndex >= state.Forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        if (selectionIndex >= state.Forces[forceIndex].Selections.Count)
            throw new ArgumentOutOfRangeException(nameof(selectionIndex));

        var parentEntryId = state.Forces[forceIndex].Selections[selectionIndex].EntryId;
        var parentEntry = FindEntryById(parentEntryId);
        var childEntries = FlattenChildEntries(parentEntry);
        if (childEntryIndex >= childEntries.Count)
            throw new ArgumentOutOfRangeException(nameof(childEntryIndex),
                $"Child entry index {childEntryIndex} out of range for parent '{parentEntryId}'");

        var childEntryId = childEntries[childEntryIndex];
        NewRecruitActions.SelectChildEntryByIdAsync(_browser.Page, forceIndex, selectionIndex, childEntryId)
            .GetAwaiter().GetResult();
    }

    private ProtocolSelectionEntry? FindEntryById(string? id)
    {
        if (id is null) return null;
        // Search catalogues
        if (_catalogues is not null)
        {
            foreach (var cat in _catalogues)
            {
                var found = FindEntryRecursive(cat.SelectionEntries, id)
                    ?? FindEntryRecursive(cat.SharedSelectionEntries, id);
                if (found is not null) return found;
            }
        }
        // Search GameSystem entries
        if (_gameSystem is not null)
        {
            var found = FindEntryRecursive(_gameSystem.SelectionEntries, id)
                ?? FindEntryRecursive(_gameSystem.SharedSelectionEntries, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static ProtocolSelectionEntry? FindEntryRecursive(List<ProtocolSelectionEntry>? entries, string id)
    {
        if (entries is null) return null;
        foreach (var entry in entries)
        {
            if (entry.Id == id) return entry;
            var found = FindEntryRecursive(entry.SelectionEntries, id);
            if (found is not null) return found;
        }
        return null;
    }

    public void DeselectSelection(int forceIndex, int selectionIndex)
    {
        NewRecruitActions.DeselectSelectionAsync(_browser.Page, forceIndex, selectionIndex)
            .GetAwaiter().GetResult();
    }

    public void SetSelectionCount(int forceIndex, int entryIndex, int count)
    {
        NewRecruitActions.SetSelectionCountAsync(_browser.Page, forceIndex, entryIndex, count)
            .GetAwaiter().GetResult();
    }

    public void DuplicateSelection(int forceIndex, int selectionIndex)
    {
        NewRecruitActions.DuplicateSelectionAsync(_browser.Page, forceIndex, selectionIndex)
            .GetAwaiter().GetResult();
    }

    public void SetCostLimit(string costTypeId, double value)
    {
        NewRecruitActions.SetCostLimitAsync(_browser.Page, costTypeId, value)
            .GetAwaiter().GetResult();
    }

    public RosterState GetRosterState()
    {
        Timings.StartPhase("GetRosterState");
        try
        {
            return NewRecruitStateReader.ReadRosterStateAsync(_browser.Page)
                .GetAwaiter().GetResult();
        }
        finally
        {
            Timings.EndPhase();
        }
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
    {
        return GetRosterState().ValidationErrors;
    }

    public void Cleanup()
    {
        CleanupAsync().GetAwaiter().GetResult();
    }

    private async Task CleanupAsync()
    {
        try
        {
            await _browser.WaitForPiniaAsync();

            var cleanupError = await _browser.Page.EvaluateAsync<string?>("""
                async () => {
                    try {
                        const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return null; // no stores — nothing to clean

                        const sysStore = pinia._s.get('systemsStore');
                        const listsStore = pinia._s.get('lists');
                        if (!sysStore || !listsStore) return null;

                        if (window.__bsspec?.row?.list_key) {
                            const currentList = listsStore.getCurrentList?.();
                            if (currentList?.army) {
                                const forces = currentList.army.getForces?.() || [];
                                for (const f of [...forces]) {
                                    if (typeof f.delete === 'function') f.delete();
                                }
                            }
                            await listsStore.deleteList?.(window.__bsspec.row.list_key);
                        }
                        for (const key of Object.keys(sysStore.localLibrary || {})) {
                            delete sysStore.localLibrary[key];
                        }
                        window.__bsspec = undefined;
                        return null;
                    } catch(e) {
                        const errorText = e?.stack ?? e?.message ?? String(e);
                        return 'Cleanup error: ' + errorText;
                    }
                }
                """);

            if (cleanupError != null)
                Console.Error.WriteLine($"[NewRecruitRosterEngine] {cleanupError}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NewRecruitRosterEngine] Cleanup failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Flatten child entry IDs from a parent entry, including direct children,
    /// entries from SelectionEntryGroups (recursive), and resolved EntryLinks.
    /// Mirrors OracleRosterEngine.FlattenChildEntries behavior.
    /// </summary>
    private IReadOnlyList<string> FlattenChildEntries(ProtocolSelectionEntry? entry)
    {
        if (entry is null) return [];
        var result = new List<string>();
        if (entry.SelectionEntries is not null)
            result.AddRange(entry.SelectionEntries.Select(e => e.Id));
        if (entry.SelectionEntryGroups is not null)
        {
            foreach (var group in entry.SelectionEntryGroups)
                FlattenGroupEntries(group, result);
        }
        if (entry.EntryLinks is not null)
        {
            foreach (var link in entry.EntryLinks)
                result.Add(link.TargetId);
        }
        return result;
    }

    private void FlattenGroupEntries(ProtocolSelectionEntryGroup group, List<string> result)
    {
        if (group.SelectionEntries is not null)
            result.AddRange(group.SelectionEntries.Select(e => e.Id));
        if (group.SelectionEntryGroups is not null)
        {
            foreach (var nested in group.SelectionEntryGroups)
                FlattenGroupEntries(nested, result);
        }
        if (group.EntryLinks is not null)
        {
            foreach (var link in group.EntryLinks)
                result.Add(link.TargetId);
        }
    }

    /// <summary>
    /// Recursively collect entry IDs from selection entries in catalogue-defined order.
    /// Used to preserve XML ordering for state reader sorting.
    /// </summary>
    private static void CollectEntryIds(List<ProtocolSelectionEntry>? entries, List<string> ids)
    {
        if (entries is null) return;
        foreach (var entry in entries)
        {
            ids.Add(entry.Id);
            CollectEntryIds(entry.SelectionEntries, ids);
        }
    }
}
