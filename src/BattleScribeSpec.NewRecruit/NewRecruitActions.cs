using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Translates IRosterEngine action calls to New Recruit browser interactions.
/// Uses page.EvaluateAsync() to call NR's internal engine APIs where possible,
/// falling back to UI-level Playwright automation when needed.
/// </summary>
public static class NewRecruitActions
{
    /// <summary>
    /// Add a force to the roster by force entry index.
    /// </summary>
    public static async Task AddForceAsync(IPage page, int forceEntryIndex, int catalogueIndex = 0)
    {
        // Placeholder — will be refined during NR integration.
        // Strategy: call NR's internal API via page.EvaluateAsync() or click UI elements.
        await page.EvaluateAsync("""
            (forceEntryIndex) => {
                // TODO: Discover NR's internal API for adding forces
                // Placeholder: interact with NR's Pinia store or exposed methods
                throw new Error('NewRecruitActions.AddForce not yet implemented — requires NR store discovery');
            }
            """, forceEntryIndex);
    }

    /// <summary>
    /// Remove a force from the roster by index.
    /// </summary>
    public static async Task RemoveForceAsync(IPage page, int forceIndex)
    {
        await page.EvaluateAsync("""
            (forceIndex) => {
                throw new Error('NewRecruitActions.RemoveForce not yet implemented');
            }
            """, forceIndex);
    }

    /// <summary>
    /// Select an entry in the specified force, creating a new selection.
    /// </summary>
    public static async Task SelectEntryAsync(IPage page, int forceIndex, int entryIndex)
    {
        await page.EvaluateAsync("""
            ({forceIndex, entryIndex}) => {
                throw new Error('NewRecruitActions.SelectEntry not yet implemented');
            }
            """, new { forceIndex, entryIndex });
    }

    /// <summary>
    /// Select a child entry under an existing selection.
    /// </summary>
    public static async Task SelectChildEntryAsync(IPage page, int forceIndex, int selectionIndex, int childEntryIndex)
    {
        await page.EvaluateAsync("""
            ({forceIndex, selectionIndex, childEntryIndex}) => {
                throw new Error('NewRecruitActions.SelectChildEntry not yet implemented');
            }
            """, new { forceIndex, selectionIndex, childEntryIndex });
    }

    /// <summary>
    /// Deselect (remove) a selection by its index within the force.
    /// </summary>
    public static async Task DeselectSelectionAsync(IPage page, int forceIndex, int selectionIndex)
    {
        await page.EvaluateAsync("""
            ({forceIndex, selectionIndex}) => {
                throw new Error('NewRecruitActions.DeselectSelection not yet implemented');
            }
            """, new { forceIndex, selectionIndex });
    }

    /// <summary>
    /// Set the number of instances for a selection entry.
    /// </summary>
    public static async Task SetSelectionCountAsync(IPage page, int forceIndex, int entryIndex, int count)
    {
        await page.EvaluateAsync("""
            ({forceIndex, entryIndex, count}) => {
                throw new Error('NewRecruitActions.SetSelectionCount not yet implemented');
            }
            """, new { forceIndex, entryIndex, count });
    }

    /// <summary>
    /// Duplicate a selection within a force.
    /// </summary>
    public static async Task DuplicateSelectionAsync(IPage page, int forceIndex, int selectionIndex)
    {
        await page.EvaluateAsync("""
            ({forceIndex, selectionIndex}) => {
                throw new Error('NewRecruitActions.DuplicateSelection not yet implemented');
            }
            """, new { forceIndex, selectionIndex });
    }

    /// <summary>
    /// Set cost limit for a cost type.
    /// </summary>
    public static async Task SetCostLimitAsync(IPage page, string costTypeId, double value)
    {
        await page.EvaluateAsync("""
            ({costTypeId, value}) => {
                throw new Error('NewRecruitActions.SetCostLimit not yet implemented');
            }
            """, new { costTypeId, value });
    }
}
