using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// Helpers for the setup phase of NrRosterUiEngine:
///   1. Loading game data files into NR via the loadSystemFromFs Pinia store API
///      (NR's web version has no file-upload UI for custom game data).
///   2. Creating a new roster/list via the NR UI (JS fallback for now).
/// </summary>
public static class NrUiSetup
{
    /// <summary>
    /// Load game data XML files into NR via the loadSystemFromFs Pinia store API.
    /// NR's public web version at newrecruit.eu has no file-upload UI for custom data
    /// (MySystems only has "Update All" for its own hosted systems), so we use JS.
    /// </summary>
    public static async Task LoadGameDataAsync(
        NewRecruitBrowser browser,
        IReadOnlyList<(string FileName, string Content)> files,
        string? systemId)
    {
        var page = browser.Page;

        var fileData = files.Select(f => new { name = f.FileName, path = $"/spec/{f.FileName}", data = f.Content }).ToArray();

        var result = await page.EvaluateAsync<string?>("""
            async ([fileData, systemId]) => {
                try {
                    const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return 'ERROR:Pinia not found';
                    const sysStore = pinia._s.get('systemsStore');
                    if (!sysStore) return 'ERROR:systemsStore not found';

                    // Clear any previously loaded local system with the same id
                    if (systemId && sysStore.localLibrary?.[systemId])
                        delete sysStore.localLibrary[systemId];

                    const files = fileData.map(f => ({ name: f.name, path: f.path, data: f.data }));
                    await sysStore.loadSystemFromFs(files);
                    return null;
                } catch(e) {
                    return 'ERROR:' + e.message;
                }
            }
            """, new object?[] { fileData, systemId });

        if (result?.StartsWith("ERROR:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException($"LoadGameData failed: {result[6..]}");
        }
    }

    /// <summary>
    /// Creates a new roster in NR. Currently uses JS fallback (same as NewRecruitRosterEngine)
    /// until the /app/AddList UI flow is fully mapped via probe.
    /// </summary>
    public static async Task<string?> CreateRosterAsync(
        IPage page,
        string rosterName,
        ProtocolGameSystem? gameSystem)
    {
        return await CreateRosterViaJsAsync(page, rosterName, gameSystem);
    }

    /// <summary>
    /// Creates the roster via the Pinia store JS API (same as NewRecruitRosterEngine).
    /// Remove and replace with UI-driven flow once /app/AddList is probed.
    /// </summary>
    private static async Task<string?> CreateRosterViaJsAsync(
        IPage page,
        string rosterName,
        ProtocolGameSystem? gameSystem)
    {
        var systemId = gameSystem?.Id;

        var result = await page.EvaluateAsync<string?>("""
            async ([systemId, rosterName]) => {
                try {
                    const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return 'ERROR:Pinia not found';
                    const sysStore = pinia._s.get('systemsStore');
                    const listsStore = pinia._s.get('lists');
                    if (!sysStore || !listsStore) return 'ERROR:Required stores not found';

                    const localSys = systemId ? sysStore.localLibrary[systemId]
                        : Object.values(sysStore.localLibrary || {})[0];
                    if (!localSys) return 'ERROR:System not found in localLibrary';

                    sysStore.selectSystem(localSys);
                    const sys = sysStore._selectedSystem;
                    if (!sys) return 'ERROR:No selected system after selectSystem()';

                    const playableBooks = sys.books?.array?.filter(b => b.playable) || [];
                    if (!playableBooks.length) return 'ERROR:No playable books';

                    const pb = playableBooks[0];
                    const bd = await sys.getBook(pb.id);
                    if (!bd) return 'ERROR:Failed to load book';

                    bd.catalogue.costIndex = {};
                    const gs = bd.catalogue.gameSystem;
                    if (gs?.costTypes) {
                        for (const ct of gs.costTypes)
                            bd.catalogue.costIndex[ct.id] = ct;
                    }

                    const costs = bd.getCosts();
                    const roster = bd.createRoster(costs);
                    if (!roster) return 'ERROR:Failed to create roster';
                    roster.setCustomName(rosterName);

                    const maxCosts = roster.getMaxCosts?.() || [];
                    if (maxCosts.length > 0) {
                        roster.setMaxCosts(maxCosts.map(c => ({
                            ...c, value: c.defaultCostLimit >= 0 ? c.defaultCostLimit : -1
                        })));
                    }

                    const autoForces = roster.getForces?.() || [];
                    for (const f of [...autoForces]) {
                        if (typeof f.delete === 'function') f.delete();
                    }

                    const listKey = 'nrui_' + Date.now();
                    const row = {
                        list_key: listKey,
                        name: rosterName,
                        id_game_system: pb.id_game_system || sys.id,
                        id_system: pb.id || sys.id,
                        id_book: pb.id,  // required by getBookFromListRow for editor navigation
                        nrversion: pb.nrversion,
                        date_mod: new Date(), date_create: new Date(),
                        synced: false, uid: null,
                        bsid_book: pb.bsid, bsid_system: sys.bsid
                    };

                    // addList expects a live roster instance (doSaveList calls army.getPointsCost).
                    // After addList, serialize the army in the stored row so that loadList can
                    // call loadRosterFromJson on it without failing (class instance would hang).
                    await listsStore.addList({ row, army: roster, book: bd });
                    const storedRow = listsStore.lists?.find(l => l.list_key === listKey);
                    if (storedRow?.army && typeof storedRow.army.toJson === 'function') {
                        storedRow.army = storedRow.army.toJson();
                    }

                    window.__bsspec = {
                        army: roster, book: bd,
                        books: [bd], bookCatalogueIds: [pb.bsid || ''], row
                    };

                    return listKey;
                } catch(e) {
                    return 'ERROR:' + e.message + '\n' + e.stack;
                }
            }
            """, new object?[] { systemId, rosterName });

        if (result?.StartsWith("ERROR:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException($"CreateRoster failed: {result[6..]}");
        }

        return result;
    }

    /// <summary>
    /// Waits for the roster editor to fully load after navigating to /app/Lists/{listKey}.
    /// Once loaded, syncs window.__bsspec.army to currentList.army (the re-hydrated roster).
    /// </summary>
    public static async Task WaitForEditorLoadedAsync(IPage page, int timeoutMs = 30_000)
    {
        // Wait for the editor to have both book and army loaded
        await page.WaitForFunctionAsync(
            """
            () => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const ls = pinia?._s?.get('lists');
                return ls?.currentList?.book != null && ls?.currentList?.army != null;
            }
            """,
            null,
            new() { Timeout = timeoutMs });

        // Sync __bsspec.army to the re-hydrated roster from the editor
        await page.EvaluateAsync("""
            () => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const ls = pinia?._s?.get('lists');
                if (window.__bsspec && ls?.currentList?.army) {
                    window.__bsspec.army = ls.currentList.army;
                    window.__bsspec.book = ls.currentList.book;
                }
            }
            """);
    }
}
