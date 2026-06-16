using BattleScribeSpec.GameData;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Playwright UI action helpers for NrGameDataUiEngine.
///
/// Each method drives a single IGameDataEngine operation through the NR Editor's
/// rendered catalogue tree interface. The hybrid pattern applies:
///   - Mutations: performed through real UI interactions (clicks, context menus, forms)
///   - IDs of new entries: read back via JS after each mutation
///   - State: read from NR Editor's Pinia editorStore (see <see cref="ReadStateAsync"/>)
///
/// The NR Editor shows a catalogue tree in the main panel. Entry operations are
/// accessed via right-click context menus on tree nodes. Field editing happens in
/// a properties panel (input/checkbox/select for each editable field).
///
/// <b>Selector notes:</b> Selectors target NR Editor v1.x (giloushaker/nr-editor).
/// If the editor updates its DOM structure, run the probe workflow to re-discover
/// selectors:
/// <code>
///   dotnet run --project src/BattleScribeSpec.Debugger -- --engine nr-editor-ui --probe spec-id
/// </code>
/// See .agents/skills/nr-gamedata-ui/ for the full probe workflow documentation.
/// </summary>
public static class NrGameDataUiActions
{
    // ===== Structural mutations =====

    /// <summary>
    /// Adds a new entry of the given type under the specified parent.
    ///
    /// When <paramref name="parentId"/> matches the currently open catalogue ID (read from
    /// the page URL), drives the catalogue editor's collapsible section headers:
    /// right-clicks the section <c>&lt;h3&gt;</c>, picks the icon-based context menu item,
    /// waits for the properties form, reads the auto-generated ID, and sets the name.
    ///
    /// When <paramref name="parentId"/> is a child entry, right-clicks that entry's tree node
    /// and picks the text-labelled "add child" item from its context menu.
    /// </summary>
    public static async Task<GameDataActionOutputs> AddEntryAsync(
        IPage page, string parentId, string entryType, string? name)
    {
        var catalogueId = await GetCurrentCatalogueIdAsync(page);

        if (parentId == catalogueId)
        {
            return await AddEntryToRootSectionAsync(page, entryType, name);
        }

        return await AddEntryToParentNodeAsync(page, parentId, entryType, name);
    }

