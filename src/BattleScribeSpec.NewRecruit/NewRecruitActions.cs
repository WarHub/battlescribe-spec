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
    /// <summary>
    /// JS helper: get forces array from army, preferring getForces() over getChildren().
    /// </summary>
    private const string JsGetForces = """
        function getForces(army) {
            const f = army.getForces?.();
            if (f?.length) return f;
            const c = army.getChildren?.();
            if (c?.length) return c;
            return [];
        }
        """;

    /// <summary>
    /// JS helper: get selections from a force.
    /// </summary>
    private const string JsGetSelections = """
        function getSelections(force) {
            const s = force.getSelections?.();
            if (s?.length) return s;
            const c = force.getChildren?.();
            if (c?.length) return c;
            return [];
        }
        """;

    /// <summary>
    /// JS helper: recursively find a selector by ID in the selector tree.
    /// NR's tree: node.selectors[].first().selectors[] — entries are leaf selectors.
    /// </summary>
    private const string JsFindSelectorById = """
        function findSelectorById(node, targetId) {
            if (!node) return null;
            // Check if this node's ids include the target
            if (node.ids?.includes(targetId)) return node;
            // Check selectors array
            const sels = node.selectors || [];
            for (const s of sels) {
                if (s.ids?.includes(targetId)) return s;
                // Go deeper via first() (gets the instance node with sub-selectors)
                if (typeof s.first === 'function') {
                    const inst = s.first();
                    if (inst?.selectors) {
                        const found = findSelectorById(inst, targetId);
                        if (found) return found;
                    }
                }
                // Also check nested selectors directly
                if (s.selectors) {
                    const found = findSelectorById(s, targetId);
                    if (found) return found;
                }
            }
            return null;
        }
        """;

    /// <summary>
    /// JS helper: recursively find a selector by name in the selector tree.
    /// </summary>
    private const string JsFindSelectorByName = """
        function findSelectorByName(node, targetName) {
            if (!node) return null;
            // Check this node's name
            if (node.getName?.() === targetName || node.name === targetName) return node;
            // Check selectors array
            const sels = node.selectors || [];
            for (const s of sels) {
                if (s.getName?.() === targetName || s.name === targetName) return s;
                // Go deeper via first()
                if (typeof s.first === 'function') {
                    const inst = s.first();
                    if (inst?.selectors) {
                        const found = findSelectorByName(inst, targetName);
                        if (found) return found;
                    }
                }
                if (s.selectors) {
                    const found = findSelectorByName(s, targetName);
                    if (found) return found;
                }
            }
            return null;
        }
        """;

    /// <summary>
    /// Add a force to the roster by force entry name.
    /// Searches the game system's force entries for a matching name.
    /// </summary>
    public static async Task AddForceByNameAsync(IPage page, string forceName, int catalogueIndex = 0)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceName, catalogueIndex}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'No spec state — was Setup called?';
                    const army = spec.army;
                    const books = spec.books || [spec.book];
                    const book = books[catalogueIndex] || books[0];
                    if (!army || !book) return 'No army or book';

                    // Find force entry by name in the book's catalogue
                    const cat = book.catalogue;
                    if (!cat) return 'No catalogue in book';

                    // Search forceEntries for matching name
                    const forceEntries = cat.forceEntries || cat.gameSystem?.forceEntries || [];
                    let forceId = null;
                    for (const fe of forceEntries) {
                        if (fe.name === forceName) {
                            forceId = fe.id;
                            break;
                        }
                    }
                    if (!forceId) return `Force entry '${forceName}' not found in catalogue`;

                    army.insertForce(book, forceId);
                    return null;
                } catch(e) {
                    return 'AddForceByName error: ' + e.message;
                }
            }
            """, new { forceName, catalogueIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select an entry in the specified force by entry name.
    /// Searches the force's selector tree for a matching name.
    /// </summary>
    public static async Task SelectEntryByNameAsync(IPage page, int forceIndex, string entryName)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, entryName}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range (${forces.length} forces)`;

                    const force = forces[forceIndex];

                    {{JsFindSelectorByName}}
                    const selector = findSelectorByName(force, entryName);
                    if (!selector) return `Entry '${entryName}' not found in force selector tree`;

                    if (typeof selector.addInstance === 'function') {
                        selector.addInstance();
                    } else if (selector.getAmount?.() === 0 && typeof selector.incrementAmount === 'function') {
                        selector.incrementAmount();
                    } else {
                        selector.setAmount?.((selector.getAmount?.() || 0) + 1);
                    }
                    return null;
                } catch(e) {
                    return 'SelectEntryByName error: ' + e.message;
                }
            }
            """, new { forceIndex, entryName });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select a child entry under an existing selection by child entry name.
    /// </summary>
    public static async Task SelectChildEntryByNameAsync(IPage page, int forceIndex, int selectionIndex, string childEntryName)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, selectionIndex, childEntryName}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    {{JsGetSelections}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = getSelections(forces[forceIndex]);
                    if (selectionIndex >= selections.length) return `Selection index ${selectionIndex} out of range`;

                    const sel = selections[selectionIndex];

                    {{JsFindSelectorByName}}
                    const found = sel.selector ? findSelectorByName(sel.selector, childEntryName) : null;
                    const result = found || findSelectorByName(sel, childEntryName);
                    if (!result) return `Child entry '${childEntryName}' not found under selection`;

                    if (typeof result.addInstance === 'function') {
                        result.addInstance();
                    } else if (result.getAmount?.() === 0 && typeof result.incrementAmount === 'function') {
                        result.incrementAmount();
                    } else {
                        result.setAmount?.((result.getAmount?.() || 0) + 1);
                    }
                    return null;
                } catch(e) {
                    return 'SelectChildEntryByName error: ' + e.message;
                }
            }
            """, new { forceIndex, selectionIndex, childEntryName });
        if (error != null) throw new InvalidOperationException(error);
    }

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
    /// Remove a force from the roster by index.
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, int forceIndex)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            (forceIndex) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range (${forces.length} forces)`;

                    forces[forceIndex].delete();
                    return null;
                } catch(e) {
                    return 'RemoveForce error: ' + e.message;
                }
            }
            """, forceIndex);
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select an entry in the specified force by entry ID.
    /// Traverses the force's selector tree to find the entry selector, then calls addInstance().
    /// </summary>
    public static async Task SelectEntryByIdAsync(IPage page, int forceIndex, string entryId)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, entryId}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range (${forces.length} forces)`;

                    const force = forces[forceIndex];

                    {{JsFindSelectorById}}
                    const selector = findSelectorById(force, entryId);
                    if (!selector) return `Entry '${entryId}' not found in force selector tree`;

                    // addInstance() on the selector creates a new selection instance
                    if (typeof selector.addInstance === 'function') {
                        selector.addInstance();
                    } else if (selector.getAmount?.() === 0 && typeof selector.incrementAmount === 'function') {
                        selector.incrementAmount();
                    } else {
                        selector.setAmount?.((selector.getAmount?.() || 0) + 1);
                    }
                    return null;
                } catch(e) {
                    return 'SelectEntry error: ' + e.message;
                }
            }
            """, new { forceIndex, entryId });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select an entry in the specified force by flat index across all categories.
    /// Iterates the force's selector tree to find the Nth non-force entry selector.
    /// </summary>
    public static async Task SelectEntryByIndexAsync(IPage page, int forceIndex, int entryIndex)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, entryIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range (${forces.length} forces)`;

                    const force = forces[forceIndex];

                    // Collect all entry selectors from all categories in the force
                    const allEntrySelectors = [];
                    for (const catSel of force.selectors || []) {
                        const catInst = typeof catSel.first === 'function' ? catSel.first() : null;
                        if (!catInst) continue;
                        for (const entrySel of catInst.selectors || []) {
                            // Skip force-type selectors (they're for sub-forces, not entries)
                            if (entrySel.isForce) continue;
                            allEntrySelectors.push(entrySel);
                        }
                    }

                    if (entryIndex >= allEntrySelectors.length)
                        return `Entry index ${entryIndex} out of range (${allEntrySelectors.length} entry selectors in force)`;

                    const sel = allEntrySelectors[entryIndex];
                    if (typeof sel.addInstance === 'function') {
                        sel.addInstance();
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
            """, new { forceIndex, entryIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Select a child entry under an existing selection.
    /// Child entries are in the selection's selector tree.
    /// </summary>
    public static async Task SelectChildEntryByIdAsync(IPage page, int forceIndex, int selectionIndex, string childEntryId)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, selectionIndex, childEntryId}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    {{JsGetSelections}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = getSelections(forces[forceIndex]);
                    if (selectionIndex >= selections.length) return `Selection index ${selectionIndex} out of range`;

                    const sel = selections[selectionIndex];

                    {{JsFindSelectorById}}
                    // Search in the selection's selector tree
                    const selector = sel.selector ? findSelectorById(sel.selector, childEntryId) : null;
                    // Also try the selection node itself
                    const found = selector || findSelectorById(sel, childEntryId);
                    if (!found) return `Child entry '${childEntryId}' not found under selection`;

                    if (typeof found.addInstance === 'function') {
                        found.addInstance();
                    } else if (found.getAmount?.() === 0 && typeof found.incrementAmount === 'function') {
                        found.incrementAmount();
                    } else {
                        found.setAmount?.((found.getAmount?.() || 0) + 1);
                    }
                    return null;
                } catch(e) {
                    return 'SelectChildEntry error: ' + e.message;
                }
            }
            """, new { forceIndex, selectionIndex, childEntryId });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Deselect (remove) a selection by calling delete() or setting amount to 0.
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, int forceIndex, int selectionIndex)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, selectionIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    {{JsGetSelections}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = getSelections(forces[forceIndex]);
                    if (selectionIndex >= selections.length) return `Selection index ${selectionIndex} out of range`;

                    const sel = selections[selectionIndex];
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
            """, new { forceIndex, selectionIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Set the number of instances for a selection entry using setAmount().
    /// </summary>
    public static async Task SetSelectionCountAsync(IPage page, int forceIndex, int entryIndex, int count)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, entryIndex, count}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    {{JsGetSelections}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = getSelections(forces[forceIndex]);
                    if (entryIndex >= selections.length) return `Selection index ${entryIndex} out of range`;

                    selections[entryIndex].setAmount(count);
                    return null;
                } catch(e) {
                    return 'SetSelectionCount error: ' + e.message;
                }
            }
            """, new { forceIndex, entryIndex, count });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Duplicate a selection within a force using dupe().
    /// </summary>
    public static async Task DuplicateSelectionAsync(IPage page, int forceIndex, int selectionIndex)
    {
        var error = await page.EvaluateAsync<string?>($$"""
            ({forceIndex, selectionIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    {{JsGetForces}}
                    {{JsGetSelections}}
                    const forces = getForces(army);
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = getSelections(forces[forceIndex]);
                    if (selectionIndex >= selections.length) return `Selection index ${selectionIndex} out of range`;

                    const sel = selections[selectionIndex];
                    if (typeof sel.dupe === 'function') {
                        sel.dupe();
                    } else {
                        return 'dupe() method not available on selection';
                    }
                    return null;
                } catch(e) {
                    return 'DuplicateSelection error: ' + e.message;
                }
            }
            """, new { forceIndex, selectionIndex });
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
