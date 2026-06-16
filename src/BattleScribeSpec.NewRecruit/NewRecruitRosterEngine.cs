using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

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
    /// <summary>
    /// Exposes the underlying browser for advanced probing in integration tests.
    /// </summary>
    internal NewRecruitBrowser Browser { get; }
    private bool _disposed;
    private ProtocolGameSystem? _gameSystem;
    private string _rosterName = "Spec Test";

    /// <summary>
    /// Performance timing collector. Populated during test execution.
    /// Access after tests to get a timing report.
    /// </summary>
    public NrPerfTimings Timings { get; } = new();

    /// <summary>
    /// When true, navigates to the roster editor UI after setup so the browser
    /// visually reflects the roster state. Useful with <c>NR_HEADLESS=false</c>
    /// for debugging and demos. State reading is unaffected (reads from JS memory).
    /// </summary>
    public bool Visual { get; set; }

    public void SetTestContext(string specId) => _rosterName = specId;

    private NewRecruitRosterEngine(NewRecruitBrowser browser)
    {
        Browser = browser;
    }

    /// <summary>
    /// Create and initialize a NewRecruitRosterEngine with a browser session.
    /// </summary>
    public static async Task<NewRecruitRosterEngine> CreateAsync(
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true,
        float? slowMo = null)
    {
        var browser = await NewRecruitBrowser.CreateAsync(baseUrl, headless, slowMo);
        return new NewRecruitRosterEngine(browser);
    }

    /// <summary>
    /// Create a NewRecruitRosterEngine in frozen (HAR replay) mode.
    /// All network requests are served from a pre-recorded HAR file (no internet required).
    /// </summary>
    public static async Task<NewRecruitRosterEngine> CreateFrozenAsync(
        string harFilePath,
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true,
        float? slowMo = null)
    {
        var browser = await NewRecruitBrowser.CreateFrozenAsync(harFilePath, baseUrl, headless, slowMo);
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
            if (Browser.FrozenReady)
            {
                Timings.RecordSkip("NavigateToApp");
                Timings.RecordSkip("WaitForPinia");
            }
            else
            {
                await Timings.TimeAsync("NavigateToApp", Browser.NavigateToAppAsync);
                await Timings.TimeAsync("WaitForPinia", () => Browser.WaitForPiniaAsync());
            }

            // Generate BattleScribe XML from spec data
            string gstXml = null!;
            IReadOnlyList<(string FileName, string Xml)> allCatXml = null!;
            Timings.Time("XmlGeneration", () =>
            {
                gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
                allCatXml = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues);
            });

            // Build files array and catalogue name list for multi-catalogue support
            var catFiles = allCatXml.Select(c => new { name = c.FileName, path = $"/spec/{c.FileName}", data = c.Xml }).ToArray();
            var catNames = catalogues.Select(c => c.Name).ToArray();

            // Single consolidated EvaluateAsync: setup
            var setupResult = await Timings.TimeAsync("SetupJsEval", () => Browser.Page.EvaluateAsync<string?>("""
                async ([gstXml, catFiles, systemId, catNames, rosterName]) => {
                    try {
                        const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return 'Pinia store not found';

                        const sysStore = pinia._s.get('systemsStore');
                        const listsStore = pinia._s.get('lists');
                        if (!sysStore || !listsStore) return 'Required stores not found';

                        // Load synthetic data into NR's local library
                        const files = [
                            { name: systemId + '.gst', path: '/spec/' + systemId + '.gst', data: gstXml },
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
                        roster.setCustomName(rosterName);

                        // Apply defaultCostLimit as the actual max cost limits.
                        // NR's createRoster uses costs[].value (always 0) as limits;
                        // we must explicitly set them from defaultCostLimit.
                        const maxCosts = roster.getMaxCosts?.() || [];
                        if (maxCosts.length > 0) {
                            const corrected = maxCosts.map(c => ({
                                ...c,
                                value: c.defaultCostLimit >= 0 ? c.defaultCostLimit : -1
                            }));
                            roster.setMaxCosts(corrected);
                        }

                        const autoForces = roster.getForces?.() || [];
                        for (const f of [...autoForces]) {
                            if (typeof f.delete === 'function') f.delete();
                        }

                        // Build row metadata and add list
                        const selectedBook = allBooks[0].bookRef;
                        const row = {
                            list_key: 'spec_' + Date.now(),
                            name: rosterName,
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
                            bookCatalogueIds: allBooks.map(b => b.bookRef.bsid || ''),
                            row
                        };

                        return null; // success
                    } catch(e) {
                        return 'Setup error: ' + e.message + '\n' + e.stack;
                    }
                }
                """, new object[] { gstXml, catFiles, gameSystem.Id, catNames,
                    _rosterName }));

            if (setupResult != null)
            {
                errors.Add(setupResult);
            }

            // Mark frozen mode as ready to skip navigation on subsequent setups
            if (setupResult == null && Browser.IsFrozen)
            {
                Browser.FrozenReady = true;
            }

            // In visual mode, navigate to the roster editor so the UI shows the roster
            if (setupResult == null && Visual)
            {
                await NavigateToEditorVisualAsync();
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
        return SetupFromFilesAsync(files).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupFromFilesAsync(IReadOnlyList<(string FileName, string Content)> files)
    {
        var errors = new List<string>();
        try
        {
            await Browser.NavigateToAppAsync();
            await Browser.WaitForPiniaAsync();

            // Build files array for loadSystemFromFs
            var fileData = files.Select(f => new { name = f.FileName, path = $"/spec/{f.FileName}", data = f.Content }).ToArray();

            var setupResult = await Browser.Page.EvaluateAsync<string?>("""
                async ([fileData, rosterName]) => {
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
                        roster.setCustomName(rosterName);

                        // Apply defaultCostLimit as actual max cost limits
                        const maxCosts = roster.getMaxCosts?.() || [];
                        if (maxCosts.length > 0) {
                            const corrected = maxCosts.map(c => ({
                                ...c,
                                value: c.defaultCostLimit >= 0 ? c.defaultCostLimit : -1
                            }));
                            roster.setMaxCosts(corrected);
                        }

                        const autoForces = roster.getForces?.() || [];
                        for (const f of [...autoForces]) {
                            if (typeof f.delete === 'function') f.delete();
                        }

                        const selectedBook = allBooks[0].bookRef;
                        const row = {
                            list_key: 'spec_' + Date.now(),
                            name: rosterName,
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
                            bookCatalogueIds: allBooks.map(b => b.bookRef.bsid || ''),
                            row
                        };

                        return null; // success
                    } catch(e) {
                        return 'Setup error: ' + e.message + '\n' + e.stack;
                    }
                }
                """, new object[] { fileData, _rosterName });

            if (setupResult != null)
            {
                errors.Add(setupResult);
            }

            // In visual mode, navigate to the roster editor so the UI shows the roster
            if (setupResult == null && Visual)
            {
                await NavigateToEditorVisualAsync();
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Setup exception: {ex.Message}");
        }

        return errors;
    }

    public ActionOutputs AddForce(string forceEntryId, string catalogueId)
    {
        var forceId = NewRecruitActions.AddForceByIdAsync(Browser.Page, forceEntryId, catalogueId)
            .GetAwaiter().GetResult();
        // Collect auto-selected entries (from min constraints)
        var selections = forceId is not null
            ? NewRecruitActions.GetForceAutoSelectionsAsync(Browser.Page, forceId)
                .GetAwaiter().GetResult()
            : null;
        return new ActionOutputs { ForceId = forceId, Selections = selections };
    }

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
    {
        var forceId = NewRecruitActions.AddChildForceByIdAsync(Browser.Page, parentForceId, forceEntryId, catalogueId)
            .GetAwaiter().GetResult();
        return new ActionOutputs { ForceId = forceId };
    }

    public void RemoveForce(string forceId)
    {
        NewRecruitActions.RemoveForceAsync(Browser.Page, forceId)
            .GetAwaiter().GetResult();
    }

    public ActionOutputs SelectEntry(string forceId, string entryId)
    {
        var selectionId = NewRecruitActions.SelectEntryByIdAsync(Browser.Page, forceId, entryId)
            .GetAwaiter().GetResult();
        return new ActionOutputs { SelectionId = selectionId };
    }

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
    {
        var selectionId = NewRecruitActions.SelectChildEntryByIdAsync(Browser.Page, forceId, parentSelectionId, entryId)
            .GetAwaiter().GetResult();
        return new ActionOutputs { SelectionId = selectionId };
    }

    public void DeselectSelection(string forceId, string selectionId)
    {
        NewRecruitActions.DeselectSelectionAsync(Browser.Page, forceId, selectionId)
            .GetAwaiter().GetResult();
    }

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        NewRecruitActions.SetSelectionCountAsync(Browser.Page, forceId, selectionId, count)
            .GetAwaiter().GetResult();
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
    {
        var newSelectionId = NewRecruitActions.DuplicateSelectionAsync(Browser.Page, forceId, selectionId)
            .GetAwaiter().GetResult();
        return new ActionOutputs { SelectionId = newSelectionId };
    }

    public ActionOutputs DuplicateForce(string forceId)
    {
        var newForceId = NewRecruitActions.DuplicateForceAsync(Browser.Page, forceId)
            .GetAwaiter().GetResult();
        return new ActionOutputs { ForceId = newForceId };
    }

    public void SetCostLimit(string costTypeId, decimal value)
    {
        NewRecruitActions.SetCostLimitAsync(Browser.Page, costTypeId, value)
            .GetAwaiter().GetResult();
    }

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
    {
        NewRecruitActions.SetCustomizationAsync(Browser.Page, forceId, selectionId, categoryEntryId, customName, customNotes)
            .GetAwaiter().GetResult();
    }

    public RosterState GetRosterState()
    {
        Timings.StartPhase("GetRosterState");
        try
        {
            return NewRecruitStateReader.ReadRosterStateAsync(Browser.Page)
                .GetAwaiter().GetResult();
        }
        finally
        {
            Timings.EndPhase();
        }
    }

    /// <summary>
    /// Navigate to the roster editor page so the NR UI visually reflects roster state.
    /// Uses Vue Router client-side navigation — preserves all JS state.
    /// </summary>
    private async Task NavigateToEditorVisualAsync()
    {
        var listKey = await Browser.Page.EvaluateAsync<string?>(
            "window.__bsspec?.row?.list_key");
        if (listKey != null)
        {
            await Browser.NavigateToEditorAsync(listKey);
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
            await Browser.WaitForPiniaAsync();

            var cleanupError = await Browser.Page.EvaluateAsync<string?>("""
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
            {
                Console.Error.WriteLine($"[NewRecruitRosterEngine] {cleanupError}");
            }
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
                Browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                _disposed = true;
            }
        }
    }

}
