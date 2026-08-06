using BattleScribeSpec.GameData;
using BattleScribeSpec.NewRecruit;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Playwright UI action helpers for NrGameDataUiEngine.
///
/// Each method drives a single IGameDataEngine operation through the NR Editor's
/// rendered catalogue tree interface. The hybrid pattern applies:
///   - Mutations: performed through real UI interactions (clicks, context menus, forms)
///   - IDs of new entries: read back via JS after each mutation
///   - State: read from NR Editor's Pinia editorStore (see <see cref="NrEditorStore.ReadStateAsync"/>)
///
/// The NR Editor shows a catalogue tree in the main panel. Entry operations are
/// accessed via right-click context menus on tree nodes. Field editing happens in
/// a properties panel (input/checkbox/select for each editable field).
///
/// <b>Selector notes:</b> Selectors target NR Editor v1.x (giloushaker/nr-editor).
/// If the editor updates its DOM structure, run the probe workflow to re-discover
/// selectors:
/// <code>
///   dotnet run --project src/BattleScribeSpec.Cli -- probe --engine newrecruit --ui spec-id
/// </code>
/// See .agents/skills/bsspec-cli/references/NR-GAMEDATA-UI.md for the full probe workflow documentation.
/// </summary>
public static class NrGameDataUiActions
{
    // ===== Structural mutations =====




