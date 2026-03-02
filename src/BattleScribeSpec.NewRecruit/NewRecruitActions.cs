using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Translates IRosterEngine action calls to New Recruit browser interactions.
/// 
/// NR's roster engine is in the private nr-shared module and NOT exposed as
/// Pinia store actions. The Pinia stores (lists, listsPage, etc.) manage UI
/// state, not engine operations. Therefore we use a hybrid approach:
/// - Pinia store methods where available (e.g., listsPage.setAddingUnit)
/// - Direct JS evaluation to access the engine through the current list object
/// - UI-level Playwright automation as fallback
///
/// The key entry point is `lists.getCurrentList()` which returns the current
/// roster list object with the engine's data model.
/// </summary>
public static class NewRecruitActions
{
    private const string PiniaAccess =
        "document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia";

    private static string StoreAccess(string storeId) =>
        $"{PiniaAccess}?._s?.get('{storeId}')";

    /// <summary>
    /// Add a force to the roster by force entry index.
    /// NR's list object should have force management in its data model.
    /// </summary>
    public static async Task AddForceAsync(IPage page, int forceEntryIndex, int catalogueIndex = 0)
    {
        await page.EvaluateAsync("""
            async ({forceEntryIndex, catalogueIndex}) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                if (!lists) throw new Error('lists store not found');

                const currentList = lists.getCurrentList();
                if (!currentList) throw new Error('No current list');

                // Access the roster from the current list
                const roster = currentList.roster || currentList;

                // Get game system force entries
                const gameSystem = currentList.gameSystem || currentList.system;
                if (!gameSystem) throw new Error('No game system on current list');

                const forceEntries = gameSystem.forceEntries || [];
                if (forceEntryIndex >= forceEntries.length) {
                    throw new Error(`Force entry index ${forceEntryIndex} out of range (${forceEntries.length} entries)`);
                }

                const forceEntry = forceEntries[forceEntryIndex];

                // Try to call addForce on the roster/list object
                if (typeof roster.addForce === 'function') {
                    roster.addForce(forceEntry, catalogueIndex);
                } else if (typeof currentList.addForce === 'function') {
                    currentList.addForce(forceEntry, catalogueIndex);
                } else {
                    throw new Error('addForce method not found on roster or list object. Available keys: ' +
                        Object.keys(roster).filter(k => typeof roster[k] === 'function').join(', '));
                }
            }
            """, new { forceEntryIndex, catalogueIndex });
    }

