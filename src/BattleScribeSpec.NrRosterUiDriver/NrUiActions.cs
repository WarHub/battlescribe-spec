using Microsoft.Playwright;

#pragma warning disable IDE0060 // Remove unused parameter — UI stubs with params reserved for future use

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// Playwright UI action helpers for NrRosterUiEngine.
///
/// Each method drives a single IRosterEngine operation through NR's rendered UI.
/// Where the UI does not expose BattleScribe IDs as DOM attributes, entries are
/// located by visible name (resolved from spec data by the engine) and then
/// interacted with via Playwright locators.
///
/// After each mutation a minimal JS read retrieves the uid of the newly created
/// element — this is the "hybrid" aspect (actions = UI, IDs = JS).
/// </summary>
public static class NrUiActions
{
    // ===== Force operations =====

    /// <summary>
    /// Clicks the "Add Force" button in the roster editor and selects the force
    /// type matching <paramref name="forceName"/>.
    /// Returns the uid of the newly created force.
    /// </summary>
    public static async Task<string?> AddForceByNameAsync(IPage page, string forceName, string? forceEntryId = null, string? catalogueId = null)
    {
        // Dismiss any consent dialogs that may have appeared after page load
        await DismissOverlaysAsync(page);

        // Capture existing force uids before the action
        var before = await GetAllForceUidsAsync(page);

        // In multi-catalogue setups, NR shows force entries without distinguishing which
        // catalogue they belong to. Use JS directly when we need a specific catalogue and
        // there are multiple books (to ensure the correct book is used for insertForce).
        var hasMultipleBooks = catalogueId is not null && await HasMultipleBooksAsync(page);
        if (hasMultipleBooks && forceEntryId is not null)
        {
            return await AddForceByJsAsync(page, forceEntryId, catalogueId);
        }

        // Open the forces panel (picker of available force types).
        // Two entry points depending on roster state:
        //   1. Empty roster: big "Add Force" button in the middle panel (button.bouton)
        //   2. Roster with forces: "List Options" → "Add Force" in the dropdown menu
        var forcesPanel = page.Locator(".forces").First;
        if (!await forcesPanel.IsVisibleAsync())
        {
            var addForceBigBtn = page.Locator("button.bouton").Filter(new() { HasText = "Add Force" });
            if (await addForceBigBtn.IsVisibleAsync())
            {
                await addForceBigBtn.ClickAsync();
            }
            else
            {
                // Forces already exist — open via List Options dropdown
                await page.Locator(".dotsMenuContainer").Filter(new() { HasText = "List Options" }).First.ClickAsync();
                await page.GetByText("Add Force").First.ClickAsync(new() { Timeout = 5_000 });
            }

            await forcesPanel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        }

        // Find the force row with matching name and click its addButton (+)
        var forceRow = forcesPanel.Locator(".unit-wrap.force").Filter(new() { Has = page.Locator(".name", new() { HasTextString = forceName }) });
        if (await forceRow.CountAsync() > 0)
        {
            await forceRow.First.Locator(".addButton").ClickAsync(new() { Timeout = 10_000 });
        }
        else if (forceEntryId is not null)
        {
            // Force not visible in panel — close popup and use JS
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(300);
            return await AddForceByJsAsync(page, forceEntryId, catalogueId);
        }
        else
        {
            throw new TimeoutException($"Force '{forceName}' not found in the forces panel and no forceEntryId for JS fallback");
        }

        return await WaitForNewForceUidAsync(page, before);
    }

    private static async Task<string?> AddForceByJsAsync(IPage page, string forceEntryId, string? catalogueId)
    {
        var newUid = await page.EvaluateAsync<string?>("""
            ({forceEntryId, catalogueId}) => {
                const spec = window.__bsspec;
                if (!spec) throw new Error('no spec state');
                const army = spec.army;
                const books = spec.books || [spec.book];
                const catIds = spec.bookCatalogueIds || [];
                const book = catalogueId
                    ? (books[catIds.indexOf(catalogueId)] || books[0])
                    : books[0];
                if (!army || !book) throw new Error('no army or book');

                const beforeUids = new Set(
                    (army.getForces?.() || []).map(f => f.uid));
                army.insertForce(book, forceEntryId);
                const afterForces = army.getForces?.() || [];
                for (const f of afterForces) {
                    if (f.uid && !beforeUids.has(f.uid)) return f.uid;
                }
                return null;
            }
            """, new { forceEntryId, catalogueId });
        await page.WaitForTimeoutAsync(500);
        return newUid;
    }

