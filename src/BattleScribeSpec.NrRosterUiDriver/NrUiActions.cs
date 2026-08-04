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

        // Point the panel at the catalogue this force belongs to, before reading the list off it.
        //
        // NR renders `select.faction-select` here whenever the roster's system has more than one
        // playable catalogue, and the force list below is derived from it — the add-force component
        // filters by `newForceBook` and passes that book to `roster.insertForce(book, entryId)`,
        // which is also what calls `addCatalogue()` to bring the second catalogue into the roster.
        // Skipping it does not "just work": the panel stays on the list's own catalogue, so a force
        // requested from another one is silently created against the WRONG book, and the entries
        // that force should offer are then absent. Measured on
        // force/force-multi-catalogue-two-forces: force 2 was created against cat-a and
        // `selectEntry se-b1` returned Alpha Unit instead of Beta Unit.
        //
        // This mirrors what AddChildForceByNameAsync already does with `.childForces select`; the
        // top-level path simply never did it, on the strength of a comment asserting "the UI picks
        // the correct book internally when clicked". It does not.
        await SelectForceCatalogueAsync(page, catalogueId);

        // Find the force row with matching name and click its addButton (+)
        var forceRow = forcesPanel.Locator(".unit-wrap.force").Filter(new() { Has = page.Locator(".name", new() { HasTextString = forceName }) });
        if (await forceRow.CountAsync() > 0)
        {
            await forceRow.First.Locator(".addButton").ClickAsync(new() { Timeout = 10_000 });
        }
        else if (forceEntryId is not null)
        {
            // Hidden forces are not accessible via NR UI — throw
            await page.Keyboard.PressAsync("Escape");
            throw new NotSupportedException(
                $"NR UI: force '{forceName}' (entryId={forceEntryId}) is not visible in the forces panel (hidden force). " +
                "Hidden forces cannot be added via UI interaction.");
        }
        else
        {
            throw new TimeoutException($"Force '{forceName}' not found in the forces panel (no matching entry visible)");
        }

        return await WaitForNewForceUidAsync(page, before);
    }

    /// <summary>
    /// Points the add-force panel's catalogue picker at <paramref name="catalogueId"/>.
    /// <para>
    /// No-op when the picker is absent — NR only renders it for a system with more than one
    /// playable catalogue, so a single-catalogue spec has nothing to choose and every existing
    /// spec keeps its current behaviour.
    /// </para>
    /// </summary>
    private static async Task SelectForceCatalogueAsync(IPage page, string? catalogueId)
    {
        if (catalogueId is null)
        {
            return;
        }

        var factionSelect = page.Locator("select.faction-select").First;
        if (await factionSelect.CountAsync() == 0 || !await factionSelect.IsVisibleAsync())
        {
            return;
        }

        // NR labels the options by catalogue NAME; the spec addresses catalogues by id. Resolve
        // through the loaded books, which is where both live side by side.
        var catalogueName = await page.EvaluateAsync<string?>("""
            (catId) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const sys = pinia?._s?.get('systemsStore')?.selectedSystem;
                const book = (sys?.books?.array || []).find(b => b.id === catId || b.bsid === catId);
                return book?.name ?? null;
            }
            """, catalogueId);

        if (catalogueName is null)
        {
            return;
        }

        var options = await factionSelect.Locator("option").AllTextContentsAsync();
        if (!options.Any(o => o.Trim() == catalogueName))
        {
            // The catalogue exists but this picker is not offering it. Say so rather than silently
            // adding the force against whatever happens to be selected — that is the failure mode
            // this method was written to end.
            throw new InvalidOperationException(
                $"NR UI: catalogue '{catalogueName}' (id={catalogueId}) is not offered by the add-force " +
                $"faction picker. Offered: [{string.Join(", ", options.Select(o => o.Trim()))}].");
        }

        await factionSelect.SelectOptionAsync(new SelectOptionValue { Label = catalogueName });
        // The force list is derived from the selection; let it re-render before it is read.
        await page.WaitForTimeoutAsync(300);
    }

    /// <summary>
    /// Adds a child force under <paramref name="parentForceId"/> by name.
    /// Flow: locate the parent .bookForce by force name → expand .childForces accordion
    /// → click the matching force type's .addButton in .childForces .unitList.
    /// </summary>
    public static async Task<string?> AddChildForceByNameAsync(IPage page, string parentForceId, string forceName, string? forceEntryId = null, string? catalogueId = null)
    {
        var before = await GetAllForceUidsAsync(page);

        // Address the parent force by UID, never by name.
        //
        // This used to locate it with `.bookForce` filtered on `HasText = <parent's name>`, taking
        // `.First`. That is wrong for any nested force, because a parent's section LISTS ITS
        // AVAILABLE CHILD FORCE TYPES BY NAME: Army's `.bookForce` contains the text "Division", so
        // filtering for "Division" matched Army's section first and the driver then hunted for
        // "Platoon" in Army's child-force picker — which only ever offers "Division". Measured on
        // force/force-nested-multi-level, where it surfaced as the misleading "child force 'Platoon'
        // section is not visible/interactable".
        //
        // Name matching is also ambiguous whenever two forces share a name, which
        // force/force-multi-catalogue-two-forces does by design (two "Patrol" forces).

        var tagError = await TagBookForceElementsAsync(page);
        if (tagError is not null)
        {
            throw new InvalidOperationException($"NR UI: cannot address forces by uid — {tagError}");
        }

        var parentBookForce = page.Locator($".bookForce[data-nrui-force-uid='{parentForceId}']");

        // Try UI path: expand childForces accordion and click force type row
        try
        {
            // Close any open editing panel to ensure the bookForce is fully accessible
            await DismissOverlaysAsync(page);
            await CloseEditingPanelAsync(page);
            await page.WaitForTimeoutAsync(500);

            var childForcesHeader = parentBookForce.Locator(".childForces h3.arrowTitle").First;
            await childForcesHeader.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

            var isCollapsed = await childForcesHeader.EvaluateAsync<bool>(
                "el => el.classList.contains('collapsed')");
            if (isCollapsed)
            {
                await childForcesHeader.ClickAsync(new() { Timeout = 3_000 });
                await page.WaitForTimeoutAsync(300);
            }

            // If multiple catalogues exist, NR shows a <select> picker before the unitList
            var catSelect = parentBookForce.Locator(".childForces select");
            if (await catSelect.CountAsync() > 0)
            {
                // Resolve catalogue name from ID
                var catName = catalogueId != null
                    ? await page.EvaluateAsync<string?>("""
                        ([catId]) => {
                            const pinia = document.querySelector('#__nuxt')
                                ?.__vue_app__?.config?.globalProperties?.$pinia;
                            const army = pinia?._s?.get('lists')?.currentList?.army
                                ?? window.__bsspec?.army;
                            if (!army) return null;
                            const sys = army.system || army.gameSystem;
                            const books = sys?.books?.array || [];
                            const book = books.find(b => b.id === catId);
                            return book?.name ?? null;
                        }
                        """, new[] { catalogueId })
                    : null;

                if (catName != null)
                {
                    await catSelect.SelectOptionAsync(new SelectOptionValue { Label = catName });
                    await page.WaitForTimeoutAsync(300);
                }
                else
                {
                    // Select first non-disabled option
                    await catSelect.SelectOptionAsync(new SelectOptionValue { Index = 1 });
                    await page.WaitForTimeoutAsync(300);
                }
            }

            var unitList = parentBookForce.Locator(".childForces .unitList");
            await unitList.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

            var forceRow = unitList.Locator(".unit-wrap").Filter(new() { HasText = forceName });
            await forceRow.Locator(".addButton").First.ClickAsync(new() { Timeout = 5_000 });
        }
        catch (TimeoutException ex)
        {
            throw new NotSupportedException(
                $"NR UI: child force '{forceName}' section is not visible/interactable. " +
                $"Hidden or inaccessible child forces cannot be added via UI interaction. Detail: {ex.Message}");
        }

        return await WaitForNewForceUidAsync(page, before);
    }

    /// <summary>
    /// Removes a force via Force Options → "Delete Force" menu item.
    /// The force options dots menu is in the middle panel (.forceSection),
    /// not inside .bookForce. We identify it by force index.
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, string forceUid)
    {
        await DismissOverlaysAsync(page);
        await CloseEditingPanelAsync(page);

        // Find force index
        var forceIndex = await page.EvaluateAsync<int>("""
            ([uid]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return -1;
                const forces = army.getForces?.() || [];
                return forces.findIndex(f => f.uid === uid);
            }
            """, new[] { forceUid });

        if (forceIndex < 0)
        {
            throw new InvalidOperationException($"NR UI: Force '{forceUid}' not found in army.getForces().");
        }
        var forceOptions = page.Locator(".forceOptions").Nth(forceIndex);
        await forceOptions.Locator(".dots").ClickAsync(new() { Timeout = 5_000 });
        await page.GetByText("Delete Force", new() { Exact = true }).ClickAsync(new() { Timeout = 5_000 });
        await MaybeConfirmDeletionAsync(page);
        await page.WaitForTimeoutAsync(300);
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

        // Hidden entries are not accessible via NR UI — throw
        throw new NotSupportedException(
            $"NR UI: entry '{entryId}' is not visible in the catalogue panel (hidden entry). " +
            "Hidden entries cannot be selected via UI interaction.");
    }

    /// <summary>
    /// Selects a child entry under an existing selection by incrementing its count
    /// in the parent selection's options panel.
    /// Supports two NR UI styles:
    ///   • numeric (input[type=number]): child has max > 1 → increment value by 1
    ///   • binary (button.boutonSubUnit): child has max = 1 → click the "+" button
    /// Throws NotSupportedException for hidden entries not visible in the UI.
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
            // Hidden entries are not accessible via NR UI — throw
            throw new NotSupportedException(
                $"NR UI: child entry '{entryName}' is not visible in the options panel (hidden entry). " +
                "Hidden entries cannot be selected via UI interaction.");
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
    /// Root selection count is not editable via NR UI (no number input for root selections).
    /// Use selectEntry/deselectSelection to add/remove root instances instead.
    /// </summary>
    public static Task SetSelectionCountAsync(IPage page, string selectionUid, int count)
    {
        _ = page;
        _ = count;
        throw new NotSupportedException(
            $"NR UI: root selection '{selectionUid}' does not have a count input. " +
            "Root selection count is managed via selectEntry (add) and deselectSelection (remove).");
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
            await selEl.Locator("[title='Delete Unit']").ClickAsync();
            await MaybeConfirmDeletionAsync(page);
        }
        else
        {
            // Hidden/nested selections without a .unitRow are not accessible via NR UI — throw
            throw new NotSupportedException(
                $"NR UI: selection '{selectionUid}' has no .unitRow in DOM (hidden or nested). " +
                "Hidden selections cannot be deselected via UI interaction.");
        }
    }

    /// <summary>
    /// Duplicates a selection using the "Duplicate Unit" button and returns the new uid.
    /// </summary>
    public static async Task<string?> DuplicateSelectionAsync(IPage page, string selectionUid)
    {
        var before = await GetAllSelectionUidsAsync(page);
        var selEl = await GetSelectionLocatorAsync(page, selectionUid);
        await selEl.Locator("[title='Duplicate Unit']").ClickAsync();
        return await WaitForNewSelectionUidAsync(page, before);
    }

    /// <summary>
    /// Duplicates a force via Force Options → "Duplicate" menu item.
    /// Returns the uid of the newly created force.
    /// </summary>
    public static async Task<string?> DuplicateForceAsync(IPage page, string forceUid)
    {
        var before = await GetAllForceUidsAsync(page);
        await DismissOverlaysAsync(page);
        await CloseEditingPanelAsync(page);

        // Find force index to pick the correct .forceOptions element
        var forceIndex = await page.EvaluateAsync<int>("""
            ([uid]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return -1;
                const forces = army.getForces?.() || [];
                return forces.findIndex(f => f.uid === uid);
            }
            """, new[] { forceUid });

        if (forceIndex < 0)
        {
            throw new InvalidOperationException($"NR UI: Force '{forceUid}' not found in army.getForces().");
        }

        var forceOptions = page.Locator(".forceOptions").Nth(forceIndex);
        await forceOptions.Locator(".dots").ClickAsync(new() { Timeout = 5_000 });
        await page.GetByText("Duplicate Force", new() { Exact = true }).ClickAsync(new() { Timeout = 5_000 });
        return await WaitForNewForceUidAsync(page, before);
    }

    // ===== Roster-level operations =====

    /// <summary>
    /// Sets a cost limit via the "List Configuration" dialog:
    /// List Options → "List Configuration" → fill .maxCostInput for the target type → close.
    /// </summary>
    public static async Task SetCostLimitAsync(IPage page, string costTypeId, decimal value)
    {
        await DismissOverlaysAsync(page);

        // Open "List Options" dropdown
        await page.Locator(".dotsMenuContainer").Filter(new() { HasText = "List Options" }).First.ClickAsync();

        // Click the "List Configuration" menu item. Matched by its label, because the icon that
        // used to identify it (img[alt='edit cost limits']) is now an <nr-icon> with no alt and no
        // title. The item's own markup is otherwise unchanged across v34.93 and v35.12:
        // `<div class="imgBt"><span class="dropDownIcon">[icon]</span><span>List Configuration</span></div>`.
        // Label-matching is already how this driver picks every other menu item (Rename Unit,
        // Duplicate Force, and the "List Options" opener two lines up).
        await page.Locator(".subMenu .imgBt")
            .Filter(new() { HasText = "List Configuration" })
            .First.ClickAsync(new() { Timeout = 5_000 });

        // Wait for the configuration dialog to appear with cost limit inputs
        // Use attribute selector since typeId often contains special chars (dots, dashes)
        var costInput = page.Locator($"input[id='{costTypeId}']");
        await costInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        // Set the value
        var valueStr = value < 0 ? "" : ((int)value).ToString();
        await costInput.FillAsync(valueStr);
        await costInput.DispatchEventAsync("change");

        // Close the dialog
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(300);
    }

    /// <summary>
    /// Sets custom name/notes for a force or selection via UI interaction.
    /// <para>
    /// Selection-level: opens the unit's editing panel → submenu → "Rename Unit" / "Add Note".
    /// Force-level name: Force Options → "Rename Force".
    /// Force-level notes: JS fallback (no dedicated UI in NR).
    /// Supporter bypass (set during setup) unlocks notes editing.
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

        if (selectionId is not null)
        {
            await SetSelectionCustomizationAsync(page, selectionId, customName, customNotes);
        }
        else
        {
            await SetForceCustomizationAsync(page, forceId, customName, customNotes);
        }
    }

    /// <summary>
    /// Sets custom name/notes on a selection via the "Unit Options" submenu.
    /// Opens panel → clicks "Unit Options" in .unitNameTitle → "Rename Unit" / "Add Note".
    /// The submenu renders as a .subMenu overlay with menu items.
    /// </summary>
    private static async Task SetSelectionCustomizationAsync(
        IPage page,
        string selectionUid,
        string? customName,
        string? customNotes)
    {
        // Open the selection's options/editing panel
        await OpenOptionsPanelAsync(page, selectionUid);

        if (customName is not null)
        {
            // Open "Unit Options" submenu — the button is in .unitNameTitle .rightButton
            await OpenUnitOptionsSubmenuAsync(page);

            // Click "Rename Unit" in the dropdown
            await page.GetByText("Rename Unit").First.ClickAsync(new() { Timeout = 3_000 });
            await page.WaitForTimeoutAsync(300);

            // An editable pre element appears in the name area
            var nameInput = page.Locator(".unitNameTitle .editableDiv[contenteditable='true']").First;
            if (await nameInput.CountAsync() == 0)
            {
                // Fallback: any contenteditable in the title area
                nameInput = page.Locator(".unitNameTitle [contenteditable='true']").First;
            }
            await nameInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
            await nameInput.FillAsync(customName);
            await nameInput.PressAsync("Enter");
            await page.WaitForTimeoutAsync(300);
        }

        if (customNotes is not null)
        {
            // Open "Unit Options" submenu (may need to reopen after rename)
            await OpenUnitOptionsSubmenuAsync(page);

            // Click "Add Note" in the dropdown
            await page.GetByText("Add Note").First.ClickAsync(new() { Timeout = 3_000 });
            await page.WaitForTimeoutAsync(300);

            // Fill the note field — a contenteditable pre with class "note" appears in .content
            var noteField = page.Locator("pre.editableDiv.note[contenteditable='true'], pre[contenteditable='true'].note").First;
            if (await noteField.CountAsync() == 0)
            {
                // Fallback: any contenteditable in the content area
                noteField = page.Locator(".content [contenteditable='true']").First;
            }
            await noteField.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
            await noteField.FillAsync(customNotes);
            await page.WaitForTimeoutAsync(100);
        }
    }

    /// <summary>
    /// Opens the "Unit Options" submenu in the editing panel header.
    /// The button is the <c>.unitNameTitle .rightButton</c> carrying the <c>.menu</c> (kebab) icon.
    /// </summary>
    private static async Task OpenUnitOptionsSubmenuAsync(IPage page)
    {
        // Dismiss any existing submenu/overlay first
        var existingSubmenu = page.Locator(".subMenu");
        if (await existingSubmenu.CountAsync() > 0)
        {
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(200);
        }

        // Identify the button by the ICON'S CLASS, not its alt text. NR client v35 swapped every
        // raster icon for an <nr-icon> SVG component, so `img[alt='list menu']` — and every other
        // alt this driver matched on — ceased to exist. The wrapper's own class survived the swap
        // untouched: `<img class="menu" alt="list menu">` became `<nr-icon class="nr-icon menu">`,
        // inside the same `.imgBt.rightButton` div. `.menu` therefore matches both snapshots, and
        // does not depend on NR's UI language the way the sibling "Unit Options" label would.
        var unitOptionsBtn = page.Locator(".unitNameTitle .rightButton")
            .Filter(new() { Has = page.Locator(".menu") });
        await unitOptionsBtn.ClickAsync(new() { Timeout = 5_000 });
        // Wait for submenu to appear
        await page.Locator(".subMenu").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
    }

    /// <summary>
    /// Sets custom name/notes on a force.
    /// Name: Force Options → "Rename Force" → inline editable field.
    /// Notes: not supported in NR (no UI control) — silently ignored.
    /// </summary>
    private static async Task SetForceCustomizationAsync(
        IPage page,
        string forceId,
        string? customName,
        string? customNotes)
    {
        _ = customNotes; // NR doesn't support force-level notes (no UI control)

        if (customName is not null)
        {
            // Ensure we're viewing the force list (close any open editing panel)
            await DismissOverlaysAsync(page);
            await CloseEditingPanelAsync(page);

            // Find force index to pick the correct .forceOptions element
            var forceIndex = await page.EvaluateAsync<int>("""
                ([uid]) => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army
                        ?? window.__bsspec?.army;
                    if (!army) return -1;
                    const forces = army.getForces?.() || [];
                    return forces.findIndex(f => f.uid === uid);
                }
                """, new[] { forceId });

            var forceOptions = forceIndex >= 0
                ? page.Locator(".forceOptions").Nth(forceIndex)
                : page.Locator(".forceOptions").First;

            // Click "Force Options" dots menu
            await forceOptions.Locator(".dotsMenuContainer .dots").ClickAsync(new() { Timeout = 5_000 });

            // Click "Rename Force"
            await page.GetByText("Rename Force").First.ClickAsync(new() { Timeout = 3_000 });
            await page.WaitForTimeoutAsync(300);

            // Fill the inline rename field (contenteditable pre or input)
            var nameInput = forceOptions.Locator("[contenteditable='true']").First;
            if (await nameInput.CountAsync() == 0)
            {
                // Broader: any contenteditable that appeared in the force header area
                nameInput = page.Locator(".forceSection [contenteditable='true'], .titreForce [contenteditable='true']").First;
            }
            await nameInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
            await nameInput.FillAsync(customName);
            await nameInput.PressAsync("Enter");
            await page.WaitForTimeoutAsync(200);
        }
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
    /// <summary>
    /// Stamps each rendered <c>.bookForce</c> with the uid of the force it shows, so forces can be
    /// addressed by identity rather than by name. Returns null on success, or a diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapping is positional: NR renders one <c>.bookForce</c> per force as siblings, in the
    /// order <c>army.getForces()</c> returns them (flattened, all depths). That correspondence is
    /// load-bearing, so it is now <b>checked</b> — the counts must match. This used to loop to
    /// <c>Math.min(forces.length, bookForces.length)</c>, which silently left elements untagged
    /// whenever the two disagreed, and an untagged element is indistinguishable from a force that
    /// does not exist.
    /// </para>
    /// <para>
    /// Reads the army from the live store first and falls back to <c>window.__bsspec</c>, because
    /// the editor re-hydrates its own roster object and the two can diverge.
    /// </para>
    /// </remarks>
    private static async Task<string?> TagBookForceElementsAsync(IPage page)
    {
        return await page.EvaluateAsync<string?>("""
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army ?? window.__bsspec?.army;
                if (!army) return 'no army loaded';
                const forces = army.getForces?.() || [];
                const bookForces = document.querySelectorAll('.bookForce');
                if (forces.length !== bookForces.length) {
                    return 'roster has ' + forces.length + ' force(s) but the page renders '
                        + bookForces.length + ' .bookForce section(s); the positional mapping between '
                        + 'them does not hold, so a force cannot be addressed by uid';
                }
                for (let i = 0; i < forces.length; i++) {
                    bookForces[i].setAttribute('data-nrui-force-uid', forces[i].uid);
                }
                return null;
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

    /// <summary>
    /// Closes any open unit editing panel by clicking the close (X) button in the panel header.
    /// This returns the view to the force list, making left panel elements accessible.
    /// </summary>
    private static async Task CloseEditingPanelAsync(IPage page)
    {
        // `.back` is the button's own class (`imgBt back rightButton`, unchanged from v34.93 to
        // v35.12); the old `img[alt='Save unit']` selector matched its icon, which v35 replaced
        // with an <nr-icon> SVG component carrying no alt text.
        var saveBtn = page.Locator(".unitNameTitle .back");
        if (await saveBtn.CountAsync() > 0)
        {
            await saveBtn.First.ClickAsync(new() { Timeout = 3_000 });
            await page.WaitForTimeoutAsync(300);
        }
    }
}
