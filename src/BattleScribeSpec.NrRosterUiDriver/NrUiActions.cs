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
    public static async Task<string?> AddForceByNameAsync(IPage page, string forceName)
    {
        // Capture existing force uids before the action
        var before = await GetAllForceUidsAsync(page);

        // Click "Add Force" to open the force-type picker
        await page.Locator("button.bouton").Filter(new() { HasText = "Add Force" }).ClickAsync();

        // Wait for the forces panel to appear
        var forcesPanel = page.Locator(".forces");
        await forcesPanel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Find the force row with matching name and click its addButton (+)
        // Row structure: div.unit-wrap.force > div.units > div.unit-top-row > span.name
        //                                    > div.right > div.icons > div.addButton
        var forceRow = forcesPanel.Locator(".unit-wrap.force").Filter(new() { Has = page.Locator(".name", new() { HasTextString = forceName }) });
        await forceRow.Locator(".addButton").ClickAsync(new() { Timeout = 10_000 });

        return await WaitForNewForceUidAsync(page, before);
    }

    /// <summary>
    /// Adds a child force under <paramref name="parentForceId"/> by name.
    /// </summary>
    public static async Task<string?> AddChildForceByNameAsync(IPage page, string parentForceId, string forceName)
    {
        var before = await GetAllForceUidsAsync(page);

        // Scroll to / expand the parent force, then click its "add sub-force" button
        await ClickAddChildForceButtonAsync(page, parentForceId);

        await page.GetByRole(AriaRole.Button, new() { Name = forceName, Exact = true })
                  .First.ClickAsync(new() { Timeout = 10_000 });

        return await WaitForNewForceUidAsync(page, before);
    }

    /// <summary>
    /// Removes a force by clicking its delete/remove button.
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, string forceUid)
    {
        var forceEl = await FindForceElementAsync(page, forceUid);
        var deleteBtn = forceEl.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("delete|remove|×|✕", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await deleteBtn.First.ClickAsync();

        // Confirm if a confirmation dialog appears
        await MaybeConfirmDeletionAsync(page);
    }

    // ===== Selection operations =====

    /// <summary>
    /// Selects (adds) an entry in a force by clicking its "+" button in the catalog.
    /// Returns the uid of the newly created selection.
    /// </summary>
    public static async Task<string?> SelectEntryByNameAsync(IPage page, string forceUid, string entryName)
    {
        var before = await GetAllSelectionUidsAsync(page);

        // The catalog is the .unitList panel visible in the force view.
        // Each entry is div.unit-wrap.newui (no .force class) with a .addButton inside.
        // We use forceUid to ensure we're in the right force context (sync __bsspec.army if needed).
        _ = forceUid;
        var entryRow = page.Locator(".unitList .unit-wrap").Filter(new() { HasText = entryName });
        await entryRow.Locator(".addButton").ClickAsync(new() { Timeout = 10_000 });

        return await WaitForNewSelectionUidAsync(page, before);
    }

    /// <summary>
    /// Selects a child entry under an existing selection by clicking its "+" in the catalog.
    /// Returns the uid of the newly created child selection.
    /// </summary>
    public static async Task<string?> SelectChildEntryByNameAsync(IPage page, string parentSelectionUid, string entryName)
    {
        var before = await GetAllSelectionUidsAsync(page);

        var selEl = await FindSelectionElementAsync(page, parentSelectionUid);
        var entryRow = FindCatalogRowByName(selEl, entryName);
        var addBtn = entryRow.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex(@"\+|add", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await addBtn.First.ClickAsync();

        return await WaitForNewSelectionUidAsync(page, before);
    }

    /// <summary>
    /// Removes a selection by clicking its delete/× button.
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, string selectionUid)
    {
        var selEl = await FindSelectionElementAsync(page, selectionUid);
        var deleteBtn = selEl.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex(@"×|✕|delete|remove", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await deleteBtn.First.ClickAsync();
        await MaybeConfirmDeletionAsync(page);
    }

    /// <summary>
    /// Sets the quantity of a selection by filling its number input.
    /// </summary>
    public static async Task SetSelectionCountAsync(IPage page, string selectionUid, int count)
    {
        var selEl = await FindSelectionElementAsync(page, selectionUid);
        var numInput = selEl.GetByRole(AriaRole.Spinbutton)
                           .Or(selEl.Locator("input[type='number']"));
        await numInput.First.FillAsync(count.ToString());
        // Commit the value by pressing Tab or Enter
        await numInput.First.PressAsync("Enter");
    }

    /// <summary>
    /// Duplicates a selection and returns the uid of the new copy.
    /// </summary>
    public static async Task<string?> DuplicateSelectionAsync(IPage page, string selectionUid)
    {
        var before = await GetAllSelectionUidsAsync(page);

        var selEl = await FindSelectionElementAsync(page, selectionUid);
        var dupBtn = selEl.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("duplicate|copy", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await dupBtn.First.ClickAsync();

        return await WaitForNewSelectionUidAsync(page, before);
    }

    /// <summary>
    /// Duplicates a force and returns the uid of the new copy.
    /// </summary>
    public static async Task<string?> DuplicateForceAsync(IPage page, string forceUid)
    {
        var before = await GetAllForceUidsAsync(page);

        var forceEl = await FindForceElementAsync(page, forceUid);
        var dupBtn = forceEl.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("duplicate|copy", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await dupBtn.First.ClickAsync();

        return await WaitForNewForceUidAsync(page, before);
    }

    // ===== Roster-level operations =====

    /// <summary>
    /// Sets a cost limit for a cost type. Opens roster settings if necessary.
    /// </summary>
    public static async Task SetCostLimitAsync(IPage page, string costTypeId, decimal value)
    {
        // TODO: locate the cost limit input for the given cost type.
        // This may require opening a roster settings panel first.
        // Use JS to find the input associated with costTypeId, then interact via Playwright.
        var selector = await page.EvaluateAsync<string?>("""
            (costTypeId) => {
                // Try to find cost limit input by inspecting visible inputs near cost type labels
                const army = window.__bsspec?.army;
                if (!army) return null;
                const maxCosts = army.getMaxCosts?.() || [];
                const idx = maxCosts.findIndex(c => c.typeId === costTypeId || c.id === costTypeId);
                if (idx < 0) return null;
                // Return a data-attribute selector hint; the actual DOM binding depends on NR's template
                return `[data-cost-id="${costTypeId}"] input, [data-type-id="${costTypeId}"] input`;
            }
            """, costTypeId);

        if (selector is not null)
        {
            var input = page.Locator(selector).First;
            if (await input.CountAsync() > 0)
            {
                await input.FillAsync(value.ToString("G"));
                await input.PressAsync("Enter");
                return;
            }
        }

        // Fallback: set via JS until UI binding is confirmed
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
    /// Sets a custom name and/or notes on a force, category, or selection via the NR UI.
    /// </summary>
    public static async Task SetCustomizationAsync(
        IPage page,
        string forceId,
        string? selectionId,
        string? categoryEntryId,
        string? customName,
        string? customNotes)
    {
        _ = categoryEntryId;
        ILocator targetEl;
        if (selectionId is not null)
        {
            targetEl = await FindSelectionElementAsync(page, selectionId);
        }
        else
        {
            targetEl = await FindForceElementAsync(page, forceId);
        }

        if (customName is not null)
        {
            // Look for a "rename" / edit-name button or directly editable name field
            var nameInput = targetEl.GetByRole(AriaRole.Textbox, new() { NameRegex = new System.Text.RegularExpressions.Regex("name|custom", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await nameInput.CountAsync() > 0)
            {
                await nameInput.First.FillAsync(customName);
                await nameInput.First.PressAsync("Enter");
            }
            else
            {
                // Try clicking a rename/pencil button first
                var renameBtn = targetEl.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("rename|edit|name", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
                if (await renameBtn.CountAsync() > 0)
                {
                    await renameBtn.First.ClickAsync();
                    await page.GetByRole(AriaRole.Textbox).First.FillAsync(customName);
                    await page.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = false }).First.ClickAsync();
                }
            }
        }

        if (customNotes is not null)
        {
            var notesInput = targetEl.GetByRole(AriaRole.Textbox, new() { NameRegex = new System.Text.RegularExpressions.Regex("note|comment", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            if (await notesInput.CountAsync() > 0)
            {
                await notesInput.First.FillAsync(customNotes);
                await notesInput.First.PressAsync("Enter");
            }
        }
    }

    // ===== Internal: element finders =====

    /// <summary>
    /// Returns a Playwright locator scoped to the DOM element that represents a force
    /// with the given NR uid. Uses a JS-assisted lookup to find the DOM node.
    /// </summary>
    private static async Task<ILocator> FindForceElementAsync(IPage page, string forceUid)
    {
        // Ask JS to tag the element with a temporary data attribute so we can
        // use a CSS selector to find it. This is the hybrid approach: JS locates,
        // Playwright interacts.
        await page.EvaluateAsync("""
            (forceUid) => {
                const army = window.__bsspec?.army;
                if (!army) return;
                const forces = army.getForces?.() || [];
                const force = forces.find(f => f.uid === forceUid);
                if (!force) return;
                // Mark any rendered component that exposes this uid as data
                document.querySelectorAll('[data-uid]').forEach(el => {
                    if (el.dataset.uid === forceUid) el.dataset.nruiTarget = 'force-' + forceUid;
                });
            }
            """, forceUid);

        var tagged = page.Locator($"[data-nrui-target='force-{forceUid}']");
        if (await tagged.CountAsync() > 0)
        {
            return tagged.First;
        }

        // Fallback: find by uid text representation or a force section heading
        // TODO: replace with verified selector from probe
        return page.Locator($"[data-uid='{forceUid}']").First;
    }

    /// <summary>
    /// Returns a Playwright locator scoped to the DOM element representing a selection.
    /// </summary>
    private static async Task<ILocator> FindSelectionElementAsync(IPage page, string selectionUid)
    {
        await page.EvaluateAsync("""
            (selectionUid) => {
                const army = window.__bsspec?.army;
                if (!army) return;
                const allSel = getAllSelections(army);
                const sel = allSel.find(s => s.uid === selectionUid);
                if (!sel) return;
                document.querySelectorAll('[data-uid]').forEach(el => {
                    if (el.dataset.uid === selectionUid) el.dataset.nruiTarget = 'sel-' + selectionUid;
                });

                function getAllSelections(node) {
                    const results = [];
                    for (const f of (node.getForces?.() || [])) {
                        results.push(...getSelectionsInForce(f));
                    }
                    return results;
                }
                function getSelectionsInForce(force) {
                    const results = [];
                    for (const s of (force.getSelections?.() || force.getChildren?.() || [])) {
                        results.push(s, ...getSelectionsInForce(s));
                    }
                    return results;
                }
            }
            """, selectionUid);

        var tagged = page.Locator($"[data-nrui-target='sel-{selectionUid}']");
        if (await tagged.CountAsync() > 0)
        {
            return tagged.First;
        }

        return page.Locator($"[data-uid='{selectionUid}']").First;
    }

    /// <summary>
    /// Returns a locator for a catalog row (entry option) by its visible name,
    /// scoped within a parent element.
    /// </summary>
    private static ILocator FindCatalogRowByName(ILocator parent, string entryName)
    {
        // Look for a row or list item containing the entry name text.
        // TODO: verify exact container structure from probe.
        return parent.GetByText(entryName, new() { Exact = true })
                     .Or(parent.Locator("li, tr, [role='listitem'], [role='row']")
                         .Filter(new() { HasText = entryName }))
                     .First;
    }

    // ===== Internal: "Add Child Force" button helper =====

    private static async Task ClickAddChildForceButtonAsync(IPage page, string parentForceUid)
    {
        var forceEl = await FindForceElementAsync(page, parentForceUid);
        var addBtn = forceEl.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex(@"add|sub.?force|detachment", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await addBtn.First.ClickAsync();
    }

    // ===== Internal: uid diffing =====

    private static async Task<HashSet<string>> GetAllForceUidsAsync(IPage page)
    {
        var uids = await page.EvaluateAsync<string[]>("""
            () => {
                const army = window.__bsspec?.army;
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
                const army = window.__bsspec?.army;
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
}
