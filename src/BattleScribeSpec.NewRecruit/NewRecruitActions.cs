using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Translates IRosterEngine action calls to NR roster tree operations.
///
/// All actions use ID-based addressing:
///   - Force/selection instances are identified by NR's internal uid
///   - Entry definitions use BattleScribe data model IDs
///
/// JS helper functions (getForces, getSelections, getForceByUid,
/// getSelectionByUid, findSelectorById) are registered as page init scripts
/// via NewRecruitBrowser.RegisterHelpersOnPageAsync() and are automatically
/// available as window globals after every navigation.
/// </summary>
public static class NewRecruitActions
{
    private const string ErrorPrefix = "ERROR:";

    /// <summary>
    /// For create actions: returns uid on success, throws on error.
    /// JS returns uid string, null (success without uid), or "ERROR:message".
    /// </summary>
    internal static string? HandleCreateResult(string? result)
    {
        if (result?.StartsWith(ErrorPrefix, StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(result[ErrorPrefix.Length..]);
        }

        return result;
    }

    /// <summary>
    /// Add a force to the roster by force entry ID.
    /// Returns the uid of the created force.
    /// </summary>
    public static async Task<string?> AddForceByIdAsync(IPage page, string forceEntryId, string catalogueId)
    {
        var result = await page.EvaluateAsync<string?>("""
            ({forceEntryId, catalogueId}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'ERROR:No spec state — was Setup called?';
                    const army = spec.army;
                    const books = spec.books || [spec.book];
                    const catIds = spec.bookCatalogueIds || [];
                    const book = catalogueId
                        ? (books[catIds.indexOf(catalogueId)] || books[0])
                        : books[0];
                    if (!army || !book) return 'ERROR:No army or book';

                    const beforeUids = new Set(
                        (army.getForces?.() || []).map(f => f.uid));

                    army.insertForce(book, forceEntryId);

                    for (const f of (army.getForces?.() || [])) {
                        if (f.uid && !beforeUids.has(f.uid)) return f.uid;
                    }
                    return null;
                } catch(e) {
                    return 'ERROR:AddForce error: ' + e.message;
                }
            }
            """, new { forceEntryId, catalogueId });
        return HandleCreateResult(result);
    }

    /// <summary>
    /// Collect auto-selected root selections for a force after addForce.
    /// Returns a map of entryId → selection uid, or null if no selections.
    /// </summary>
    public static async Task<Dictionary<string, string>?> GetForceAutoSelectionsAsync(IPage page, string forceUid)
    {
        var json = await page.EvaluateAsync<string?>("""
            (forceUid) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'ERROR:No current roster';
                    const force = getForceByUid(army, forceUid);
                    if (!force) return `ERROR:Force not found with uid '${forceUid}'`;
                    const sels = getSelections(force);
                    if (!sels || sels.length === 0) return null;
                    const map = {};
                    for (const s of sels) {
                        const entryId = s.getId();
                        const uid = s.uid;
                        if (entryId && uid) map[entryId] = uid;
                    }
                    return Object.keys(map).length > 0 ? JSON.stringify(map) : null;
                } catch(e) {
                    return 'ERROR:GetForceAutoSelections error: ' + e.message;
                }
            }
            """, forceUid);
        if (json is null)
        {
            return null;
        }

        HandleCreateResult(json);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }

    /// <summary>
    /// Add a child force under an existing force by child force entry ID.
    /// Returns the uid of the created child force.
    /// </summary>
    public static async Task<string?> AddChildForceByIdAsync(IPage page, string parentForceUid, string childForceEntryId, string catalogueId)
    {
        var result = await page.EvaluateAsync<string?>("""
            ({parentForceUid, childForceEntryId, catalogueId}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'ERROR:No spec state — was Setup called?';
                    const army = spec.army;
                    const books = spec.books || [spec.book];
                    const catIds = spec.bookCatalogueIds || [];
                    const book = catalogueId
                        ? (books[catIds.indexOf(catalogueId)] || books[0])
                        : books[0];
                    if (!army || !book) return 'ERROR:No army or book';

                    const parentForce = getForceByUid(army, parentForceUid);
                    if (!parentForce) return `ERROR:Parent force not found with uid '${parentForceUid}'`;

                    const beforeUids = new Set(
                        (army.getForces?.() || []).map(f => f.uid));

                    if (typeof parentForce.insertForce === 'function') {
                        parentForce.insertForce(book, childForceEntryId);
                    } else {
                        return 'ERROR:insertForce() not available on force object';
                    }

                    for (const f of (army.getForces?.() || [])) {
                        if (f.uid && !beforeUids.has(f.uid)) return f.uid;
                    }
                    return null;
                } catch(e) {
                    return 'ERROR:AddChildForce error: ' + e.message;
                }
            }
            """, new { parentForceUid, childForceEntryId, catalogueId });
        return HandleCreateResult(result);
    }

    /// <summary>
    /// Remove a force from the roster by its uid.
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, string forceUid)
    {
        var error = await page.EvaluateAsync<string?>("""
            (forceUid) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `Force not found with uid '${forceUid}'`;

                    if (typeof force.delete !== 'function')
                        return `Force '${forceUid}' has no delete method (unexpected node type)`;
                    force.delete();
                    return null;
                } catch(e) {
                    return 'RemoveForce error: ' + e.message;
                }
            }
            """, forceUid);
        if (error != null)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// Select an entry in the specified force by entry ID.
    /// Returns the uid of the created selection.
    /// </summary>
    public static async Task<string?> SelectEntryByIdAsync(IPage page, string forceUid, string entryId)
    {
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

                    // Selectors have addInstance; instances have incrementAmount/setAmount.
                    // findSelectorById returns a selector node — it must have addInstance.
                    if (typeof selector.addInstance !== 'function')
                        return `ERROR:Selector for '${entryId}' has no addInstance (unexpected node type)`;

                    selector.addInstance();

                    // When an entry has categoryLinks, NR creates the instance under the
                    // correct category selector, not under the one findSelectorById found
                    // (which may be in the (Illegal Units) fallback category).
                    // Use before/after uid diff to find the new instance reliably.
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
        return HandleCreateResult(result);
    }

    /// <summary>
    /// Select a child entry under an existing selection.
    /// Returns the uid of the created/activated child selection.
    /// </summary>
    public static async Task<string?> SelectChildEntryByIdAsync(IPage page, string forceUid, string selectionUid, string childEntryId)
    {
        var result = await page.EvaluateAsync<string?>("""
            ({forceUid, selectionUid, childEntryId}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'ERROR:No current roster';

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `ERROR:Force not found with uid '${forceUid}'`;

                    const sel = getSelectionByUid(force, selectionUid);
                    if (!sel) return `ERROR:Selection not found with uid '${selectionUid}'`;

                    // Try to find an existing pre-created instance in getSelections().
                    // For entryLinks, getId() returns the target ID, not the link ID.
                    // Also check source.id and selector.ids for link ID matching.
                    // sel is an instance node — getSelections always exists.
                    const children = sel.getSelections();
                    const child = children.find(c =>
                        c.getId() === childEntryId
                        || c.source?.id === childEntryId
                        || c.selector?.ids?.includes?.(childEntryId));

                    if (child) {
                        // Found existing instance — activate via incrementAmount.
                        // Instance nodes always have incrementAmount (not addInstance).
                        if (typeof child.incrementAmount !== 'function')
                            return `ERROR:Child instance '${childEntryId}' has no incrementAmount (unexpected node type)`;
                        child.incrementAmount();
                        child.autocheck();
                        return child.uid;
                    }

                    // Entry not pre-created as instance. Search the selector tree
                    // and use addInstance() to create the first instance.
                    // NR's dual-tree alternates: instance→selectors→instances→selectors.
                    // Selector nodes DON'T have 'selectors'; only instances do.
                    // We must traverse selector→instances→selectors to find nested entries.
                    function findSelectorDeep(selectors, id) {
                        for (const s of selectors) {
                            if (s.id === id || s.ids?.includes(id)) return s;
                            // Recurse through each instance's child selectors
                            for (const inst of (s.instances || [])) {
                                const found = findSelectorDeep(inst.selectors || [], id);
                                if (found) return found;
                            }
                        }
                        return null;
                    }
                    // Instance nodes own the 'selectors' array (always present).
                    // sel.selector is a selector node — it never has 'selectors'.
                    const targetSelector = findSelectorDeep(sel.selectors || [], childEntryId);
                    if (!targetSelector) {
                        const selectorIds = (sel.selectors || []).map(s => s.id).join(', ');
                        const childCount = children.length;
                        const childIds = children.map(c => c.getId?.()).join(', ');
                        return `ERROR:Child entry '${childEntryId}' not found under selection (selectors: [${selectorIds}], children: ${childCount} [${childIds}])`;
                    }

                    // Selector nodes have addInstance; instance nodes do not.
                    if (typeof targetSelector.addInstance !== 'function')
                        return `ERROR:Selector for '${childEntryId}' has no addInstance (unexpected node type)`;

                    targetSelector.addInstance();
                    // Find the new child instance via getSelections diff.
                    const afterChildren = sel.getSelections();
                    const newChild = afterChildren.find(c =>
                        (c.getId() === childEntryId
                        || c.source?.id === childEntryId
                        || c.selector?.ids?.includes?.(childEntryId))
                        && c.getAmount() > 0);
                    if (!newChild)
                        return `ERROR:addInstance on '${childEntryId}' did not produce a child selection`;
                    newChild.autocheck();
                    return newChild.uid;
                } catch(e) {
                    return 'ERROR:SelectChildEntry error: ' + e.message;
                }
            }
            """, new { forceUid, selectionUid, childEntryId });
        return HandleCreateResult(result);
    }

    /// <summary>
    /// Deselect (remove) a selection by uid.
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, string forceUid, string selectionUid)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceUid, selectionUid}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `Force not found with uid '${forceUid}'`;

                    const sel = getSelectionByUid(force, selectionUid);
                    if (!sel) return `Selection not found with uid '${selectionUid}'`;

                    // Instance nodes always have delete(). Using setAmount with 1 arg corrupts state.
                    if (typeof sel.delete !== 'function')
                        return `Selection '${selectionUid}' has no delete method (unexpected node type)`;
                    sel.delete();
                    return null;
                } catch(e) {
                    return 'DeselectSelection error: ' + e.message;
                }
            }
            """, new { forceUid, selectionUid });
        if (error != null)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// Set the number of instances for a selection.
    /// Uses NR's native setAmount({}, count) — matches the UI spinbutton behavior.
    /// </summary>
    public static async Task SetSelectionCountAsync(IPage page, string forceUid, string selectionUid, int count)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceUid, selectionUid, count}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `Force not found with uid '${forceUid}'`;

                    const sel = getSelectionByUid(force, selectionUid);
                    if (!sel) return `Selection not found with uid '${selectionUid}'`;

                    if (typeof sel.getAmount !== 'function')
                        return `Selection '${selectionUid}' has no getAmount (unexpected node type)`;
                    if (typeof sel.setAmount !== 'function')
                        return `Selection '${selectionUid}' has no setAmount (unexpected node type)`;

                    const current = sel.getAmount();
                    if (current === count) return null;

                    sel.setAmount({}, count);
                    return null;
                } catch(e) {
                    return 'SetSelectionCount error: ' + e.message;
                }
            }
            """, new { forceUid, selectionUid, count });
        if (error != null)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// Duplicate a selection within a force using dupe().
    /// Returns the uid of the duplicated selection.
    /// </summary>
    public static async Task<string?> DuplicateSelectionAsync(IPage page, string forceUid, string selectionUid)
    {
        var result = await page.EvaluateAsync<string?>("""
            async ({forceUid, selectionUid}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'ERROR:No current roster';

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `ERROR:Force not found with uid '${forceUid}'`;

                    const before = new Set(
                        getSelections(force).map(s => s.uid));

                    const sel = getSelectionByUid(force, selectionUid);
                    if (!sel) return `ERROR:Selection not found with uid '${selectionUid}'`;

                    if (typeof sel.dupe !== 'function')
                        return 'ERROR:dupe() method not available on selection';

                    await sel.dupe();

                    // Find the duplicated selection
                    const after = getSelections(force);
                    for (const s of after) {
                        if (s.uid && !before.has(s.uid)) {
                            return s.uid;
                        }
                    }

                    return null;
                } catch(e) {
                    return 'ERROR:DuplicateSelection error: ' + e.message;
                }
            }
            """, new { forceUid, selectionUid });
        return HandleCreateResult(result);
    }

    /// <summary>
    /// Duplicate a force using dupe().
    /// Returns the uid of the duplicated force.
    /// </summary>
    public static async Task<string?> DuplicateForceAsync(IPage page, string forceUid)
    {
        var result = await page.EvaluateAsync<string?>("""
            async ({forceUid}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'ERROR:No current roster';

                    const before = new Set(
                        (army.getForces?.() || []).map(f => f.uid));

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `ERROR:Force not found with uid '${forceUid}'`;

                    if (typeof force.dupe !== 'function')
                        return 'ERROR:dupe() method not available on force';

                    await force.dupe();

                    // Find the duplicated force by comparing before/after uid sets
                    for (const f of (army.getForces?.() || [])) {
                        if (f.uid && !before.has(f.uid)) {
                            return f.uid;
                        }
                    }

                    return null;
                } catch(e) {
                    return 'ERROR:DuplicateForce error: ' + e.message;
                }
            }
            """, new { forceUid });
        return HandleCreateResult(result);
    }

    /// <summary>
    /// Set cost limit for a cost type using army.setMaxCosts().
    /// </summary>
    public static async Task SetCostLimitAsync(IPage page, string costTypeId, double value)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({costTypeId, value}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const maxCosts = army.getMaxCosts?.();
                    if (maxCosts && Array.isArray(maxCosts)) {
                        const cost = maxCosts.find(c => c.typeId === costTypeId || c.name === costTypeId);
                        if (cost) {
                            cost.value = value;
                            army.setMaxCosts(maxCosts);
                            return null;
                        }
                    }
                    return `Cost type '${costTypeId}' not found in roster maxCosts`;
                } catch(e) {
                    return 'SetCostLimit error: ' + e.message;
                }
            }
            """, new { costTypeId, value });
        if (error != null)
        {
            throw new InvalidOperationException(error);
        }
    }

    public static async Task SetCustomizationAsync(IPage page, string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceId, selectionId, categoryEntryId, customName, customNotes}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const forces = army.getForces?.() || [];
                    const force = forces.find(f => f.uid === forceId);
                    if (!force) return `Force '${forceId}' not found`;

                    if (selectionId) {
                        // Target a selection (or category on selection)
                        function findSel(parent) {
                            for (const s of (parent.getSelections?.() || [])) {
                                if (s.uid === selectionId) return s;
                                const found = findSel(s);
                                if (found) return found;
                            }
                            return null;
                        }
                        const sel = findSel(force);
                        if (!sel) return `Selection '${selectionId}' not found in force '${forceId}'`;

                        if (categoryEntryId) {
                            // NR doesn't support category-level customNotes — skip silently
                            return null;
                        }
                        if (customName !== null && customName !== undefined) sel.customName = customName;
                        if (customNotes !== null && customNotes !== undefined) sel.note = customNotes;
                    } else {
                        if (categoryEntryId) {
                            // NR doesn't support category-level customNotes — skip silently
                            return null;
                        }
                        if (customName !== null && customName !== undefined) force.customName = customName;
                        if (customNotes !== null && customNotes !== undefined) force.note = customNotes;
                    }
                    return null;
                } catch(e) {
                    return 'SetCustomization error: ' + e.message;
                }
            }
            """, new { forceId, selectionId, categoryEntryId, customName, customNotes });
        if (error != null)
        {
            throw new InvalidOperationException(error);
        }
    }
}
