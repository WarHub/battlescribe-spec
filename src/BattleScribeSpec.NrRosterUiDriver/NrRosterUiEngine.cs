using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;
using Microsoft.Playwright;

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
            await NrUiTiming.MeasureAsync("load-gamedata", () =>
                NrUiSetup.LoadGameDataAsync(Browser, allFiles, gameSystem.Id));
            _systemLoaded = true;
            _loadedSystemId = gameSystem.Id;

            if (Browser.IsFrozen)
            {
                Browser.FrozenReady = true;
            }
        }

        // Arm the store tracer for this spec (no-op unless NR_TRACE_STORE is set). Installed here,
        // after Pinia is up, because the buffer lives on `window` and a page load clears it — which
        // is the scope we want: a failure needs the mutations of the spec that failed.
        _diagnostics ??= new NrUiDiagnostics(Browser.Page);
        await _diagnostics.InstallStoreTraceAsync();

        // Roster creation is deferred to the first AddForce call (like BS UI driver).
        // This matches the user-facing NR flow: load data → pick force → roster created.
        return [];
    }

    /// <summary>
    /// Creates the roster if it hasn't been created yet. Called before the first mutation.
    /// Currently uses JS (same as previous Setup flow). Will be replaced with UI-driven
    /// roster creation once the NR "Add List" flow is probed.
    /// </summary>
    private async Task EnsureRosterCreatedAsync(string? catalogueId = null, string? forceEntryId = null)
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

        // The force from that same first AddForce, so NR's Create List dialog builds the force the
        // spec asked for rather than whichever one it would default to. Null for a step-0 read,
        // which has no force in hand and keeps NR's default.
        var listId = await NrUiTiming.MeasureAsync("create-roster", () =>
            NrUiSetup.CreateRosterAsync(Browser.Page, _rosterName, catalogueName, forceEntryId));
        _listId = listId;

        // Wait for editor to stabilize and bypass supporter paywall
        await NrUiTiming.MeasureAsync("wait-editor-loaded", () =>
            NrUiSetup.WaitForEditorLoadedAsync(Browser.Page));

        await NrUiTiming.MeasureAsync("bypass-paywall", () =>
            NrUiSetup.BypassSupporterPaywallAsync(Browser.Page));

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

    /// <summary>
    /// Runs a UI action and, if it throws, captures a diagnostic report to disk before rethrowing.
    /// <para>
    /// Mirrors <c>NrGameDataUiEngine.CaptureFailureDiagnostics</c>, which this driver had no
    /// equivalent of: it owned a <see cref="NrUiDiagnostics"/> and a public
    /// <c>CaptureDiagnosticsAsync</c> that nothing ever called, so an NR roster UI failure produced
    /// no artifacts whatsoever. Wrapping the roster-creation and force paths covers where failures
    /// actually land; any other action becomes covered by one more <c>WithDiagnosticsAsync</c>.
    /// </para>
    /// </summary>
    private async Task<T> WithDiagnosticsAsync<T>(string label, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch
        {
            await CaptureFailureDiagnosticsAsync(label);
            throw;
        }
    }

    private async Task CaptureFailureDiagnosticsAsync(string label)
    {
        try
        {
            _diagnostics ??= new NrUiDiagnostics(Browser.Page);
            var report = await _diagnostics.CaptureFullReportAsync();
            await NrUiDiagnostics.SaveReportAsync(report, $"{_rosterName}-{label}");
        }
        catch
        {
            // Best-effort — diagnostics must never mask the original failure.
        }
    }

    private Task<ActionOutputs> AddForceAsync(string forceEntryId, string catalogueId)
        => WithDiagnosticsAsync($"addForce-{forceEntryId}", () => AddForceCoreAsync(forceEntryId, catalogueId));

    private async Task<ActionOutputs> AddForceCoreAsync(string forceEntryId, string catalogueId)
    {
        var isFirstAddForce = !_rosterCreated;
        await EnsureRosterCreatedAsync(catalogueId, forceEntryId);

        string? uid;

        if (isFirstAddForce)
        {
            // NR auto-creates a force during "Create List" — but NR chooses which one, and it is not
            // necessarily the one this step asked for. This used to adopt `forces[0]` unconditionally
            // and return it, so `addForce fe-cat` silently returned the game system's "GS Detachment"
            // whenever NR picked that instead. Measured on catalogue/catalogue-force-entries. It
            // works for most specs only because their requested force IS the one NR auto-creates,
            // which is luck, not agreement.
            var autoJson = await Browser.Page.EvaluateAsync<string?>("""
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army
                        ?? window.__bsspec?.army;
                    const forces = army?.getForces?.() || [];
                    if (forces.length === 0) return null;
                    // `source.id` is the force ENTRY id — the same accessor the state reader uses.
                    return JSON.stringify({ uid: forces[0].uid, entryId: forces[0].source?.id ?? null });
                }
                """);

            string? autoUid = null;
            string? autoEntryId = null;
            if (autoJson is not null)
            {
                using var autoDoc = System.Text.Json.JsonDocument.Parse(autoJson);
                autoUid = autoDoc.RootElement.GetProperty("uid").GetString();
                autoEntryId = autoDoc.RootElement.GetProperty("entryId").GetString();
            }

            if (autoUid is not null && autoEntryId == forceEntryId)
            {
                // It is the force we asked for — adopt it, as before.
                uid = autoUid;
            }
            else
            {
                // It is not. Add the requested one through the UI and drop NR's, so the id this
                // method returns is the force the spec actually named. Both paths are the ones
                // every other step already uses.
                var requestedName = _forceEntryNames.GetValueOrDefault(forceEntryId, forceEntryId);
                uid = await NrUiActions.AddForceByNameAsync(
                    Browser.Page, requestedName, forceEntryId, catalogueId);

                if (autoUid is not null)
                {
                    await NrUiActions.RemoveForceAsync(Browser.Page, autoUid);
                }
            }
        }
        else
        {
            var name = _forceEntryNames.GetValueOrDefault(forceEntryId, forceEntryId);
            uid = await NrUiActions.AddForceByNameAsync(Browser.Page, name, forceEntryId, catalogueId);
        }

        // Capture any auto-added selections (e.g. from min=1 constraints).
        //
        // NR adds these asynchronously: the selection appears immediately with `id` null and the
        // entry id populates ~2s later, and the editor can briefly re-hydrate (currentList.army
        // replaced) so the force is momentarily absent. Both look identical to "this force has no
        // auto-added selections" if all you can see is an empty map — which is why this used to
        // burn a FIXED 8 seconds on every addForce, whether or not the spec could ever produce one.
        //
        // Measured: over the 56-spec NR-UI roster lane that is ~9.4s of every ~18s spec — about half
        // the lane — and not one of those specs declares a `min` constraint, so the early break
        // could never fire. The engine now asks NR which of the three states it is in and stops as
        // soon as the answer is settled. The 8s stays as a CEILING, not a cost.
        Dictionary<string, string> selections = [];
        if (uid is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            // An empty force is only believed after two consecutive quiet reads: a single one can
            // land inside the re-hydration window, which is the case the old fixed wait was really
            // paying for.
            var quietReads = 0;
            while (true)
            {
                var (state, map) = await NrUiActions.GetForceSelectionsWithStateAsync(Browser.Page, uid);
                selections = map;

                if (state == NrUiActions.ForceSelectionState.Resolved)
                {
                    break;
                }

                quietReads = state == NrUiActions.ForceSelectionState.Empty ? quietReads + 1 : 0;
                if (quietReads >= 2 || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Browser.Page.WaitForTimeoutAsync(state == NrUiActions.ForceSelectionState.Empty ? 250 : 100);
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
    /// <remarks>
    /// <b>Not a duplicate of <c>BsUiRosterEngine.ResolveEntryLabel</c>, though it reads like one.</b>
    /// That method splits the same composite id on <c>::</c> and walks the segments right-to-left
    /// too, and the two must NOT be unified: they resolve a link's name in opposite directions,
    /// because their apps do. Here <see cref="RegisterEntryLink"/> stores the LINK's own name
    /// against the link id and only falls back to the target's when the link is unnamed — NR's UI
    /// shows the link's name. BattleScribe's driver does the reverse: its <c>NameOfResolved</c>
    /// follows the link to its target and takes the TARGET's name, because BattleScribe labels the
    /// control with what the link resolves to. Sharing one implementation would hand one of the two
    /// apps a label its DOM does not contain, and the failure would read as "entry not found", not
    /// as a naming disagreement.
    /// </remarks>
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
            // Pass the child's uid: an instanced entry renders two rows under one name, and only the
            // uid distinguishes the instance's stepper from the "+" add row.
            NrUiActions.SetChildEntryCountByNameAsync(
                Browser.Page, info.ParentUid, info.EntryName, count, selectionId).GetAwaiter().GetResult();
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
        => GetRosterStateAsync().GetAwaiter().GetResult();

    private async Task<RosterState> GetRosterStateAsync()
    {
        await EnsureRosterMaterialisedForReadAsync();
        return await NewRecruitStateReader.ReadRosterStateAsync(Browser.Page);
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
        => GetValidationErrorsAsync().GetAwaiter().GetResult();

    private async Task<IReadOnlyList<ValidationErrorState>> GetValidationErrorsAsync()
    {
        await EnsureRosterMaterialisedForReadAsync();
        return await NewRecruitStateReader.ReadValidationErrorsAsync(Browser.Page);
    }

    /// <summary>
    /// Materialises the roster for a read that arrives before the first mutation.
    /// </summary>
    /// <remarks>
    /// Roster creation is deferred to the first <c>addForce</c>, so a spec whose FIRST step is an
    /// assertion had nothing to read and reported empties. (Before <c>window.__bsspec</c> was cleared
    /// on reset it reported the PREVIOUS spec's roster instead, which is worse — some of those specs
    /// were passing on another spec's data.)
    /// </remarks>
    private async Task EnsureRosterMaterialisedForReadAsync()
    {
        // `_gameSystem is null` is the SetupFromFiles (dataSource) path, which records none of the
        // spec's model and so cannot pick a faction for the Create List dialog. Leave those reads as
        // they were rather than throwing out of a state read.
        if (_rosterCreated || _gameSystem is null)
        {
            return;
        }

        await WithDiagnosticsAsync("read-create-roster", async () =>
        {
            await EnsureRosterCreatedAsync();
            await RemoveCreateListForcesAsync();
            return true;
        });
    }

    /// <summary>
    /// Drops the force(s) NR's Create List dialog auto-adds, so a freshly created roster reads as
    /// empty — which is what every other engine means by "a new roster".
    /// </summary>
    /// <remarks>
    /// <para>
    /// This purge is what makes creating-on-read work at all. Without it, materialising the roster
    /// for a step-0 read leaves NR's auto-created force in place, so <c>forceCount: 0</c> is false
    /// and the read still fails.
    /// </para>
    /// <para>
    /// It also fixes the second half. <c>AddForceCoreAsync</c> selects its branch on
    /// <c>_rosterCreated</c>, which the read has now flipped — so the first real <c>addForce</c>
    /// takes the "add another" path. Against an EMPTY roster that path is exactly right; against the
    /// auto-populated one it added a second force and left NR's behind. Measured: two identical
    /// "Patrol" forces, and every later assertion wrong.
    /// </para>
    /// </remarks>
    private async Task RemoveCreateListForcesAsync()
    {
        // Bounded: a RemoveForceAsync that removed nothing would otherwise spin here forever.
        for (var guard = 0; guard < 8; guard++)
        {
            var uid = await Browser.Page.EvaluateAsync<string?>("""
                () => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army
                        ?? window.__bsspec?.army;
                    // Root forces only — removing one takes its children with it.
                    const forces = (army?.getForces?.() || [])
                        .filter(f => !f.getParent?.()?.isForce?.());
                    return forces.length > 0 ? forces[0].uid : null;
                }
                """);

            if (uid is null)
            {
                return;
            }

            await NrUiActions.RemoveForceAsync(Browser.Page, uid);
        }

        throw new InvalidOperationException(
            "NR UI: could not empty the roster NR's Create List dialog auto-populated — "
            + "8 removals left forces behind.");
    }

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
        var returnRoute = RosterEditorRouteOrNull(page.Url);
        string? xml;
        await page.EvaluateAsync(CaptureHookJs);
        try
        {
            // "Export" is a toolbar button that opens the export options (.ros/.rosz/.json/...).
            await page.Locator(".outOfMenuButton").Filter(new() { HasText = "Export" }).First
                .ClickAsync(new() { Timeout = NrUiTimeouts.Interaction });

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

            await rosButton.First.ClickAsync(new() { Timeout = NrUiTimeouts.Interaction });

            // Wait for the export hook to have CAPTURED the blob, rather than for 150ms and then
            // reading whatever is there. The read below is a snapshot: too early and it returns
            // null, which surfaces as "export produced no XML" — an accusation against NR for a
            // race on this side.
            try
            {
                await page.WaitForFunctionAsync(
                    "() => window.__bsspec_rosCapture != null",
                    null,
                    new() { Timeout = NrUiTimeouts.Condition });
            }
            catch (TimeoutException)
            {
                // Fall through: the read below reports the empty capture with its own message.
            }

            xml = await page.EvaluateAsync<string?>("window.__bsspec_rosCapture ?? null");
        }
        finally
        {
            await page.EvaluateAsync(RestoreHookJs);
            // Put the app back where the export found it. `expectedFile` is an ASSERTION, and an
            // assertion that moves the app out from under the steps after it is not reading the
            // roster, it is changing what the rest of the spec can reach.
            //
            // This used to navigate to the app home unconditionally, with a comment about the next
            // spec's setup. It leaves the roster editor UNMOUNTED — no `.unitRow`, no `.inputOption`,
            // `location` back at `/app` — while the Pinia model stays intact, so state READS keep
            // working and only UI-driven mutations break. kitchen-sink is the one spec with actions
            // after its export, and both of them (selectChildEntry se-inf-banner, the deselect that
            // takes it back) failed as "child entry 'Squad Banner' has no row in the options panel"
            // and were opted out of this engine, on that reading, from the moment they were written.
            // The entry's shape was never involved: NR renders that row as a checkbox, and the panel
            // it belongs to was simply no longer on screen.
            //
            // The next spec's clean start does not depend on this: the frozen lane gets it from
            // `Cleanup` → `ResetBrowserStateAsync`, which ends in the same NavigateToApp, and the live
            // lane from `Setup`, which navigates whenever `FrozenReady` is false — which, live, it
            // always is. The home fallback below still covers an export invoked from anywhere else.

            // The menu gets its own try, so one that refuses to close cannot cost the navigation
            // below. The step after would then fail on Playwright's own "intercepts pointer events",
            // which names the popup precisely — a worse failure than this one, but not a silent one.
            try
            {
                await CloseExportMenuAsync(page);
            }
            catch
            {
                // Best-effort.
            }

            try
            {
                if (returnRoute is not null)
                {
                    await Browser.NavigateToRouteAsync(returnRoute);
                    await NrUiSetup.WaitForEditorLoadedAsync(page);
                }
                else
                {
                    await Browser.NavigateToAppAsync();
                    await Browser.WaitForPiniaAsync();
                }
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

        // Re-indent NR's single-line export to a readable, git-diffable layout (adapter feature).
        return NrRosterXml.Pretty(xml);
    }

    /// <summary>
    /// Dismisses the export menu the way a user does — Escape — and waits for it to be gone.
    /// </summary>
    /// <remarks>
    /// Clicking <c>.ros</c> does not close it: NR leaves <c>#popups &gt; div.exports</c> mounted,
    /// full-screen, and it swallows every click aimed at the editor underneath. That went unnoticed
    /// while this method's caller navigated away afterwards, taking the popup with it. Playwright
    /// names it precisely when it bites — "&lt;div class="exports"&gt; from &lt;div id="popups"&gt;
    /// subtree intercepts pointer events" — against the <c>.displayName</c> of the very unit row the
    /// next step wants to open. Measured: Escape empties <c>#popups</c>; so does a click on empty
    /// canvas; re-clicking the Export button re-opens rather than toggles.
    /// </remarks>
    private static async Task CloseExportMenuAsync(IPage page)
    {
        var menu = page.Locator("#popups .exports");
        if (await menu.CountAsync() == 0)
        {
            return;
        }

        await page.Keyboard.PressAsync("Escape");

        // `Condition`, not `OptionalProbe`: the menu being gone is not optional — every click the
        // rest of the spec makes lands on it otherwise.
        await menu.First.WaitForAsync(new()
        {
            State = WaitForSelectorState.Detached,
            Timeout = NrUiTimeouts.Condition,
        });
    }

    /// <summary>
    /// The route part of <paramref name="url"/> when it addresses a roster editor, else null.
    /// </summary>
    /// <remarks>
    /// Path AND query: NR carries the open options panel in <c>?view=&lt;uid&gt;</c> (<c>?view=main</c>
    /// with none open), so dropping the query would return to a different view than the caller had.
    /// Restoration goes through <see cref="NewRecruitBrowser.NavigateToRouteAsync"/>, a Vue Router
    /// push — no page load, so nothing re-fetches (frozen replay is untouched) and page globals,
    /// <c>window.__bsspec</c> among them, survive.
    /// </remarks>
    private static string? RosterEditorRouteOrNull(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.Contains("/Lists/", StringComparison.OrdinalIgnoreCase)
            ? uri.PathAndQuery
            : null;

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
        // Capture the list id BEFORE clearing local state — the browser reset needs it to ask NR
        // to delete the list this spec created.
        var listId = _listId;

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
            NrUiTiming.MeasureAsync("cleanup-reset-browser", () => ResetBrowserStateAsync(listId))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Best-effort — the next spec's setup will surface any real problem — but say so.
            // A reset that fails without a word is how the leftover-list bug stayed invisible.
            Console.Error.WriteLine($"[nr-ui] browser reset failed after list '{listId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the shared browser to a clean <c>/app</c> for the next spec.
    /// <para>
    /// The list MUST be removed through NR's own store API, and the loaded game data cleared
    /// (<c>systemsStore.localLibrary</c>) — mirroring <c>NewRecruitRosterEngine.Cleanup</c>. Merely
    /// splicing <c>lists</c> and nulling <c>currentList</c> (as this method used to do) never tells
    /// NR to delete anything, so navigating back to <c>/app</c> re-hydrates the old list from
    /// persistence. The leftover row then makes the Create List dialog's controls ambiguous and
    /// EVERY subsequent spec's first <c>addForce</c> times out — warm batches passed only their
    /// first roster-creating spec.
    /// </para>
    /// <para>
    /// That is what this method was written to fix, but it called <c>listsStore.deleteList?.(key)</c>
    /// — an action that does not exist — so it never actually deleted anything either. See
    /// <see cref="NrListStoreJs.DeleteListsFn"/> for the store's real API.
    /// </para>
    /// </summary>
    private async Task ResetBrowserStateAsync(string? listId)
    {
        if (!Browser.FrozenReady && !Browser.IsFrozen)
        {
            return;
        }

        var error = await Browser.Page.EvaluateAsync<string?>($$"""
            async (listId) => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return null;
                    const listsStore = pinia._s?.get('lists');
                    const sysStore = pinia._s?.get('systemsStore');
                    if (!listsStore) return null;

                    {{NrListStoreJs.DeleteListsFn}}

                    // Drop the open list's forces first (mirrors the store-direct engine).
                    const current = listsStore.getCurrentList?.() ?? listsStore.currentList;
                    if (current?.army) {
                        for (const f of [...(current.army.getForces?.() || [])]) {
                            if (typeof f.delete === 'function') f.delete();
                        }
                    }

                    // Delete EVERY list NR knows about through its own API, not just ours — a spec
                    // may have created more than one. The rows live in `listData`; `listsStore.lists`
                    // (which this loop used to read) is not a thing, so it always yielded nothing.
                    const keys = [];
                    if (listId) keys.push(listId);
                    for (const l of (listsStore.listData ?? [])) {
                        const k = l?.list_key;
                        if (k && !keys.includes(k)) keys.push(k);
                    }
                    const listError = await bsspecDeleteLists(listsStore, keys);
                    listsStore.currentList = null;

                    // Deleting the ROWS is not enough, because NR's roster editor page is kept alive
                    // rather than destroyed. Its `activated` hook re-runs `updateRoute` on every
                    // navigation back, which calls `listsStore.selectList(<the list it still holds>)`
                    // — and `selectList` re-selects that row's system. So the next spec would load its
                    // own game data, have it clobbered on the way to the Create List dialog, and be
                    // offered the PREVIOUS spec's faction. `addForce` then spent its whole 30s
                    // reporting "did not find some options".
                    //
                    // Two references keep that alive, and both must go:
                    //   lastSelectedListKey — the store's own record of what to re-select
                    //   unloadList          — the slot the editor page fills with
                    //                         `() => { this.list = null }` so the store can tell it to
                    //                         drop its cached list. NR calls this itself in
                    //                         `syncAllLists`; `removeList` does not.
                    //
                    // Measured: with the rows deleted but these two left alone, `selectList` fired with
                    // the previous spec's row while `listData` was already empty — the row came from the
                    // kept-alive component, not the store. Clearing both makes every spec select its own
                    // list and nothing else (docs/warm-reuse.md).
                    //
                    // `unloadList` is legitimately conditional, unlike the store ACTIONS above: it is
                    // null whenever no list page is mounted, so absent means "nobody is holding a
                    // reference", not "the API moved".
                    listsStore.lastSelectedListKey = null;
                    if (typeof listsStore.unloadList === 'function') {
                        listsStore.unloadList();
                    }

                    // The fifth place NR-adjacent per-spec state hides: `window.__bsspec`, a plain
                    // page global. It is the state reader's ONLY source (JsHelpers.__bsspec_readState
                    // reads name/gameSystemId/gameSystemName straight off `spec.row`), it is written
                    // only by CreateRosterAsync, and nothing here cleared it. Navigation is a Vue
                    // Router push rather than a page load, so it survived into the next spec — and
                    // because roster creation is deferred to the first addForce, any spec whose FIRST
                    // step is an assertion read the PREVIOUS spec's roster.
                    //
                    // Measured: roster/roster-name-and-metadata reported name, gameSystemId and
                    // gameSystemName all as "roster-multiple-selections"; roster/roster-no-cost-types
                    // reported a cost of [Points=0] — the previous spec's cost TYPE, valued 0 because
                    // this very reset had just deleted that army's forces through the object
                    // `__bsspec.army` still pointed at.
                    //
                    // Note this makes some currently-green specs go red, correctly: `forceCount: 0`
                    // was passing for two of them by reading a roster that had just been emptied.
                    delete window.__bsspec;

                    // Unload game data so the next spec's Setup loads its own cleanly.
                    for (const k of Object.keys(sysStore?.localLibrary || {})) {
                        delete sysStore.localLibrary[k];
                    }

                    for (const k of Object.keys(localStorage)) {
                        if (/list/i.test(k)) localStorage.removeItem(k);
                    }
                    return listError;
                } catch (e) {
                    return 'reset error: ' + (e?.stack ?? e?.message ?? String(e));
                }
            }
            """, listId);

        if (error is not null)
        {
            Console.Error.WriteLine($"[nr-ui] {error}");
        }

        await Browser.NavigateToAppAsync();
        await Browser.WaitForPiniaAsync();

        // Re-arm after the navigation above; installation is idempotent.
        if (_diagnostics is not null)
        {
            await _diagnostics.InstallStoreTraceAsync();
        }
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

        // Shared entries defined on the GAME SYSTEM, which this used to skip while
        // RegisterCatalogue registered the catalogue-level equivalents — an asymmetry with a real
        // cost. Every UI action addresses entries by their visible NAME (the DOM does not carry
        // BattleScribe ids), so an unregistered entry falls back to its raw id: the driver went
        // looking for a label "se-shared-weapon" in a panel that says "Shared Weapon", found
        // nothing, and reported it as a hidden entry. Measured on gamesystem/gamesystem-shared-entry.
        foreach (var se in gameSystem.SharedSelectionEntries ?? [])
        {
            RegisterSelectionEntry(se);
        }

        foreach (var grp in gameSystem.SharedSelectionEntryGroups ?? [])
        {
            RegisterSelectionEntryGroup(grp);
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
