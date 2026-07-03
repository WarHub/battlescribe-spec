using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

#pragma warning disable IDE0060 // Remove unused parameter — interface implementations and UI stubs

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// IRosterEngine implementation that drives the New Recruit web app through
/// Playwright UI interactions rather than direct JS/API access.
///
/// Actions (AddForce, SelectEntry, etc.) are performed via real UI clicks and
/// form inputs. State is read from NR's Pinia store via JS (hybrid approach).
///
/// Data loading (Setup) uses the File System Access API mock or &lt;input type="file"&gt;
/// depending on what NR uses — see NrUiSetup.LoadGameDataAsync.
/// </summary>
public sealed class NrRosterUiEngine : IRosterEngine
{
    internal NewRecruitBrowser Browser { get; }
    private bool _disposed;
    private string _rosterName = "Spec Test";
    private string? _listId;
    private bool _systemLoaded;
    private string? _loadedSystemId;
    private bool _rosterCreated;

    // Spec data retained from Setup for deferred roster creation.
    private ProtocolGameSystem? _gameSystem;
    private ProtocolCatalogue[]? _catalogues;

    // ID → Name lookups built from spec data during Setup.
    // Used by UI actions that must find entries by their visible label.
    private readonly Dictionary<string, string> _forceEntryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _entryNames = new(StringComparer.Ordinal);

    // Tracks child selection uid → (parentSelectionUid, entryName) so SetSelectionCount
    // can route child entries to the options panel rather than the unitRow.
    private readonly Dictionary<string, (string ParentUid, string EntryName)> _childSelectionParent
        = new(StringComparer.Ordinal);

    private NrUiDiagnostics? _diagnostics;

    private NrRosterUiEngine(NewRecruitBrowser browser)
    {
        Browser = browser;
    }

    /// <summary>Create a live (internet-connected) engine instance.</summary>
    public static async Task<NrRosterUiEngine> CreateAsync(
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true,
        float? slowMo = null)
    {
        var browser = await NewRecruitBrowser.CreateAsync(baseUrl, headless, slowMo);
        return new NrRosterUiEngine(browser);
    }

    /// <summary>Create an engine that replays all network traffic from a HAR file.</summary>
    public static async Task<NrRosterUiEngine> CreateFrozenAsync(
        string harFilePath,
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true,
        float? slowMo = null)
    {
        var browser = await NewRecruitBrowser.CreateFrozenAsync(harFilePath, baseUrl, headless, slowMo);
        return new NrRosterUiEngine(browser);
    }

    public void SetTestContext(string specId) => _rosterName = specId;

    // ===== IRosterEngine: Setup =====

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        => SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<string>> SetupAsync(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        _gameSystem = gameSystem;
        _catalogues = catalogues;
        BuildEntryLookups(gameSystem, catalogues);

        var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);