    /// <summary>
    /// Adds a child entry under a non-root parent entry by driving the parent tree node's
    /// context menu.
    ///
    /// The entry-node context menu lists "add child" items as text-labelled <c>&lt;div&gt;</c>
    /// elements ("Entry" for a selection entry, "Group" for a selection entry group). Their
    /// icons are inline base64 data URIs, so the item is matched by exact text — anchored to
    /// avoid "Group" matching "Modifier Group" / "Info Group".
    /// </summary>
    private static async Task<GameDataActionOutputs> AddEntryToParentNodeAsync(
        IPage page, string parentId, string entryType, string? name)
    {
        var parentNode = await FindTreeNodeByIdAsync(page, parentId);
        await parentNode.ScrollIntoViewIfNeededAsync();
        await parentNode.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

        var label = GetAddChildMenuLabel(entryType);
        await page.Locator(".context-menu > div")
            .Filter(new LocatorFilterOptions
            {
                HasTextRegex = new System.Text.RegularExpressions.Regex($"^\\s*{label}\\s*$"),
            })
            .First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });

        // Wait for the properties form to open — the "Unique ID" row is the reliable signal.
        var idRow = page.Locator("tr").Filter(new LocatorFilterOptions { HasText = "Unique ID" });
        await idRow.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });

        var entryId = await idRow.Locator("td:last-child input[type='text']").InputValueAsync();

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
    /// Returns the context-menu text label for adding a child entry of the given type from an
    /// entry node's context menu (distinct from the root-section icon items).
    /// </summary>
    private static string GetAddChildMenuLabel(string entryType) => entryType switch
    {
        "selectionEntry" => "Entry",
        "selectionEntryGroup" => "Group",
        "profile" => "Profile",
        "rule" => "Rule",
        "infoGroup" => "Info Group",
        _ => GetAddMenuLabel(entryType),
    };

    /// <summary>
    /// Removes the entry with the given ID from the NR Editor tree.
    /// Locates the node, opens context menu, and clicks Delete/Remove.
    /// Confirms any deletion dialog.
    /// </summary>
    public static async Task RemoveEntryAsync(IPage page, string entryId)
    {
        var node = await FindTreeNodeByIdAsync(page, entryId);

        await node.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

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
    public static async Task SetFieldAsync(IPage page, string entryId, string field, string? value)
    {
        // Click the entry to select it and open its properties panel
        var node = await FindTreeNodeByIdAsync(page, entryId);
        await node.ClickAsync();

        // Wait for properties to appear — "Unique ID" row is the reliable signal
        await page.Locator(".rightPanel tr")
            .Filter(new LocatorFilterOptions { HasText = "Unique ID" })
            .WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });

        var fieldLabel = GetFieldLabel(field);
        var rightPanel = page.Locator(".rightPanel");

        // Several fields use NR Editor's custom autocomplete widget (not a standard input/select).
        // Handle these before the generic input strategies.
        if (field is "publicationId" or "defaultSelectionEntryId" or "targetId")
        {
            if (value is not null)
            {
                var lookupJs = field == "publicationId" ? PublicationNameLookupJs : EntryNameLookupJs;
                var displayName = await page.EvaluateAsync<string?>(lookupJs, value);
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
            fieldInput = rightPanel.Locator("table.editorTable tr")
                .Filter(new LocatorFilterOptions { HasText = fieldLabel })
                .Locator("td:last-child input, td:last-child select")
                .First;
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
        await fieldRow.Locator(".autocomplete-input").ClickAsync();
        await page.WaitForTimeoutAsync(300);

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
            const pubs = gsSys?.loadedCatalogues?.[systemId]?.publications ?? [];
            return pubs.find(p => p.id === pubId)?.name ?? null;
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
            "Use --probe to discover nested tree selectors and extend AddLinkAsync.");
    }

    // ===== State reads =====

    /// <summary>
    /// Reads the current GameDataState from NR Editor's Pinia editorStore.
    /// Called after each mutation to assert expected state in specs.
    /// </summary>
    public static async Task<GameDataState> ReadStateAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("""
            () => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return JSON.stringify({ error: 'Pinia not found' });

                    // Resolve the currently open catalogue and system IDs from the page URL:
                    // .../catalogue?systemId=gs-1&id=cat-1
                    const params = new URLSearchParams(window.location.search);
                    const catId = params.get('id');
                    const systemId = params.get('systemId');
                    if (!catId) {
                        return JSON.stringify({ error: 'No catalogue ID in URL — not on catalogue page? URL: ' + window.location.href });
                    }

                    // The actual catalogue data lives in editor.gameSystems[systemId].loadedCatalogues[catId].
                    // The 'catalogues' Pinia store is a dependency tracker (not the data store).
                    const editorStore = pinia._s.get('editor');
                    if (!editorStore) {
                        return JSON.stringify({ error: 'editor store not found. Available: [' + [...pinia._s.keys()].join(', ') + ']' });
                    }

                    const gsSys = editorStore.gameSystems?.[systemId];
                    if (!gsSys) {
                        return JSON.stringify({ error: `Game system '${systemId}' not found in editor.gameSystems` });
                    }

                    const catalogue = gsSys.loadedCatalogues?.[catId];
                    if (!catalogue) {
                        return JSON.stringify({ error: `Catalogue '${catId}' not in loadedCatalogues for system '${systemId}'` });
                    }

                    const gameSystemData = gsSys.loadedCatalogues?.[systemId] ?? null;

                    // Helper to serialize an entry node
                    const serializeEntry = (entry, entryType) => {
                        if (!entry || typeof entry !== 'object') return null;
                        const result = {
                            id: entry.id || '',
                            name: entry.name || '',
                            entryType: entryType || entry.type || '',
                            hidden: !!entry.hidden,
                            children: [],
                            fields: {},
                        };

                        const skipKeys = new Set(['id', 'name', 'hidden', 'selectionEntries', 'selectionEntryGroups',
                            'entryLinks', 'infoLinks', 'categoryLinks', 'rules', 'profiles', 'infoGroups',
                            'constraints', 'modifiers', 'modifierGroups', 'conditions', 'conditionGroups',
                            'forceEntries', 'categoryEntries', 'publications', 'costTypes', 'profileTypes', 'repeats']);

                        for (const [key, val] of Object.entries(entry)) {
                            if (skipKeys.has(key)) continue;
                            if (typeof val === 'function') continue;
                            if (val !== null && val !== undefined && !Array.isArray(val) && typeof val !== 'object') {
                                // NR stores the import flag as 'import' but the spec protocol uses 'imported'
                                const fieldKey = key === 'import' ? 'imported' : key;
                                result.fields[fieldKey] = String(val);
                            }
                        }

                        const childContainers = ['selectionEntries', 'selectionEntryGroups', 'rules',
                            'profiles', 'infoGroups', 'constraints', 'modifiers', 'modifierGroups',
                            'conditions', 'conditionGroups', 'entryLinks', 'infoLinks', 'categoryLinks',
                            'forceEntries', 'categoryEntries', 'repeats'];

                        for (const ck of childContainers) {
                            const items = entry[ck];
                            if (Array.isArray(items)) {
                                const singularType = ck.replace(/ies$/, 'y').replace(/s$/, '');
                                for (const item of items) {
                                    const child = serializeEntry(item, singularType);
                                    if (child) result.children.push(child);
                                }
                            }
                        }

                        return result;
                    };

                    const serializeContainer = (container) => {
                        if (!container) return null;
                        const r = {
                            id: container.id || '',
                            name: container.name || '',
                            gameSystemId: container.gameSystemId || container.id_game_system || '',
                            selectionEntries: [],
                            entryLinks: [],
                            rules: [],
                            sharedSelectionEntries: [],
                            sharedSelectionEntryGroups: [],
                            sharedRules: [],
                            sharedProfiles: [],
                            forceEntries: [],
                            categoryEntries: [],
                            publications: [],
                            costTypes: [],
                            profileTypes: [],
                        };

                        const mappings = [
                            ['selectionEntries', 'selectionEntries', 'selectionEntry'],
                            ['entryLinks', 'entryLinks', 'entryLink'],
                            ['rules', 'rules', 'rule'],
                            ['sharedSelectionEntries', 'sharedSelectionEntries', 'selectionEntry'],
                            ['sharedSelectionEntryGroups', 'sharedSelectionEntryGroups', 'selectionEntryGroup'],
                            ['sharedRules', 'sharedRules', 'rule'],
                            ['sharedProfiles', 'sharedProfiles', 'profile'],
                            ['forceEntries', 'forceEntries', 'forceEntry'],
                            ['categoryEntries', 'categoryEntries', 'categoryEntry'],
                            ['publications', 'publications', 'publication'],
                            ['costTypes', 'costTypes', 'costType'],
                            ['profileTypes', 'profileTypes', 'profileType'],
                        ];

                        for (const [srcKey, destKey, entryType] of mappings) {
                            const items = container[srcKey];
                            if (Array.isArray(items)) {
                                r[destKey] = items.map(e => serializeEntry(e, entryType)).filter(Boolean);
                            }
                        }
                        return r;
                    };

                    const state = { catalogues: [], gameSystem: null };
                    if (gameSystemData) state.gameSystem = serializeContainer(gameSystemData);
                    if (catalogue) state.catalogues = [serializeContainer(catalogue)];

                    return JSON.stringify(state);
                } catch (e) {
                    return JSON.stringify({ error: 'ReadState error: ' + e.message });
                }
            }
            """);

        return DeserializeState(json);
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
    private static async Task<ILocator> FindTreeNodeByIdAsync(IPage page, string entryId)
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
                        'rules','profileTypes','costTypes','publications',
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
                await page.WaitForTimeoutAsync(400);
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

                await collapsed.EvaluateAsync("el => (el.querySelector('.arrow-wrap') || el).click()");
                await page.WaitForTimeoutAsync(120);
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
        "page" => "Page",
        "publicationId" => "Publication",
        "defaultSelectionEntryId" => "Default Selection",
        "targetId" => "Target:",
        _ => char.ToUpperInvariant(field[0]) + field[1..], // Capitalize first letter
    };

    /// <summary>Sets the name of a newly created entry in any inline editor or dialog.</summary>
    private static async Task SetNewEntryNameAsync(IPage page, string name)
    {
        // NR Editor may show an inline rename editor or a "New entry" dialog
        var nameInput = page.GetByPlaceholder("Name", new PageGetByPlaceholderOptions { Exact = false })
            .Or(page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }))
            .Or(page.Locator("[class*='new-entry'] input, [class*='rename'] input").First);

        if (await nameInput.First.IsVisibleAsync())
        {
            await nameInput.First.FillAsync(name);
            await nameInput.First.PressAsync("Enter");
            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>
    /// Reads the ID of the most recently created entry under parentId of the given type,
    /// by querying the Pinia editorStore.
    /// </summary>
    private static async Task<string?> ReadLastCreatedEntryIdAsync(
        IPage page, string parentId, string entryType)
    {
        var containerKey = entryType switch
        {
            "selectionEntry" => "selectionEntries",
            "selectionEntryGroup" => "selectionEntryGroups",
            "forceEntry" => "forceEntries",
            "categoryEntry" => "categoryEntries",
            "rule" => "rules",
            "profile" => "sharedProfiles",
            "entryLink" => "entryLinks",
            "infoLink" => "infoLinks",
            "categoryLink" => "categoryLinks",
            _ => entryType + "s",
        };

        return await page.EvaluateAsync<string?>("""
            ([parentId, containerKey]) => {
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const ed = pinia?._s?.get('editor');
                    const sId = new URLSearchParams(window.location.search).get('systemId');
                    const cId = new URLSearchParams(window.location.search).get('id');
                    const cat = ed?.gameSystems?.[sId]?.loadedCatalogues?.[cId];
                    if (!cat) return null;

                    const findById = (obj, id) => {
                        if (!obj || typeof obj !== 'object') return null;
                        if (obj.id === id) return obj;
                        for (const val of Object.values(obj)) {
                            if (Array.isArray(val)) {
                                for (const item of val) {
                                    const found = findById(item, id);
                                    if (found) return found;
                                }
                            }
                        }
                        return null;
                    };

                    const parent = parentId === cat.id ? cat : findById(cat, parentId);
                    if (!parent) return null;

                    const items = parent[containerKey];
                    if (!Array.isArray(items) || items.length === 0) return null;

                    // Return the last item's ID (the most recently added)
                    return items[items.length - 1]?.id || null;
                } catch (e) {
                    return null;
                }
            }
            """, new object[] { parentId, containerKey });
    }

    /// <summary>Dismisses any open modal dialog (confirm/cancel button).</summary>
    private static async Task DismissActiveDialogAsync(IPage page)
    {
        var confirmBtn = page.GetByRole(AriaRole.Button, new() { Name = "OK" })
            .Or(page.GetByRole(AriaRole.Button, new() { Name = "Save" }))
            .Or(page.GetByRole(AriaRole.Button, new() { Name = "Confirm" }))
            .Or(page.GetByRole(AriaRole.Button, new() { Name = "Close" }));
        if (await confirmBtn.First.IsVisibleAsync())
        {
            await confirmBtn.First.ClickAsync();
            await page.WaitForTimeoutAsync(200);
        }
    }

    /// <summary>
    /// Returns the ID of the currently open catalogue by reading the <c>id</c> query
    /// parameter from the page URL (e.g. <c>.../catalogue?systemId=gs-1&amp;id=cat-1</c>).
    /// </summary>
    private static async Task<string> GetCurrentCatalogueIdAsync(IPage page)
        => await page.EvaluateAsync<string>(
            "() => new URLSearchParams(window.location.search).get('id') ?? ''") ?? "";

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
    private static async Task<GameDataActionOutputs> AddEntryToRootSectionAsync(
        IPage page, string entryType, string? name)
    {
        var sectionClass = GetSectionCssClass(entryType);

        // Right-click the section header to open the context menu. The header is the depth-0
        // section box's *direct* child <h3>; once entries exist they add deeper descendant
        // <h3>s, so scope to the direct child to avoid a strict-mode multi-match.
        await page.Locator($".{sectionClass}.depth-0 > h3").ClickAsync(
            new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

        // Click the add menu item — identified by the entry type icon image
        await page.Locator($".context-menu div:has(img[src*=\"{entryType}\"])").ClickAsync(
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
    private static async Task<GameDataActionOutputs> AddLinkToRootSectionAsync(
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

        // Set the target entry via the properties panel Target: autocomplete widget
        var rightPanel = page.Locator(".rightPanel");
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

    private static GameDataState DeserializeState(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorProp))
        {
            throw new InvalidOperationException($"ReadState failed: {errorProp.GetString()}");
        }

        var catalogues = new List<CatalogueDataState>();
        if (root.TryGetProperty("catalogues", out var catsEl))
        {
            foreach (var catEl in catsEl.EnumerateArray())
            { catalogues.Add(DeserializeCatalogue(catEl)); }
        }

        GameSystemDataState? gameSystem = null;
        if (root.TryGetProperty("gameSystem", out var gsEl) && gsEl.ValueKind != System.Text.Json.JsonValueKind.Null)
        { gameSystem = DeserializeGameSystem(gsEl); }

        return new GameDataState { GameSystem = gameSystem, Catalogues = catalogues };
    }

    private static CatalogueDataState DeserializeCatalogue(System.Text.Json.JsonElement el) =>
        new()
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Name = el.GetProperty("name").GetString() ?? "",
            GameSystemId = el.GetProperty("gameSystemId").GetString() ?? "",
            SelectionEntries = DeserializeEntryList(el, "selectionEntries"),
            EntryLinks = DeserializeEntryList(el, "entryLinks"),
            Rules = DeserializeEntryList(el, "rules"),
            SharedSelectionEntries = DeserializeEntryList(el, "sharedSelectionEntries"),
            SharedSelectionEntryGroups = DeserializeEntryList(el, "sharedSelectionEntryGroups"),
            SharedRules = DeserializeEntryList(el, "sharedRules"),
            SharedProfiles = DeserializeEntryList(el, "sharedProfiles"),
            ForceEntries = DeserializeEntryList(el, "forceEntries"),
            CategoryEntries = DeserializeEntryList(el, "categoryEntries"),
            Publications = DeserializeEntryList(el, "publications"),
            CostTypes = DeserializeEntryList(el, "costTypes"),
            ProfileTypes = DeserializeEntryList(el, "profileTypes"),
        };

    private static GameSystemDataState DeserializeGameSystem(System.Text.Json.JsonElement el) =>
        new()
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Name = el.GetProperty("name").GetString() ?? "",
            SelectionEntries = DeserializeEntryList(el, "selectionEntries"),
            EntryLinks = DeserializeEntryList(el, "entryLinks"),
            Rules = DeserializeEntryList(el, "rules"),
            SharedSelectionEntries = DeserializeEntryList(el, "sharedSelectionEntries"),
            SharedSelectionEntryGroups = DeserializeEntryList(el, "sharedSelectionEntryGroups"),
            SharedRules = DeserializeEntryList(el, "sharedRules"),
            SharedProfiles = DeserializeEntryList(el, "sharedProfiles"),
            ForceEntries = DeserializeEntryList(el, "forceEntries"),
            CategoryEntries = DeserializeEntryList(el, "categoryEntries"),
            CostTypes = DeserializeEntryList(el, "costTypes"),
            ProfileTypes = DeserializeEntryList(el, "profileTypes"),
            Publications = DeserializeEntryList(el, "publications"),
        };

    private static IReadOnlyList<DataEntryState> DeserializeEntryList(
        System.Text.Json.JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
        { return []; }

        var entries = new List<DataEntryState>();
        foreach (var el in arr.EnumerateArray())
        { entries.Add(DeserializeEntry(el)); }
        return entries;
    }

    private static DataEntryState DeserializeEntry(System.Text.Json.JsonElement el)
    {
        var fields = new Dictionary<string, string?>();
        if (el.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in fieldsEl.EnumerateObject())
            { fields[prop.Name] = prop.Value.GetString(); }
        }

        var children = new List<DataEntryState>();
        if (el.TryGetProperty("children", out var childrenEl) && childrenEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var childEl in childrenEl.EnumerateArray())
            { children.Add(DeserializeEntry(childEl)); }
        }

        return new DataEntryState
        {
            Id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            Name = el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            EntryType = el.TryGetProperty("entryType", out var typeEl) ? typeEl.GetString() ?? "" : "",
            Hidden = el.TryGetProperty("hidden", out var hiddenEl) && hiddenEl.GetBoolean(),
            Children = children,
            Fields = fields.Count > 0 ? fields : null,
        };
    }
}
