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
    private static Task SelectForceCatalogueAsync(IPage page, string? catalogueId)
        => SelectCatalogueInPickerAsync(page, page.Locator("select.faction-select").First, catalogueId, "add-force");

    /// <summary>
    /// Points a catalogue picker at <paramref name="catalogueId"/>, by name, and refuses to guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NR renders one of these wherever a force can come from more than one catalogue: the top-level
    /// add-force panel (<c>select.faction-select</c>) and each force's child-force section (a bare
    /// <c>select</c> under <c>.childForces</c>). Both are the same decision, so both go through here.
    /// </para>
    /// <para>
    /// <b>It must never fall back to an index.</b> The child-force path used to resolve the catalogue
    /// name through <c>army.system || army.gameSystem</c>, which is not where the books live — so the
    /// lookup returned null and the code selected <c>Index = 1</c>, i.e. whatever the first real
    /// option happened to be. On force/force-nested-multi-catalogue that is "Faction A", so a child
    /// force requested from <c>cat-b</c> was built against <c>cat-a</c>, and the entry it should then
    /// have offered (<c>se-b1</c>) was absent. That surfaced two steps later as "entry 'se-b1' is not
    /// visible in the catalogue panel" — blaming the entry panel for a force built against the wrong
    /// catalogue.
    /// </para>
    /// </remarks>
    private static async Task SelectCatalogueInPickerAsync(
        IPage page, ILocator picker, string? catalogueId, string what)
    {
        if (catalogueId is null || await picker.CountAsync() == 0 || !await picker.IsVisibleAsync())
        {
            return;
        }

        // NR labels the options by catalogue NAME; specs address catalogues by id. The loaded books
        // are where both live side by side — read them off systemsStore, which is the store that
        // actually holds them.
        var catalogueName = await page.EvaluateAsync<string?>("""
            (catId) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const sys = pinia?._s?.get('systemsStore')?.selectedSystem;
                const book = (sys?.books?.array || []).find(b => b.id === catId || b.bsid === catId);
                return book?.name ?? null;
            }
            """, catalogueId);

        var options = (await picker.Locator("option").AllTextContentsAsync())
            .Select(o => o.Trim())
            .ToList();

        if (catalogueName is null || !options.Contains(catalogueName))
        {
            // Say what went wrong rather than adding the force against whatever is selected.
            // Guessing here produced a wrong roster AND an error pointing at the wrong panel.
            throw new InvalidOperationException(
                $"NR UI: cannot point the {what} catalogue picker at id={catalogueId} — "
                + (catalogueName is null
                    ? "no loaded book has that id."
                    : $"'{catalogueName}' is not among its options.")
                + $" Offered: [{string.Join(", ", options)}].");
        }

        await picker.SelectOptionAsync(new SelectOptionValue { Label = catalogueName });

        // The force list is derived from the selection, so wait for the picker to actually CARRY it
        // rather than for 300ms to pass. The caller then reads that list with `CountAsync()` — a
        // snapshot — and on zero throws "not found in the forces panel"; worse, when two forces
        // share a name (force-multi-catalogue-two-forces has two "Patrol"), a stale list can match
        // the WRONG one and build a wrong roster instead of erroring.
        await page.WaitForFunctionAsync(
            """
            ([sel, want]) => {
                const el = document.evaluate(sel, document, null, 9, null).singleNodeValue;
                if (!el) { return false; }
                const opt = el.selectedOptions?.[0];
                return (opt?.textContent || '').trim() === want;
            }
            """,
            new object[] { await picker.EvaluateAsync<string>(XPathOfElement), catalogueName },
            new() { Timeout = 10_000 });
    }

    /// <summary>
    /// Returns a unique XPath for the element a locator resolves to, so a JS predicate can re-find
    /// exactly that node. Playwright locators cannot be passed into <c>WaitForFunctionAsync</c>.
    /// </summary>
    private const string XPathOfElement = """
        el => {
            const seg = n => {
                if (!n.parentElement) { return '/' + n.tagName.toLowerCase(); }
                const sibs = [...n.parentElement.children].filter(c => c.tagName === n.tagName);
                const i = sibs.indexOf(n) + 1;
                return seg(n.parentElement) + '/' + n.tagName.toLowerCase() + '[' + i + ']';
            };
            return seg(el);
        }
        """;


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

            // No sleep between closing the panel and reaching for the accordion. The 500ms here
            // stood in for "the panel finished closing", which CloseEditingPanelAsync now asserts
            // itself (it waits for `.unitRow.editing` to be hidden), and the line below already
            // waits for the element this step actually needs.
            var childForcesHeader = parentBookForce.Locator(".childForces h3.arrowTitle").First;
            await childForcesHeader.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });

            var isCollapsed = await childForcesHeader.EvaluateAsync<bool>(
                "el => el.classList.contains('collapsed')");
            if (isCollapsed)
            {
                await childForcesHeader.ClickAsync(new() { Timeout = 3_000 });

                // Wait for the accordion to be EXPANDED, not for 300ms.
                //
                // This is the highest-consequence wait in the file for its size. The next call,
                // SelectCatalogueInPickerAsync, early-returns when `picker.CountAsync() == 0 ||
                // !IsVisibleAsync()` — both snapshots — so a section that has not finished expanding
                // reads as "there is no catalogue picker here", the catalogue choice is skipped
                // WITHOUT A WORD, and the child force is built against the wrong book. That is the
                // documented force/force-nested-multi-catalogue bug, which surfaced two steps later
                // as "entry 'se-b1' is not visible in the catalogue panel".
                await childForcesHeader.WaitForAsync(new() { Timeout = 5_000 });
                await page.WaitForFunctionAsync(
                    """
                    (uid) => {
                        const bf = document.querySelector(`.bookForce[data-nrui-force-uid='${uid}']`);
                        const h = bf?.querySelector('.childForces h3.arrowTitle');
                        return !!h && !h.classList.contains('collapsed');
                    }
                    """,
                    parentForceId,
                    new() { Timeout = 5_000 });
            }

            // Same catalogue decision as the top-level add-force panel, same helper — NR renders a
            // bare <select> under .childForces when the child force could come from more than one.
            await SelectCatalogueInPickerAsync(
                page, parentBookForce.Locator(".childForces select").First, catalogueId, "child-force");

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

        // Wait for the force to be GONE from the army, which is what this method promises.
        //
        // The 300ms here was doing real work despite its size: callers re-read `army.getForces()`
        // immediately afterwards, and `RemoveCreateListForcesAsync` loops on that read — so a
        // deletion that had not landed made it re-target the same uid it had just removed.
        await page.WaitForFunctionAsync(
            """
            (uid) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army ?? window.__bsspec?.army;
                if (!army) { return true; }
                return !(army.getForces?.() || []).some(f => f.uid === uid);
            }
            """,
            forceUid,
            new() { Timeout = 10_000 });
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
        // `:not([data-nrui-force-row])` keeps both lookups off the child-force picker rows, which
        // share this markup and sit in the same .bookForce (see TagUnitListEntriesAsync).
        var idLocator = page.Locator(
            $"[id='{forceUid}'] .unitList .unit-wrap:not([data-nrui-force-row])[data-spec-entry-id='{entryId}']");
        var nameLocator = page.Locator($"[id='{forceUid}'] .unitList .unit-wrap:not([data-nrui-force-row])")
            .Filter(new() { HasText = entryName }).First;

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
            var selectionUid = await WaitForNewSelectionUidAsync(page, before);

            // NR can DISCARD the first '+' of a freshly created roster.
            //
            // It is not slowness — the wait above is 10s and the selection never arrives. The row is
            // rendered and clickable while NR is still finishing the catalogue work it starts when
            // the faction is chosen, and a click that lands in that window does nothing at all. It
            // used to be hidden by a flat 1500ms during roster creation, which simply ran the clock
            // out before the first step; removing that sleep exposed it on 8 specs.
            //
            // Retried rather than waited out, and safe to retry precisely because it is verifiable:
            // re-read the selections and only click again if NOTHING was added, so a slow-but-real
            // first click can never be turned into two selections.
            if (selectionUid is null)
            {
                var after = await GetAllSelectionUidsAsync(page);
                if (after.Except(before).Any())
                {
                    // It did land, just after the deadline. Take it rather than clicking again.
                    return after.Except(before).First();
                }

                // One retry, and deliberately WITHOUT re-tagging first. Re-running
                // TagUnitListEntriesAsync here looked like the obvious repair for a row whose
                // `data-spec-entry-id` had been lost to a re-render, and it was catastrophic:
                // 4 failures became 52 and the lane went from 16m44s to 47m31s. Stamping attributes
                // onto Vue-managed rows makes NR patch them, so tagging mid-flow perturbs exactly
                // the render it was meant to survive. The locators re-resolve on use anyway.
                await entryRow.Locator(".addButton").First.ClickAsync(new() { Timeout = 10_000 });
                selectionUid = await WaitForNewSelectionUidAsync(page, before);
            }

            // A click that produced no selection means the row was not a catalogue entry. Returning
            // null here let that surface two steps later as a wrong ASSERTION instead of an error —
            // which is how "clicked the child-force picker" was reported as "expected 1 child forces
            // but got 2". Fail where the mistake happened.
            return selectionUid ?? throw new InvalidOperationException(
                $"NR UI: clicked the '+' for entry '{entryId}' in force '{forceUid}' but no new "
                + "selection appeared — the click landed on something that is not a catalogue entry row.");
        }

        // No row for this entry. NR omits one for at least TWO measured reasons and this driver
        // cannot tell them apart from here, so report the observation and what the panel actually
        // offered rather than asserting a cause. "hidden entry" was asserted unconditionally, and on
        // catalogue/catalogue-category-entries it is simply false — `se-1` carries neither `hidden`
        // nor any modifier; its primary category just is not one of the force's own, so NR files it
        // under the `(Illegal Units)` group it builds with hidden:true and never renders.
        var offered = await DescribeUnitListAsync(page, forceUid);
        throw new NotSupportedException(
            $"NR UI: no selectable row for entry '{entryId}' ('{entryName}') in force '{forceUid}'. "
            + "NR renders a row only when the entry is not hidden AND its primary category is one of "
            + "the force's own categories; everything else goes to NR's '(Illegal Units)' group, which "
            + $"it builds hidden and never renders. Panel offered: {offered}");
    }

    /// <summary>
    /// What the catalogue panel actually offers for <paramref name="forceUid"/> — every entry row
    /// with the id this driver tagged it with, plus the panel's text.
    /// </summary>
    /// <remarks>
    /// Only used in a failure message. The wanted row is absent by then, and which rows ARE present
    /// is the first thing any diagnosis needs — four bugs this session were mis-diagnosed because
    /// the message named a cause instead of reporting the observation.
    /// </remarks>
    private static Task<string> DescribeUnitListAsync(IPage page, string forceUid)
        => page.EvaluateAsync<string>("""
            (forceUid) => {
                const force = document.querySelector("[id='" + forceUid + "']");
                if (!force) return '(no element carries this force uid)';
                const rows = [...force.querySelectorAll('.unitList .unit-wrap')]
                    .filter(w => !w.closest('.childForces'))
                    .map(w => (w.getAttribute('data-spec-entry-id') ?? '?')
                        + ':' + (w.querySelector('.name')?.textContent?.trim() ?? '?'));
                const panel = force.querySelector('.unitList')?.innerText?.trim()
                    .replace(/\s+/g, ' ').slice(0, 300) ?? '';
                return 'rows=[' + rows.join(', ') + '] text="' + panel + '"';
            }
            """, forceUid);

    /// <summary>
    /// Stamps the options-panel row NR renders for <paramref name="uid"/> with
    /// <c>data-nrui-option</c>. False when the panel has no row for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NR puts the option's own uid on the row's <c>&lt;label for&gt;</c>. That is the only identifier
    /// the panel carries, and the visible label is <b>not</b> one: it is the entry's CURRENT name, so
    /// a <c>set name</c> modifier renames it before it has ever been selected
    /// (condition/condition-instance-of-ancestor renders "Child Model" as "Has Ancestor"), and NR
    /// labels an entryLink with its TARGET's name, so two links to one shared entry render two rows
    /// that both read "Trigger" (condition/condition-shared-flag-nested). Text matching cannot
    /// separate those even in principle. Both were reported as "hidden entry".
    /// </para>
    /// <para>
    /// Two row shapes carry the label: <c>.inputOption</c>, and — for an instanced entry's own
    /// instance — <c>.subUnitHeaderRow</c>, whose <c>.stepper input.numInput</c> is its count control.
    /// </para>
    /// </remarks>
    private static Task<bool> TagOptionRowAsync(IPage page, string uid)
        => page.EvaluateAsync<bool>("""
            (uid) => {
                for (const el of document.querySelectorAll('[data-nrui-option]')) {
                    el.removeAttribute('data-nrui-option');
                }
                const label = document.querySelector("label[for='" + uid + "']");
                const row = label?.closest('.inputOption, .subUnitHeaderRow');
                if (!row) return false;
                row.setAttribute('data-nrui-option', uid);
                return true;
            }
            """, uid);

    /// <summary>
    /// Resolves <paramref name="entryId"/> to the uid of the child node NR renders for it under
    /// <paramref name="parentSelectionUid"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate is the one <c>NewRecruitActions.SelectChildEntryByIdAsync</c> already uses: an
    /// entryLink's <c>getId()</c> returns its TARGET's id, so the link's own id shows up only in
    /// <c>source.id</c> / <c>selector.ids</c> / <c>getBattleScribePath()</c>. NR pre-creates a
    /// zero-amount child node for every option it renders, so this resolves before selection.
    /// </para>
    /// <para>
    /// <paramref name="excludeInstanced"/> is for the SELECT path: selecting an instanced entry
    /// again must add an INSTANCE via its "+" row, not increment the one that already exists.
    /// </para>
    /// </remarks>
    private static Task<string?> FindChildOptionUidAsync(
        IPage page, string parentSelectionUid, string entryId, bool excludeInstanced)
        => page.EvaluateAsync<string?>("""
            ([parentUid, entryId, excludeInstanced]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army ?? window.__bsspec?.army;
                if (!army) return null;
                function find(node) {
                    for (const s of (node.getSelections?.() || [])) {
                        if (s.uid === parentUid) return s;
                        const found = find(s);
                        if (found) return found;
                    }
                    return null;
                }
                let parent = null;
                for (const f of (army.getForces?.() || [])) { parent = find(f); if (parent) break; }
                if (!parent) return null;
                const child = (parent.getSelections?.() || []).find(c =>
                    c.getId?.() === entryId
                    || c.source?.id === entryId
                    || c.selector?.ids?.includes?.(entryId)
                    || (entryId.includes('::') && c.getBattleScribePath?.() === entryId));
                if (!child) return null;
                if (excludeInstanced && child.selector?.isInstanced === true) return null;
                return child.uid ?? null;
            }
            """, new object[] { parentSelectionUid, entryId, excludeInstanced });

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

        // Address the row by NR's own uid; fall back to the label only for an instanced entry with
        // no instance yet, whose "+" row carries neither the child's uid nor the selector's.
        var childUid = entryId is null
            ? null
            : await FindChildOptionUidAsync(page, parentSelectionUid, entryId, excludeInstanced: true);

        var entryOption = childUid is not null && await TagOptionRowAsync(page, childUid)
            ? page.Locator($"[data-nrui-option='{childUid}']")
            : page.Locator(".inputOption")
                .Filter(new() { Has = page.Locator("span.optionLabel", new() { HasTextString = entryName }) });

        if (!await entryOption.First.IsVisibleAsync())
        {
            throw new NotSupportedException(
                $"NR UI: child entry '{entryName}' (entryId={entryId}) has no row in the options panel. " +
                "Hidden entries cannot be selected via UI interaction.");
        }

        var numInput = entryOption.Locator("input[type='number']");
        var checkbox = entryOption.Locator("input[type='checkbox']");
        if (await numInput.CountAsync() > 0)
        {
            var currentVal = int.TryParse(await numInput.First.InputValueAsync(), out var v) ? v : 0;
            await numInput.First.FillAsync((currentVal + 1).ToString());
            await numInput.First.PressAsync("Tab");
        }
        else if (await checkbox.CountAsync() > 0)
        {
            // An entry inside a CONSTRAINED GROUP renders as a checkbox — no number input and no
            // "+" button. Clicking `button.boutonSubUnit` on such a row waited out Playwright's
            // full 30s default (selection/selection-entry-group-constraint).
            await checkbox.First.CheckAsync(new() { Timeout = 5_000 });
        }
        else
        {
            // Binary (checkbox-style) entry — click the "+" boutonSubUnit button
            await entryOption.Locator("button.boutonSubUnit").First.ClickAsync(new() { Timeout = 5_000 });
        }

        // Report the uid by ENTRY ID where we have one. By name it cannot work for either failing
        // case: a modifier-renamed option is not called what the spec calls it, and two links to one
        // shared entry are both called the target's name.
        if (entryId is not null)
        {
            return await FindChildOptionUidAsync(page, parentSelectionUid, entryId, excludeInstanced: false);
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
    public static async Task SetChildEntryCountByNameAsync(
        IPage page, string parentSelectionUid, string entryName, int count, string? childSelectionUid = null)
    {
        await OpenOptionsPanelAsync(page, parentSelectionUid);

        // By uid where we have one, because an INSTANCED entry renders TWO rows under one name: the
        // "+" add row, which has no number input, and the instance's own `.subUnitHeaderRow` stepper.
        // The name filter picked the first and spent its 5s timeout on it
        // (selection/collective-instance-amount). `.stepper input.numInput` is itself
        // `input[type=number]`, so no extra branch is needed below.
        if (childSelectionUid is not null && await TagOptionRowAsync(page, childSelectionUid))
        {
            var row = page.Locator($"[data-nrui-option='{childSelectionUid}']");
            var rowInput = row.Locator("input[type='number']");
            await rowInput.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            await rowInput.First.FillAsync(count.ToString());
            await rowInput.First.PressAsync("Tab");
            return;
        }

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
            return;
        }

        // No `.unitRow` means this is a CHILD selection — not a hidden one. NR gives children no row
        // in the unit list; it renders them in the parent's options panel, where the control is the
        // count input, and deselecting one is decrementing it. That is exactly what the store-direct
        // engine does with `decrementAmount()`.
        //
        // The old message said "hidden or nested" and then refused both, which was wrong twice over:
        // nothing was hidden, and the child was fully editable one panel across. Measured on
        // selection/selection-nested-deselect, selection-collective-deselect and
        // collective-per-model-operations.
        //
        // The input holds the PER-PARENT amount, so a collective child at 2-per-model goes 6 -> 3 —
        // which is the per-model semantics collective-per-model-operations asserts.
        var parentUid = await FindParentUidAsync(page, selectionUid);
        if (parentUid is not null)
        {
            // OpenOptionsPanelAsync already walks up to the nearest ancestor that has a row, which is
            // what makes the two-level Squad -> Trooper -> Weapon case reachable.
            await OpenOptionsPanelAsync(page, parentUid);
            if (await TagOptionRowAsync(page, selectionUid))
            {
                var row = page.Locator($"[data-nrui-option='{selectionUid}']");

                var numInput = row.Locator("input[type='number']");
                if (await numInput.CountAsync() > 0)
                {
                    var current = int.TryParse(await numInput.First.InputValueAsync(), out var v) ? v : 1;
                    await numInput.First.FillAsync(Math.Max(0, current - 1).ToString());
                    await numInput.First.PressAsync("Tab");
                    return;
                }

                var checkbox = row.Locator("input[type='checkbox']");
                if (await checkbox.CountAsync() > 0)
                {
                    await checkbox.First.UncheckAsync(new() { Timeout = 5_000 });
                    return;
                }
            }
        }

        throw new NotSupportedException(
            $"NR UI: selection '{selectionUid}' has neither a .unitRow nor an options-panel row (hidden). " +
            "Hidden selections cannot be deselected via UI interaction.");
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

        // Close the dialog, and wait for it to BE closed — the input disappearing is the
        // observable end of it, and callers read the roster right afterwards.
        await page.Keyboard.PressAsync("Escape");
        await costInput.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
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

            // Wait for the editable field, rather than sleeping and then SNAPSHOTTING for it.
            // The 300ms here existed to prop up the `CountAsync() == 0` below — a snapshot, so a
            // field that had not rendered yet silently took the fallback branch. Both selectors
            // match the same element when the specific one exists, so one union locator with a real
            // wait replaces the sleep, the count check and the fallback together.
            var nameInput = page
                .Locator(".unitNameTitle .editableDiv[contenteditable='true'], "
                    + ".unitNameTitle [contenteditable='true']")
                .First;
            await nameInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            await nameInput.FillAsync(customName);
            await nameInput.PressAsync("Enter");

            // Wait for NR to COMMIT the rename to the store. Enter starts that; the 300ms assumed it
            // finished. The spec asserts on customName, so committing is the postcondition that
            // matters — and an assertion racing the commit reads the old name, which is a wrong
            // answer rather than an error.
            await WaitForSelectionCustomAsync(page, selectionUid, "customName", customName);
        }

        if (customNotes is not null)
        {
            // Open "Unit Options" submenu (may need to reopen after rename)
            await OpenUnitOptionsSubmenuAsync(page);

            // Click "Add Note" in the dropdown
            await page.GetByText("Add Note").First.ClickAsync(new() { Timeout = 3_000 });

            // Same shape as the rename above: wait for the field instead of sleeping and then
            // snapshotting for it.
            var noteField = page
                .Locator("pre.editableDiv.note[contenteditable='true'], pre[contenteditable='true'].note, "
                    + ".content [contenteditable='true']")
                .First;
            await noteField.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            await noteField.FillAsync(customNotes);

            // No Enter is pressed here, so the commit rides on the input event alone — which is
            // exactly why the old 100ms was the least defensible number in this file. Wait for the
            // store to carry the note.
            await WaitForSelectionCustomAsync(page, selectionUid, "note", customNotes);
        }
    }

    /// <summary>
    /// Waits until the selection <paramref name="uid"/> carries <paramref name="expected"/> in
    /// <paramref name="property"/> — the SAME property the state reader reports, so this asserts the
    /// commit rather than the keystroke that started it.
    /// </summary>
    /// <remarks>
    /// The property names are NR's, and they are not symmetric: a custom name lives on
    /// <c>customName</c> but a custom note lives on <c>note</c> (JsHelpers reads exactly these). An
    /// earlier version of this guessed `getCustomNotes()` from the C# parameter name and timed out
    /// on every customization spec — inventing an accessor rather than reading the one in use.
    /// </remarks>
    private static Task WaitForSelectionCustomAsync(
        IPage page, string uid, string property, string expected)
        => page.WaitForFunctionAsync(
            """
            ([uid, property, expected]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army ?? window.__bsspec?.army;
                if (!army) { return false; }
                let found = null;
                const walk = node => {
                    for (const s of (node.getSelections?.() || node.getChildren?.() || [])) {
                        if (s.uid === uid) { found = s; return; }
                        walk(s);
                        if (found) { return; }
                    }
                };
                for (const f of (army.getForces?.() || [])) {
                    walk(f);
                    if (found) { break; }
                }
                if (!found) { return false; }
                return (found[property] ?? null) === expected;
            }
            """,
            new[] { uid, property, expected },
            new() { Timeout = 10_000 });

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

            // Wait for it to be gone rather than for 200ms. Tolerated: if Escape does not close it,
            // the button click below has its own actionability check and reports the overlay far
            // better than a bare timeout here would.
            try
            {
                await existingSubmenu.First.WaitForAsync(
                    new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
            }
            catch (TimeoutException)
            {
                // Still open — let the click speak for itself.
            }
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

            // Wait for the field instead of sleeping and then snapshotting for it with CountAsync;
            // one union locator covers both shapes the fallback was reaching for.
            var nameInput = page
                .Locator(".forceOptions [contenteditable='true'], "
                    + ".forceSection [contenteditable='true'], .titreForce [contenteditable='true']")
                .First;
            await nameInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            await nameInput.FillAsync(customName);
            await nameInput.PressAsync("Enter");

            // Wait for NR to commit the rename to the force, which is what the spec asserts.
            await page.WaitForFunctionAsync(
                """
                ([uid, expected]) => {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army ?? window.__bsspec?.army;
                    const f = (army?.getForces?.() || []).find(x => x.uid === uid);
                    if (!f) { return false; }
                    return (f.getCustomName?.() ?? f.customName) === expected;
                }
                """,
                new[] { forceId, customName },
                new() { Timeout = 10_000 });
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

            // Wait for the row to actually BE editing, which is the postcondition this method
            // exists to establish — and, three lines up, exactly the state it tests for. A flat
            // 300ms asserted nothing: everything downstream reads the panel with a SNAPSHOT
            // (`TagOptionRowAsync` does a bare `querySelector("label[for=…]")`), so a panel that had
            // not rendered yet was indistinguishable from a genuinely hidden entry, and got reported
            // as one. That is a wrong ANSWER, not a slow one, which is why this site was singled out
            // as correctness-critical rather than merely 42 seconds of lane time.
            await page.WaitForFunctionAsync(
                """
                (uid) => document.querySelector(`[data-nrui-uid='${uid}']`)
                    ?.classList.contains('editing') === true
                """,
                selectionUid,
                new() { Timeout = 10_000 });
        }
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

                    // CATALOGUE entry rows only. A force's `.childForces .unitList` renders the
                    // available CHILD FORCE TYPES using the same `.unit-wrap` markup, and it sits
                    // ABOVE the entry list inside the same `.bookForce`. Swept in, a force row became
                    // `unmatchedWraps[0]`, the positional second pass below stamped it with a real
                    // entry's id, and selectEntry then clicked ITS "+" — adding a duplicate child
                    // force instead of the entry. Measured on scope/scope-include-child-forces and
                    // scope-include-child-forces-nested ("expected 1 child forces but got 2"), and
                    // only when a modifier renames the entry, because an exact name match otherwise
                    // wins before the positional pass is reached.
                    for (const w of document.querySelectorAll('.childForces .unitList .unit-wrap')) {
                        // Marked as well as filtered, so the NAME fallback in SelectEntryByNameAsync
                        // cannot land on one either — a force type and an entry may share a name.
                        w.setAttribute('data-nrui-force-row', '1');
                    }
                    const unitWraps = [...document.querySelectorAll('.unitList .unit-wrap')]
                        .filter(w => !w.closest('.childForces'));
                    if (unitWraps.length === 0) return;

                    // Use only playable books (active force's catalogues) to avoid
                    // stale/unrelated books from prior tests in a shared browser session.
                    const playableBooks = (sys.books?.array || []).filter(b => b.playable);

                    // Build name→id and ordered id list from Pinia (original names).
                    const nameToId = new Map();
                    const orderedIds = [];
                    // Everything the catalogue panel can render, not just `selectionEntries`.
                    //
                    // An entryLink renders as its own row under its own name, and a shared entry can
                    // be rendered too — but only `selectionEntries` used to be indexed here. Two
                    // consequences, both bad: the link's row never got a `data-spec-entry-id`, so
                    // addressing it by id found nothing; and the second pass below then handed that
                    // row a LEFTOVER id positionally, labelling "Alpha Squad" as the shared entry it
                    // links to. Measured on constraint/constraint-shared-flag and four others, where
                    // it surfaced as "entry 'link-alpha' is not visible in the catalogue panel
                    // (hidden entry)" for an entry that is neither hidden nor absent.
                    const indexEntries = (entries) => {
                        for (const entry of (entries || [])) {
                            if (!entry?.id) continue;
                            const name = entry.name;
                            if (name && !nameToId.has(name)) nameToId.set(name, entry.id);
                            orderedIds.push(entry.id);
                        }
                    };
                    for (const book of playableBooks) {
                        const bd = await sys.getBook?.(book.id);
                        if (!bd?.catalogue) continue;
                        indexEntries(bd.catalogue.selectionEntries);
                        indexEntries(bd.catalogue.entryLinks);
                        // NOT sharedSelectionEntries. A shared entry is not rendered as its own row —
                        // the LINKS to it are. Indexing its name puts the target's name in the map,
                        // and since NR labels a link with its TARGET's name, that name then matches
                        // the first link's row and steals it: two links to one shared entry render as
                        // two rows both reading "Squad", and the first was being tagged with the
                        // shared entry's id instead of the link's. Leaving shared entries out lets
                        // same-named link rows fall through to the ordered second pass below, which
                        // assigns them in declaration order — the only thing that can tell them apart.
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
                // Report the force's settling state alongside the map, so a caller polling for
                // auto-added selections can tell "the force is not rendered yet" and "its selections
                // exist but their ids have not populated" apart from "there are genuinely none".
                // Without that distinction the only safe policy is to burn a fixed timeout every
                // time — which is what the caller used to do, for 8s on every addForce.
                if (!army) return JSON.stringify({ state: 'no-army', map: {} });
                const force = (army.getForces?.() || []).find(f => f.uid === forceUid);
                if (!force) return JSON.stringify({ state: 'no-force', map: {} });
                const out = {};
                let raw = 0, unresolved = 0;
                for (const s of (force.getSelections?.() || [])) {
                    raw++;
                    // Use || (not ??) so empty strings also fall through to the next option.
                    const entryId = s.id || s.entryId || s.getEntryId?.();
                    if (entryId && s.uid) {
                        out[entryId] = s.uid;
                    } else {
                        unresolved++;
                    }
                }
                return JSON.stringify({
                    state: unresolved > 0 ? 'unresolved' : (raw > 0 ? 'resolved' : 'empty'),
                    map: out,
                });
            }
            """, new object[] { forceUid });

        return ParseForceSelections(json).Map;
    }

    /// <summary>
    /// How settled a force's selections are, for callers that must wait for NR to finish adding
    /// them. <c>NoForce</c>/<c>Unresolved</c> mean "not done yet"; <c>Empty</c> means this force has
    /// no auto-added selections and never will.
    /// </summary>
    internal enum ForceSelectionState
    {
        NoForce,
        Unresolved,
        Empty,
        Resolved,
    }

    /// <summary>As <see cref="GetForceSelectionsAsync"/>, but also reporting how settled NR is.</summary>
    internal static async Task<(ForceSelectionState State, Dictionary<string, string> Map)>
        GetForceSelectionsWithStateAsync(IPage page, string forceUid)
    {
        var json = await page.EvaluateAsync<string>("""
            ([forceUid]) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army
                    ?? window.__bsspec?.army;
                if (!army) return JSON.stringify({ state: 'no-army', map: {} });
                const force = (army.getForces?.() || []).find(f => f.uid === forceUid);
                if (!force) return JSON.stringify({ state: 'no-force', map: {} });
                const out = {};
                let raw = 0, unresolved = 0;
                for (const s of (force.getSelections?.() || [])) {
                    raw++;
                    const entryId = s.id || s.entryId || s.getEntryId?.();
                    if (entryId && s.uid) { out[entryId] = s.uid; } else { unresolved++; }
                }
                return JSON.stringify({
                    state: unresolved > 0 ? 'unresolved' : (raw > 0 ? 'resolved' : 'empty'),
                    map: out,
                });
            }
            """, new object[] { forceUid });

        return ParseForceSelections(json);
    }

    private static (ForceSelectionState State, Dictionary<string, string> Map) ParseForceSelections(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return (ForceSelectionState.NoForce, []);
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
        var map = doc.RootElement.TryGetProperty("map", out var m)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(m.GetRawText()) ?? []
            : [];

        return (state switch
        {
            "resolved" => ForceSelectionState.Resolved,
            "unresolved" => ForceSelectionState.Unresolved,
            "empty" => ForceSelectionState.Empty,
            _ => ForceSelectionState.NoForce,
        }, map);
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

            // Wait for the panel to be closed, which is the postcondition and — per this method's
            // own summary — what makes the left-panel elements reachable again. Callers index
            // `.forceOptions` positionally right afterwards, so acting while the panel is still up
            // reads the wrong list.
            await page.Locator(".unitRow.editing").First.WaitForAsync(
                new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        }
    }
}
