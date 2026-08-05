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
        await NrUiTiming.MeasureAsync("load-gamedata/route-mysystems", () =>
            browser.NavigateToRouteAsync("/app/MySystems"));

        // Wait for MySystems to have actually rendered, rather than for 500ms to pass.
        //
        // "Add more games" is the control this method clicks two statements below, and it exists only
        // on this page — so its visibility IS "the route settled and painted", which is what the sleep
        // was standing in for. Measured at ~30ms against a flat 500ms, and strictly stronger: a slow
        // render satisfies this and did not satisfy the sleep.
        await NrUiTiming.MeasureAsync("load-gamedata/wait-mysystems-rendered", () =>
            page.GetByText("Add more games").First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }));

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
        await NrUiTiming.MeasureAsync("load-gamedata/inject-picker-mock", () =>
            InjectDirectoryPickerMockAsync(page, files));

        // Click "Add More Games" to open the install popup
        var addMoreGames = page.GetByText("Add more games");
        await NrUiTiming.MeasureAsync("load-gamedata/click-add-more-games", () =>
            addMoreGames.ClickAsync());

        // Click "Add From Folder" which triggers showDirectoryPicker()
        //
        // No wait between the two clicks. The 500ms that used to sit here was standing in for "the
        // install popup has rendered its options", and `ClickAsync` already waits for exactly that —
        // attached, visible, stable, receiving events — on the specific element it needs, for up to
        // 30s. The sleep could only ever be redundant with that wait or too short for it. At 363
        // specs it cost 3 minutes of lane time to be neither.
        var addFromFolder = page.GetByText("Add From Folder");
        await NrUiTiming.MeasureAsync("load-gamedata/click-add-from-folder", () =>
            addFromFolder.ClickAsync());

        // Wait for NR to finish loading (system appears marked as local)
        await NrUiTiming.MeasureAsync("load-gamedata/wait-local-library", () => page.WaitForFunctionAsync(
            """
            (systemId) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const sysStore = pinia?._s?.get('systemsStore');
                if (systemId) return !!sysStore?.localLibrary?.[systemId];
                return Object.keys(sysStore?.localLibrary || {}).length > 0;
            }
            """,
            systemId,
            new() { Timeout = 10_000 }));

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
        await NrUiTiming.MeasureAsync("load-gamedata/close-popup", async () =>
        {
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

                // Wait for it to be GONE rather than for 300ms to pass. The point of this block is
                // that nothing overlays the page afterwards, and that is exactly "hidden".
                //
                // Tolerated, not asserted, for the same reason the click above is: the only thing
                // this step owes the caller is that the popup is not in the way, and if it somehow
                // lingers, the next interaction's own auto-waiting reports it far better than a
                // setup-time throw would.
                try
                {
                    await closeBtn.WaitForAsync(
                        new() { State = WaitForSelectorState.Hidden, Timeout = 3_000 });
                }
                catch (TimeoutException)
                {
                    // Still there — let the next interaction speak for itself.
                }
            }
        });
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
        await NrUiTiming.MeasureAsync("create-roster/click-mylists", () => listsLink.ClickAsync());

        // Wait for the MyLists route, rather than for 500ms to pass.
        //
        // This one is not only about speed. The next locator is `a[href='#']` with the substring text
        // "New" — loose enough to match a control on the page being navigated AWAY from, and
        // `ClickAsync`'s auto-waiting cannot help with that: it protects against an element being
        // absent, never against the wrong element being present. Gating on the route first means the
        // click can only resolve against MyLists.
        await NrUiTiming.MeasureAsync("create-roster/wait-mylists-route", () => page.WaitForFunctionAsync(
            "() => location.pathname.includes('MyLists')",
            null,
            new() { Timeout = 15_000 }));

        // Click "New" to open Create List dialog (the nav link with href="#")
        var newBtn = page.Locator("a[href='#']", new() { HasTextString = "New" });
        await NrUiTiming.MeasureAsync("create-roster/click-new", () => newBtn.ClickAsync());

        var box = page.Locator(".box").First;

        // Select the preferred catalogue from the Faction dropdown
        var factionSelect = box.Locator("select").First;
        await NrUiTiming.MeasureAsync("create-roster/select-faction", async () =>
        {
            if (!string.IsNullOrEmpty(preferredCatalogueName))
            {
                // No wait for the dialog first. `SelectOptionAsync` auto-waits until an option
                // carrying this label exists, which is strictly STRONGER than the "the dialog has
                // rendered and populated its <select>" that the 1000ms here was standing in for — a
                // rendered dialog with an empty dropdown satisfies the sleep and not this. 361 of the
                // 363 lane specs take this branch, so the sleep cost 6 minutes to assert less.
                await factionSelect.SelectOptionAsync(new SelectOptionValue { Label = preferredCatalogueName });
            }
            else
            {
                // This branch has no such anchor: it wants "whatever the last option happens to be",
                // so there is no label to wait for. `CountAsync` is a snapshot, and an unpopulated
                // <select> reads as 0-or-1 options and silently selects NOTHING — a wrong roster
                // rather than an error. The fixed wait therefore stays, rather than being converted
                // into a condition this side cannot state precisely. Only the two gamesystem-only
                // specs reach it, so keeping it costs 2 seconds across the lane, not 6 minutes.
                await page.WaitForTimeoutAsync(1000);

                // Select the last option (typically the non-library catalogue)
                var optionCount = await factionSelect.Locator("option").CountAsync();
                if (optionCount > 1)
                {
                    await factionSelect.SelectOptionAsync(new SelectOptionValue { Index = optionCount - 1 });
                }
            }
        });

        // Wait for NR's two outcomes instead of timing them.
        //
        // This was a flat 1500ms — the largest number in the driver, 9 minutes across the lane — and
        // it was defended here as an irreducible NEGATIVE gate: it gave NR a chance to render "could
        // not be loaded" so a snapshot `IsVisibleAsync() == false` could be read as "loaded fine",
        // with no positive condition available to wait for instead. That was wrong twice over, and
        // it took three failed hypotheses to find out how.
        //
        // 1. The dialog IS positively observable. Polling `.box` every 20ms after the faction select:
        //    loadable settles at t=25ms with a "Create List" button and no error text; an empty
        //    catalogue settles at t=13ms with the error and NO buttons at all. NR renders the form
        //    only on a successful load, so the outcomes are mutually exclusive and both positive.
        //
        // 2. But waiting only for that BROKE 12 specs, so the sleep was covering something else too.
        //    Pinia, the component tree and the network all showed nothing moving between 50ms and
        //    3.2s, and building the roster at t=20ms produced a byte-identical army to building it at
        //    1500ms — on a FRESH browser. The isolated probe simply could not reproduce the lane,
        //    which runs specs sequentially through one shared browser.
        //
        // 3. Reproducing that (four specs, one browser) named it in one line:
        //       "force[0].name: expected Catalogue Detachment but got GS Detachment"
        //    NR was building the force from the GAME SYSTEM. The book object and the dialog are ready
        //    long before the catalogue is PARSED, and until it is, NR falls back to the game system's
        //    force entry. A breadth-first walk of the store graph found where that lands:
        //    `systemsStore._selectedSystem.manager.loadedCatalogues[<id>]`.
        //
        // So the real condition is a conjunction — the dialog is rendered AND the chosen catalogue is
        // parsed — and the 1500ms was a guess that happened to cover both. Waiting for them is also
        // strictly more correct than the sleep: it fixes the double duty the sleep did badly, since
        // `nameInput.IsVisibleAsync()` below is a snapshot, and a half-rendered dialog meant the
        // roster silently took NR's default name, which roster-name-and-metadata asserts against.
        var outcome = await NrUiTiming.MeasureAsync("create-roster/wait-catalogue-outcome", async () =>
        {
            var handle = await page.WaitForFunctionAsync(
                """
                (wantName) => {
                    const box = document.querySelector('.box');
                    if (!box) { return null; }
                    if (/could not be loaded/i.test(box.innerText || '')) { return 'error'; }

                    // The dialog must be rendered far enough to have its Create List button (and
                    // therefore its name input — see below).
                    const hasCreate = [...box.querySelectorAll('button')]
                        .some(b => /create list/i.test(b.textContent || ''));
                    if (!hasCreate) { return null; }

                    // ...AND the chosen catalogue must actually be PARSED, which is a separate event.
                    //
                    // `manager.loadedCatalogues[id]` is NOT that signal, though it looks like it:
                    // it is the shallow entry NR creates when the SYSTEM is installed, so it is
                    // already true before the faction is even chosen. Waiting on it is vacuous, and
                    // measurably so — it changed nothing.
                    //
                    // The parse lands as `manager.catalogueFiles[id].catalogue`, carrying the
                    // forceEntries/forces/gameSystem the roster is built from, and the shallow entry
                    // is REMOVED in the same tick. Until it arrives NR builds the force from the game
                    // system instead of the catalogue.
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const files = pinia?._s?.get('systemsStore')?._selectedSystem?.manager?.catalogueFiles;
                    if (!files) { return null; }

                    const parsed = Object.values(files)
                        .map(f => f && f.catalogue)
                        .filter(c => c && c.book && c.gameSystem);
                    if (parsed.length === 0) { return null; }

                    // When a specific faction was chosen, wait for THAT one rather than for any
                    // catalogue: with several loaded, "some catalogue is parsed" is satisfied by the
                    // wrong one, which is precisely the multi-catalogue bug class this driver has
                    // already been bitten by twice.
                    if (wantName && !parsed.some(c => (c.book.name ?? c.name) === wantName)) { return null; }
                    return 'ready';
                }
                """,
                preferredCatalogueName,
                new() { Timeout = 15_000 });
            return await handle.JsonValueAsync<string>();
        });

        // ...and then, for now, still wait.
        //
        // This 1500ms is NOT what the comment above it used to claim, and it is not what three
        // successive replacements assumed either. Every one of them was falsified by measurement, so
        // the sequence is recorded here to stop the next attempt repeating it:
        //
        //   "it gives the error a chance to render"    — false: both outcomes are positively
        //                                                observable (see the race above), and the
        //                                                dialog settles by t=25ms.
        //   "it waits for the catalogue to load"       — false: building the roster at t=20ms yields
        //                                                a byte-identical army on a fresh browser.
        //   "it waits for manager.loadedCatalogues"    — false: that is populated when the SYSTEM is
        //                                                installed, so the condition is vacuous.
        //   "it waits for catalogueFiles[id].catalogue"— false: also already present; still 12 fails.
        //
        // Snapshotting the state at the race and again 1500ms later, in the SEQUENTIAL context where
        // the failure actually lives, leaves exactly one field moving — `manager.loadedCatalogues`
        // draining to empty — and one telling piece of dialog text on the specs that fail:
        //
        //   "Create List | Faction … | List Name | Force  GS Detachment  Catalogue Detachment"
        //
        // When the game system AND the catalogue both define force entries, NR renders a FORCE
        // dropdown, and this driver never touches it — so NR creates whichever the dropdown defaults
        // to. Draining `loadedCatalogues` changes that default. That is why the sleep "works", and it
        // is why every state-based replacement failed: the thing being waited for is not a load at
        // all, it is a re-render that flips a control the driver should be setting explicitly.
        //
        // So this is a driver gap wearing a timing costume, and the fix is to select the intended
        // force in that dropdown rather than to wait for NR to guess right. That is a behaviour
        // change with its own blast radius (it interacts with AddForceCoreAsync's reconciliation of
        // NR's auto-created force), so it is deliberately NOT bundled into a performance change.
        // Until then the sleep stays: 9 minutes of lane time is a bad trade for 12 wrong answers.
        await NrUiTiming.MeasureAsync("create-roster/sleep-force-dropdown-default", () =>
            page.WaitForTimeoutAsync(1500));

        // NR refuses to build a list from a catalogue it cannot load. Report the observation and the
        // causes actually known, rather than asserting one that was never checked.
        //
        // This used to read "NR does not support creating rosters from library catalogues", which was
        // a guess and, for every spec that hits it, wrong: measured across 56 roster specs, all five
        // failures here were specs whose catalogue is EMPTY, and not one of them declares `library`.
        //
        // The criterion first recorded here was "empty catalogue AND empty game system", on the
        // strength of gamesystem/gamesystem-root-selectionentry passing with an empty catalogue. That
        // is too loose, and the wider lane disproved it: ordering/ordering-nested-forces HAS game
        // system content (nested forceEntries) and still fails. The threshold is at least one
        // SELECTABLE entry — selectionEntries or entryLinks — anywhere NR can reach; force and
        // category entries do not count. force/force-with-categories confirms it from the other side:
        // categoryEntries plus forceEntries, and it fails.
        if (outcome == "error")
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
        await NrUiTiming.MeasureAsync("create-roster/fill-name", async () =>
        {
            var nameInput = box.Locator("input[type='text'], input:not([type])").First;
            if (await nameInput.IsVisibleAsync())
            {
                await nameInput.FillAsync(rosterName);
            }
        });

        // Click "Create List" button
        var createBtn = box.GetByRole(AriaRole.Button, new() { Name = "Create List" });
        await NrUiTiming.MeasureAsync("create-roster/click-create", () => createBtn.ClickAsync());

        // Wait for NR to actually build the list, rather than guessing how long that takes.
        //
        // This was a flat 2000ms — the single most expensive line in the lane (2s x 363 specs = 12
        // minutes) and, more importantly, a silent-failure risk rather than merely a slow one. The
        // evaluate below is a ONE-SHOT SNAPSHOT: it returns null when `currentList.army` is not ready
        // yet, and on that path `window.__bsspec` is never created at all. Nothing downstream repairs
        // that — WaitForEditorLoadedAsync, three lines later in the only caller, guards its sync with
        // `if (window.__bsspec && ...)`, so it UPDATES an existing handle and never makes one. Since
        // __bsspec is the state reader's only source, a create that ran slow produced a roster that
        // nothing could read, at whatever distance from here the first read happened to be.
        //
        // The condition below is the one WaitForEditorLoadedAsync already waits for, so this is not a
        // new assumption about NR's timing. It is the assumption that was already being made — made
        // earlier, explicitly, and where its failure can still be attributed to roster creation.
        await NrUiTiming.MeasureAsync("create-roster/wait-army", () => page.WaitForFunctionAsync(
            """
            () => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                return pinia?._s?.get('lists')?.currentList?.army != null;
            }
            """,
            null,
            new() { Timeout = 30_000 }));

        // After creation, set up __bsspec for state reading
        var listKey = await NrUiTiming.MeasureAsync("create-roster/eval-bsspec", () => page.EvaluateAsync<string?>("""
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
            """));

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
