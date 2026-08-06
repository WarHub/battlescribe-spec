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
        // Install the system from MySystems, retrying the WHOLE sequence if NR navigates away.
        //
        // The three steps below (be on MySystems, open the install popup, choose Add From Folder)
        // are one unit of work, and the page can be taken out from under any of them: the previous
        // spec's navigation is still in flight — its CreateRosterAsync clicked the MyLists nav link —
        // and when it lands it takes this page with it. The controls used here exist only on
        // MySystems, so they were visible, then gone.
        //
        // That was misread twice, as an animating element and then as a re-render, and "fixed" twice
        // by guarding a single step. Guarding the WAIT was not enough precisely because the drift
        // happens after it: the wait passes, and the click a moment later is on MyLists. Only the
        // failure message printing page.Url settled what was actually happening.
        //
        // Retried rather than waited out. Losing a race to another navigation is fixed by asserting
        // the route again, not by more patience — and only when the page really has drifted, so a
        // genuine failure still fails on its first attempt instead of three times as slowly.
        var addMoreGames = page.GetByText("Add more games").First;
        var addFromFolder = page.GetByText("Add From Folder").First;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await NrUiTiming.MeasureAsync("load-gamedata/wait-mysystems-rendered", async () =>
                {
                    await page.WaitForFunctionAsync(
                        "() => location.pathname.includes('MySystems')",
                        null,
                        new() { Timeout = 10_000 });
                    await addMoreGames.WaitForAsync(
                        new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                    await WaitForTransitionsAsync(page);
                });

                // Inject the directory picker mock — AFTER the navigation, not before it.
                //
                // It used to be the first thing this method did, which put an `evaluate` immediately
                // before a navigation and immediately after the previous spec's cleanup navigation.
                // Both are hazards: the mock is installed into a context the next line is about to
                // change, and the evaluate can land while a route change is still settling, which
                // Playwright reports as "Execution context was destroyed".
                //
                // It also has to be re-injected per attempt: a navigation clears it.
                await NrUiTiming.MeasureAsync("load-gamedata/inject-picker-mock", () =>
                    InjectDirectoryPickerMockAsync(page, files));

                await NrUiTiming.MeasureAsync("load-gamedata/click-add-more-games", () =>
                    ClickWhenReadyAsync(page, addMoreGames, "Add more games"));

                // Triggers showDirectoryPicker(). No wait before it: the 500ms that used to sit here
                // stood in for "the popup has rendered its options", which ClickAsync already waits
                // for — on the specific element, with actionability — so the sleep could only be
                // redundant or too short.
                await NrUiTiming.MeasureAsync("load-gamedata/click-add-from-folder", () =>
                    ClickWhenReadyAsync(page, addFromFolder, "Add From Folder"));

                break;
            }
            catch (Exception ex) when (attempt < 3 && ex is TimeoutException or InvalidOperationException)
            {
                var path = await page.EvaluateAsync<string>("() => location.pathname");
                if (path.Contains("MySystems", StringComparison.Ordinal))
                {
                    throw; // Still on the right page — this is a real failure, not a lost race.
                }

                Console.Error.WriteLine(
                    $"[nr-ui] navigated to '{path}' mid-install (attempt {attempt}) — returning to "
                    + "MySystems and starting the sequence again.");
                await browser.NavigateToRouteAsync("/app/MySystems");
            }
        }

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
        string? preferredCatalogueName = null,
        string? preferredForceEntryId = null)
    {
        // Navigate to MyLists
        var listsLink = page.Locator("a[href*='MyLists']").First;
        await NrUiTiming.MeasureAsync("create-roster/click-mylists", () =>
            ClickWhenReadyAsync(page, listsLink, "MyLists nav link"));

        // Wait for the MyLists route, rather than for 500ms to pass.
        //
        // This one is not only about speed. The next locator is `a[href='#']` with the substring text
        // "New" — loose enough to match a control on the page being navigated AWAY from, and
        // `ClickAsync`'s auto-waiting cannot help with that: it protects against an element being
        // absent, never against the wrong element being present. Gating on the route first means the
        // click can only resolve against MyLists.
        await NrUiTiming.MeasureAsync("create-roster/wait-mylists-route", async () =>
        {
            await page.WaitForFunctionAsync(
                "() => location.pathname.includes('MyLists')",
                null,
                new() { Timeout = 15_000 });
            await WaitForTransitionsAsync(page);
        });

        // Click "New" to open Create List dialog (the nav link with href="#")
        var newBtn = page.Locator("a[href='#']", new() { HasTextString = "New" });
        await NrUiTiming.MeasureAsync("create-roster/click-new", () =>
            ClickWhenReadyAsync(page, newBtn, "New list button"));

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
                // This branch wants "whatever the last option happens to be", so there is no label
                // to wait for — but there is still a condition: the <select> has been POPULATED.
                // `CountAsync` is a snapshot, and an unpopulated select reads as 0-or-1 options and
                // silently selects NOTHING, which is a wrong roster rather than an error. Waiting
                // for the dialog to render its options states that precisely; the 1000ms here only
                // hoped for it.
                //
                // Tolerated, because a single-catalogue system legitimately offers one option and
                // would otherwise wait out the bound for nothing.
                try
                {
                    await page.WaitForFunctionAsync(
                        """
                        () => {
                            const box = document.querySelector('.box');
                            const sel = box?.querySelector('select');
                            return !!sel && sel.options.length > 1;
                        }
                        """,
                        null,
                        new() { Timeout = 5_000 });
                }
                catch (TimeoutException)
                {
                    // One option (or none) is a real shape here — fall through and let the count
                    // check below decide, exactly as it did before.
                }

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

        // Tell NR which force to build, instead of waiting for it to default to the right one.
        //
        // NR renders a FORCE dropdown next to the faction one whenever the roster could start from
        // more than one force entry. This driver never set it, so the roster began life with NR's
        // default — and a flat 1500ms here was what made that default correct, because draining
        // `manager.loadedCatalogues` re-renders the dropdown and flips it. 1.5s x 363 specs = 9
        // minutes spent nudging a control rather than setting it.
        //
        // The option carries the force ENTRY ID as its bound value, which is the whole fix:
        //
        //   select#0  opt _value = {id: 'cat-1', name: …}   <- faction, binds a catalogue OBJECT
        //   select#1  opt _value = 'fe-gs' / 'fe-cat'        <- force,   binds the entry id STRING
        //
        // So the control identifies itself by what it carries: a string equal to the requested entry
        // id can only be a force option, never the faction one. No position, no label, and no name —
        // names are ambiguous by design here (force-multi-catalogue-two-forces has two "Patrol"
        // forces), which is why the previous attempt at this failed.
        //
        // That attempt is worth recording because its two mistakes are easy to repeat. It matched
        // option TEXT and required the select to be VISIBLE. But the force select always exists —
        // NR just hides it when there is only one option — so the visibility filter skipped exactly
        // the specs that had nothing to choose, while the text match wandered onto other controls.
        // Measured at both t=0 and t+1500ms, the option list is identical; only the default differs.
        if (!string.IsNullOrEmpty(preferredForceEntryId))
        {
            await NrUiTiming.MeasureAsync("create-roster/select-force", async () =>
            {
                var found = await page.EvaluateAsync<int[]>(
                    """
                    (wantEntryId) => {
                        const box = document.querySelector('.box');
                        if (!box) { return [-1, -1, 0]; }
                        const sels = [...box.querySelectorAll('select')];
                        for (let i = 0; i < sels.length; i++) {
                            const opts = [...sels[i].options];
                            // Vue 3 stashes a non-string v-model value on the element as `_value`;
                            // for force options it is the entry id itself.
                            const j = opts.findIndex(o => o._value === wantEntryId);
                            if (j >= 0) { return [i, j, sels[i].selectedIndex]; }
                        }
                        return [-1, -1, 0];
                    }
                    """,
                    preferredForceEntryId);

                if (found is not [var selectIndex, var optionIndex, var selectedIndex]
                    || selectIndex < 0)
                {
                    // No option carries this id. The ordinary case for a spec whose force lives only
                    // in the game system and is the sole choice — NR then has nothing to get wrong.
                    return;
                }

                if (optionIndex == selectedIndex)
                {
                    // Already what NR picked. Skip rather than re-select: a redundant change event
                    // makes NR rebuild the dialog for no reason.
                    return;
                }

                // Set it through the DOM and announce it, rather than through Playwright's
                // SelectOptionAsync. That call needs the control to be visible and actionable, and
                // this one legitimately is not when NR collapses a single-choice dropdown — the very
                // case the previous attempt filtered out and then mis-handled. Vue's v-model listens
                // for `change`, so dispatching it is what "the user picked this" means here.
                await page.EvaluateAsync(
                    """
                    ([selectIndex, optionIndex]) => {
                        const box = document.querySelector('.box');
                        const sel = [...box.querySelectorAll('select')][selectIndex];
                        sel.selectedIndex = optionIndex;
                        sel.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    """,
                    new[] { selectIndex, optionIndex });
            });
        }

        // Let NR finish parsing the catalogue BEFORE the list is created.
        //
        // Position matters more than duration here, which cost a lane run to learn: the same wait
        // placed AFTER creation left 63 specs failing, because a list built from a half-parsed
        // catalogue is wired to incomplete data and no later waiting repairs it. The '+' clicks that
        // then get discarded are a symptom of the roster, not of the click.
        //
        // This replaces a flat 1500ms and costs about the same — measured avg 1467ms — because the
        // 1500ms was never padding: it is how long NR's parse actually takes. That is the honest
        // answer to "why is this lane slow here", and it is worth stating plainly so nobody spends
        // another day trying to tune it away. The condition still earns its place over the constant:
        // it adapts when the parse is quick, and it does not under-wait the specs that need longer,
        // which the constant did (measured max 3380ms, more than twice the old sleep).
        await WaitForCatalogueWorkSettledAsync(page);

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
    /// Waits out NR's page transitions, so a click is not aimed at a moving target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Playwright refuses to click an element whose bounding box is still changing, and reports it
    /// as <c>element is not stable</c> after burning its full 30s default. That is exactly what 11
    /// of 363 specs did once the fixed sleeps came out of this file: the sleeps had been sitting
    /// through NR's route transitions, so nothing ever clicked mid-animation. The clicks were not
    /// blocked by an overlay and were not too early in any state sense — the target was moving.
    /// </para>
    /// <para>
    /// Running animations are the precise condition, so wait for those rather than for a duration.
    /// Infinite ones are excluded deliberately: a spinner never ends, and waiting for it would
    /// convert a 30s flake into a guaranteed hang.
    /// </para>
    /// <para>
    /// Tolerated rather than asserted. If something does animate forever, the click's own
    /// actionability check is still there and still reports it — this is a way to arrive at a good
    /// moment, not another thing that can fail setup.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Clicks <paramref name="locator"/>, falling back to a dispatched event if NR will not hold it
    /// still.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Playwright will not click an element whose bounding box is still changing, and says so —
    /// <c>element is not stable</c> — after burning its full 30s default. Waiting for CSS animations
    /// to finish (<see cref="WaitForTransitionsAsync"/>) removed most of it, 11 failing specs down to
    /// 3, but not all: the remaining movement is LAYOUT REFLOW as NR's lists fill in, which runs no
    /// animation and so is invisible to <c>document.getAnimations()</c>.
    /// </para>
    /// <para>
    /// A forced click is the obvious next step and the wrong one: <c>Force = true</c> still clicks a
    /// screen COORDINATE, so against a moving target it lands wherever the element used to be.
    /// Dispatching the event to the element instead removes position from the question entirely —
    /// the handler runs on the node we resolved, whatever it is doing on screen.
    /// </para>
    /// <para>
    /// The normal path stays first and stays honest: a real click, with its full actionability
    /// checks, just bounded to 8s so a moving target costs seconds rather than half a minute. The
    /// dispatch is the fallback, not the default, because a real click is what a user does and it
    /// catches things a synthetic event cannot — an element covered by an overlay, for one.
    /// </para>
    /// </remarks>
    private static async Task ClickWhenReadyAsync(IPage page, ILocator locator, string what)
    {
        await WaitForTransitionsAsync(page);
        try
        {
            await locator.ClickAsync(new() { Timeout = 8_000 });
            return;
        }
        catch (TimeoutException)
        {
            // Falls through to the dispatch below.
        }

        // Re-establish the element before dispatching.
        //
        // A click can fail here for two different reasons and they need different handling. Most are
        // a MOVING target — NR reflowing — and a dispatch fixes those, since it delivers the event to
        // the node rather than to a screen coordinate. But three specs failed the dispatch too, which
        // a moving element cannot cause: dispatch only needs the node ATTACHED. Those had lost it
        // entirely between the visibility wait above and this click, because NR re-rendered the page
        // in between.
        //
        // So wait for it again rather than assuming it is still there. Locators re-resolve, so if NR
        // put the control back this finds it; the earlier version went straight to dispatch and
        // reported a 5s timeout whose message blamed reflow — a cause it had never checked.
        try
        {
            await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"NR UI: '{what}' did not come back after the click failed — it was visible a moment "
                + $"earlier and is now gone for 10s (page: {page.Url}). This is NR re-rendering the "
                + "page out from under the step, not a slow animation.");
        }

        Console.Error.WriteLine(
            $"[nr-ui] '{what}' would not hold still for a real click within 8s — dispatching the "
            + "event directly.");

        // Bounded too. DispatchEventAsync carries the same 30s default as ClickAsync, so leaving it
        // open turned an element that was genuinely absent into 8s + 30s = 38s of waiting rather
        // than the 30s it cost before this fallback existed — measured, on the run that added it.
        await locator.DispatchEventAsync("click", eventInit: null, options: new() { Timeout = 5_000 });
    }

    private static async Task WaitForTransitionsAsync(IPage page, int timeoutMs = 5_000)
    {
        try
        {
            await page.WaitForFunctionAsync(
                """
                () => {
                    if (!document.getAnimations) { return true; }
                    return !document.getAnimations().some(a => {
                        if (a.playState !== 'running') { return false; }
                        const t = a.effect && a.effect.getComputedTiming
                            ? a.effect.getComputedTiming() : null;
                        return !t || t.iterations !== Infinity;
                    });
                }
                """,
                null,
                new() { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            // Something is animating indefinitely. Let the click speak for itself.
        }
    }

    /// <summary>
    /// Waits for the roster editor to fully load after navigating to /app/Lists/{listKey}.
    /// Once loaded, syncs window.__bsspec.army to currentList.army (the re-hydrated roster).
    /// </summary>
    /// <summary>
    /// Waits for NR to finish the catalogue work it starts when a faction is chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the 1500ms sleep during roster creation was really buying, and it is why deleting
    /// that sleep broke 8 specs on the FIRST action rather than during creation: NR renders and wires
    /// the unit list before it has finished, so a '+' clicked in that window is silently discarded —
    /// not delayed, discarded, with a 10s wait proving it.
    /// </para>
    /// <para>
    /// `manager.loadedCatalogues` draining to empty is the observable end of that work. It was found
    /// by snapshotting the whole store graph at the race and again 1500ms later, in the sequential
    /// context where the failure lives: of ~98,000 facts it was the only one that moved.
    /// </para>
    /// <para>
    /// Bounded and TOLERATED, because draining is not universal — some specs never populate
    /// `loadedCatalogues` at all, and for those there is nothing to wait for. A hard wait would hang
    /// them; this costs them the bound and costs everyone else milliseconds.
    /// </para>
    /// </remarks>
    public static Task WaitForCatalogueWorkSettledAsync(IPage page, int timeoutMs = 2_500)
        => NrUiTiming.MeasureAsync("wait-catalogue-work-settled", async () =>
        {
            try
            {
                await page.WaitForFunctionAsync(
                    """
                    () => {
                        const pinia = document.querySelector('#__nuxt')
                            ?.__vue_app__?.config?.globalProperties?.$pinia;
                        const mgr = pinia?._s?.get('systemsStore')?._selectedSystem?.manager;
                        if (!mgr) { return true; }
                        return Object.keys(mgr.loadedCatalogues || {}).length === 0;
                    }
                    """,
                    null,
                    new() { Timeout = timeoutMs });
            }
            catch (TimeoutException)
            {
                // Never drained — nothing was pending. Proceed.
            }
        });

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
