using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Translates IRosterEngine action calls to NR roster tree operations.
///
/// NR uses a unified node model where roster, force, category, entry, and selection
/// objects share the same prototype chain with methods like:
/// - getForces(), getEntries(), getSelections(), getChildren()
/// - setAmount(n), incrementAmount(), decrementAmount()
/// - getName(), getId(), getCosts(), delete(), dupe()
///
/// Access pattern: lists.getCurrentList() → {row, army, book}
/// The 'army' property IS the roster object.
/// </summary>
public static class NewRecruitActions
{
    private const string GetArmy = "window.__bsspec?.army";
    private const string GetBook = "window.__bsspec?.book";

    /// <summary>
    /// Add a force to the roster by force entry index.
    /// Uses book.getForces()[index] to get the force definition, then army.insertForce(book, forceId).
    /// </summary>
    public static async Task AddForceAsync(IPage page, int forceEntryIndex, int catalogueIndex = 0)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceEntryIndex}) => {
                try {
                    const spec = window.__bsspec;
                    if (!spec) return 'No spec state — was Setup called?';
                    const army = spec.army;
                    const book = spec.book;
                    if (!army || !book) return 'No army or book';

                    const forces = book.getForces();
                    if (forceEntryIndex >= forces.length) return `Force entry index ${forceEntryIndex} out of range (${forces.length} available)`;

                    const force = forces[forceEntryIndex];
                    army.insertForce(book, force.id);
                    return null;
                } catch(e) {
                    return 'AddForce error: ' + e.message;
                }
            }
            """, new { forceEntryIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Remove a force from the roster by index.
    /// Uses army.getForces()[index].delete().
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, int forceIndex)
    {
        var error = await page.EvaluateAsync<string?>("""
            (forceIndex) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const forces = army.getForces();
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
    /// Select an entry in the specified force, adding it to the roster.
    /// Uses force.getEntries()[entryIndex].incrementAmount().
    /// </summary>
    public static async Task SelectEntryAsync(IPage page, int forceIndex, int entryIndex)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceIndex, entryIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const forces = army.getForces();
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const entries = forces[forceIndex].getEntries();
                    if (entryIndex >= entries.length) return `Entry index ${entryIndex} out of range (${entries.length} entries)`;

                    const entry = entries[entryIndex];
                    if (entry.getAmount() === 0) {
                        entry.incrementAmount();
                    } else {
                        entry.setAmount(entry.getAmount() + 1);
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
    /// Uses force.getSelections()[selectionIndex].getEntries()[childEntryIndex].incrementAmount().
    /// </summary>
    public static async Task SelectChildEntryAsync(IPage page, int forceIndex, int selectionIndex, int childEntryIndex)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceIndex, selectionIndex, childEntryIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const forces = army.getForces();
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = forces[forceIndex].getSelections();
                    if (selectionIndex >= selections.length) return `Selection index ${selectionIndex} out of range`;

                    const childEntries = selections[selectionIndex].getEntries();
                    if (childEntryIndex >= childEntries.length) return `Child entry index ${childEntryIndex} out of range (${childEntries.length} entries)`;

                    const entry = childEntries[childEntryIndex];
                    if (entry.getAmount() === 0) {
                        entry.incrementAmount();
                    } else {
                        entry.setAmount(entry.getAmount() + 1);
                    }
                    return null;
                } catch(e) {
                    return 'SelectChildEntry error: ' + e.message;
                }
            }
            """, new { forceIndex, selectionIndex, childEntryIndex });
        if (error != null) throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Deselect (remove) a selection by setting its amount to 0 or calling delete().
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, int forceIndex, int selectionIndex)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({forceIndex, selectionIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const forces = army.getForces();
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = forces[forceIndex].getSelections();
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
        var error = await page.EvaluateAsync<string?>("""
            ({forceIndex, entryIndex, count}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const forces = army.getForces();
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = forces[forceIndex].getSelections();
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
        var error = await page.EvaluateAsync<string?>("""
            ({forceIndex, selectionIndex}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    const forces = army.getForces();
                    if (forceIndex >= forces.length) return `Force index ${forceIndex} out of range`;

                    const selections = forces[forceIndex].getSelections();
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
    /// Set cost limit for a cost type using army.setMaxCosts() or updating maxCosts array.
    /// </summary>
    public static async Task SetCostLimitAsync(IPage page, string costTypeId, double value)
    {
        var error = await page.EvaluateAsync<string?>("""
            ({costTypeId, value}) => {
                try {
                    const army = window.__bsspec?.army;
                    if (!army) return 'No current roster';

                    // Try setMaxCosts method first
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
