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
        // The four steps below (be on MySystems, open the install popup, choose Add From Folder, and
        // see the system land in the store) are one unit of work, and the page can be taken out from
        // under any of them: the previous spec's navigation is still in flight — its
        // CreateRosterAsync clicked the MyLists nav link — and when it lands it takes this page with
        // it. The controls used here exist only on MySystems, so they were visible, then gone.
        //
        // That was misread twice, as an animating element and then as a re-render, and "fixed" twice
        // by guarding a single step. Guarding the WAIT was not enough precisely because the drift
        // happens after it: the wait passes, and the click a moment later is on MyLists. Only the
        // failure message printing page.Url settled what was actually happening.
        //
        // Retried rather than waited out. Losing a race to another navigation is fixed by asserting
        // the route again, not by more patience — and only when the page really has drifted, so a
        // genuine failure still fails on its first attempt instead of three times as slowly.
        // A note on the numbers below: every Timeout here is a CEILING, not a cost. These waits
        // return the moment their condition holds — measured at ~17-30ms locally — so a generous
        // bound is free in the common case and only decides how long a genuinely stuck run takes to
        // report. They were first set to 5-10s, which passed a 363-spec lane twice locally and then
        // failed two specs on a Linux/headless CI runner with "Setup failed: Timeout 10000ms
        // exceeded". That is the wrong trade: a ceiling tight enough to fail a slow-but-correct run
        // buys nothing, because nothing is waiting for it when things are healthy.
        //
        // ── The FOURTH step, and why it was not always inside this loop ──
        //
        // The guarded unit used to end at "Add From Folder was clicked". But that only establishes
        // that the BUTTONS WERE PRESSED; what the caller needs is that the SYSTEM IS INSTALLED, and
        // that landed one statement later, outside the loop. A drift into that window was past the
        // guard and unrecoverable. It presented as `Setup failed: TimeoutException: Timeout 30000ms
        // exceeded` on a different spec each run, roughly one run in two across the 363-spec lane
        // (runs 31409213032 and 31415790894, on PRs that touched neither this driver nor NR) — the
        // same cause as the races above, one step further along.
        //
        // Raising the ceiling was the obvious response and the wrong one, and it had already been
        // tried: this is the exact pair of specs that prompted 10s → 30s (see NrUiTimeouts). That
        // took two failures per run down to about half of one, which reads as progress and is really
        // the signature of a bound clipping TWO DIFFERENT POPULATIONS — slow-but-correct installs
        // stopped being clipped, while a page that has already been navigated away has nothing to
        // wait for and does not install faster with more patience.
        //
        // The measurement settles it. Over a clean 363-spec lane (NR_UI_TIMINGS=1, 340 passed / 23
        // expected failures / 0 failures), `load-gamedata/wait-local-library` ran 363 times at avg
        // 67ms, min 3ms, max 5066ms — roughly SIX TIMES of headroom under its 30s ceiling. Nothing
        // here is running out of time. The same run shows the race alive and frequent, and the phase
        // counts locate every occurrence: four "navigated to '/app/MyLists' mid-install" retries,
        // wait-mysystems-rendered 367 (= 363 + those 4), click-add-more-games 365,
        // click-add-from-folder 363. So every drift observed LOCALLY arrives during the guarded
        // clicks — which is precisely why the guard has always looked sufficient. What walks past it
        // is the same drift arriving in the window after them: 67ms wide on average, 5s at its worst.
        var addMoreGames = page.GetByText("Add more games").First;
        var addFromFolder = page.GetByText("Add From Folder").First;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await NrUiTiming.MeasureAsync("load-gamedata/wait-mysystems-rendered", async () =>
                {
                    await WaitForSetupConditionAsync(
                        page,
                        "the MySystems route arrived",
                        "() => location.pathname.includes('MySystems')",
                        null,
                        NrUiTimeouts.Condition,
                        "() => 'pathname=' + location.pathname");
                    await addMoreGames.WaitForAsync(
                        new() { State = WaitForSelectorState.Visible, Timeout = NrUiTimeouts.Interaction });
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

                // Wait for NR to finish loading (system appears marked as local).
                //
                // Inside the loop, because this is the step that says the unit of work SUCCEEDED —
                // everything above it only says the buttons were pressed. Clicking "Add From Folder"
                // on a page that is about to be replaced is indistinguishable from clicking it on one
                // that is not, right up until this wait never completes.
                await NrUiTiming.MeasureAsync("load-gamedata/wait-local-library", () =>
                    WaitForSetupConditionAsync(
                        page,
                        $"NR installed the game data for system '{systemId ?? "(any)"}'",
                        """
                        (systemId) => {
                            const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                            const sysStore = pinia?._s?.get('systemsStore');
                            if (systemId) return !!sysStore?.localLibrary?.[systemId];
                            return Object.keys(sysStore?.localLibrary || {}).length > 0;
                        }
                        """,
                        systemId,
                        NrUiTimeouts.Condition,
                        """
                        () => {
                            const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                            const sysStore = pinia?._s?.get('systemsStore');
                            const keys = Object.keys(sysStore?.localLibrary || {});
                            return 'pathname=' + location.pathname
                                + ', localLibrary=[' + keys.join(', ') + ']'
                                + ', systemsStore=' + (sysStore ? 'present' : 'MISSING');
                        }
                        """));

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
                    await closeBtn.ClickAsync(new() { Timeout = NrUiTimeouts.OptionalProbe });
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
                        new() { State = WaitForSelectorState.Hidden, Timeout = NrUiTimeouts.OptionalProbe });
                }
                catch (TimeoutException)
                {
                    // Still there — let the next interaction speak for itself.
                }
            }
        });
    }

    /// <summary>
    /// Waits for <paramref name="conditionJs"/>, and reports what it was waiting for — and what the
    /// page looked like instead — when it does not arrive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exists because a bare Playwright timeout is anonymous.</b> A locator wait names its target
    /// ("waiting for Locator(\"a[href*='MySystems']\")"), but <c>WaitForFunctionAsync</c> has no
    /// locator to name, so every one of them fails with the same seven words:
    /// <c>Timeout 30000ms exceeded.</c> Setup has two of them, they mean opposite things — "the route
    /// never arrived" versus "NR never installed the game data" — and for two CI runs there was no
    /// way to tell which had happened, on a lane where the answer decides between re-run and
    /// investigate.
    /// </para>
    /// <para>
    /// The observation is the point, not the label. <c>observeJs</c> reads back the very state the
    /// condition was testing, so the message carries the counter-evidence rather than asserting a
    /// cause: <c>localLibrary=[]</c> with <c>pathname=/app/MyLists</c> is the lost-page race, while
    /// the same empty library on <c>/app/MySystems</c> is NR genuinely failing to install. The same
    /// distinction the retry loop above makes, made legible to whoever reads the failure.
    /// </para>
    /// <para>
    /// Kept as a <see cref="TimeoutException"/> deliberately: the retry loop discriminates on that
    /// type, so a friendlier exception here would silently opt these waits out of the guard that
    /// makes them survivable.
    /// </para>
    /// </remarks>
    internal static async Task WaitForSetupConditionAsync(
        IPage page,
        string what,
        string conditionJs,
        object? arg,
        int timeoutMs,
        string observeJs)
    {
        try
        {
            await page.WaitForFunctionAsync(conditionJs, arg, new() { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            string observed;
            try
            {
                observed = await page.EvaluateAsync<string>(observeJs) ?? "(no observation)";
            }
            catch (Exception ex)
            {
                // The page can be gone entirely — that IS the observation.
                observed = $"(could not be read: {ex.GetType().Name}: {ex.Message})";
            }

            throw new TimeoutException(
                $"NR UI setup: waited {timeoutMs}ms for {what} and it did not happen "
                + $"(page: {page.Url}). Observed: {observed}.");
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
        string? preferredCatalogueName = null,
        string? preferredForceEntryId = null)
    {
        // By route, not by clicking the navbar link: NR's message bar renders over that link, and
        // Playwright will not click an element another one covers. Routes are the stable contract
        // (see NewRecruitBrowser.PushRouteAsync); navbar controls are NR's to restyle.
        await NrUiTiming.MeasureAsync("create-roster/click-mylists", () =>
            NewRecruitBrowser.PushRouteAsync(page, "/app/MyLists"));

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
                new() { Timeout = NrUiTimeouts.Condition });
            await WaitForTransitionsAsync(page);
        });

        // Click "New" to open Create List dialog (the nav link with href="#")
        var newBtn = page.Locator("a[href='#']", new() { HasTextString = "New" });
        await NrUiTiming.MeasureAsync("create-roster/click-new", () =>
            ClickWhenReadyAsync(page, newBtn, "New list button"));

        // Root on the AddList component, not on a bare `.box`. Six other components render
        // `class="box"` (PopupDialog, Prompt, the supporter promo, the login form), all inside
        // #mainContent, which PRECEDES #popups in document order — so `.box` silently picks the
        // wrong dialog the first time one of them is on screen, and #popups can hold more than one
        // box by design (PopupDialog sizes itself from `#popups.childElementCount + 1`). The
        // strings `vueAddlist`, `force-card` and `newListSave` each occur in exactly two files in
        // the whole v35.72 asset tree, so nothing else in the app can forge them. Inside it the
        // faction <select> is still the first select and the name input the first text input.
        var box = page.Locator("#vueAddlist");

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
                            const add = document.querySelector('#vueAddlist');
                            const sel = add?.querySelector('select');
                            return !!sel && sel.options.length > 1;
                        }
                        """,
                        null,
                        new() { Timeout = NrUiTimeouts.OptionalProbe });
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
                    const add = document.querySelector('#vueAddlist');
                    if (!add) { return null; }
                    if (/could not be loaded/i.test(add.textContent || '')) { return 'error'; }

                    // The dialog must be rendered far enough to carry its CREATE control.
                    //
                    // Until v35.72 that control was a button reading "Create List", and scanning
                    // for it was the whole gate. v35.72 rewrote the dialog: the create action is
                    // now one `<button class="force-card">` per force, and the surviving
                    // "Create List" button renders only under `needsConfirmButton`, which is
                    // `!!bookData && !forces.length`. That state is unreachable — loadBook() runs
                    // `this.forces[0].id` with no optional chaining, so a book with no forces
                    // throws before Vue can flush and lands in the catch as loadBookError. The
                    // "no forces" shape therefore arrives as the ERROR outcome above, not as a
                    // confirm button. The .newListSave branch is kept in the disjunction only so
                    // this starts working again the day NR writes `forces[0]?.id`; nothing may
                    // depend on it. "Create List" also survives as div.headTitle, which is why the
                    // old scan had to become structural rather than merely re-pointed.
                    //
                    // Both shapes below live on the `downloading === false` side of the template's
                    // v-if, so either one positively proves the load finished. No "spinner is gone"
                    // check is needed, and an absence check would be worse: it is false-negative
                    // prone across a flush boundary.
                    const hasForceCards = add.querySelectorAll('.forces button.force-card').length > 0;
                    const hasConfirm = !!add.querySelector('.newListSave button');
                    if (!hasForceCards && !hasConfirm) { return null; }

                    // ...AND the form is rendered, which the force cards do NOT imply: they sit
                    // under `v-if="selectedBook && !loadBookError && forces.length"` while the name
                    // input sits under `v-if="lib"`. The old comment inferred one from the other
                    // and was already wrong in v35.27, where the confirm button was outside the
                    // form too. It matters because the fill below was a snapshot, so a missing
                    // input silently took NR's default name — what roster-name-and-metadata
                    // asserts against. Safe to require: systemsStore initialises
                    // `library: {index:{},array:[]}`, so `lib` is never falsy once mounted.
                    if (!add.querySelector('input[pattern=".{3,}"]')) { return null; }

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
                new() { Timeout = NrUiTimeouts.Condition });
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

        // The force choice used to happen HERE, and in v35.72 it cannot: it moved below the
        // catalogue settle and the name fill, because it is now the same action as creating.
        //
        // NR used to render a FORCE dropdown next to the faction one, and each option carried the
        // force ENTRY ID as its bound value — Vue stashes a non-string v-model value on the element
        // as `_value`, so the control identified itself by what it CARRIED rather than by position
        // or label. v35.72 deleted that select. Each force is now a `<button class="force-card">`,
        // and `pickForce(force)` sets `selectedForceId` and then awaits `addNewList()`. There is
        // nothing left to pre-select: choosing and committing are one click, which must land AFTER
        // the settle below and after the name fill, or the list is built from a half-parsed
        // catalogue and under NR's default name. See the pick at the end of this method.
        //
        // One correction while this is being rewritten, so it stops being repeated: the old comment
        // justified matching by id with "names are ambiguous by design here
        // (force-multi-catalogue-two-forces has two Patrol forces)". That is not true. That spec
        // declares ONE forceEntry and adds it twice from two catalogues — two forces in the ROSTER,
        // one card in the dialog. A sweep of all 492 spec files found zero forceEntryLinks, zero
        // sortIndex, and no dialog anywhere that renders two same-named sibling force entries.
        // Matching by id is still right, but the reason is that this driver HOLDS an entry id and
        // never a name — not that the corpus is ambiguous.

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

        // Set list name. Unconditional now, not a snapshot `IsVisibleAsync()`: the readiness wait
        // above conjoins this input, so a missing one is a bug to surface rather than a branch to
        // take. It also has to happen BEFORE the click below, which is no longer merely tidy — the
        // click creates the list, and `addNewList()` reads `this.listName || "Unnamed list"`. The
        // fill is enough to commit it: the binding is plain vModelText with no modifiers, so it
        // listens on `input` and assigns synchronously inside the dispatch, with no flush needed.
        await NrUiTiming.MeasureAsync("create-roster/fill-name", () =>
            box.Locator("input[type='text'], input:not([type])").First.FillAsync(rosterName));

        // Pick the force card, which is also how the list gets created.
        //
        // v35.72 replaced the "Create List" button with one `<button class="force-card">` per force;
        // `pickForce` sets `selectedForceId` and awaits `addNewList()`, so the click both chooses
        // and commits. The question the old force <select> answered by reading `option._value` —
        // given an entry id, WHICH control do I drive — has no DOM answer any more: the cards carry
        // the id only as their vnode `key`, which is never written to the DOM, and this build ships
        // no devtools hooks (`__vnode` and `__vueParentComponent` are absent from the bundle), so
        // there is no route UP from the element. The route down still exists: the renderer sets
        // `container._vnode`, and #__nuxt is the container.
        //
        // Resolution and click are ONE synchronous evaluate with no `await` between them. Vue's
        // scheduler is a microtask, so any await — or any CDP round trip — is a flush opportunity,
        // and a card resolved in one task can be detached by the next. A detached card still holds
        // its listener and would call `pickForce` with the previous render's force: a silently wrong
        // roster, which is the exact failure class the old comment here existed to prevent.
        //
        // The ladder is deliberately ordered cheapest-and-safest first:
        //   1. one card, or no force asked for -> click it. Nothing to resolve, and this is exactly
        //      what v35.27 produced (return without selecting, then click Create). Roughly 361 of
        //      the 363 lane specs land here, so a defect in the walk below cannot take out the lane.
        //   2. vnode `key` per card -> an exact element-to-entry-id map, free of order assumptions.
        //   3. the AddList instance's own `forces` array, cross-checked against the rendered names.
        //   4. otherwise throw. Clicking a guessed card builds a wrong roster at a distance.
        await NrUiTiming.MeasureAsync("create-roster/click-create", async () =>
        {
            var pick = await page.EvaluateAsync<string[]>(
                """
                (wantEntryId) => {
                    const add = document.querySelector('#vueAddlist');
                    if (!add) { return ['no-dialog', '']; }

                    const cards = [...add.querySelectorAll('.forces button.force-card')];
                    const names = cards.map(
                        c => (c.querySelector('.force-name')?.textContent || '').trim());

                    // Guarded in the same task as the read: a disabled button receives no click
                    // events at all, so without this a lost race is a mute 30s wait-army timeout.
                    const fire = (el, mode, detail) => {
                        if (!el || !el.isConnected) { return ['stale', detail]; }
                        if (el.disabled) { return ['busy', detail]; }
                        el.click();
                        return [mode, detail];
                    };

                    if (cards.length === 0) {
                        // The needsConfirmButton branch. Unreachable today (loadBook throws on an
                        // empty force list before Vue renders), kept so it works if NR ever guards
                        // that line. Reaching it means the force choice was made for us.
                        const save = add.querySelector('.newListSave button');
                        return save ? fire(save, 'confirm-button', '') : ['no-control', ''];
                    }

                    if (cards.length === 1 || !wantEntryId) {
                        return fire(cards[0], 'sole-card', names[0]);
                    }

                    // Walk DOWN from the container's root vnode. Only element vnodes own their el;
                    // a component vnode shares its subtree's el and would shadow it with the wrong
                    // key. Teleport keeps its children in `vnode.children`, which is how this
                    // reaches a dialog that lives under #popups.
                    const nuxt = document.querySelector('#__nuxt');
                    const want = new Set(cards);
                    const keyOf = new Map();
                    const seen = new Set();
                    const stack = nuxt && nuxt._vnode ? [nuxt._vnode] : [];
                    let addList = null;
                    let budget = 200000;
                    while (stack.length && budget-- > 0) {
                        const v = stack.pop();
                        if (!v || typeof v !== 'object' || seen.has(v)) { continue; }
                        seen.add(v);
                        if (!v.component && v.el && want.has(v.el)) { keyOf.set(v.el, v.key); }
                        if (v.component) {
                            if (v.component.type && v.component.type.name === 'AddList') {
                                addList = v.component;
                            }
                            stack.push(v.component.subTree);
                        }
                        if (v.suspense) {
                            stack.push(v.suspense.activeBranch, v.suspense.pendingBranch);
                        }
                        if (Array.isArray(v.children)) {
                            for (const c of v.children) {
                                if (c && typeof c === 'object') { stack.push(c); }
                            }
                        }
                    }
                    const proxy = addList && addList.proxy;

                    // All-or-nothing: a partial map falls through rather than indexing on a guess.
                    if (keyOf.size === cards.length) {
                        const ids = cards.map(el => keyOf.get(el));
                        if (ids.every(id => typeof id === 'string' && id.length)) {
                            const at = ids.indexOf(wantEntryId);
                            if (at >= 0) { return fire(cards[at], 'vnode-key', ids[at]); }
                            // Not offered. getForces() drops forces whose categories are all empty,
                            // so this is legitimate. Defer to NR's own current choice, which is what
                            // v35.27's silent return produced — not to a position we computed.
                            const nrAt = proxy ? ids.indexOf(proxy.selectedForceId) : -1;
                            if (nrAt >= 0) { return fire(cards[nrAt], 'nr-default', ids[nrAt]); }
                            return ['unresolved', ids.join(',')];
                        }
                    }

                    // Fallback: the array the cards were rendered from, read straight off the
                    // component rather than rebuilt from the store (no await, and no second guess at
                    // NR's `engine === "bs"` hidden filter). Trust it only if it still describes
                    // what is on screen.
                    if (proxy && proxy.downloading === false) {
                        let forces = [];
                        try { forces = proxy.forces || []; } catch (e) { forces = []; }
                        if (forces.length === cards.length
                            && forces.every((f, i) => (f.name || '') === names[i])) {
                            const at = forces.findIndex(f => f.id === wantEntryId);
                            if (at >= 0) { return fire(cards[at], 'instance-forces', forces[at].id); }
                            const nrAt = forces.findIndex(f => f.id === proxy.selectedForceId);
                            if (nrAt >= 0) { return fire(cards[nrAt], 'nr-default', forces[nrAt].id); }
                        }
                    }

                    return ['unresolved', names.join(' | ')];
                }
                """,
                preferredForceEntryId ?? "");

            var mode = pick.Length > 0 ? pick[0] : "no-result";
            if (mode is "vnode-key" or "instance-forces" or "sole-card"
                or "nr-default" or "confirm-button")
            {
                return;
            }

            // Name the mechanism that gave up. Every alternative here — clicking the first card, or
            // letting the wait-army gate below time out — reports something other than what actually
            // happened.
            throw new InvalidOperationException(
                $"NR Create List: could not commit force entry '{preferredForceEntryId}' " +
                $"(catalogue '{preferredCatalogueName}'). Card picker reported '{mode}'" +
                (pick.Length > 1 && pick[1].Length > 0 ? $": {pick[1]}" : "") + ". " +
                "v35.72 makes the force card the create button, so this is a roster that was never " +
                "created, not a force that was mis-picked.");
        });

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
            new() { Timeout = NrUiTimeouts.Condition }));

        // After creation, set up __bsspec for state reading
        // ...and wait for the dialog to go away, which is a separate event from the list existing.
        //
        // `addNewList` awaits `$listStore.addList(...)` and only then emits `added` and `close`, so
        // the wait above can be satisfied while the dialog is still mounted. That was harmless until
        // v35.72 gave the dialog a `div.forces` of its own: AddForceByNameAsync opens with a snapshot
        // `page.Locator(".forces").First.IsVisibleAsync()`, and a create dialog still on screen
        // answers yes — skipping the "Add Force" click and failing later as "Force 'X' not found in
        // the forces panel", which names the wrong cause. Cheaper to close the window here than to
        // teach every downstream selector about the popup layer.
        await NrUiTiming.MeasureAsync("create-roster/wait-dialog-closed", () =>
            page.WaitForSelectorAsync(
                "#vueAddlist",
                new() { State = WaitForSelectorState.Detached, Timeout = NrUiTimeouts.Interaction }));

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
            await locator.ClickAsync(new() { Timeout = NrUiTimeouts.Interaction });
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
            await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = NrUiTimeouts.Interaction });
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
        // Interaction, not OptionalProbe: this one is NOT tolerated — if the dispatch fails the
        // click fails, so it gets the same ceiling as any other interaction rather than the short
        // bound reserved for probes whose failure is handled.
        await locator.DispatchEventAsync(
            "click", eventInit: null, options: new() { Timeout = NrUiTimeouts.Interaction });
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

    /// <summary>
    /// Stops NR reporting the server-save refusal that frozen replay manufactures, so its message bar
    /// never covers the controls setup has to click. Idempotent; call once Pinia exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Frozen replay fulfils every <c>/api/</c> call with <c>{}</c> because there is no server, so
    /// every list save comes back "rejected" and NR reports it. The notice is never news here, and it
    /// is expensive: Playwright will not click an element the banner covers.
    /// </para>
    /// <para>
    /// Silenced at NR's reporter rather than by clearing the message bar, which cannot distinguish a
    /// manufactured notice from a message a spec asserts on. Throws rather than skipping if it cannot
    /// install — a suppression that quietly does nothing costs the lane twice its runtime with
    /// nothing to point at. See docs/nr-ui-roster-coverage.md.
    /// </para>
    /// </remarks>
    public static async Task SuppressServerSaveNoticeAsync(IPage page)
    {
        var status = await page.EvaluateAsync<string>("""
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                if (!lists) { return 'no-store'; }
                if (lists.__bsspecServerSaveNoticeSuppressed) { return 'already'; }
                if (typeof lists.reportListSaveRejected !== 'function') { return 'no-reporter'; }
                const stub = function (row) {
                    console.warn('[bsspec] server-save refusal suppressed (frozen replay has no server): '
                        + (row && row.list_key));
                };
                stub.__bsspecStub = true;
                lists.reportListSaveRejected = stub;
                // Pinia hands out a proxy; a write it declines to keep would suppress nothing. Read
                // back by marker as well as by identity, because a store that re-binds its actions
                // would hand back a different function object having kept the write.
                const back = lists.reportListSaveRejected;
                if (back !== stub && back?.__bsspecStub !== true) { return 'write-ignored'; }
                lists.__bsspecServerSaveNoticeSuppressed = true;
                return 'installed';
            }
            """);

        if (status is "installed" or "already")
        {
            return;
        }

        throw new InvalidOperationException(status switch
        {
            "no-store" => "NR UI: no 'lists' Pinia store, so NR's server-save refusal could not be "
                + "silenced. The store id is what moved — every other step that reaches this store "
                + "is about to fail too.",
            "no-reporter" => "NR UI: lists.reportListSaveRejected is gone. If NR renamed it, find the "
                + "new name and stub that — the banner it posts covers the controls setup clicks, "
                + "and waiting each one out costs the lane roughly double its runtime. If NR stopped "
                + "reporting refusals at all, delete this method and its caller.",
            "write-ignored" => "NR UI: assigning lists.reportListSaveRejected did not stick, so the "
                + "refusal banner will still be posted. NR's store proxy now refuses the write.",
            _ => $"NR UI: server-save notice suppression reported '{status}'.",
        });
    }
}