    /// <summary>
    /// Adds a child force under <paramref name="parentForceId"/> by name.
    /// Flow: locate the parent .bookForce by force name → expand .childForces accordion
    /// → click the matching force type's .addButton in .childForces .unitList.
    /// </summary>
    public static async Task<string?> AddChildForceByNameAsync(IPage page, string parentForceId, string forceName, string? forceEntryId = null, string? catalogueId = null)
    {
        var before = await GetAllForceUidsAsync(page);

        // Resolve the parent force's name to find its .bookForce element by header text
        var parentForceName = await page.EvaluateAsync<string?>("""
            ([targetUid]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return null;
                const force = (army.getForces?.() || []).find(f => f.uid === targetUid);
                return force?.getName?.() ?? force?.name ?? null;
            }
            """, new[] { parentForceId });

        ILocator parentBookForce;
        if (parentForceName != null)
        {
            parentBookForce = page.Locator(".bookForce")
                .Filter(new() { HasText = parentForceName })
                .First;
        }
        else
        {
            await TagBookForceElementsAsync(page);
            parentBookForce = page.Locator($".bookForce[data-nrui-force-uid='{parentForceId}']");
        }

        // Try UI path: expand childForces accordion and click force type row
        var uiSuccess = false;
        try
        {
            var childForcesHeader = parentBookForce.Locator(".childForces h3.arrowTitle").First;
            // Use short timeout — if the section isn't usable, fall back to JS quickly
            await childForcesHeader.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 2_000 });

            var isCollapsed = await childForcesHeader.EvaluateAsync<bool>(
                "el => el.classList.contains('collapsed')");
            if (isCollapsed)
            {
                await childForcesHeader.ClickAsync(new() { Timeout = 3_000 });
            }

            var unitList = parentBookForce.Locator(".childForces .unitList");
            await unitList.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

