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
        if (result?.StartsWith(ErrorPrefix) == true)
            throw new InvalidOperationException(result[ErrorPrefix.Length..]);
        return result;
    }

    /// <summary>
    /// Add a force to the roster by force entry ID.
    /// Returns the uid of the created force.
    /// </summary>
    public static async Task<string?> AddForceByIdAsync(IPage page, string forceEntryId, int catalogueIndex = 0)
    {
        var result = await page.EvaluateAsync<string?>("""
            ({forceEntryId, catalogueIndex}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'ERROR:No spec state — was Setup called?';
                    const army = spec.army;
                    const books = spec.books || [spec.book];
                    const book = books[catalogueIndex] || books[0];
                    if (!army || !book) return 'ERROR:No army or book';

                    const beforeUids = new Set(
                        (army.getForces?.() || []).map(f => f?.uid || ''));

                    army.insertForce(book, forceEntryId);

                    for (const f of (army.getForces?.() || [])) {
                        const raw = f;
                        if (raw?.uid && !beforeUids.has(raw.uid)) return raw.uid;
                    }
                    return null;
                } catch(e) {
                    return 'ERROR:AddForce error: ' + e.message;
                }
            }
            """, new { forceEntryId, catalogueIndex });
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
                    if (!army) return null;
                    const force = getForceByUid(army, forceUid);
                    if (!force) return null;
                    const sels = getSelections(force);
                    if (!sels || sels.length === 0) return null;
                    const map = {};
                    for (const s of sels) {
                        const raw = s;
                        const entryId = s.getId?.() || null;
                        const uid = raw?.uid || null;
                        if (entryId && uid) map[entryId] = uid;
                    }
                    return Object.keys(map).length > 0 ? JSON.stringify(map) : null;
                } catch(e) {
                    return null;
                }
            }
            """, forceUid);
        if (json is null) return null;
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }

    /// <summary>
    /// Add a child force under an existing force by child force entry ID.
    /// Returns the uid of the created child force.
    /// </summary>
    public static async Task<string?> AddChildForceByIdAsync(IPage page, string parentForceUid, string childForceEntryId, int catalogueIndex = 0)
    {
        var result = await page.EvaluateAsync<string?>("""
            ({parentForceUid, childForceEntryId, catalogueIndex}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'ERROR:No spec state — was Setup called?';
                    const army = spec.army;
                    const books = spec.books || [spec.book];
                    const book = books[catalogueIndex] || books[0];
                    if (!army || !book) return 'ERROR:No army or book';

                    const parentForce = getForceByUid(army, parentForceUid);
                    if (!parentForce) return `ERROR:Parent force not found with uid '${parentForceUid}'`;

                    const beforeUids = new Set(
                        (army.getForces?.() || []).map(f => f?.uid || ''));

                    if (typeof parentForce.insertForce === 'function') {
                        parentForce.insertForce(book, childForceEntryId);
                    } else {
                        return 'ERROR:insertForce() not available on force object';
                    }

                    for (const f of (army.getForces?.() || [])) {
                        const raw = f;
                        if (raw?.uid && !beforeUids.has(raw.uid)) return raw.uid;
                    }
                    return null;
                } catch(e) {
                    return 'ERROR:AddChildForce error: ' + e.message;
                }
            }
            """, new { parentForceUid, childForceEntryId, catalogueIndex });
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

                    force.delete();
                    return null;
                } catch(e) {
                    return 'RemoveForce error: ' + e.message;
                }
            }
            """, forceUid);
        if (error != null) throw new InvalidOperationException(error);
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
                        getSelections(force).map(s => s?.uid || ''));

                    const selector = findSelectorById(force, entryId);
                    if (!selector) return `ERROR:Entry '${entryId}' not found in force selector tree`;

                    if (typeof selector.addInstance === 'function') {
                        selector.addInstance();
                        const insts = selector.instances || [];
                        insts[insts.length - 1]?.autocheck?.();
                    } else if (selector.getAmount?.() === 0 && typeof selector.incrementAmount === 'function') {
                        selector.incrementAmount();
                    } else {
                        selector.setAmount?.((selector.getAmount?.() || 0) + 1);
                    }

                    // Tag and find the new selection
                    window.__bsspec._selSeq = (window.__bsspec._selSeq || 0) + 1;
                    const after = getSelections(force);
                    let newUid = null;
                    for (const s of after) {
                        const raw = s;
                        if (raw && !before.has(raw.uid || '')) {
                            if (raw.__bsspec_seq === undefined) {
                                raw.__bsspec_seq = window.__bsspec._selSeq;
                            }
                            if (!newUid) newUid = raw.uid || null;
                        }
                    }

                    return newUid;
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

                    // NR pre-creates child nodes with amount=0 for all child entries.
                    const children = sel.getSelections?.() || sel.getChildren?.() || [];
                    let child = children.find(c => c.getId?.() === childEntryId);

                    if (child) {
                        if (typeof child.incrementAmount === 'function') {
                            child.incrementAmount();
                        } else if (typeof child.setAmount === 'function') {
                            child.setAmount((child.getAmount?.() || 0) + 1);
                        }
                        child.autocheck?.();
                        return child?.uid || null;
                    }

                    // Fallback: search the selector tree
                    const selector = sel.selector ? findSelectorById(sel.selector, childEntryId) : null;
                    const found = selector || findSelectorById(sel, childEntryId);
                    if (!found) return `ERROR:Child entry '${childEntryId}' not found under selection`;

                    if (typeof found.addInstance === 'function') {
                        found.addInstance();
                        const insts = found.instances || [];
                        const last = insts[insts.length - 1];
                        last?.autocheck?.();
                        return last?.uid || null;
                    } else if (typeof found.incrementAmount === 'function') {
                        found.incrementAmount();
                    } else {
                        found.setAmount?.((found.getAmount?.() || 0) + 1);
                    }
                    return null;
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

                    if (typeof sel.delete === 'function') {
                        sel.delete();
                    } else {
                        sel.setAmount(0);
                    }
                    return null;
                } catch(e) {
                    return 'DeselectSelection error: ' + e.message;
                }
            }
            """, new { forceUid, selectionUid });
        if (error != null) throw new InvalidOperationException(error);
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

                    const current = sel.getAmount?.() ?? 1;
                    if (current === count) return null;

                    if (typeof sel.setAmount === 'function') {
                        sel.setAmount({}, count);
                        return null;
                    }

                    return `Selection has no setAmount method — cannot change count`;
                } catch(e) {
                    return 'SetSelectionCount error: ' + e.message;
                }
            }
            """, new { forceUid, selectionUid, count });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Duplicate a selection within a force using dupe().
    /// Returns the uid of the duplicated selection.
    /// </summary>
    public static async Task<string?> DuplicateSelectionAsync(IPage page, string forceUid, string selectionUid)
    {
        var result = await page.EvaluateAsync<string?>("""
            ({forceUid, selectionUid}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'ERROR:No current roster';

                    const force = getForceByUid(army, forceUid);
                    if (!force) return `ERROR:Force not found with uid '${forceUid}'`;

                    const before = new Set(
                        getSelections(force).map(s => s?.uid || ''));

                    const sel = getSelectionByUid(force, selectionUid);
                    if (!sel) return `ERROR:Selection not found with uid '${selectionUid}'`;

                    if (typeof sel.dupe === 'function') {
                        sel.dupe();
                    } else {
                        return 'ERROR:dupe() method not available on selection';
                    }

                    // Tag and find the duplicated selection
                    window.__bsspec._selSeq = (window.__bsspec._selSeq || 0) + 1;
                    const after = getSelections(force);
                    let newUid = null;
                    for (const s of after) {
                        const raw = s;
                        if (raw && !before.has(raw.uid || '')) {
                            if (raw.__bsspec_seq === undefined) {
                                raw.__bsspec_seq = window.__bsspec._selSeq;
                            }
                            if (!newUid) newUid = raw.uid || null;
                        }
                    }

                    return newUid;
                } catch(e) {
                    return 'ERROR:DuplicateSelection error: ' + e.message;
                }
            }
            """, new { forceUid, selectionUid });
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
        if (error != null) throw new InvalidOperationException(error);
    }
}