    /// <summary>
    /// Removes the entry with the given ID from the NR Editor tree.
    /// Locates the node, opens context menu, and clicks Delete/Remove.
    /// Confirms any deletion dialog.
    /// </summary>
    public static async Task RemoveEntryAsync(IPage page, string entryId)
    {
        var node = await FindTreeNodeByIdAsync(page, entryId);

        await node.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        // Reactive: wait for the context menu to render rather than a fixed delay.
        await page.WaitForSelectorAsync(".context-menu:visible", new PageWaitForSelectorOptions { Timeout = 5_000 });

        // Click "Remove" in the context menu.
        // NR Editor context menu items are <div> elements inside .context-menu (not role=menuitem).
        // The Remove option text is "Remove" followed by a <span class="gray right">Del</span>.
        var deleteItem = page.Locator(".context-menu > div")
            .Filter(new LocatorFilterOptions { HasText = "Remove" });
        await deleteItem.First.ClickAsync(new() { Timeout = 5_000 });
        await page.WaitForTimeoutAsync(300);

        // Confirm deletion dialog if present
        var confirmBtn = page.GetByRole(AriaRole.Button, new() { Name = "Confirm" })
            .Or(page.GetByRole(AriaRole.Button, new() { Name = "Delete" }))
            .Or(page.GetByRole(AriaRole.Button, new() { Name = "Yes" }));
        if (await confirmBtn.First.IsVisibleAsync())
        {
            await confirmBtn.First.ClickAsync();
            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>
    /// Sets a field value on an entry in the NR Editor properties panel.
    ///
    /// NR Editor renders two kinds of field controls in the right panel:
    /// <list type="bullet">
    ///   <item><b>Boolean fields</b> — <c>&lt;input type="checkbox" id="{field}"&gt;</c> with
    ///     an associated <c>&lt;label for="{field}"&gt;</c> inside <c>.booleans</c>. Located via
    ///     Playwright's <c>GetByLabel</c> which resolves the <c>for</c> attribute.</item>
    ///   <item><b>Text/select fields</b> — <c>&lt;tr&gt;&lt;td&gt;{Label}:&lt;/td&gt;&lt;td&gt;&lt;input/select&gt;&lt;/td&gt;&lt;/tr&gt;</c>
    ///     inside <c>table.editorTable</c> inside fieldsets. The label <c>&lt;td&gt;</c> is NOT a
    ///     <c>&lt;label&gt;</c> element — located by filtering the tr by its first-cell text.</item>
    /// </list>
    /// </summary>
    public static async Task SetFieldAsync(IPage page, string field, string? value)
    {
        // The caller (NrGameDataUiDriver) has already selected the entry, so the right panel is
        // open on it — re-selecting here via the tree is redundant and fragile for deeply-nested
        // named entities (e.g. an infoLink under an infoGroup) and after a rename. Just confirm
        // the panel is ready.
        await page.Locator(".rightPanel tr")
            .Filter(new LocatorFilterOptions { HasText = "Unique ID" })
            .WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });

        var fieldLabel = GetFieldLabel(field);
        var rightPanel = page.Locator(".rightPanel");

        // Catalogue links carry an #importRoot checkbox and a raw "Target ID:" text input. Use the
        // text input for the target (the autocomplete only lists existing catalogues, so it can't
        // express the spec's re-point to a non-existent target) and #importRoot for importRootEntries.
        var isCatalogueLink = await rightPanel.Locator("#importRoot").CountAsync() > 0;
        if (isCatalogueLink && field == "targetId")
        {
            var tidInput = rightPanel.Locator("table.editorTable tr")
                .Filter(new LocatorFilterOptions
                {
                    Has = page.Locator("td").Filter(new LocatorFilterOptions
                    {
                        HasTextRegex = new System.Text.RegularExpressions.Regex("^\\s*Target ID:?\\s*$"),
                    }),
                })
                .Locator("td:last-child input").First;
            await tidInput.FillAsync(value ?? "");
            await tidInput.PressAsync("Tab");
            await page.WaitForTimeoutAsync(300);
            return;
        }

        if (field == "importRootEntries")
        {
            var cb = rightPanel.Locator("#importRoot").First;
            if (value == "false")
            {
                if (await cb.IsCheckedAsync())
                {
                    await cb.UncheckAsync();
                }
            }
            else if (!await cb.IsCheckedAsync())
            {
                await cb.CheckAsync();
            }

            await page.WaitForTimeoutAsync(200);
            return;
        }

        // A link renders its kind enum in a "Link Type:" select (entryLink: selectionEntry/
        // selectionEntryGroup; infoLink: profile/rule/infoGroup). This is a different row from a
        // selection entry's own "Type:" (unit/upgrade), which the generic path below still handles —
        // so only intercept `type` when a "Link Type:" select is actually present.
        if (field == "type")
        {
            var linkTypeSelect = rightPanel.Locator("table.editorTable tr")
                .Filter(new LocatorFilterOptions
                {
                    Has = page.Locator("td").Filter(new LocatorFilterOptions
                    {
                        HasTextRegex = new System.Text.RegularExpressions.Regex("^\\s*Link Type:?\\s*$"),
                    }),
                })
                .Locator("td:last-child select").First;
            if (await linkTypeSelect.CountAsync() > 0)
            {
                try
                {
                    await linkTypeSelect.SelectOptionAsync(new SelectOptionValue { Value = value });
                }
                catch
                {
                    await linkTypeSelect.SelectOptionAsync(new SelectOptionValue { Label = value });
                }
                await page.WaitForTimeoutAsync(200);
                return;
            }
        }

        // Several fields use NR Editor's custom autocomplete widget (not a standard input/select).
        // Handle these before the generic input strategies.
        if (field is "publicationId" or "defaultSelectionEntryId" or "targetId" or "typeId" or "typeName")
        {
            if (value is not null)
            {
                // typeName IS the profile-type display name — no id→name lookup needed; the others
                // resolve the target's display name from its id.
                var displayName = field switch
                {
                    "typeName" => value,
                    "publicationId" => await page.EvaluateAsync<string?>(PublicationNameLookupJs, value),
                    "typeId" => await page.EvaluateAsync<string?>(ProfileTypeNameLookupJs, value),
                    _ => await page.EvaluateAsync<string?>(EntryNameLookupJs, value),
                };
                // A link's Target autocomplete is filtered by its Link Type — align it to the
                // target's kind first so the target appears in the list.
                if (field == "targetId")
                {
                    await SetLinkTypeFromTargetAsync(page, rightPanel, value);
                }
                await SetAutocompleteFieldAsync(page, rightPanel, fieldLabel, value, displayName);
            }
            return;
        }

        // Strategy 1: GetByLabel — works for checkbox fields that have a proper <label for>
        // association (all boolean fields in the .booleans div use this pattern).
        // Scope to .rightPanel to avoid matching unrelated labels elsewhere.
        var fieldInput = rightPanel.GetByLabel(fieldLabel, new LocatorGetByLabelOptions { Exact = false });

        if (!await fieldInput.IsVisibleAsync())
        {
            // Strategy 2: Table row approach for text/select fields.
            // NR Editor renders: <tr><td>Label:</td><td><input or select></td></tr>
            // The td label is NOT a <label> element so GetByLabel does not find these inputs.
            // Match the label *cell* precisely (tolerant of a trailing colon) so a label like
            // "Publication" doesn't also match "Publication Date:" / "Publication URL:".
            fieldInput = rightPanel.Locator("table.editorTable tr")
                .Filter(new LocatorFilterOptions
                {
                    Has = page.Locator("td").Filter(new LocatorFilterOptions
                    {
                        HasTextRegex = new System.Text.RegularExpressions.Regex(
                            $"^\\s*{System.Text.RegularExpressions.Regex.Escape(fieldLabel)}:?\\s*$"),
                    }),
                })
                .Locator("td:last-child input, td:last-child select")
                .First;
        }

        // A disabled control genuinely cannot be set through the UI (e.g. `collective` on an
        // entry link, which NR derives rather than exposes). Skip it rather than timing out; the
        // spec accounts for it via a per-engine expectedState override.
        if (await fieldInput.CountAsync() > 0 && await fieldInput.IsDisabledAsync())
        {
            Console.Error.WriteLine(
                $"[nr-gamedata-ui] field '{field}' control is disabled in NR's UI — skipping.");
            return;
        }

        if (value is null)
        {
            // Clear the field
            if (await IsCheckboxAsync(fieldInput))
            {
                if (await fieldInput.IsCheckedAsync())
                {
                    await fieldInput.UncheckAsync();
                }
            }
            else
            {
                await fieldInput.FillAsync("");
            }
        }
        else if (value is "true" or "false")
        {
            // Only treat as boolean if the control is actually a checkbox.
            // Strings "true"/"false" could also be valid text input values.
            if (await IsCheckboxAsync(fieldInput))
            {
                if (value == "true")
                {
                    await fieldInput.CheckAsync();
                }
                else
                {
                    await fieldInput.UncheckAsync();
                }
            }
            else
            {
                await fieldInput.FillAsync(value);
            }
        }
        else
        {
            // Try as select option first, fall back to text input fill
            try
            {
                await fieldInput.SelectOptionAsync(value, new() { Timeout = 500 });
            }
            catch
            {
                await fieldInput.FillAsync(value);
            }
        }

        // Commit the change (Tab triggers model update in NR Editor for text fields;
        // checkboxes commit immediately on click so Tab is a no-op for them)
        await fieldInput.PressAsync("Tab");
        await page.WaitForTimeoutAsync(300);
    }