            var forceRow = unitList.Locator(".unit-wrap").Filter(new() { HasText = forceName });
            await forceRow.Locator(".addButton").First.ClickAsync(new() { Timeout = 5_000 });
            uiSuccess = true;
        }
        catch (TimeoutException)
        {
            // UI path failed — fall through to JS fallback
        }

        if (!uiSuccess)
        {
            if (forceEntryId is not null)
            {
                // JS fallback — child forces section not visible/interactable in NR UI
                await page.EvaluateAsync("""
                    ([parentForceUid, childForceEntryId, catalogueId]) => {
                        const spec = window.__bsspec;
                        if (!spec) throw new Error('no spec state');
                        const army = spec.army;
                        const books = spec.books || [spec.book];
                        const catIds = spec.bookCatalogueIds || [];
                        const book = catalogueId
                            ? (books[catIds.indexOf(catalogueId)] || books[0])
                            : books[0];
                        if (!army || !book) throw new Error('no army or book');
                        const parentForce = (army.getForces?.() || []).find(f => f.uid === parentForceUid);
                        if (!parentForce) throw new Error('parent force not found: ' + parentForceUid);
                        parentForce.insertForce(book, childForceEntryId);
                    }
                    """, new object[] { parentForceId, forceEntryId, catalogueId ?? "" });
                await page.WaitForTimeoutAsync(500);
            }
            else
            {
                throw new TimeoutException($"Child force '{forceName}' section not visible and no forceEntryId for JS fallback");
            }
        }

        return await WaitForNewForceUidAsync(page, before);
    }

    /// <summary>
    /// Removes a force. TODO: probe force delete button in NR UI; uses JS fallback for now.
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, string forceUid)
    {
        _ = forceUid;
        // TODO: probe what the force delete button looks like — for now, use JS direct mutation
        await page.EvaluateAsync("""
            (forceUid) => {
                const army = window.__bsspec?.army;
                if (!army) throw new Error('no army');
                const force = army.getForces?.()?.find(f => f.uid === forceUid);
                if (!force) throw new Error('force not found: ' + forceUid);
                force.delete?.();
            }
            """, forceUid);
        await MaybeConfirmDeletionAsync(page);
    }

    // ===== Selection operations =====

    /// <summary>
    /// Selects (adds) an entry in a force by clicking its "+" button in the force's unit list.
    /// NR renders each force's available entries inside its own .bookForce element (whose id
    /// attribute equals the force UID), so we scope the locator to that force directly.
    /// Annotates the unitList DOM with entry IDs via Pinia so entries can be found by ID even
    /// when their display name has been changed by a modifier.
    /// Returns the uid of the newly created selection.
    /// </summary>
    public static async Task<string?> SelectEntryByNameAsync(IPage page, string forceUid, string entryId, string entryName)
    {
        var before = await GetAllSelectionUidsAsync(page);

        // Annotate unitList DOM elements with data-spec-entry-id so we can find by ID
        // rather than display name (which may differ after modifiers are applied)
        await TagUnitListEntriesAsync(page);

        // Scope the locator to the specific force's bookForce container using attribute selector.
        // NR's force UIDs can start with a digit (e.g. "5ttp79g"), which makes "#uid" an invalid
        // CSS selector; use [id='uid'] instead which has no such restriction.
        // In multi-force rosters every bookForce has its own .unitList; without scoping we
        // would always click force[0]'s entry list.
        var idLocator = page.Locator($"[id='{forceUid}'] .unitList .unit-wrap[data-spec-entry-id='{entryId}']");
        var nameLocator = page.Locator($"[id='{forceUid}'] .unitList .unit-wrap").Filter(new() { HasText = entryName }).First;

        ILocator entryRow;
        if (await idLocator.CountAsync() > 0)
        {
            entryRow = idLocator.First;
        }
        else
        {
            // ID annotation missed this entry — fall back to name search within the force
            entryRow = nameLocator;
        }

        // Check if the entry is visible in the UI (hidden entries won't be)
        var isVisible = await entryRow.CountAsync() > 0 && await entryRow.IsVisibleAsync();
        if (isVisible)
        {
            await entryRow.Locator(".addButton").First.ClickAsync(new() { Timeout = 10_000 });
            return await WaitForNewSelectionUidAsync(page, before);
        }

        // JS fallback for hidden entries not visible in the catalogue panel
        var result = await page.EvaluateAsync<string?>("""
            ({forceUid, entryId}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'ERROR:No current roster';

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `ERROR:Force not found with uid '${forceUid}'`;

                    const before = new Set(
                        getSelections(force).map(s => s.uid));

                    const selector = findSelectorById(force, entryId);
                    if (!selector) return `ERROR:Entry '${entryId}' not found in force selector tree`;

                    if (typeof selector.addInstance !== 'function')
                        return `ERROR:Selector for '${entryId}' has no addInstance`;

                    selector.addInstance();

                    const after = getSelections(force);
                    for (const s of after) {
                        if (s.uid && !before.has(s.uid)) {
                            s.autocheck();
                            return s.uid;
                        }
                    }

                    return `ERROR:addInstance on '${entryId}' did not produce a new selection`;
                } catch(e) {
                    return 'ERROR:SelectEntry error: ' + e.message;
                }
            }
            """, new { forceUid, entryId });
        if (result?.StartsWith("ERROR:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(result[6..]);
        }
        return result;
    }

    /// <summary>
    /// Selects a child entry under an existing selection by incrementing its count
    /// in the parent selection's options panel.
    /// Supports two NR UI styles:
    ///   • numeric (input[type=number]): child has max > 1 → increment value by 1
    ///   • binary (button.boutonSubUnit): child has max = 1 → click the "+" button
    /// Falls back to JS-based selection for hidden entries not visible in the UI.
    /// Returns the uid of the child selection.
    /// </summary>
    public static async Task<string?> SelectChildEntryByNameAsync(IPage page, string parentSelectionUid, string entryName, string? entryId = null)
    {
        // Open the options panel for the parent selection
        await OpenOptionsPanelAsync(page, parentSelectionUid);

        // Find the .inputOption container for this child entry
        var entryOption = page.Locator(".inputOption")
            .Filter(new() { Has = page.Locator("span.optionLabel", new() { HasTextString = entryName }) });

        var isVisible = await entryOption.First.IsVisibleAsync();
        if (isVisible)
        {
            // UI path: numeric input or binary button
            var numInput = entryOption.Locator("input[type='number']");
            if (await numInput.CountAsync() > 0)
            {
                var currentVal = int.TryParse(await numInput.First.InputValueAsync(), out var v) ? v : 0;
                await numInput.First.FillAsync((currentVal + 1).ToString());
                await numInput.First.PressAsync("Tab");
            }
            else
            {
                // Binary (checkbox-style) entry — click the "+" boutonSubUnit button
                await entryOption.Locator("button.boutonSubUnit").First.ClickAsync();
            }
        }
        else
        {
            // JS fallback for hidden entries not visible in the options panel.
            // Uses the same selector-tree traversal as NewRecruitActions.SelectChildEntryByIdAsync.
            var searchId = entryId ?? entryName;
            await page.EvaluateAsync("""
                ([parentSelectionUid, searchId]) => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army
                        ?? window.__bsspec?.army;
                    if (!army) throw new Error('no army');
                    function findInNode(node) {
                        for (const s of (node.getSelections?.() || [])) {
                            if (s.uid === parentSelectionUid) return s;
                            const found = findInNode(s);
                            if (found) return found;
                        }
                        return null;
                    }
                    let sel = null;
                    for (const f of (army.getForces?.() || [])) {
                        sel = findInNode(f);
                        if (sel) break;
                    }
                    if (!sel) throw new Error('parent not found: ' + parentSelectionUid);

                    // Check if already exists as a child instance
                    const children = sel.getSelections?.() || [];
                    const existing = children.find(c =>
                        c.getId?.() === searchId
                        || c.source?.id === searchId
                        || c.selector?.ids?.includes?.(searchId));
                    if (existing) {
                        if (existing.selector?.isInstanced) {
                            existing.selector.addInstance?.();
                        } else {
                            existing.incrementAmount?.();
                            existing.autocheck?.();
                        }
                        return;
                    }

                    // Not pre-created — search selector tree and addInstance
                    function findSelectorDeep(selectors, id) {
                        for (const s of selectors) {
                            if (s.id === id || s.ids?.includes(id)) return s;
                            for (const inst of (s.instances || [])) {
                                const found = findSelectorDeep(inst.selectors || [], id);
                                if (found) return found;
                            }
                        }
                        return null;
                    }
                    const targetSelector = findSelectorDeep(sel.selectors || [], searchId);
                    if (!targetSelector)
                        throw new Error('child entry not found: ' + searchId);
                    targetSelector.addInstance?.();
                }
                """, new object[] { parentSelectionUid, searchId });
            await page.WaitForTimeoutAsync(300);
        }

        // Query the child uid from the parent selection's children
        return await page.EvaluateAsync<string?>("""
            ([parentSelectionUid, childName]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return null;
                function findInForce(node) {
                    for (const s of (node.getSelections?.() || [])) {
                        if (s.uid === parentSelectionUid) return s;
                        const found = findInForce(s);
                        if (found) return found;
                    }
                    return null;
                }
                let parent = null;
                for (const f of (army.getForces?.() || [])) {
                    parent = findInForce(f);
                    if (parent) break;
                }
                if (!parent) return null;
                const child = (parent.getSelections?.() || []).find(s => s.getName?.() === childName || s.name === childName);
                return child?.uid ?? null;
            }
            """, new object[] { parentSelectionUid, entryName });
    }

    /// <summary>
    /// Sets the count of a child entry in the parent selection's options panel.
    /// Supports numeric inputs (input[type=number]) only — binary entries (boutonSubUnit) are not applicable for count-setting.
    /// </summary>
    public static async Task SetChildEntryCountByNameAsync(IPage page, string parentSelectionUid, string entryName, int count)
    {
        await OpenOptionsPanelAsync(page, parentSelectionUid);

        var entryOption = page.Locator(".inputOption")
            .Filter(new() { Has = page.Locator("span.optionLabel", new() { HasTextString = entryName }) });
        var numInput = entryOption.Locator("input[type='number']");
        await numInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await numInput.First.FillAsync(count.ToString());
        await numInput.First.PressAsync("Tab");
    }

    /// <summary>
    /// Falls back to JS-based count mutation for root selections that have no direct spinner in the UI.
    /// </summary>
    public static async Task SetSelectionCountAsync(IPage page, string selectionUid, int count)
    {
        await page.EvaluateAsync("""
            ([selectionUid, count]) => {
                const army = window.__bsspec?.army;
                if (!army) throw new Error('no army');
                function findInNode(node) {
                    for (const s of (node.getSelections?.() || [])) {
                        if (s.uid === selectionUid) return s;
                        const found = findInNode(s);
                        if (found) return found;
                    }
                    return null;
                }
                let sel = null;
                for (const f of (army.getForces?.() || [])) {
                    sel = findInNode(f);
                    if (sel) break;
                }
                if (!sel) throw new Error('selection not found: ' + selectionUid);
                sel.setNumSelections?.(Number(count));
            }
            """, new object[] { selectionUid, count });
    }

    /// <summary>
    /// Tags the .unitRow[draggable=false] for the given selection uid, then returns a Playwright
    /// locator for it. Throws if the element is not found in the DOM (e.g., nested selection
    /// whose parent panel is not yet open).
    /// </summary>
    private static async Task<ILocator> GetSelectionLocatorAsync(IPage page, string selectionUid)
    {
        await FindSelectionElementAsync(page, selectionUid);
        return page.Locator($"[data-nrui-uid='{selectionUid}']");
    }

    /// <summary>
    /// Removes a selection by clicking its "Delete Unit" trash icon.
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, string selectionUid)
    {
        var found = await FindSelectionElementAsync(page, selectionUid);
        if (found)
        {
            var selEl = page.Locator($"[data-nrui-uid='{selectionUid}']");
            await selEl.Locator("img[title='Delete Unit']").ClickAsync();
            await MaybeConfirmDeletionAsync(page);
        }
        else
        {
            // JS fallback for child/hidden selections without .unitRow in DOM.
            // Mirrors the logic from NewRecruitActions.DeselectSelectionAsync.
            await page.EvaluateAsync("""
                (selectionUid) => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army
                        ?? window.__bsspec?.army;
                    if (!army) throw new Error('no army');
                    function findInNode(node) {
                        for (const s of (node.getSelections?.() || [])) {
                            if (s.uid === selectionUid) return s;
                            const found = findInNode(s);
                            if (found) return found;
                        }
                        return null;
                    }
                    let sel = null;
                    for (const f of (army.getForces?.() || [])) {
                        sel = findInNode(f);
                        if (sel) break;
                    }
                    if (!sel) throw new Error('selection not found: ' + selectionUid);
                    if (typeof sel.decrementAmount === 'function') {
                        sel.decrementAmount();
                        if (typeof sel.getAmount === 'function' && sel.getAmount() === 0
                            && typeof sel.delete === 'function') {
                            sel.delete();
                        }
                    } else if (typeof sel.delete === 'function') {
                        sel.delete();
                    } else {
                        throw new Error('cannot deselect: no decrementAmount or delete on ' + selectionUid);
                    }
                }
                """, selectionUid);
            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>
    /// Duplicates a selection using the "Duplicate Unit" button and returns the new uid.
    /// </summary>
    public static async Task<string?> DuplicateSelectionAsync(IPage page, string selectionUid)
    {
        var before = await GetAllSelectionUidsAsync(page);
        var selEl = await GetSelectionLocatorAsync(page, selectionUid);
        await selEl.Locator("img[title='Duplicate Unit']").ClickAsync();
        return await WaitForNewSelectionUidAsync(page, before);
    }

    /// <summary>
    /// Duplicates a force. TODO: probe force duplicate button location.
    /// </summary>
    public static async Task<string?> DuplicateForceAsync(IPage page, string forceUid)
    {
        // NR's force method is `dupe()` (async), not `duplicate()`
        var newUid = await page.EvaluateAsync<string?>("""
            async (forceUid) => {
                const army = window.__bsspec?.army;
                if (!army) throw new Error('no army');
                const beforeUids = new Set((army.getForces?.() || []).map(f => f.uid));
                const force = (army.getForces?.() || []).find(f => f.uid === forceUid);
                if (!force) throw new Error('force not found: ' + forceUid);
                if (typeof force.dupe !== 'function')
                    throw new Error('dupe() method not available on force');
                await force.dupe();
                for (const f of (army.getForces?.() || [])) {
                    if (f.uid && !beforeUids.has(f.uid)) return f.uid;
                }
                return null;
            }
            """, forceUid);
        return newUid;
    }

    // ===== Roster-level operations =====

    /// <summary>
    /// Sets a cost limit via JS (NR UI doesn't expose a direct cost-limit input in the editor).
    /// </summary>
    public static async Task SetCostLimitAsync(IPage page, string costTypeId, decimal value)
    {
        await page.EvaluateAsync("""
            ([costTypeId, value]) => {
                const army = window.__bsspec?.army;
                if (!army) throw new Error('No army');
                const maxCosts = army.getMaxCosts?.() || [];
                const cost = maxCosts.find(c => c.typeId === costTypeId || c.id === costTypeId);
                if (!cost) throw new Error('Cost type not found: ' + costTypeId);
                cost.value = Number(value);
                army.setMaxCosts?.(maxCosts);
            }
            """, new object[] { costTypeId, (double)value });
    }

    /// <summary>
    /// Sets custom name/notes for a force or selection.
    /// <para>
    /// NR's roster UI does not expose dedicated custom name/notes editing fields for
    /// individual selections or force custom names. The force notes can be opened via an
    /// inline editname button, but for consistency and reliability, we use direct JS property
    /// writes for all customization. The supporter paywall bypass (set in setup) ensures
    /// that NR's internal serialization includes these fields.
    /// </para>
    /// </summary>
    public static async Task SetCustomizationAsync(
        IPage page,
        string forceId,
        string? selectionId,
        string? categoryEntryId,
        string? customName,
        string? customNotes)
    {
        // Category-level customization is not supported in NR — skip entirely
        if (categoryEntryId is not null && selectionId is null)
        {
            return;
        }

        // NR's UI has limited customization support:
        // - Selection customName/note: no UI at all
        // - Force customName: no UI (only catalogue/entry names shown)
        // - Force note: only a contenteditable notes field (no name input, no save button)
        // For reliability, use direct JS property writes for all cases.
        await page.EvaluateAsync("""
            ([forceId, selectionId, customName, customNotes]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s.get('lists');
                const army = lists?.currentList?.army;
                if (!army) return;
                const forces = army.getForces?.() ?? [];
                for (const f of forces) {
                    if (f.uid === forceId || f.getId?.() === forceId) {
                        if (selectionId) {
                            // Find the selection (including nested) via recursive search
                            function findSel(node) {
                                for (const s of (node.getSelections?.() ?? [])) {
                                    if (s.uid === selectionId || s.getId?.() === selectionId) return s;
                                    const found = findSel(s);
                                    if (found) return found;
                                }
                                return null;
                            }
                            const sel = findSel(f);
                            if (sel) {
                                if (customName !== null && customName !== undefined) sel.customName = customName;
                                if (customNotes !== null && customNotes !== undefined) sel.note = customNotes;
                            }
                        } else {
                            if (customName !== null && customName !== undefined) f.customName = customName;
                            if (customNotes !== null && customNotes !== undefined) f.note = customNotes;
                        }
                        return;
                    }
                }
            }
            """, new object?[] { forceId, selectionId, customName, customNotes });
    }

    // ===== Internal: element finders =====

    /// <summary>
    /// Tags the .unitRow[draggable=false] element for the given selection uid using DFS index matching.
    /// Returns true if the element was found and tagged, false if it is not currently in the DOM
    /// (e.g. nested selections whose parent panel is not yet open).
    ///
    /// Uses Pinia currentList.army as primary source to avoid stale army references.
    /// </summary>
    private static async Task<bool> FindSelectionElementAsync(IPage page, string selectionUid)
    {
        return await page.EvaluateAsync<bool>("""
            (selectionUid) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return false;

                // Clear all previously tagged elements to avoid stale duplicates
                // (e.g., after duplicateForce when DOM is re-rendered)
                for (const el of document.querySelectorAll('[data-nrui-uid]')) {
                    el.removeAttribute('data-nrui-uid');
                }

                // Collect only TOP-LEVEL selection UIDs (direct children of forces).
                // Child selections (e.g., Trooper under Infantry Squad) do NOT have
                // .unitRow elements — they appear in the options panel instead.
                // Recursing into children would break the index-to-DOM-row mapping.
                const allSels = [];
                for (const f of (army.getForces?.() || [])) {
                    for (const s of (f.getSelections?.() || [])) {
                        allSels.push(s.uid);
                    }
                }

                const idx = allSels.indexOf(selectionUid);
                if (idx < 0) return false;
                const rows = document.querySelectorAll('.unitRow[draggable=false]');
                if (rows[idx]) {
                    rows[idx].setAttribute('data-nrui-uid', selectionUid);
                    return true;
                }
                return false;
            }
            """, selectionUid);
    }

    /// <summary>
    /// Returns the UID of the direct parent (force or selection) of the given selection.
    /// Returns null if not found.
    /// </summary>
    private static Task<string?> FindParentUidAsync(IPage page, string selectionUid)
    {
        return page.EvaluateAsync<string?>("""
            (selectionUid) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return null;
                function findParent(node) {
                    for (const s of (node.getSelections?.() || [])) {
                        if (s.uid === selectionUid) return node;
                        const found = findParent(s);
                        if (found !== null) return found;
                    }
                    return null;
                }
                for (const f of (army.getForces?.() || [])) {
                    const parent = findParent(f);
                    if (parent !== null) return parent.uid ?? null;
                }
                return null;
            }
            """, selectionUid);
    }

    /// <summary>
    /// Opens the options panel for a selection by clicking its .displayName if not already editing.
    /// For nested selections (children of other selections) that don't have their own .unitRow,
    /// opens the closest ancestor that does have a .unitRow — the nested entries are then visible
    /// as collapsible sub-sections within that ancestor's panel.
    /// </summary>
    private static async Task OpenOptionsPanelAsync(IPage page, string selectionUid)
    {
        var found = await FindSelectionElementAsync(page, selectionUid);
        if (!found)
        {
            // Nested selection: no .unitRow in DOM. Find parent and open its panel instead.
            // The nested selection's child entries appear as collapsible sub-sections within
            // the parent's (or ancestor's) options panel — no separate panel navigation needed.
            var parentUid = await FindParentUidAsync(page, selectionUid);
            if (parentUid != null)
            {
                await OpenOptionsPanelAsync(page, parentUid);
            }
            return;
        }

        var selEl = page.Locator($"[data-nrui-uid='{selectionUid}']");
        await DismissOverlaysAsync(page);
        var isEditing = await selEl.EvaluateAsync<bool>("el => el.classList.contains('editing')");
        if (!isEditing)
        {
            await selEl.Locator(".displayName").ClickAsync();
            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>
    /// Returns the number input for a child entry option by its label text.
    /// Uses Filter-based locator rather than :text-is() inside :has() for reliability.
    /// Not scoped to a specific parent element — the options panel is the only active
    /// context, so any visible .inputOption on the page is from the correct panel.
    /// </summary>
    private static ILocator GetOptionsInput(IPage page, string entryName)
    {
        return page.Locator(".inputOption")
            .Filter(new() { Has = page.Locator("span.optionLabel", new() { HasTextString = entryName }) })
            .Locator("input[type='number']")
            .First;
    }

    // ===== Internal: bookForce element tagging =====

    /// <summary>
    /// Navigates NR to show the specified force's catalogue in the left panel by clicking
    /// its bookForce header. No-op when only one force exists (already active).
    /// </summary>
    private static async Task NavigateToForceAsync(IPage page, string forceUid)
    {
        var forceCount = await page.Locator(".bookForce").CountAsync();
        if (forceCount <= 1)
        {
            return;
        }

        // Read force name from the LIVE Pinia currentList.army (not window.__bsspec.army,
        // which can become stale after NR replaces the army object on UI mutations).
        var forceName = await page.EvaluateAsync<string?>("""
            ([targetUid]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return null;
                const force = (army.getForces?.() || []).find(f => f.uid === targetUid);
                return force?.getName?.() ?? force?.name ?? null;
            }
            """, new[] { forceUid });

        if (forceName == null)
        {
            return;
        }

        // Find the .bookForce containing this force's name, then click its header div.
        // Filtering on .bookForce (the container) rather than the header div alone is more
        // reliable: the container accumulates all force text, header text is a substring.
        var targetBookForce = page.Locator(".bookForce")
            .Filter(new() { HasText = forceName })
            .First;
        await targetBookForce.Locator("> div").First.ClickAsync(new() { Timeout = 5_000 });

        // Wait for NR to re-render the left catalogue panel.
        await page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Annotates each visible .unit-wrap in the .unitList with a data-spec-entry-id attribute.
    ///
    /// Strategy:
    ///   1. Build a name→id map from the PLAYABLE books' selectionEntries (Pinia).
    ///   2. First pass: for each unit-wrap, match its displayed name to a Pinia entry id.
    ///      This covers the common case where display name == original name.
    ///   3. Second pass: remaining unmatched wraps (modifier-renamed entries whose DOM name
    ///      differs from the Pinia original) are assigned remaining entry ids in relative
    ///      declaration order.
    ///
    /// Using only PLAYABLE books avoids stale data from prior tests in a shared browser
    /// session and avoids index-offset bugs when the game system book is iterated first.
    /// </summary>
    private static async Task TagUnitListEntriesAsync(IPage page)
    {
        await page.EvaluateAsync("""
            async () => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return;
                    const sysStore = pinia._s.get('systemsStore');
                    if (!sysStore) return;
                    const sys = sysStore._selectedSystem;
                    if (!sys) return;

                    const unitWraps = [...document.querySelectorAll('.unitList .unit-wrap')];
                    if (unitWraps.length === 0) return;

                    // Use only playable books (active force's catalogues) to avoid
                    // stale/unrelated books from prior tests in a shared browser session.
                    const playableBooks = (sys.books?.array || []).filter(b => b.playable);

                    // Build name→id and ordered id list from Pinia (original names).
                    const nameToId = new Map();
                    const orderedIds = [];
                    for (const book of playableBooks) {
                        const bd = await sys.getBook?.(book.id);
                        if (!bd?.catalogue) continue;
                        for (const entry of (bd.catalogue.selectionEntries || [])) {
                            if (!nameToId.has(entry.name)) {
                                nameToId.set(entry.name, entry.id);
                            }
                            orderedIds.push(entry.id);
                        }
                    }

                    // First pass: match unit-wraps by their current displayed name.
                    // This is reliable for entries whose name hasn't been changed by a modifier.
                    const matchedIds = new Set();
                    const unmatchedWraps = [];
                    for (const wrap of unitWraps) {
                        const domName = wrap.querySelector('.name')?.textContent?.trim();
                        const entryId = domName ? nameToId.get(domName) : undefined;
                        if (entryId && !matchedIds.has(entryId)) {
                            wrap.setAttribute('data-spec-entry-id', entryId);
                            matchedIds.add(entryId);
                        } else {
                            unmatchedWraps.push(wrap);
                        }
                    }

                    // Second pass: assign remaining ids (modifier-renamed entries) to
                    // unmatched wraps in their relative declaration order.
                    const unmatchedIds = orderedIds.filter(id => !matchedIds.has(id));
                    for (let i = 0; i < Math.min(unmatchedWraps.length, unmatchedIds.length); i++) {
                        unmatchedWraps[i].setAttribute('data-spec-entry-id', unmatchedIds[i]);
                    }
                } catch (_) {
                    // Non-fatal — Playwright will fall back to name-based lookup
                }
            }
            """);
    }

    /// <summary>
    /// Tags each .bookForce DOM element with data-nrui-force-uid by index-matching
    /// to army.getForces() — same approach used for selection uid tagging.
    /// Must be called before scoping Playwright locators to a specific parent force.
    /// </summary>
    private static async Task TagBookForceElementsAsync(IPage page)
    {
        await page.EvaluateAsync("""
            () => {
                const army = window.__bsspec?.army;
                if (!army) return;
                const forces = army.getForces?.() || [];
                const bookForces = document.querySelectorAll('.bookForce');
                for (let i = 0; i < Math.min(forces.length, bookForces.length); i++) {
                    bookForces[i].setAttribute('data-nrui-force-uid', forces[i].uid);
                }
            }
            """);
    }

    // ===== Internal: uid diffing =====

    /// <summary>
    /// Returns a mapping of entryId → selectionUid for all direct (top-level) selections
    /// in the specified force. Used to capture auto-added selections after addForce.
    /// </summary>
    public static async Task<Dictionary<string, string>> GetForceSelectionsAsync(IPage page, string forceUid)
    {
        // Return JSON.stringify instead of raw object to avoid Playwright's structured-clone
        // serialization issues with Vue reactive proxies wrapping uid strings.
        var json = await page.EvaluateAsync<string>("""
            ([forceUid]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return '{}';
                const force = (army.getForces?.() || []).find(f => f.uid === forceUid);
                if (!force) return '{}';
                const out = {};
                for (const s of (force.getSelections?.() || [])) {
                    // Use || (not ??) so empty strings also fall through to the next option.
                    const entryId = s.id || s.entryId || s.getEntryId?.();
                    if (entryId && s.uid) {
                        out[entryId] = s.uid;
                    }
                }
                return JSON.stringify(out);
            }
            """, new object[] { forceUid });
        if (string.IsNullOrEmpty(json) || json == "{}")
        {
            return [];
        }

        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }

    private static async Task<bool> HasMultipleBooksAsync(IPage page)
    {
        var count = await page.EvaluateAsync<int>("""
            () => {
                const spec = window.__bsspec;
                if (!spec) return 0;
                const books = spec.books || (spec.book ? [spec.book] : []);
                return books.length;
            }
            """);
        return count > 1;
    }

    private static async Task<HashSet<string>> GetAllForceUidsAsync(IPage page)
    {
        var uids = await page.EvaluateAsync<string[]>("""
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return [];
                return (army.getForces?.() || []).map(f => f.uid).filter(Boolean);
            }
            """);
        return new HashSet<string>(uids ?? [], StringComparer.Ordinal);
    }

    private static async Task<HashSet<string>> GetAllSelectionUidsAsync(IPage page)
    {
        var uids = await page.EvaluateAsync<string[]>("""
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return [];
                const results = [];
                function collect(node) {
                    for (const f of (node.getForces?.() || [])) {
                        collectSels(f);
                    }
                }
                function collectSels(node) {
                    for (const s of (node.getSelections?.() || node.getChildren?.() || [])) {
                        if (s.uid) results.push(s.uid);
                        collectSels(s);
                    }
                }
                collect(army);
                return results;
            }
            """);
        return new HashSet<string>(uids ?? [], StringComparer.Ordinal);
    }

    private static async Task<string?> WaitForNewForceUidAsync(IPage page, HashSet<string> before, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var after = await GetAllForceUidsAsync(page);
            var newUid = after.Except(before).FirstOrDefault();
            if (newUid is not null)
            {
                return newUid;
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static async Task<string?> WaitForNewSelectionUidAsync(IPage page, HashSet<string> before, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var after = await GetAllSelectionUidsAsync(page);
            var newUid = after.Except(before).FirstOrDefault();
            if (newUid is not null)
            {
                return newUid;
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static async Task MaybeConfirmDeletionAsync(IPage page)
    {
        try
        {
            var confirm = page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("ok|yes|confirm|delete", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await confirm.CountAsync() > 0 && await confirm.First.IsVisibleAsync())
            {
                await confirm.First.ClickAsync();
            }
        }
        catch
        {
            // No confirmation dialog — that's fine
        }
    }

    /// <summary>
    /// Dismisses overlay popups (cookie consent, etc.) that can block UI interactions.
    /// </summary>
    private static async Task DismissOverlaysAsync(IPage page)
    {
        try
        {
            var fcRoot = page.Locator(".fc-consent-root");
            try
            {
                await fcRoot.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 500 });
            }
            catch
            {
                return;
            }

            // CookieFirst consent dialog — try to reject/decline
            var rejectBtn = fcRoot.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("do not consent|reject|decline", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            try
            {
                await rejectBtn.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 500 });
                await rejectBtn.First.ClickAsync();
                await fcRoot.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
            }
            catch { /* no button visible */ }
        }
        catch
        {
            // No overlay — that's fine
        }
    }
}