    /// <summary>
    /// Remove a force from the roster by index.
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, int forceIndex)
    {
        await page.EvaluateAsync("""
            async (forceIndex) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                const currentList = lists?.getCurrentList();
                if (!currentList) throw new Error('No current list');

                const roster = currentList.roster || currentList;
                const forces = roster.forces || [];
                if (forceIndex >= forces.length) {
                    throw new Error(`Force index ${forceIndex} out of range (${forces.length} forces)`);
                }

                if (typeof roster.removeForce === 'function') {
                    roster.removeForce(forces[forceIndex]);
                } else if (typeof currentList.removeForce === 'function') {
                    currentList.removeForce(forces[forceIndex]);
                } else {
                    // Fallback: splice from array
                    forces.splice(forceIndex, 1);
                }
            }
            """, forceIndex);
    }

    /// <summary>
    /// Select an entry in the specified force, creating a new selection.
    /// </summary>
    public static async Task SelectEntryAsync(IPage page, int forceIndex, int entryIndex)
    {
        await page.EvaluateAsync("""
            async ({forceIndex, entryIndex}) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                const currentList = lists?.getCurrentList();
                if (!currentList) throw new Error('No current list');

                const roster = currentList.roster || currentList;
                const forces = roster.forces || [];
                if (forceIndex >= forces.length) {
                    throw new Error(`Force index ${forceIndex} out of range`);
                }

                const force = forces[forceIndex];
                // Get available entries for this force (from catalogue or force entry)
                const entries = force.availableEntries || force.entries || force.selectionEntries || [];
                if (entryIndex >= entries.length) {
                    throw new Error(`Entry index ${entryIndex} out of range (${entries.length} entries)`);
                }

                const entry = entries[entryIndex];

                if (typeof force.addSelection === 'function') {
                    force.addSelection(entry);
                } else if (typeof roster.selectEntry === 'function') {
                    roster.selectEntry(force, entry);
                } else if (typeof currentList.selectEntry === 'function') {
                    currentList.selectEntry(force, entry);
                } else {
                    throw new Error('selectEntry/addSelection not found. Force keys: ' +
                        Object.keys(force).filter(k => typeof force[k] === 'function').join(', '));
                }
            }
            """, new { forceIndex, entryIndex });
    }

    /// <summary>
    /// Select a child entry under an existing selection.
    /// </summary>
    public static async Task SelectChildEntryAsync(IPage page, int forceIndex, int selectionIndex, int childEntryIndex)
    {
        await page.EvaluateAsync("""
            async ({forceIndex, selectionIndex, childEntryIndex}) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                const currentList = lists?.getCurrentList();
                if (!currentList) throw new Error('No current list');

                const roster = currentList.roster || currentList;
                const forces = roster.forces || [];
                const force = forces[forceIndex];
                if (!force) throw new Error(`Force index ${forceIndex} out of range`);

                const selections = force.selections || [];
                const selection = selections[selectionIndex];
                if (!selection) throw new Error(`Selection index ${selectionIndex} out of range`);

                const childEntries = selection.availableEntries || selection.entries || selection.selectionEntries || [];
                if (childEntryIndex >= childEntries.length) {
                    throw new Error(`Child entry index ${childEntryIndex} out of range (${childEntries.length} entries)`);
                }

                const childEntry = childEntries[childEntryIndex];

                if (typeof selection.addSelection === 'function') {
                    selection.addSelection(childEntry);
                } else if (typeof roster.selectChildEntry === 'function') {
                    roster.selectChildEntry(selection, childEntry);
                } else {
                    throw new Error('selectChildEntry/addSelection not found on selection');
                }
            }
            """, new { forceIndex, selectionIndex, childEntryIndex });
    }

    /// <summary>
    /// Deselect (remove) a selection by its index within the force.
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, int forceIndex, int selectionIndex)
    {
        await page.EvaluateAsync("""
            async ({forceIndex, selectionIndex}) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                const currentList = lists?.getCurrentList();
                if (!currentList) throw new Error('No current list');

                const roster = currentList.roster || currentList;
                const forces = roster.forces || [];
                const force = forces[forceIndex];
                if (!force) throw new Error(`Force index ${forceIndex} out of range`);

                const selections = force.selections || [];
                const selection = selections[selectionIndex];
                if (!selection) throw new Error(`Selection index ${selectionIndex} out of range`);

                if (typeof force.removeSelection === 'function') {
                    force.removeSelection(selection);
                } else if (typeof roster.removeSelection === 'function') {
                    roster.removeSelection(force, selection);
                } else if (typeof currentList.removeSelection === 'function') {
                    currentList.removeSelection(force, selection);
                } else {
                    // Fallback: splice from array
                    selections.splice(selectionIndex, 1);
                }
            }
            """, new { forceIndex, selectionIndex });
    }

    /// <summary>
    /// Set the number of instances for a selection entry.
    /// </summary>
    public static async Task SetSelectionCountAsync(IPage page, int forceIndex, int entryIndex, int count)
    {
        await page.EvaluateAsync("""
            async ({forceIndex, entryIndex, count}) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                const currentList = lists?.getCurrentList();
                if (!currentList) throw new Error('No current list');

                const roster = currentList.roster || currentList;
                const forces = roster.forces || [];
                const force = forces[forceIndex];
                if (!force) throw new Error(`Force index ${forceIndex} out of range`);

                const selections = force.selections || [];
                const selection = selections[entryIndex];
                if (!selection) throw new Error(`Selection index ${entryIndex} out of range`);

                if (typeof selection.setNumber === 'function') {
                    selection.setNumber(count);
                } else if (selection.number !== undefined) {
                    selection.number = count;
                } else {
                    throw new Error('Cannot set selection count — no setNumber method or number property');
                }
            }
            """, new { forceIndex, entryIndex, count });
    }

    /// <summary>
    /// Duplicate a selection within a force.
    /// </summary>
    public static async Task DuplicateSelectionAsync(IPage page, int forceIndex, int selectionIndex)
    {
        await page.EvaluateAsync("""
            async ({forceIndex, selectionIndex}) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                const currentList = lists?.getCurrentList();
                if (!currentList) throw new Error('No current list');

                const roster = currentList.roster || currentList;
                const forces = roster.forces || [];
                const force = forces[forceIndex];
                if (!force) throw new Error(`Force index ${forceIndex} out of range`);

                const selections = force.selections || [];
                const selection = selections[selectionIndex];
                if (!selection) throw new Error(`Selection index ${selectionIndex} out of range`);

                if (typeof force.duplicateSelection === 'function') {
                    force.duplicateSelection(selection);
                } else if (typeof roster.duplicateSelection === 'function') {
                    roster.duplicateSelection(force, selection);
                } else {
                    throw new Error('duplicateSelection not found');
                }
            }
            """, new { forceIndex, selectionIndex });
    }

    /// <summary>
    /// Set cost limit for a cost type.
    /// </summary>
    public static async Task SetCostLimitAsync(IPage page, string costTypeId, double value)
    {
        await page.EvaluateAsync("""
            async ({costTypeId, value}) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                const currentList = lists?.getCurrentList();
                if (!currentList) throw new Error('No current list');

                const roster = currentList.roster || currentList;

                if (typeof roster.setCostLimit === 'function') {
                    roster.setCostLimit(costTypeId, value);
                } else {
                    // Try to find cost limits on the roster
                    const costLimits = roster.costLimits || roster.costs || [];
                    const costLimit = costLimits.find(c => c.typeId === costTypeId || c.id === costTypeId);
                    if (costLimit) {
                        costLimit.value = value;
                    } else {
                        throw new Error(`Cost type ${costTypeId} not found in roster cost limits`);
                    }
                }
            }
            """, new { costTypeId, value });
    }
}