    /// <summary>
    /// Drives an NR Editor autocomplete widget in the right panel to select the item
    /// identified by <paramref name="id"/>.
    ///
    /// The same widget pattern is used for Publication, Target:, and Default Selection rows:
    /// <code>
    /// &lt;div class="autocomplete container"&gt;
    ///   &lt;div class="autocomplete-input"&gt;...&lt;/div&gt;
    ///   &lt;div class="suggestions hidden"&gt;
    ///     &lt;div&gt;&lt;span class="inline"&gt;Display Name&lt;/span&gt;&lt;/div&gt;
    ///   &lt;/div&gt;
    /// &lt;/div&gt;
    /// </code>
    /// Clicking <c>.autocomplete-input</c> removes <c>hidden</c> from <c>.suggestions</c> after a Vue tick.
    /// The suggestion is filtered by <paramref name="displayName"/> (or falls back to <paramref name="id"/>).
    /// </summary>
    private static async Task SetAutocompleteFieldAsync(
        IPage page, ILocator rightPanel, string rowLabel, string id, string? displayName)
    {
        var fieldRow = rightPanel.Locator("table.editorTable tr")
            .Filter(new LocatorFilterOptions { HasText = rowLabel });

        // Click the autocomplete input — Vue removes 'hidden' from .suggestions after a tick
        // No sleep: Autocomplete.startEditing() sets editing=true synchronously and
        // `.suggestions :class="{hidden: !editing}"` follows in the same flush, so the wait on the
        // next statement IS this condition — the sleep only delayed reaching it.
        await fieldRow.Locator(".autocomplete-input").ClickAsync();

        // Wait for suggestions to become visible
        var suggestions = fieldRow.Locator(".suggestions:not(.hidden) > div");
        await suggestions.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5_000,
        });

        // Click the suggestion whose text contains the display name (or fall back to ID)
        var matchingSuggestion = suggestions
            .Filter(new LocatorFilterOptions { HasText = displayName ?? id });
        try
        {
            await matchingSuggestion.First.ClickAsync(new() { Timeout = 3_000 });
        }
        catch (TimeoutException)
        {
            var available = await suggestions.AllTextContentsAsync();
            throw new InvalidOperationException(
                $"NR Editor UI: no '{rowLabel}' suggestion matched '{displayName ?? id}' " +
                $"(displayName={(displayName ?? "<null>")}, id={id}). Available: [{string.Join(", ", available)}]");
        }
        await page.WaitForTimeoutAsync(300);
    }

    /// <summary>
    /// JS expression to look up a publication name by ID from the Pinia editorStore.
    /// Argument: publication ID string; returns name string or null.
    /// </summary>
    private const string PublicationNameLookupJs = """
        (pubId) => {
            const pinia = document.querySelector('#__nuxt')
                ?.__vue_app__?.config?.globalProperties?.$pinia;
            if (!pinia) return null;
            const params = new URLSearchParams(window.location.search);
            const systemId = params.get('systemId');
            const gsSys = pinia._s.get('editor')?.gameSystems?.[systemId];
            // Publications can live on the game system or any loaded catalogue.
            for (const cat of Object.values(gsSys?.loadedCatalogues ?? {})) {
                const hit = (cat.publications ?? []).find(p => p.id === pubId);
                if (hit) return hit.name;
            }
            return null;
        }
        """;

    /// <summary>
    /// JS to determine a link target's kind (the value NR's "Link Type" select expects) by finding
    /// which collection holds the id across all loaded catalogues. Argument: targetId; returns one
    /// of selectionEntry/selectionEntryGroup/rule/profile/infoGroup, or null if not found.
    /// </summary>
    private const string LinkTargetKindLookupJs = """
        (targetId) => {
            const pinia = document.querySelector('#__nuxt')
                ?.__vue_app__?.config?.globalProperties?.$pinia;
            if (!pinia) return null;
            const systemId = new URLSearchParams(location.search).get('systemId');
            const gsSys = pinia._s.get('editor')?.gameSystems?.[systemId];
            if (!gsSys) return null;
            const kindByCol = {
                selectionEntries: 'selectionEntry', sharedSelectionEntries: 'selectionEntry',
                selectionEntryGroups: 'selectionEntryGroup', sharedSelectionEntryGroups: 'selectionEntryGroup',
                rules: 'rule', sharedRules: 'rule',
                profiles: 'profile', sharedProfiles: 'profile',
                infoGroups: 'infoGroup', sharedInfoGroups: 'infoGroup',
            };
            const seen = new WeakSet();
            const search = (obj) => {
                if (!obj || typeof obj !== 'object' || seen.has(obj)) return null;
                seen.add(obj);
                for (const col of Object.keys(kindByCol)) {
                    const arr = obj[col];
                    if (Array.isArray(arr)) for (const e of arr) if (e && e.id === targetId) return kindByCol[col];
                }
                for (const k of Object.keys(obj)) {
                    const v = obj[k];
                    if (Array.isArray(v)) for (const it of v) { const r = search(it); if (r) return r; }
                }
                return null;
            };
            for (const c of Object.values(gsSys.loadedCatalogues ?? {})) { const r = search(c); if (r) return r; }
            return null;
        }
        """;

    /// <summary>
    /// NR filters a link's "Target" autocomplete by its "Link Type" select, so a non-default
    /// target (group/rule/profile/infoGroup) isn't listed until the type matches. Sets the Link
    /// Type select from the target's kind before the target is chosen. No-op if the row/kind is absent.
    /// </summary>
    /// <summary>Resolves a link target's kind (selectionEntry/selectionEntryGroup/rule/profile/infoGroup).</summary>
    internal static Task<string?> LinkTargetKindAsync(IPage page, string targetId)
        => page.EvaluateAsync<string?>(LinkTargetKindLookupJs, targetId);

    internal static async Task SetLinkTypeFromTargetAsync(IPage page, ILocator rightPanel, string targetId)
    {
        var kind = await page.EvaluateAsync<string?>(LinkTargetKindLookupJs, targetId);
        if (kind is null)
        {
            return;
        }

        var typeSelect = rightPanel.Locator("table.editorTable tr")
            .Filter(new LocatorFilterOptions
            {
                Has = page.Locator("td").Filter(new LocatorFilterOptions
                {
                    HasTextRegex = new System.Text.RegularExpressions.Regex("^\\s*Link Type:?\\s*$"),
                }),
            })
            .Locator("td:last-child select").First;
        if (await typeSelect.CountAsync() == 0)
        {
            return;
        }

        try
        {
            await typeSelect.SelectOptionAsync(new SelectOptionValue { Value = kind });
            await page.WaitForTimeoutAsync(200);
        }
        catch
        {
            // Type not selectable (e.g. only one option) — leave as-is.
        }
    }

    /// <summary>
    /// JS expression to look up a profile-type name by ID from the Pinia editorStore (profile
    /// types live on the game system or any loaded catalogue). Argument: typeId; returns name.
    /// </summary>
    private const string ProfileTypeNameLookupJs = """
        (typeId) => {
            const pinia = document.querySelector('#__nuxt')
                ?.__vue_app__?.config?.globalProperties?.$pinia;
            if (!pinia) return null;
            const params = new URLSearchParams(window.location.search);
            const systemId = params.get('systemId');
            const gsSys = pinia._s.get('editor')?.gameSystems?.[systemId];
            for (const cat of Object.values(gsSys?.loadedCatalogues ?? {})) {
                const hit = (cat.profileTypes ?? []).find(p => p.id === typeId);
                if (hit) return hit.name;
            }
            return null;
        }
        """;

    /// <summary>
    /// JS expression to look up an entry name by ID via deep search across all loaded catalogues.
    /// Argument: entry ID string; returns name string or null.
    /// Searches known collection property names only (no circular reference traversal).
    /// </summary>
    private const string EntryNameLookupJs = """
        (entryId) => {
            const pinia = document.querySelector('#__nuxt')
                ?.__vue_app__?.config?.globalProperties?.$pinia;
            if (!pinia) return null;
            const editor = pinia._s.get('editor');
            const params = new URLSearchParams(window.location.search);
            const systemId = params.get('systemId');
            const gsSys = editor?.gameSystems?.[systemId];
            if (!gsSys) return null;
            const COLLECTIONS = [
                'selectionEntries', 'selectionEntryGroups',
                'sharedSelectionEntries', 'sharedSelectionEntryGroups',
                'forceEntries', 'categoryEntries',
                'entryLinks', 'infoLinks', 'categoryLinks', 'rules', 'sharedRules',
                'profiles', 'sharedProfiles',
            ];
            function search(entries, id, depth) {
                if (!entries || depth > 10) return null;
                for (const entry of entries) {
                    if (!entry || typeof entry !== 'object') continue;
                    if (entry.id === id) return entry.name;
                    for (const col of COLLECTIONS) {
                        const children = entry[col];
                        if (children?.length) {
                            const r = search(children, id, depth + 1);
                            if (r !== null) return r;
                        }
                    }
                }
                return null;
            }
            for (const cat of Object.values(gsSys.loadedCatalogues ?? {})) {
                for (const col of COLLECTIONS) {
                    const entries = cat[col];
                    if (entries?.length) {
                        const r = search(entries, entryId, 0);
                        if (r !== null) return r;
                    }
                }
            }
            return null;
        }
        """;


    /// <summary>
    /// Adds a link (entryLink, infoLink, categoryLink) to the given parent.
    ///
    /// When <paramref name="parentId"/> matches the currently open catalogue ID, drives the
    /// catalogue root's section header context menu to add the link, then sets the target field.
    ///
    /// Nested-parent support (parentId is a child entry) is not yet implemented.
    /// </summary>
    public static async Task<GameDataActionOutputs> AddLinkAsync(
        IPage page, string parentId, string linkType, string targetId)
    {
        var catalogueId = await GetCurrentCatalogueIdAsync(page);

        if (parentId == catalogueId)
        {
            return await AddLinkToRootSectionAsync(page, linkType, targetId);
        }

        throw new NotSupportedException(
            $"NR Editor UI: adding links under non-root parent '{parentId}' is not yet implemented. " +
            $"Currently open catalogue: '{catalogueId}'. " +
            "Use `bs-spec probe` to discover nested tree selectors and extend AddLinkAsync.");
    }

    // ===== Internal helpers =====

    /// <summary>
    /// Finds the <c>h3.normalTitle</c> DOM element for an entry in the NR Editor catalogue tree.
    ///
    /// The NR Editor does NOT render <c>data-id</c> attributes on tree nodes — entries can only
    /// be located by their display name. This method:
    /// <list type="number">
    ///   <item>Queries the Pinia store to get the entry's display name and which top-level
    ///     collection it belongs to (e.g. <c>selectionEntries</c>).</item>
    ///   <item>Expands every <c>depth-0</c> section whose CSS class matches that collection,
    ///     so entries become visible.</item>
    ///   <item>Returns a Playwright locator for the <c>h3.normalTitle</c> element inside
    ///     a <c>depth-1</c> container whose text matches the entry name.</item>
    /// </list>
    ///
    /// After this method returns, callers can click the locator to select the entry and
    /// open its properties panel.
    /// </summary>
    internal static async Task<ILocator> FindTreeNodeByIdAsync(IPage page, string entryId)
    {
        // Step 1: Look up the entry's name and collection key in the Pinia editorStore.
        // NR Editor stores catalogue data as:
        //   pinia._s.get('editor').gameSystems[systemId].loadedCatalogues[catalogueId]
        var entryJson = await page.EvaluateAsync<string?>("""
            (entryId) => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const ed = pinia?._s?.get('editor');
                    const sId = new URLSearchParams(window.location.search).get('systemId');
                    const cId = new URLSearchParams(window.location.search).get('id');
                    const cat = ed?.gameSystems?.[sId]?.loadedCatalogues?.[cId];
                    if (!cat) return null;
                    const cols = [
                        'selectionEntries','categoryEntries','selectionEntryGroups',
                        'forceEntries','entryLinks','infoLinks','categoryLinks',
                        'rules','profiles','infoGroups','profileTypes','costTypes','publications',
                        'sharedSelectionEntries','sharedSelectionEntryGroups',
                        'sharedProfiles','sharedRules','sharedInfoGroups'
                    ];
                    // Recursive search so nested entries (children of children) resolve too.
                    // Returns the entry name and the key of the collection that directly holds it.
                    let result = null;
                    const search = (obj) => {
                        if (result || !obj || typeof obj !== 'object') return;
                        for (const col of cols) {
                            const arr = obj[col];
                            if (!Array.isArray(arr)) continue;
                            for (const e of arr) {
                                if (e && e.id === entryId) { result = { name: e.name, col: col }; return; }
                                search(e);
                                if (result) return;
                            }
                        }
                    };
                    search(cat);
                    return result ? JSON.stringify(result) : null;
                } catch (ex) {
                    return null;
                }
            }
            """, entryId)
            ?? throw new InvalidOperationException(
                $"NR Editor UI: entry '{entryId}' not found in any catalogue collection via Pinia. " +
                "Ensure setup ran correctly, NavigateToCatalogueAsync completed, and the entry exists.");

        var info = System.Text.Json.JsonSerializer.Deserialize<EntryLocationInfo>(entryJson,
            JsonOptions) ?? throw new InvalidOperationException(
                $"NR Editor UI: failed to deserialize entry info for '{entryId}'.");
        var entryName = info.Name ?? entryId;
        var collectionCssClass = info.Col!;

        // Step 2: Expand all depth-0 section containers for this collection type so entries
        // become visible. Sections start collapsed and must be expanded before their children
        // render as h3.normalTitle elements in the DOM.
        //
        // Vue.js may not have finished rendering when we get here. Wait for the first
        // section element to appear before counting, otherwise CountAsync returns 0.
        var sections = page.Locator($".{collectionCssClass}.depth-0");
        try
        {
            await sections.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 10_000,
            });
        }
        catch (TimeoutException)
        {
            // Section may not exist (empty catalogue); let the final WaitForAsync report the failure.
        }

        var sectionCount = await sections.CountAsync();
        for (var i = 0; i < sectionCount; i++)
        {
            var section = sections.Nth(i);
            var isCollapsed = await section.EvaluateAsync<bool>(
                "el => el.classList.contains('collapsed')");
            if (isCollapsed)
            {
                // Use JS click to bypass Playwright actionability checks (viewport, headless, etc.).
                // This is equivalent to document.querySelector('.arrow-wrap').click() which
                // was confirmed to expand collapsible sections in probe sessions.
                await section.EvaluateAsync("el => el.querySelector('.arrow-wrap')?.click()");
                // Reactive: wait for this section to lose its `collapsed` class (children rendered)
                // instead of a fixed delay. Best-effort — Step 3's node visibility wait is the real
                // gate, so a section that never toggles (e.g. no arrow-wrap) doesn't fail here.
                try
                {
                    var handle = await section.ElementHandleAsync();
                    await page.WaitForFunctionAsync(
                        "el => !el.classList.contains('collapsed')",
                        handle,
                        new PageWaitForFunctionOptions { Timeout = 2_000 });
                }
                catch
                {
                    // Section didn't toggle cleanly — continue; the final node wait is authoritative.
                }
            }
        }

        // Step 2b: Expand collapsed parent entry nodes too, so nested entries (children of
        // children) render in the DOM. Parent nodes are <h3 class="... arrowTitle collapsed">;
        // clicking their .arrow-wrap toggles them open. Loop until none remain collapsed (the
        // tree reveals one level per pass) with a bounded pass count as a safety net.
        for (var pass = 0; pass < 8; pass++)
        {
            var collapsedCount = await page.Locator("h3.arrowTitle.collapsed").CountAsync();
            if (collapsedCount == 0)
            {
                break;
            }

            for (var i = 0; i < collapsedCount; i++)
            {
                var collapsed = page.Locator("h3.arrowTitle.collapsed").First;
                if (await collapsed.CountAsync() == 0)
                {
                    break;
                }

                // Resolve the element handle first so the click and the wait target the SAME node
                // (re-resolving `.First` after the click could race onto a different collapsed node).
                var handle = await collapsed.ElementHandleAsync();
                await handle.EvaluateAsync("el => (el.querySelector('.arrow-wrap') || el).click()");
                // Reactive: wait for this node to lose `collapsed` (its children now render) instead
                // of a fixed delay. Best-effort — the bounded outer pass loop is the safety net.
                try
                {
                    await page.WaitForFunctionAsync(
                        "el => !el.classList.contains('collapsed')",
                        handle,
                        new PageWaitForFunctionOptions { Timeout = 2_000 });
                }
                catch
                {
                    // Node didn't toggle cleanly — continue; the outer loop re-checks what remains.
                }
            }
        }

        // Step 3: Return a locator for the entry title element. Entries render as
        // collapsible-box divs containing an <h3> element, at any tree depth.
        // Leaf entries:   <h3 class="title normalTitle">
        // Parent entries: <h3 class="title arrowTitle collapsed">  (can also be "opened")
        // Both variants are matched with :is(.normalTitle, .arrowTitle); the section-header
        // h3 (e.g. "Root Selection Entries") is excluded by the entry-name text filter.
        var nodeLocator = page.Locator($".{collectionCssClass} h3:is(.normalTitle, .arrowTitle)")
            .Filter(new LocatorFilterOptions { HasText = entryName });

        await nodeLocator.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        return nodeLocator.First;
    }

    private sealed record EntryLocationInfo(string? Name, string? Col);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Returns the context menu label for adding an entry of the given type.
    /// </summary>
    private static string GetAddMenuLabel(string entryType) => entryType switch
    {
        "selectionEntry" => "Selection Entry",
        "selectionEntryGroup" => "Selection Entry Group",
        "forceEntry" => "Force Entry",
        "categoryEntry" => "Category Entry",
        "rule" => "Rule",
        "profile" => "Profile",
        "profileType" => "Profile Type",
        "costType" => "Cost Type",
        "publication" => "Publication",
        "entryLink" => "Entry Link",
        "infoLink" => "Info Link",
        "categoryLink" => "Category Link",
        "constraint" => "Constraint",
        "modifier" => "Modifier",
        "modifierGroup" => "Modifier Group",
        "condition" => "Condition",
        "conditionGroup" => "Condition Group",
        _ => entryType, // Pass through for future types
    };

    /// <summary>
    /// Returns the human-readable label used in the NR Editor's properties panel for a field.
    /// </summary>
    private static string GetFieldLabel(string field) => field switch
    {
        "name" => "Name",
        "hidden" => "Hidden",
        "type" => "Type",
        "import" => "Import",
        "imported" => "Import",
        "collective" => "Collective",
        "defaultAmount" => "Default Amount",
        "defaultCostLimit" => "Default Cost Limit",
        "page" => "Page",
        "publicationId" => "Publication",
        "typeId" => "Profile Type",
        "typeName" => "Profile Type",
        "defaultSelectionEntryId" => "Default Selection",
        "targetId" => "Target:",
        // Publication editor labels (NR uses "Publication:" for the publisher field).
        "shortName" => "Short Name",
        "publisher" => "Publication",
        "publicationDate" => "Publication Date",
        "publisherUrl" => "Publication URL",
        _ => char.ToUpperInvariant(field[0]) + field[1..], // Capitalize first letter
    };




    /// <summary>
    /// Returns the ID of the currently open catalogue by reading the <c>id</c> query
    /// parameter from the page URL (e.g. <c>.../catalogue?systemId=gs-1&amp;id=cat-1</c>).
    /// </summary>
    internal static Task<string> GetCurrentCatalogueIdAsync(IPage page)
        => NrEditorStore.GetCurrentCatalogueIdAsync(page);

    /// <summary>
    /// Adds a new entry to a top-level section in the catalogue editor by driving the
    /// collapsible section header's context menu.
    ///
    /// NR Editor layout (catalogue editor page):
    /// <list type="bullet">
    ///   <item>Each data array renders as <c>&lt;div class="collapsible-box {sectionClass} depth-0"&gt;</c>.</item>
    ///   <item>The <c>&lt;h3&gt;</c> inside that div opens a context menu on right-click.</item>
    ///   <item>Context menu items are <c>&lt;li class="context-menu"&gt;&lt;div&gt;&lt;img src="...{entryType}.png"&gt;&lt;/div&gt;&lt;/li&gt;</c>.</item>
    ///   <item>After clicking add, NR creates "New {Type}" and opens a properties form in the right panel.</item>
    ///   <item>The form is a table where each row is <c>&lt;tr&gt;&lt;td&gt;{Label}&lt;/td&gt;&lt;td&gt;&lt;input&gt;&lt;/td&gt;&lt;/tr&gt;</c>.</item>
    /// </list>
    /// </summary>
    internal static async Task<GameDataActionOutputs> AddEntryToRootSectionAsync(
        IPage page, string entryType, string? name)
    {
        var sectionClass = GetSectionCssClass(entryType);

        // Right-click the section header to open the context menu. The header is the depth-0
        // section box's *direct* child <h3>; once entries exist they add deeper descendant
        // <h3>s, so scope to the direct child to avoid a strict-mode multi-match.
        await page.Locator($".{sectionClass}.depth-0 > h3").ClickAsync(
            new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

        // Click the add menu item — identified by the entry type icon image. Shared root entries
        // reuse the base entry's icon (e.g. a sharedSelectionEntry uses the selectionEntry icon).
        var iconType = entryType.StartsWith("shared", StringComparison.Ordinal)
            ? char.ToLowerInvariant(entryType["shared".Length]) + entryType[("shared".Length + 1)..]
            : entryType;
        await page.Locator($".context-menu div:has(img[src*=\"{iconType}\"])").ClickAsync(
            new LocatorClickOptions { Timeout = 5_000 });

        // Wait for the properties form to open — the "Unique ID" row is the reliable signal
        var idRow = page.Locator("tr").Filter(new LocatorFilterOptions { HasText = "Unique ID" });
        await idRow.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        // Read the auto-generated entry ID from the Unique ID cell's input
        var entryId = await idRow.Locator("td:last-child input[type='text']").InputValueAsync();

        // Set the name if provided — find the Name row and replace its input value
        if (name is not null)
        {
            var nameRow = page.Locator("tr").Filter(new LocatorFilterOptions { HasText = "Name" });
            var nameInput = nameRow.Locator("td:last-child input[type='text']").First;
            await nameInput.ClickAsync(new LocatorClickOptions { ClickCount = 3 });
            await nameInput.FillAsync(name);
            await nameInput.PressAsync("Tab");
            await page.WaitForTimeoutAsync(200);
        }

        return new GameDataActionOutputs { EntryId = entryId };
    }

    /// <summary>
    /// Adds a new link to a top-level section in the catalogue editor.
    ///
    /// The entryLinks section has CSS class <c>entryLinks</c> (combined with selectionEntries).
    /// Right-clicking its header opens a context menu; the link type is identified by its
    /// icon image src. After creating the link, sets the <c>targetId</c> field in the
    /// properties panel.
    /// </summary>
    internal static async Task<GameDataActionOutputs> AddLinkToRootSectionAsync(
        IPage page, string linkType, string targetId)
    {
        var sectionClass = GetSectionCssClass(linkType);

        // Right-click the section header (depth-0 box's direct child <h3> — see
        // AddEntryToRootSectionAsync) to open the context menu.
        await page.Locator($".{sectionClass}.depth-0 > h3").ClickAsync(
            new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

        // From probe: context menu for section header shows two items:
        //   • "Entry" (with selectionEntry.png icon)
        //   • "Link " (with link.png icon — NOT "entryLink.png")
        // Filter by text "Link" rather than icon src to be robust against icon name changes.
        var linkMenuItem = page.Locator(".context-menu > div")
            .Filter(new LocatorFilterOptions { HasText = "Link" });
        await linkMenuItem.First.ClickAsync(new() { Timeout = 5_000 });

        // Wait for the properties form to open — "Unique ID" row is the reliable signal
        var idRow = page.Locator("tr").Filter(new LocatorFilterOptions { HasText = "Unique ID" });
        await idRow.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        // Read the auto-generated entry ID
        var entryId = await idRow.Locator("td:last-child input[type='text']").InputValueAsync();

        // Set the target entry via the properties panel Target: autocomplete widget. NR filters
        // that list by the link's "Link Type", so align the type to the target's kind first.
        var rightPanel = page.Locator(".rightPanel");
        await SetLinkTypeFromTargetAsync(page, rightPanel, targetId);
        var targetFieldLabel = GetFieldLabel("targetId");
        var targetName = await page.EvaluateAsync<string?>(EntryNameLookupJs, targetId);
        await SetAutocompleteFieldAsync(page, rightPanel, targetFieldLabel, targetId, targetName);

        return new GameDataActionOutputs { EntryId = entryId };
    }


    /// <summary>
    /// Maps an entry type to the CSS class of its collapsible section on the NR Editor
    /// catalogue editor page. NR renders each data array as:
    /// <c>&lt;div class="collapsible-box {sectionClass} depth-0"&gt;</c>
    /// </summary>
    private static string GetSectionCssClass(string entryType) => entryType switch
    {
        "categoryEntry" => "categoryEntries",
        "selectionEntry" => "selectionEntries",
        "selectionEntryGroup" => "sharedSelectionEntryGroups",
        "forceEntry" => "forceEntries",
        "rule" => "rules",
        "publication" => "publications",
        "costType" => "costTypes",
        "profileType" => "profileTypes",
        "sharedSelectionEntry" => "sharedSelectionEntries",
        "sharedSelectionEntryGroup" => "sharedSelectionEntryGroups",
        "sharedRule" => "sharedRules",
        "sharedProfile" => "sharedProfiles",
        "sharedInfoGroup" => "sharedInfoGroups",
        _ => entryType + "s",
    };

    private static async Task<bool> IsCheckboxAsync(ILocator input)
    {
        try
        {
            return await input.EvaluateAsync<bool>("el => el.type === 'checkbox'");
        }
        catch
        {
            return false;
        }
    }
}
