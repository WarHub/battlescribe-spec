using BattleScribeSpec.NewRecruit;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// Helpers for the setup phase of NrRosterUiEngine:
///   1. Loading game data files into NR via the "Add From Folder" UI flow
///      (mocking showDirectoryPicker so NR handles its full init pipeline).
///   2. Creating a new roster/list via the NR "Create List" UI dialog.
/// </summary>
public static class NrUiSetup
{
    /// <summary>
    /// Load game data XML files into NR via its native "Add From Folder" UI flow.
    /// Injects a directory picker mock, then navigates to MySystems → "Add More Games"
    /// → "Add From Folder" so NR handles the full loading pipeline itself.
    /// </summary>
    public static async Task LoadGameDataAsync(
        NewRecruitBrowser browser,
        IReadOnlyList<(string FileName, string Content)> files,
        string? systemId)
    {
        var page = browser.Page;

        // Navigate to the MySystems (game library) page.
        //
        // This used to click `a[href*='MySystems']` — NR's "Home" navbar link. That link was removed
        // from the app navbar between client v34.93 and v35.12, so the click became a 30s timeout the
        // moment testdata.json's HAR tag was bumped (#301): "Setup failed: TimeoutException ... waiting
        // for Locator("a[href*='MySystems']")". The route itself never changed — /app still redirects
        // to /app/MySystems, and the link was `router-link-exact-active` on arrival, i.e. the click was
        // already a no-op navigation. So ask the router for the route rather than depending on a nav
        // control NR is free to restyle or drop.
        await browser.NavigateToRouteAsync("/app/MySystems");
        await page.WaitForTimeoutAsync(500);

        // Inject the directory picker mock — AFTER the navigation above, not before it.
        //
        // It used to be the first thing this method did, which put an `evaluate` immediately before a
        // navigation and immediately after the previous spec's cleanup navigation. Both are hazards:
        // the mock is installed into a context the next line is about to change, and the evaluate can
        // land while a route change is still settling, which Playwright reports as "Execution context
        // was destroyed, most likely because of a navigation".
        //
        // Measured: running 56 specs sequentially through one shared browser (the widened NR-UI
        // roster lane) hit it once in two runs — invisible to `bs-spec run`, which gives every spec
        // its own engine, and invisible to the old one-spec lane, which never had a previous spec to
        // race with. The mock is only read when "Add From Folder" is clicked below, so installing it
        // here is both safe and sufficient.
        await InjectDirectoryPickerMockAsync(page, files);

        // Click "Add More Games" to open the install popup
        var addMoreGames = page.GetByText("Add more games");
        await addMoreGames.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Click "Add From Folder" which triggers showDirectoryPicker()
        var addFromFolder = page.GetByText("Add From Folder");
        await addFromFolder.ClickAsync();

        // Wait for NR to finish loading (system appears marked as local)
        await page.WaitForFunctionAsync(
            """
            (systemId) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const sysStore = pinia?._s?.get('systemsStore');
                if (systemId) return !!sysStore?.localLibrary?.[systemId];
                return Object.keys(sysStore?.localLibrary || {}).length > 0;
            }
            """,
            systemId,
            new() { Timeout = 10_000 });

        // Close the "Add More Games" popup that's still open.
        //
        // `IsVisibleAsync` is a SNAPSHOT, not a wait, so this is check-then-act: if the popup closes
        // itself between the check and the click — which it does once NR finishes installing the
        // system — the click has nothing to hit and burns Playwright's full 30s default, failing
        // Setup outright. Bounded and tolerated instead, because "the popup already went away" is a
        // success for this step, not a failure.
        //
        // Found by the widened sequential lane (force/force-remove-second,
        // selection/selection-with-children), and only there: `bs-spec run` gives every spec its own
        // engine, so its timing never lined up this way.
        var closeBtn = page.Locator(".xCross").First;
        if (await closeBtn.IsVisibleAsync())
        {
            try
            {
                await closeBtn.ClickAsync(new() { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                // Closed itself between the check and the click — which is what we wanted anyway.
            }

            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>
    /// Injects a mock for <c>window.showDirectoryPicker</c> that returns a fake
    /// <c>FileSystemDirectoryHandle</c> containing the provided XML file data.
    /// <para>
    /// When NR's "Load from Folder" button is clicked, it calls <c>showDirectoryPicker()</c>,
    /// iterates the entries, reads file contents, and passes them to <c>loadSystemFromFs</c>.
    /// This mock intercepts that call and returns our spec data without needing real
    /// filesystem permissions (which Playwright cannot grant for the File System Access API).
    /// </para>
    /// <para>
    /// The mock is one-shot: after the first call it restores the original (or removes the mock),
    /// preventing interference with subsequent tests.
    /// </para>
    /// </summary>
    public static async Task InjectDirectoryPickerMockAsync(
        IPage page,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        var fileData = files.Select(f => new { name = f.FileName, content = f.Content }).ToArray();

        await page.EvaluateAsync("""
            (fileData) => {
                const originalPicker = window.showDirectoryPicker;

                window.showDirectoryPicker = async () => {
                    // Restore original after first use (one-shot mock)
                    if (originalPicker) {
                        window.showDirectoryPicker = originalPicker;
                    } else {
                        delete window.showDirectoryPicker;
                    }

                    // Build fake File objects
                    const fakeFiles = fileData.map(f => {
                        const blob = new Blob([f.content], { type: 'application/xml' });
                        return new File([blob], f.name, { type: 'application/xml' });
                    });

                    // Build fake FileSystemFileHandle objects
                    const fileHandles = fakeFiles.map(file => ({
                        kind: 'file',
                        name: file.name,
                        getFile: async () => file,
                    }));

                    // Return a fake FileSystemDirectoryHandle
                    return {
                        kind: 'directory',
                        name: 'spec-data',
                        values: async function* () {
                            for (const handle of fileHandles) {
                                yield handle;
                            }
                        },
                        entries: async function* () {
                            for (const handle of fileHandles) {
                                yield [handle.name, handle];
                            }
                        },
                        keys: async function* () {
                            for (const handle of fileHandles) {
                                yield handle.name;
                            }
                        },
                        getFileHandle: async (name) => {
                            const h = fileHandles.find(fh => fh.name === name);
                            if (!h) throw new DOMException('File not found: ' + name, 'NotFoundError');
                            return h;
                        },
                    };
                };
            }
            """, fileData);
    }

    /// <summary>
    /// Creates a new roster in NR via the Create List UI dialog.
    /// Flow: navigates to MyLists → clicks "New" → selects catalogue → clicks "Create List".
    /// </summary>
    public static async Task<string?> CreateRosterAsync(
        IPage page,
        string rosterName,
        string? preferredCatalogueName = null)
    {
        // Navigate to MyLists
        var listsLink = page.Locator("a[href*='MyLists']").First;
        await listsLink.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Click "New" to open Create List dialog (the nav link with href="#")
        var newBtn = page.Locator("a[href='#']", new() { HasTextString = "New" });
        await newBtn.ClickAsync();
        await page.WaitForTimeoutAsync(1000);

        var box = page.Locator(".box").First;

        // Select the preferred catalogue from the Faction dropdown
        var factionSelect = box.Locator("select").First;
        if (!string.IsNullOrEmpty(preferredCatalogueName))
        {
            await factionSelect.SelectOptionAsync(new SelectOptionValue { Label = preferredCatalogueName });
        }
        else
        {
            // Select the last option (typically the non-library catalogue)
            var optionCount = await factionSelect.Locator("option").CountAsync();
            if (optionCount > 1)
            {
                await factionSelect.SelectOptionAsync(new SelectOptionValue { Index = optionCount - 1 });
            }
        }

        await page.WaitForTimeoutAsync(1500);

        // NR refuses to build a list from a catalogue it cannot load. Report the observation and the
        // causes actually known, rather than asserting one that was never checked.
        //
        // This used to read "NR does not support creating rosters from library catalogues", which was
        // a guess and, for every spec that hits it, wrong: measured across 56 roster specs, all five
        // failures here were specs whose catalogue is EMPTY, and not one of them declares `library`.
        // The distinguishing case proves the mechanism rather than muddying it —
        // gamesystem/gamesystem-root-selectionentry also has an empty catalogue and does NOT fail,
        // because its content lives in the gameSystem, so NR has something to load.
        var errorVisible = await box.Locator("text=could not be loaded").IsVisibleAsync();
        if (errorVisible)
        {
            throw new InvalidOperationException(
                $"Create List dialog shows 'could not be loaded' for catalogue '{preferredCatalogueName}'. " +
                "NR's Create List dialog needs a catalogue with loadable content: this is what an empty " +
                "catalogue looks like (no selectionEntries/entryLinks/etc., and none in the gameSystem " +
                "either), and it is also what a library catalogue looks like. The store-direct " +
                "`newrecruit` engine builds these rosters fine — it is the UI dialog that refuses, so a " +
                "spec failing only here is an NR-UI limitation, not a data error.");
        }

        // Set list name
        var nameInput = box.Locator("input[type='text'], input:not([type])").First;
        if (await nameInput.IsVisibleAsync())
        {
            await nameInput.FillAsync(rosterName);
        }

        // Click "Create List" button
        var createBtn = box.GetByRole(AriaRole.Button, new() { Name = "Create List" });
        await createBtn.ClickAsync();
        await page.WaitForTimeoutAsync(2000);

        // After creation, set up __bsspec for state reading
        var listKey = await page.EvaluateAsync<string?>("""
            () => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const ls = pinia?._s?.get('lists');
                    const cl = ls?.currentList;
                    if (!cl?.army) return null;

                    window.__bsspec = {
                        army: cl.army,
                        book: cl.book,
                        row: cl.row,
                    };

                    return cl.row?.list_key || 'ui-created';
                } catch(e) {
                    return null;
                }
            }
            """);

        return listKey;
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
                    window.__bsspec.row = ls.currentList.row;
                }
            }
            """);
    }

    /// <summary>
    /// Bypasses NR's supporter/premium paywall by setting a fake user with supporter:true.
    /// This enables Custom Names/Notes editing in the UI which is otherwise locked behind a paywall.
    /// Call once after the editor is loaded.
    /// </summary>
    public static async Task BypassSupporterPaywallAsync(IPage page)
    {
        await page.EvaluateAsync("""
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const userStore = pinia?._s?.get('userStore');
                if (userStore) {
                    userStore.user = { supporter: true, name: 'SpecTest', _id: 'spec-test-supporter' };
                }
            }
            """);
    }
}
