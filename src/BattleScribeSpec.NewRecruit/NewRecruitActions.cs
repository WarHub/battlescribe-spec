using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Translates IRosterEngine action calls to NR roster tree operations.
///
/// NR's roster tree for locally-loaded systems:
///   army → selectors[catIdx].first() → selectors[forceIdx] → instances[n] (= forces)
///   force → selectors[catIdx].first() → selectors[entryIdx] (entry selectors)
///
/// Key API patterns (verified via Playwright exploration):
///   - army.getForces() returns force instances (NOT getChildren which returns [])
///   - force.getSelections() returns selected entries
///   - army.insertForce(book, forceId) adds a force by ID
///   - entrySelector.addInstance() selects an entry (increments from 0→1)
///   - selection.delete() removes a selection
///   - army.calcTotalCosts() returns [{name, value}]
/// </summary>
public static class NewRecruitActions
{
    // JS helper functions (getForces, getSelections, getSortedSelections,
    // findSelectorById) are registered as page init scripts
    // via NewRecruitBrowser.RegisterHelpersOnPageAsync() and are automatically
    // available as window globals after every navigation. Action methods
    // reference them by name — no inline definitions needed.

    /// <summary>
    /// Add a force to the roster by force entry ID, using the specified catalogue book.
    /// </summary>
    public static async Task AddForceByIdAsync(IPage page, string forceId, int catalogueIndex = 0)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceId, catalogueIndex}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'No spec state — was Setup called?';
                    const army = spec.army;
                    const books = spec.books || [spec.book];
                    const book = books[catalogueIndex] || books[0];
                    if (!army || !book) return 'No army or book';

                    army.insertForce(book, forceId);
                    return null;
                } catch(e) {
                    return 'AddForce error: ' + e.message;
                }
            }
            """, new { forceId, catalogueIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Add a child force under an existing force by child force entry ID.
    /// </summary>
    public static async Task AddChildForceByIdAsync(IPage page, int[] parentForcePath, string childForceId, int catalogueIndex = 0)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({parentForcePath, childForceId, catalogueIndex}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'No spec state — was Setup called?';
                    const army = spec.army;
                    const books = spec.books || [spec.book];
                    const book = books[catalogueIndex] || books[0];
                    if (!army || !book) return 'No army or book';

                    const parentForce = getForceAtPath(army, parentForcePath);
                    if (!parentForce) return `Parent force not found at path [${parentForcePath}]`;

                    if (typeof parentForce.insertForce === 'function') {
                        parentForce.insertForce(book, childForceId);
                    } else {
                        return 'insertForce() not available on force object';
                    }
                    return null;
                } catch(e) {
                    return 'AddChildForce error: ' + e.message;
                }
            }
            """, new { parentForcePath, childForceId, catalogueIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, int[] forcePath)
    {
        var error = await page.EvaluateAsync<string?>("""
            (forcePath) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceAtPath(army, forcePath);
                    if (!force) return `Force not found at path [${forcePath}]`;

                    force.delete();
                    return null;
                } catch(e) {
                    return 'RemoveForce error: ' + e.message;
                }
            }
            """, forcePath);
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select an entry in the specified force by entry ID.
    /// Traverses the force's selector tree to find the entry selector, then calls addInstance().
    /// </summary>
    public static async Task SelectEntryByIdAsync(IPage page, int[] forcePath, string entryId)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forcePath, entryId}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceAtPath(army, forcePath);
                    if (!force) return `Force not found at path [${forcePath}]`;

                    // Record existing selections before adding
                    const before = new Set(
                        getSelections(force).map(s => (s?.__v_raw || s)?.uid || '')
                    );

                    const selector = findSelectorById(force, entryId);
                    if (!selector) return `Entry '${entryId}' not found in force selector tree`;

                    if (typeof selector.addInstance === 'function') {
                        selector.addInstance();
                        const insts = selector.instances || [];
                        insts[insts.length - 1]?.autocheck?.();
                    } else if (selector.getAmount?.() === 0 && typeof selector.incrementAmount === 'function') {
                        selector.incrementAmount();
                    } else {
                        selector.setAmount?.((selector.getAmount?.() || 0) + 1);
                    }

                    // Tag the new selection with insertion sequence number.
                    window.__bsspec._selSeq = (window.__bsspec._selSeq || 0) + 1;
                    const after = getSelections(force);
                    for (const s of after) {
                        const raw = s?.__v_raw || s;
                        if (raw && !before.has(raw.uid || '') && raw.__bsspec_seq === undefined) {
                            raw.__bsspec_seq = window.__bsspec._selSeq;
                        }
                    }

                    return null;
                } catch(e) {
                    return 'SelectEntry error: ' + e.message;
                }
            }
            """, new { forcePath, entryId });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select an entry in the specified force by flat index across all categories.
    /// Iterates the force's selector tree to find the Nth non-force entry selector.
    /// </summary>
    public static async Task SelectEntryByIndexAsync(IPage page, int[] forcePath, int entryIndex)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forcePath, entryIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceAtPath(army, forcePath);
                    if (!force) return `Force not found at path [${forcePath}]`;

                    // Collect all entry selectors from all categories in the force
                    const allEntrySelectors = [];
                    for (const catSel of force.selectors || []) {
                        const catInst = typeof catSel.first === 'function' ? catSel.first() : null;
                        if (!catInst) continue;
                        for (const entrySel of catInst.selectors || []) {
                            if (entrySel.isForce) continue;
                            allEntrySelectors.push(entrySel);
                        }
                    }

                    if (entryIndex >= allEntrySelectors.length)
                        return `Entry index ${entryIndex} out of range (${allEntrySelectors.length} entry selectors in force)`;

                    const sel = allEntrySelectors[entryIndex];
                    if (typeof sel.addInstance === 'function') {
                        sel.addInstance();
                        const insts = sel.instances || [];
                        insts[insts.length - 1]?.autocheck?.();
                    } else if (sel.getAmount?.() === 0 && typeof sel.incrementAmount === 'function') {
                        sel.incrementAmount();
                    } else {
                        sel.setAmount?.((sel.getAmount?.() || 0) + 1);
                    }
                    return null;
                } catch(e) {
                    return 'SelectEntry error: ' + e.message;
                }
            }
            """, new { forcePath, entryIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select a child entry under an existing selection.
    /// In NR, child entries already exist as nodes with amount=0 when the parent
    /// is selected. To "select" a child, we call incrementAmount() on the
    /// existing child node (not addInstance() on the selector template).
    /// </summary>
    public static async Task SelectChildEntryByIdAsync(IPage page, int[] forcePath, int[] selectionPath, string childEntryId)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forcePath, selectionPath, childEntryId}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceAtPath(army, forcePath);
                    if (!force) return `Force not found at path [${forcePath}]`;

                    const sel = getSelectionAtPath(force, selectionPath);
                    if (!sel) return `Selection not found at path [${selectionPath}]`;

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
                        return null;
                    }

                    // Fallback: search the selector tree
                    const selector = sel.selector ? findSelectorById(sel.selector, childEntryId) : null;
                    const found = selector || findSelectorById(sel, childEntryId);
                    if (!found) return `Child entry '${childEntryId}' not found under selection`;

                    if (typeof found.addInstance === 'function') {
                        found.addInstance();
                        const insts = found.instances || [];
                        insts[insts.length - 1]?.autocheck?.();
                    } else if (typeof found.incrementAmount === 'function') {
                        found.incrementAmount();
                    } else {
                        found.setAmount?.((found.getAmount?.() || 0) + 1);
                    }
                    return null;
                } catch(e) {
                    return 'SelectChildEntry error: ' + e.message;
                }
            }
            """, new { forcePath, selectionPath, childEntryId });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Deselect (remove) a selection by calling delete() or setting amount to 0.
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, int[] forcePath, int[] selectionPath)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forcePath, selectionPath}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceAtPath(army, forcePath);
                    if (!force) return `Force not found at path [${forcePath}]`;

                    const sel = getSelectionAtPath(force, selectionPath);
                    if (!sel) return `Selection not found at path [${selectionPath}]`;

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
            """, new { forcePath, selectionPath });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Set the number of instances for a selection.
    /// For child selections (path length > 1), uses the parent selector's addInstance/removeInstance
    /// to properly handle NR's instance-based model.
    /// Root-level selections (path length == 1) are a no-op — BattleScribe engines manage
    /// root selection count via selectEntry/deselectEntry, not setSelectionCount.
    /// </summary>
    public static async Task SetSelectionCountAsync(IPage page, int[] forcePath, int[] selectionPath, int count)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forcePath, selectionPath, count}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceAtPath(army, forcePath);
                    if (!force) return `Force not found at path [${forcePath}]`;

                    // Root-level selections: no-op (consistent with Oracle/Desktop behavior).
                    // Root entries are managed via selectEntry/deselectEntry, not count changes.
                    if (selectionPath.length <= 1) return null;

                    const sel = getSelectionAtPath(force, selectionPath);
                    if (!sel) return `Selection not found at path [${selectionPath}]`;

                    const current = sel.getAmount?.() ?? 1;
                    if (current === count) return null;

                    // For child selections, use the parent's selector mechanism.
                    // Direct setAmount/incrementAmount on child selections gets reset by NR watchers.
                    const parentPath = selectionPath.slice(0, -1);
                    const parentSel = getSelectionAtPath(force, parentPath);
                    if (!parentSel) return `Parent selection not found at path [${parentPath}]`;

                    const entryId = sel.getId?.();
                    const selector = findSelectorById(parentSel, entryId);

                    if (selector && typeof selector.addInstance === 'function') {
                        if (count > current) {
                            for (let i = current; i < count; i++) {
                                selector.addInstance();
                                selector.autocheck?.();
                            }
                        } else if (typeof selector.removeInstance === 'function') {
                            for (let i = current; i > count; i--) {
                                selector.removeInstance();
                                selector.autocheck?.();
                            }
                        }
                        return null;
                    }

                    return `No selector found for child entry '${entryId}' — cannot change count`;
                } catch(e) {
                    return 'SetSelectionCount error: ' + e.message;
                }
            }
            """, new { forcePath, selectionPath, count });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Duplicate a selection within a force using dupe().
    /// </summary>
    public static async Task DuplicateSelectionAsync(IPage page, int[] forcePath, int[] selectionPath)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forcePath, selectionPath}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const force = getForceAtPath(army, forcePath);
                    if (!force) return `Force not found at path [${forcePath}]`;

                    // Record existing selections before duplicating
                    const before = new Set(
                        getSelections(force).map(s => (s?.__v_raw || s)?.uid || '')
                    );

                    const sel = getSelectionAtPath(force, selectionPath);
                    if (!sel) return `Selection not found at path [${selectionPath}]`;

                    if (typeof sel.dupe === 'function') {
                        sel.dupe();
                    } else {
                        return 'dupe() method not available on selection';
                    }

                    // Tag the duplicated selection with insertion sequence
                    window.__bsspec._selSeq = (window.__bsspec._selSeq || 0) + 1;
                    const after = getSelections(force);
                    for (const s of after) {
                        const raw = s?.__v_raw || s;
                        if (raw && !before.has(raw.uid || '') && raw.__bsspec_seq === undefined) {
                            raw.__bsspec_seq = window.__bsspec._selSeq;
                        }
                    }

                    return null;
                } catch(e) {
                    return 'DuplicateSelection error: ' + e.message;
                }
            }
            """, new { forcePath, selectionPath });
        if (error != null) throw new InvalidOperationException(error);
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
                            // Update costLimitConfig so the state reader uses the new limit
                            if (window.__bsspec?.costLimitConfig && cost.name) {
                                window.__bsspec.costLimitConfig[cost.name] = value;
                            }
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
