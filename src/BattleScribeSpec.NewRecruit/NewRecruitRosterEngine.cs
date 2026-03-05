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
    private GameSystemSpec? _gameSystem;
    private CatalogueSpec[]? _catalogues;
    // Maps force index → catalogue index (tracked as forces are added)
    private readonly List<int> _forceCatalogueMap = [];

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

    public IReadOnlyList<string> Setup(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        _gameSystem = gameSystem;
        _catalogues = catalogues;
        return SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupAsync(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        var errors = new List<string>();

        try
        {
            // Navigate to /app and wait for NR to initialize
            await _browser.NavigateToAppAsync();
            await _browser.Page.WaitForTimeoutAsync(2000);

            // Generate BattleScribe XML from spec data
            var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
            var allCatXml = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues);

            // Build files array and catalogue name list for multi-catalogue support
            var catFiles = allCatXml.Select(c => new { name = c.FileName, path = $"/spec/{c.FileName}", data = c.Xml }).ToArray();
            var catNames = catalogues.Select(c => c.Name).ToArray();
            var setupResult = await _browser.Page.EvaluateAsync<string?>("""
                async ([gstXml, catFiles, systemId, catNames]) => {
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
                        window.__bsspec = {
                            army: roster,
                            book: primaryBook,
                            books: allBooks.map(b => b.bookData),
                            row
                        };

                        return null; // success
                    } catch(e) {
                        return 'Setup error: ' + e.message + '\n' + e.stack;
                    }
                }
                """, new object[] { gstXml, catFiles, gameSystem.Id, catNames });

            if (setupResult != null)
                errors.Add(setupResult);

            // Save catalogue-defined entry order to window.__bsspec.entryOrder
            // so the state reader can sort selections to match BS ordering.
            if (setupResult == null && catalogues.Length > 0)
            {
                var entryOrder = new List<string>();
                foreach (var cat in catalogues)
                    CollectEntryIds(cat.SelectionEntries, entryOrder);
                await _browser.Page.EvaluateAsync(
                    "entryOrder => { if (window.__bsspec) window.__bsspec.entryOrder = entryOrder; }",
                    entryOrder.ToArray());
            }
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
        return SetupFromFilesAsync(files).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupFromFilesAsync(IReadOnlyList<(string FileName, string Content)> files)
    {
        var errors = new List<string>();
        try
        {
            await _browser.NavigateToAppAsync();
            await _browser.Page.WaitForTimeoutAsync(2000);

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

    public void AddForceByName(string forceName, int catalogueIndex = 0)
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
        var forceEntry = _gameSystem?.ForceEntries?.ElementAtOrDefault(forceEntryIndex);
        var forceId = forceEntry?.Id;
        if (forceId is null)
            throw new ArgumentOutOfRangeException(nameof(forceEntryIndex),
                $"Force entry index {forceEntryIndex} out of range ({_gameSystem?.ForceEntries?.Length ?? 0} available)");
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
        // Build ordered list: direct SelectionEntries followed by resolved EntryLinks
        var entryIds = (cat?.SelectionEntries ?? []).Select(e => e.Id)
            .Concat((cat?.EntryLinks ?? []).Select(el => el.TargetId))
            .ToList();
        if (entryIndex >= entryIds.Count)
            throw new ArgumentOutOfRangeException(nameof(entryIndex),
                $"Entry index {entryIndex} out of range (catalogue has {entryIds.Count} entries)");
        NewRecruitActions.SelectEntryByIdAsync(_browser.Page, forceIndex, entryIds[entryIndex])
            .GetAwaiter().GetResult();
    }

    public void SelectChildEntry(int forceIndex, int selectionIndex, int childEntryIndex)
    {
        // Resolve child entry ID: find the parent selection's entry in the catalogue,
        // then get the child entry by index
        var state = GetRosterState();
        if (forceIndex >= state.Forces.Count)
            throw new ArgumentOutOfRangeException(nameof(forceIndex));
        if (selectionIndex >= state.Forces[forceIndex].Selections.Count)
            throw new ArgumentOutOfRangeException(nameof(selectionIndex));

        var parentEntryId = state.Forces[forceIndex].Selections[selectionIndex].EntryId;
        var parentEntry = FindEntryById(parentEntryId);
        var childEntries = parentEntry?.ChildEntries;
        if (childEntries is null || childEntryIndex >= childEntries.Length)
            throw new ArgumentOutOfRangeException(nameof(childEntryIndex),
                $"Child entry index {childEntryIndex} out of range for parent '{parentEntryId}'");

        var childEntryId = childEntries[childEntryIndex].Id;
        NewRecruitActions.SelectChildEntryByIdAsync(_browser.Page, forceIndex, selectionIndex, childEntryId)
            .GetAwaiter().GetResult();
    }

    private SelectionEntrySpec? FindEntryById(string? id)
    {
        if (id is null || _catalogues is null) return null;
        foreach (var cat in _catalogues)
        {
            var found = FindEntryRecursive(cat.SelectionEntries, id)
                ?? FindEntryRecursive(cat.SharedSelectionEntries, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static SelectionEntrySpec? FindEntryRecursive(SelectionEntrySpec[]? entries, string id)
    {
        if (entries is null) return null;
        foreach (var entry in entries)
        {
            if (entry.Id == id) return entry;
            var found = FindEntryRecursive(entry.ChildEntries, id);
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
        return NewRecruitStateReader.ReadRosterStateAsync(_browser.Page)
            .GetAwaiter().GetResult();
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
    {
        return GetRosterState().ValidationErrors;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Recursively collect entry IDs from selection entries in catalogue-defined order.
    /// Used to preserve XML ordering for state reader sorting.
    /// </summary>
    private static void CollectEntryIds(SelectionEntrySpec[]? entries, List<string> ids)
    {
        if (entries is null) return;
        foreach (var entry in entries)
        {
            ids.Add(entry.Id);
            CollectEntryIds(entry.ChildEntries, ids);
        }
    }
}
