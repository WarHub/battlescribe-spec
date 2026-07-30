using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;

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
            await NavigateToRosterEditorAsync(listKey);
        }
    }

    /// <summary>
    /// Put the app on <c>/app/Lists/{listKey}</c> — the roster editor for <em>this</em> spec's list —
    /// and prove it stayed there.
    /// <para>
    /// NR's editor page does not resolve the <c>:list</c> route param against the whole list store. It
    /// calls <c>findListByKey(key, [selectedSystem.id, selectedSystem.bsid])</c>, which first filters
    /// <c>listData</c> down to the rows belonging to the <em>currently selected game system</em>. A row
    /// owned by any other system is invisible to that lookup, and the page then falls through
    /// <c>findMostRecentList(selectedSystem.id)</c> to <c>router.push({name:'app-MyLists'})</c> — it
    /// bounces to the lists index. The list existing in the store is therefore not sufficient: the
    /// system that OWNS the list has to be the selected one at the moment we push.
    /// </para>
    /// <para>
    /// Engine reuse is what breaks that. A pooled browser context runs dozens of specs;
    /// <c>library.array</c> retains every system it ever loaded, so a stale one can be re-selected
    /// while a later spec is running. Measured on the failing exports: the roster's own system was the
    /// only entry in <c>localLibrary</c> and its row was present in <c>listData</c>
    /// (<c>rowPresent: true</c>), yet <c>selectedSystem</c> was a previous spec's game system and NR's
    /// own lookup returned null — <c>router.afterEach</c> recorded the push to the editor immediately
    /// followed by a redirect back to <c>/app/MyLists</c>. Re-asserting the selection is the fix;
    /// waiting longer never could be, because the app had left the editor route and was staying away.
    /// </para>
    /// </summary>
    private async Task NavigateToRosterEditorAsync(string listKey)
    {
        var unresolvable = await Browser.Page.EvaluateAsync<string?>(PrepareEditorRouteJs, listKey);
        if (unresolvable is not null)
        {
            throw new InvalidOperationException($"NewRecruit editor navigation failed: {unresolvable}");
        }

        await Browser.NavigateToEditorAsync(listKey);

        var redirected = await Browser.Page.EvaluateAsync<string?>(ConfirmEditorRouteJs, listKey);
        if (redirected is not null)
        {
            throw new InvalidOperationException($"NewRecruit editor navigation failed: {redirected}");
        }
    }

    /// <summary>
    /// Make NR's own route lookup for <c>listKey</c> resolve <em>before</em> we ask the router to go
    /// there. Waits for the row to appear in <c>listData</c> (setup awaits <c>addList</c>, so this is
    /// normally already true) and re-selects the system that owns the row whenever the current
    /// selection would hide it. Returns null on success, or a diagnostic naming the exact mismatch.
    /// </summary>
    private const string PrepareEditorRouteJs = """
        async (listKey) => {
            const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
            const lists = pinia?._s?.get('lists');
            const systems = pinia?._s?.get('systemsStore');
            if (!lists || !systems) return 'Pinia lists/systemsStore not reachable';
            // Checked by name up front: without this, a findListByKey that went missing would make
            // resolves() permanently false and the failure below would report "not reachable by
            // NR's own route lookup" — blaming the roster for an absent store action.
            if (typeof lists.findListByKey !== 'function') {
                return "lists store has no findListByKey() action — NR's store API changed";
            }
            // Exactly the predicate NR's editor page uses to resolve the :list route param.
            const resolves = () => {
                const sel = systems.selectedSystem;
                return !!lists.findListByKey(listKey, [sel?.id, sel?.bsid]);
            };
            const deadline = Date.now() + 10000;
            for (;;) {
                const row = (lists.listData ?? []).find(r => r.list_key === listKey);
                if (row) {
                    if (resolves()) return null;
                    // The selected system is not the one that owns this roster. Re-select the owner —
                    // bsid first, which is what the row filter matches on for a locally loaded system.
                    systems.selectSystem(row.bsid_system);
                    if (resolves()) return null;
                    systems.selectSystem(row.id_system);
                    if (resolves()) return null;
                }
                if (Date.now() >= deadline) {
                    const sel = systems.selectedSystem;
                    return 'list ' + listKey + " is not reachable by NR's own route lookup after 10s: "
                        + (row ? 'row present' : 'row MISSING')
                        + ' in listData(' + (lists.listData?.length ?? 0) + ')'
                        + ', selectedSystem=' + (sel ? sel.id + '/' + sel.bsid : 'none')
                        + (row ? ', row system=' + row.id_system + '/' + row.bsid_system : '');
                }
                await new Promise((r) => setTimeout(r, 50));
            }
        }
        """;

    /// <summary>
    /// Confirm the push actually landed on the editor route and was not bounced. <c>router.push()</c>
    /// resolves when the route is confirmed, which is before the page component's own guard has had a
    /// chance to redirect — so the route is re-read after a tick before it is believed.
    /// </summary>
    private const string ConfirmEditorRouteJs = """
        async (listKey) => {
            const router = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$router;
            if (!router) return 'Vue Router not reachable';
            const onEditor = () => {
                const r = router.currentRoute?.value;
                return r?.name === 'app-Lists' && r?.params?.list === listKey;
            };
            const deadline = Date.now() + 5000;
            for (;;) {
                if (onEditor()) {
                    await new Promise((r) => setTimeout(r, 50));
                    if (onEditor()) return null;
                }
                if (Date.now() >= deadline) {
                    const r = router.currentRoute?.value;
                    return 'pushed /app/Lists/' + listKey + ' but NR redirected away — now at '
                        + (r?.fullPath ?? location.pathname) + ' (route ' + String(r?.name) + ')';
                }
                await new Promise((r) => setTimeout(r, 50));
            }
        }
        """;

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
    {
        return GetRosterState().ValidationErrors;
    }

    public string ExportRosterXml() => ExportRosterXmlAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Capture NewRecruit's own <c>.ros</c> serialization for byte-compare. NR's roster serializer
    /// (<c>Sb</c>/<c>fX</c>) and the <c>.ros</c> export are module-scoped, reachable only through the
    /// roster editor's export menu. So we navigate to the editor, <em>wait for that menu to mount</em>
    /// bound to the current list, temporarily hook <c>Blob</c> to capture the <c>text/ros</c> payload
    /// and neutralize the download click, then invoke the mounted component's <c>exportRos()</c> method
    /// and read back the exact XML NR would have downloaded.
    /// </summary>
    private async Task<string> ExportRosterXmlAsync()
    {
        var listKey = await Browser.Page.EvaluateAsync<string?>("window.__bsspec?.row?.list_key");
        if (listKey != null)
        {
            await NavigateToRosterEditorAsync(listKey);
        }

        var json = await Browser.Page.EvaluateAsync<string?>(ExportRosterJs, listKey) ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            // Re-indent NR's single-line export to a readable, git-diffable layout (adapter feature).
            return NrRosterXml.Pretty(t.GetString()!);
        }

        // NewRecruit supports roster export, so a capture failure is a real regression (e.g. an NR
        // update changed exportRos / the editor mount) — surface it rather than silently skipping.
        var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
        throw new InvalidOperationException($"NewRecruit roster export failed: {err}");
    }

    private const string ExportRosterJs = """
        async (listKey) => {
            const captured = { text: null };
            const OrigBlob = window.Blob;
            const origClick = HTMLAnchorElement.prototype.click;
            // Hook Blob to grab the raw .ros text; neutralize the anchor click so no real download fires.
            window.Blob = function (parts, opts) {
                try {
                    const p = parts && parts[0];
                    if (typeof p === 'string' && p.indexOf('<roster') >= 0) captured.text = p;
                } catch (e) {}
                return new OrigBlob(parts, opts);
            };
            HTMLAnchorElement.prototype.click = function () {};
            const findRoot = () => {
                const el = document.querySelector('#__nuxt') || document.body;
                const app = el?.__vue_app__ || document.querySelector('#__nuxt')?.__vue_app__;
                return {
                    app,
                    root: app?._instance
                        || app?._container?._vnode?.component
                        || el?.__vueParentComponent
                        || el?.firstElementChild?.__vueParentComponent,
                };
            };
            // BFS the whole component tree for a mounted component exposing exportRos().
            const findTarget = (root) => {
                const seen = new Set();
                const queue = [root];
                while (queue.length) {
                    const inst = queue.shift();
                    if (!inst || seen.has(inst)) continue;
                    seen.add(inst);
                    const px = inst.proxy;
                    if (px && typeof px.exportRos === 'function') return px;
                    const pushVnode = (vn) => {
                        if (!vn || typeof vn !== 'object') return;
                        if (vn.component) queue.push(vn.component);
                        const ch = vn.children;
                        if (Array.isArray(ch)) ch.forEach(pushVnode);
                        else if (ch && ch.component) queue.push(ch.component);
                        if (vn.suspense) { pushVnode(vn.suspense.activeBranch); }
                        if (Array.isArray(vn.dynamicChildren)) vn.dynamicChildren.forEach(pushVnode);
                    };
                    pushVnode(inst.subTree);
                }
                return null;
            };
            // Where the app actually is. exportRos() lives on the editor page component and nowhere
            // else, so hunting the tree for it while the app sits on another route can only ever burn
            // the whole deadline and then report the truth about the wrong page.
            const routeNow = () => document.querySelector('#__nuxt')
                ?.__vue_app__?.config?.globalProperties?.$router?.currentRoute?.value;
            const offEditor = () => {
                if (!listKey) return null; // nothing was navigated — search wherever we are
                const r = routeNow();
                if (!r || (r.name === 'app-Lists' && r.params?.list === listKey)) return null;
                return 'NR left the editor route: expected app-Lists/' + listKey + ', now at '
                    + (r.fullPath ?? location.pathname) + ' (route ' + String(r.name) + ')';
            };
            try {
                // WAIT for the editor to mount; do not race it. router.push() resolves when the route
                // is CONFIRMED, which is strictly earlier than when the route's component has mounted
                // and rendered its export menu into the tree, so a single immediate BFS can search a
                // page that is on its way in and truthfully report "no mounted component exposes
                // exportRos()". The ~1s DismissDialogsAsync spends waiting for a consent root that is
                // not there used to cover that gap by accident; an accidental buffer is not a
                // synchronization primitive, so the wait stays.
                //
                // It is not, however, what made step 41 flaky: the failures were never mount lag but a
                // redirect — NR bounced the editor route back to /app/MyLists, where exportRos() does
                // not exist and never will (see NavigateToRosterEditorAsync). Hence the second bound
                // below: this loop is bounded by the ROUTE as well as by the clock, because once the
                // app has left the editor route no amount of waiting can help and burning 15s to say
                // "component missing" about the lists index actively misleads whoever reads it.
                let app = null;
                let root = null;
                let target = null;
                const deadline = Date.now() + 15000;
                for (;;) {
                    const left = offEditor();
                    if (left) return JSON.stringify({ error: left });
                    ({ app, root } = findRoot());
                    if (root) {
                        target = findTarget(root);
                        if (target) break;
                    }
                    if (Date.now() >= deadline) break;
                    await new Promise((r) => setTimeout(r, 100));
                }
                if (!root) {
                    return JSON.stringify({ error: 'no root instance after 15s; appKeys=' + (app ? Object.keys(app).join(',') : 'no-app') });
                }
                if (!target) {
                    return JSON.stringify({ error: 'no mounted component exposes exportRos() after 15s at ' + location.pathname });
                }
                // Point the menu's list at our loaded roster (window.__bsspec has {army, row:{name,...}}),
                // so exportRos() serializes exactly the spec's roster rather than the menu's own context.
                const bsspec = window.__bsspec;
                const listBefore = target.list;
                try { if (bsspec && bsspec.army) target.list = bsspec; } catch (e) {}
                await target.exportRos();
                await new Promise((r) => setTimeout(r, 40));
                try { target.list = listBefore; } catch (e) {}
                if (captured.text == null) return JSON.stringify({ error: 'exportRos() produced no <roster payload' });
                return JSON.stringify({ text: captured.text });
            } catch (e) {
                return JSON.stringify({ error: String((e && e.message) || e) });
            } finally {
                window.Blob = OrigBlob;
                HTMLAnchorElement.prototype.click = origClick;
            }
        }
        """;

    public void Cleanup()
    {
        CleanupAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Returns this engine's browser to a clean state for the next spec: the roster row this spec
    /// created is deleted through NR's own store API, and the loaded game data is unloaded.
    /// <para>
    /// The deletion goes through <see cref="NrListStoreJs.DeleteListsFn"/> — see its remarks for why
    /// <c>removeList(row)</c> is the only correct action and why the <c>listsStore.deleteList?.(key)</c>
    /// this replaced silently deleted nothing on every run.
    /// </para>
    /// </summary>
    private async Task CleanupAsync()
    {
        try
        {
            await Browser.WaitForPiniaAsync();

            var cleanupError = await Browser.Page.EvaluateAsync<string?>($$"""
                async () => {
                    try {
                        const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return null; // no stores — nothing to clean

                        const sysStore = pinia._s.get('systemsStore');
                        const listsStore = pinia._s.get('lists');
                        if (!sysStore || !listsStore) return null;

                        {{NrListStoreJs.DeleteListsFn}}

                        let listError = null;
                        const listKey = window.__bsspec?.row?.list_key;
                        if (listKey) {
                            // `?? currentList` because getCurrentList() is a one-line getter over it —
                            // the fallback is the identical value, so this cannot silently skip the
                            // force teardown the way a bare `?.()` would.
                            const currentList = listsStore.getCurrentList?.() ?? listsStore.currentList;
                            if (currentList?.army) {
                                const forces = currentList.army.getForces?.() || [];
                                for (const f of [...forces]) {
                                    if (typeof f.delete === 'function') f.delete();
                                }
                            }
                            listError = await bsspecDeleteLists(listsStore, [listKey]);
                            // Don't leave currentList pointing at a row that no longer exists.
                            if (listsStore.currentList?.row?.list_key === listKey) {
                                listsStore.currentList = null;
                            }
                        }
                        for (const key of Object.keys(sysStore.localLibrary || {})) {
                            delete sysStore.localLibrary[key];
                        }
                        window.__bsspec = undefined;
                        return listError;
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