        // Load non-library catalogues before library ones so NR uses the primary (non-library)
        // catalogue as the default when associating a force entry with a book.
        var sortedCatalogues = catalogues
            .OrderBy(c => c.Library == true ? 1 : 0)
            .ToArray();
        var catFiles = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, sortedCatalogues);
        var allFiles = new List<(string FileName, string Content)>
        {
            ($"{gameSystem.Id}.gst", gstXml),
        };
        allFiles.AddRange(catFiles.Select(f => (f.FileName, f.Xml)));

        // Navigate to app and wait for Pinia
        if (!Browser.FrozenReady)
        {
            await Browser.NavigateToAppAsync();
            await Browser.WaitForPiniaAsync();
        }

        // Load game data into NR (only once per unique system in frozen mode)
        if (!_systemLoaded || _loadedSystemId != gameSystem.Id)
        {
            await NrUiSetup.LoadGameDataAsync(Browser, allFiles, gameSystem.Id);
            _systemLoaded = true;
            _loadedSystemId = gameSystem.Id;

            if (Browser.IsFrozen)
            {
                Browser.FrozenReady = true;
            }
        }

        // Roster creation is deferred to the first AddForce call (like BS UI driver).
        // This matches the user-facing NR flow: load data → pick force → roster created.
        return [];
    }

    /// <summary>
    /// Creates the roster if it hasn't been created yet. Called before the first mutation.
    /// Currently uses JS (same as previous Setup flow). Will be replaced with UI-driven
    /// roster creation once the NR "Add List" flow is probed.
    /// </summary>
    private async Task EnsureRosterCreatedAsync(string? catalogueId = null)
    {
        if (_rosterCreated)
        {
            return;
        }

        if (_gameSystem is null)
        {
            throw new InvalidOperationException("Setup must be called before any mutation.");
        }

        // Use the catalogue from the first AddForce call to select the right faction.
        // Fall back to the first non-library catalogue if not specified.
        string? catalogueName = null;
        if (catalogueId != null)
        {
            catalogueName = _catalogues?.FirstOrDefault(c => c.Id == catalogueId)?.Name;
        }

        catalogueName ??= _catalogues?.FirstOrDefault(c => c.Library != true)?.Name;

        var listId = await NrUiSetup.CreateRosterAsync(Browser.Page, _rosterName, catalogueName);
        _listId = listId;

        // Wait for editor to stabilize and bypass supporter paywall
        await NrUiSetup.WaitForEditorLoadedAsync(Browser.Page);
        await NrUiSetup.BypassSupporterPaywallAsync(Browser.Page);

        _rosterCreated = true;
    }

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
        => SetupFromFilesAsync(files).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<string>> SetupFromFilesAsync(IReadOnlyList<(string FileName, string Content)> files)
    {
        if (!Browser.FrozenReady)
        {
            await Browser.NavigateToAppAsync();
            await Browser.WaitForPiniaAsync();
        }

        await NrUiSetup.LoadGameDataAsync(Browser, files, systemId: null);
        _systemLoaded = true;

        if (Browser.IsFrozen)
        {
            Browser.FrozenReady = true;
        }

        // Roster creation is deferred to the first AddForce call.
        return [];
    }

    // ===== IRosterEngine: Roster mutations (all UI-driven) =====

    public ActionOutputs AddForce(string forceEntryId, string catalogueId)
        => AddForceAsync(forceEntryId, catalogueId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> AddForceAsync(string forceEntryId, string catalogueId)
    {
        var isFirstAddForce = !_rosterCreated;
        await EnsureRosterCreatedAsync(catalogueId);

        string? uid;

        if (isFirstAddForce)
        {
            // NR auto-creates a force during "Create List". Adopt it instead of adding another.
            uid = await Browser.Page.EvaluateAsync<string?>("""
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army
                        ?? window.__bsspec?.army;
                    const forces = army?.getForces?.() || [];
                    return forces.length > 0 ? forces[0].uid : null;
                }
                """);
        }
        else
        {
            var name = _forceEntryNames.GetValueOrDefault(forceEntryId, forceEntryId);
            uid = await NrUiActions.AddForceByNameAsync(Browser.Page, name, forceEntryId, catalogueId);
        }

        // Capture any auto-added selections (e.g. from min=1 constraints).
        // NR adds selections asynchronously: the selection appears immediately (s.id=null)
        // then ~2s later the entry id is populated. After clicking "Add Force", the editor
        // can also briefly re-hydrate (currentList.army replaced), during which getForceSelections
        // returns empty. Poll for up to 8s without early breaks to handle both timing scenarios.
        Dictionary<string, string> selections = [];
        if (uid is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                selections = await NrUiActions.GetForceSelectionsAsync(Browser.Page, uid);
                if (selections.Count > 0)
                {
                    break;
                }

                await Browser.Page.WaitForTimeoutAsync(400);
            }
        }

        return new ActionOutputs { ForceId = uid, Selections = selections.Count > 0 ? selections : null };
    }

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
        => AddChildForceAsync(parentForceId, forceEntryId, catalogueId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> AddChildForceAsync(string parentForceId, string forceEntryId, string catalogueId)
    {
        var name = _forceEntryNames.GetValueOrDefault(forceEntryId, forceEntryId);
        var uid = await NrUiActions.AddChildForceByNameAsync(Browser.Page, parentForceId, name, forceEntryId, catalogueId);
        return new ActionOutputs { ForceId = uid };
    }

    public void RemoveForce(string forceId)
        => NrUiActions.RemoveForceAsync(Browser.Page, forceId).GetAwaiter().GetResult();

    public ActionOutputs SelectEntry(string forceId, string entryId)
        => SelectEntryAsync(forceId, entryId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> SelectEntryAsync(string forceId, string entryId)
    {
        var name = ResolveEntryName(entryId);
        var uid = await NrUiActions.SelectEntryByNameAsync(Browser.Page, forceId, entryId, name);
        return new ActionOutputs { SelectionId = uid };
    }

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
        => SelectChildEntryAsync(forceId, parentSelectionId, entryId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> SelectChildEntryAsync(string forceId, string parentSelectionId, string entryId)
    {
        _ = forceId;
        var name = ResolveEntryName(entryId);
        var uid = await NrUiActions.SelectChildEntryByNameAsync(Browser.Page, parentSelectionId, name, entryId);
        if (uid is not null)
        {
            _childSelectionParent[uid] = (parentSelectionId, name);
        }

        return new ActionOutputs { SelectionId = uid };
    }

    /// <summary>
    /// Resolves a spec entry ID (possibly composite e.g. "groupLink::linkId::targetId")
    /// to its display name by checking <see cref="_entryNames"/> from right-to-left on each
    /// "::" segment. Falls back to the raw entry ID if no match is found.
    /// </summary>
    private string ResolveEntryName(string entryId)
    {
        if (_entryNames.TryGetValue(entryId, out var name))
        {
            return name;
        }

        // Composite ID: try each segment right-to-left
        if (entryId.Contains("::"))
        {
            var segments = entryId.Split("::");
            for (var i = segments.Length - 1; i >= 0; i--)
            {
                if (_entryNames.TryGetValue(segments[i], out var segName))
                {
                    return segName;
                }
            }
        }

        return entryId;
    }

    public void DeselectSelection(string forceId, string selectionId)
    {
        _ = forceId;
        NrUiActions.DeselectSelectionAsync(Browser.Page, selectionId).GetAwaiter().GetResult();
    }

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        _ = forceId;
        if (_childSelectionParent.TryGetValue(selectionId, out var info))
        {
            NrUiActions.SetChildEntryCountByNameAsync(Browser.Page, info.ParentUid, info.EntryName, count).GetAwaiter().GetResult();
        }
        else
        {
            // Root selection — throws (no single count control in NR UI for root-level)
            NrUiActions.SetSelectionCountAsync(Browser.Page, selectionId, count).GetAwaiter().GetResult();
        }
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
        => DuplicateSelectionAsync(forceId, selectionId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> DuplicateSelectionAsync(string forceId, string selectionId)
    {
        _ = forceId;
        var uid = await NrUiActions.DuplicateSelectionAsync(Browser.Page, selectionId);
        return new ActionOutputs { SelectionId = uid };
    }

    public ActionOutputs DuplicateForce(string forceId)
        => DuplicateForceAsync(forceId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> DuplicateForceAsync(string forceId)
    {
        var uid = await NrUiActions.DuplicateForceAsync(Browser.Page, forceId);
        return new ActionOutputs { ForceId = uid };
    }

    public void SetCostLimit(string costTypeId, decimal value)
        => NrUiActions.SetCostLimitAsync(Browser.Page, costTypeId, value).GetAwaiter().GetResult();

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
        => NrUiActions.SetCustomizationAsync(Browser.Page, forceId, selectionId, categoryEntryId, customName, customNotes).GetAwaiter().GetResult();

    // ===== IRosterEngine: State (JS reads — hybrid approach) =====

    public RosterState GetRosterState()
        => NewRecruitStateReader.ReadRosterStateAsync(Browser.Page).GetAwaiter().GetResult();

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
        => NewRecruitStateReader.ReadValidationErrorsAsync(Browser.Page).GetAwaiter().GetResult();

    public string ExportRosterXml() => ExportRosterXmlAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Export the roster the way a user would: open the list's export menu and click the <c>.ros</c>
    /// entry, with the download <em>mocked</em> — we hook <c>Blob</c> to grab the serialized payload and
    /// swallow the anchor click so no real file download fires — then return the captured XML. Unlike
    /// the store-direct engine, this exercises NewRecruit's actual export UI end-to-end.
    /// </summary>
    private async Task<string> ExportRosterXmlAsync()
    {
        var page = Browser.Page;
        string? xml;
        await page.EvaluateAsync(CaptureHookJs);
        try
        {
            // "Export" is a toolbar button that opens the export options (.ros/.rosz/.json/...).
            await page.Locator(".outOfMenuButton").Filter(new() { HasText = "Export" }).First
                .ClickAsync(new() { Timeout = 10_000 });

            var rosButton = page.GetByText(".ros", new() { Exact = true });
            if (await rosButton.CountAsync() == 0)
            {
                var dump = await page.EvaluateAsync<string>("""
                    () => {
                        const out = [];
                        for (const el of document.querySelectorAll('button, [class*=Bt], [class*=menu], [class*=Menu], [class*=option], span, a')) {
                            const t = (el.innerText || el.textContent || '').trim();
                            if (t && t.length < 30 && el.offsetParent !== null) out.push(t);
                        }
                        return JSON.stringify([...new Set(out)].slice(0, 50));
                    }
                    """);
                throw new InvalidOperationException(
                    "NR UI roster export: opened Export but found no '.ros' entry. Visible text: " + dump);
            }

            await rosButton.First.ClickAsync(new() { Timeout = 5_000 });
            await page.WaitForTimeoutAsync(150);
            xml = await page.EvaluateAsync<string?>("window.__bsspec_rosCapture ?? null");
        }
        finally
        {
            await page.EvaluateAsync(RestoreHookJs);
            // Return to the app home so the next spec's setup (which, once frozen, skips navigation)
            // starts from the expected page rather than this roster's editor. The UI engine shares one
            // browser across specs, so leaving the editor open would time out the next Setup.
            try
            {
                await Browser.NavigateToAppAsync();
                await Browser.WaitForPiniaAsync();
            }
            catch
            {
                // Best-effort; a failure here surfaces as the next spec's setup error.
            }
        }

        if (string.IsNullOrEmpty(xml))
        {
            throw new InvalidOperationException("NR UI roster export: clicked .ros but captured no <roster payload.");
        }

        return xml;
    }

    // Hook Blob to capture the .ros text NR's exporter writes, and swallow the download anchor click.
    private const string CaptureHookJs = """
        () => {
            window.__bsspec_rosCapture = null;
            if (!window.__bsspec_origBlob) window.__bsspec_origBlob = window.Blob;
            if (!window.__bsspec_origClick) window.__bsspec_origClick = HTMLAnchorElement.prototype.click;
            const OrigBlob = window.__bsspec_origBlob;
            window.Blob = function (parts, opts) {
                try {
                    const p = parts && parts[0];
                    if (typeof p === 'string' && p.indexOf('<roster') >= 0) window.__bsspec_rosCapture = p;
                } catch (e) {}
                return new OrigBlob(parts, opts);
            };
            HTMLAnchorElement.prototype.click = function () {};
        }
        """;

    private const string RestoreHookJs = """
        () => {
            if (window.__bsspec_origBlob) window.Blob = window.__bsspec_origBlob;
            if (window.__bsspec_origClick) HTMLAnchorElement.prototype.click = window.__bsspec_origClick;
        }
        """;

    // ===== Diagnostics =====

    /// <summary>
    /// Captures a PNG screenshot of the current browser page.
    /// Used by the Debugger for step-by-step visual output.
    /// </summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        _diagnostics ??= new NrUiDiagnostics(Browser.Page);
        return await _diagnostics.CaptureScreenshotAsync();
    }

    /// <summary>
    /// Evaluates a JavaScript expression in the page context.
    /// Used by the Debugger REPL for interactive DOM probing.
    /// </summary>
    public async Task<T> EvaluateAsync<T>(string expression)
    {
        return await Browser.Page.EvaluateAsync<T>(expression);
    }

    /// <summary>
    /// Captures full diagnostic report (screenshot + console + DOM + Pinia state).
    /// Used on failure for debugging.
    /// </summary>
    public async Task<DiagnosticReport> CaptureDiagnosticsAsync()
    {
        _diagnostics ??= new NrUiDiagnostics(Browser.Page);
        return await _diagnostics.CaptureFullReportAsync();
    }

    // ===== Lifecycle =====

    public void Cleanup()
    {
        _listId = null;
        _rosterCreated = false;
        _systemLoaded = false;
        _loadedSystemId = null;
        _gameSystem = null;
        _catalogues = null;
        _forceEntryNames.Clear();
        _entryNames.Clear();
        _childSelectionParent.Clear();

        // The UI engine shares one browser across specs. Delete any lists this spec created and return
        // to a clean /app, so the next spec's roster creation isn't confused by leftover list rows
        // (e.g. the Create List dialog's controls become ambiguous once a prior list is present).
        try
        {
            ResetBrowserStateAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort; the next spec's setup will surface any real problem.
        }
    }

    private async Task ResetBrowserStateAsync()
    {
        if (!Browser.FrozenReady && !Browser.IsFrozen)
        {
            return;
        }

        await Browser.Page.EvaluateAsync("""
            () => {
                try {
                    const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                    const ls = pinia?._s?.get('lists');
                    if (ls) {
                        if (Array.isArray(ls.lists)) ls.lists.splice(0, ls.lists.length);
                        ls.currentList = null;
                    }
                } catch (e) {}
                try {
                    for (const k of Object.keys(localStorage)) {
                        if (/list/i.test(k)) localStorage.removeItem(k);
                    }
                } catch (e) {}
            }
            """);
        await Browser.NavigateToAppAsync();
        await Browser.WaitForPiniaAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ===== Internal: entry name lookups =====

    private void BuildEntryLookups(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        _forceEntryNames.Clear();
        _entryNames.Clear();

        foreach (var fe in gameSystem.ForceEntries ?? [])
        {
            RegisterForceEntry(fe);
        }

        foreach (var se in gameSystem.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(se);
        }

        foreach (var el in gameSystem.EntryLinks ?? [])
        {
            RegisterEntryLink(el);
        }

        foreach (var cat in catalogues)
        {
            RegisterCatalogue(cat);
        }
    }

    private void RegisterForceEntry(ProtocolForceEntry fe)
    {
        _forceEntryNames[fe.Id] = fe.Name;
        foreach (var child in fe.ForceEntries ?? [])
        {
            RegisterForceEntry(child);
        }
    }

    private void RegisterSelectionEntry(ProtocolSelectionEntry se)
    {
        _entryNames[se.Id] = se.Name;
        foreach (var child in se.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(child);
        }

        foreach (var grp in se.SelectionEntryGroups ?? [])
        {
            RegisterSelectionEntryGroup(grp);
        }

        foreach (var link in se.EntryLinks ?? [])
        {
            RegisterEntryLink(link);
        }
    }

    private void RegisterSelectionEntryGroup(ProtocolSelectionEntryGroup grp)
    {
        _entryNames[grp.Id] = grp.Name;
        foreach (var child in grp.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(child);
        }

        foreach (var nested in grp.SelectionEntryGroups ?? [])
        {
            RegisterSelectionEntryGroup(nested);
        }

        foreach (var link in grp.EntryLinks ?? [])
        {
            RegisterEntryLink(link);
        }
    }

    private void RegisterEntryLink(ProtocolEntryLink link)
    {
        // An entry link's name overrides the target's name (or falls back to it)
        var name = string.IsNullOrEmpty(link.Name)
            ? _entryNames.GetValueOrDefault(link.TargetId, link.TargetId)
            : link.Name;
        _entryNames[link.Id] = name;
    }

    private void RegisterCatalogue(ProtocolCatalogue cat)
    {
        foreach (var se in cat.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(se);
        }

        foreach (var grp in cat.SharedSelectionEntryGroups ?? [])
        {
            RegisterSelectionEntryGroup(grp);
        }

        foreach (var se in cat.SharedSelectionEntries ?? [])
        {
            RegisterSelectionEntry(se);
        }

        foreach (var link in cat.EntryLinks ?? [])
        {
            RegisterEntryLink(link);
        }

        foreach (var fe in cat.ForceEntries ?? [])
        {
            RegisterForceEntry(fe);
        }
    }
}
