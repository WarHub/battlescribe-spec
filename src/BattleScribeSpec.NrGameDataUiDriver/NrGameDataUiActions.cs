using BattleScribeSpec.GameData;
using Microsoft.Playwright;

#pragma warning disable IDE0060 // Remove unused parameter — index reserved for future ordering support

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
    /// Locates the parent node in the NR Editor tree, opens its context menu, and
    /// selects the matching "Add [type]" action.
    /// Returns the ID assigned by NR to the newly created entry.
    /// </summary>
    public static async Task<GameDataActionOutputs> AddEntryAsync(
        IPage page, string parentId, string entryType, string? name)
    {
        // Step 1: Locate the parent node in the tree by its data-id attribute
        var parentNode = await FindTreeNodeByIdAsync(page, parentId);

        // Step 2: Right-click to open context menu
        await parentNode.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

        // Step 3: Select the add action matching entryType
        var menuLabel = GetAddMenuLabel(entryType);
        var menuItem = page.GetByRole(AriaRole.Menuitem, new() { Name = menuLabel })
            .Or(page.GetByText(menuLabel).Filter(new LocatorFilterOptions { Has = page.Locator("[role='menuitem'], [class*='menu-item'], [class*='menuItem']") }))
            .Or(page.GetByText(menuLabel, new PageGetByTextOptions { Exact = false }));
        await menuItem.First.ClickAsync(new() { Timeout = 5_000 });
        await page.WaitForTimeoutAsync(500);

        // Step 4: If a name was provided, fill in the name field in the dialog/inline editor
        if (name is not null)
        {
            await SetNewEntryNameAsync(page, name);
        }

        // Step 5: Read back the ID of the newly created entry via JS
        var newId = await ReadLastCreatedEntryIdAsync(page, parentId, entryType);

        // Step 6: Confirm/close any open dialog
        await DismissActiveDialogAsync(page);

        return new GameDataActionOutputs { EntryId = newId };
    }

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

        // Click Delete / Remove in the context menu
        var deleteItem = page.GetByRole(AriaRole.Menuitem, new() { Name = "Delete" })
            .Or(page.GetByRole(AriaRole.Menuitem, new() { Name = "Remove" }))
            .Or(page.GetByText("Delete").Filter(new LocatorFilterOptions { Has = page.Locator("[role='menuitem']") }));
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
    /// Moves an entry to a new parent in the NR Editor tree.
    /// Uses context menu "Move to" or JS store manipulation as fallback
    /// (drag-and-drop is unreliable in Playwright for tree views).
    /// </summary>
    public static async Task MoveEntryAsync(IPage page, string entryId, string newParentId, int? index)
    {
        // Try context menu "Move" action first
        var node = await FindTreeNodeByIdAsync(page, entryId);
        await node.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

        var moveItem = page.GetByRole(AriaRole.Menuitem, new() { Name = "Move" })
            .Or(page.GetByText("Move to"));
        if (await moveItem.First.IsVisibleAsync())
        {
            await moveItem.First.ClickAsync();
            await page.WaitForTimeoutAsync(300);
            // In the move dialog, pick the new parent
            var targetNode = page.GetByText(newParentId, new PageGetByTextOptions { Exact = false });
            if (await targetNode.First.IsVisibleAsync())
            {
                await targetNode.First.ClickAsync();
                await page.WaitForTimeoutAsync(300);
            }
            await DismissActiveDialogAsync(page);
        }
        else
        {
            // Close context menu and fall back to store-level move via JS
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(200);

            var result = await page.EvaluateAsync<string?>("""
                ([entryId, newParentId, index]) => {
                    try {
                        const ctx = window.__bsspec_editor_ui;
                        if (!ctx?.editorStore) return 'No editor store';
                        if (ctx.editorStore.move_node) {
                            ctx.editorStore.move_node(entryId, newParentId, index);
                            return null;
                        }
                        return 'Move not supported via editorStore';
                    } catch (e) {
                        return 'MoveEntry error: ' + e.message;
                    }
                }
                """, new object[] { entryId, newParentId, index ?? -1 });

            if (result is not null)
            {
                throw new InvalidOperationException($"NR Editor UI: {result}");
            }
        }
    }

    /// <summary>
    /// Sets a field value on an entry in the NR Editor properties panel.
    /// Clicks the entry in the tree to select it, then edits the named field.
    /// </summary>
    public static async Task SetFieldAsync(IPage page, string entryId, string field, string? value)
    {
        // Click the entry to select it and open its properties
        var node = await FindTreeNodeByIdAsync(page, entryId);
        await node.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Locate the field control in the properties panel
        // NR Editor typically renders a label + input pair for each field
        var fieldLabel = GetFieldLabel(field);

        // Try label-associated input first
        var fieldInput = page.GetByLabel(fieldLabel, new PageGetByLabelOptions { Exact = false });

        if (!await fieldInput.IsVisibleAsync())
        {
            // Fall back to input/checkbox near a text that matches the field name
            fieldInput = page
                .Locator($"label:has-text('{fieldLabel}') + input, label:has-text('{fieldLabel}') + select")
                .Or(page.Locator($"[data-field='{field}'] input, [data-field='{field}'] select"));
        }

        if (value is null)
        {
            // Clear the field
            if (await IsCheckboxAsync(fieldInput))
            {
                // Uncheck if currently checked
                if (await fieldInput.IsCheckedAsync())
                { await fieldInput.UncheckAsync(); }
            }
            else
            {
                await fieldInput.FillAsync("");
            }
        }
        else if (value is "true" or "false")
        {
            // Boolean field (checkbox)
            if (value == "true")
            { await fieldInput.CheckAsync(); }
            else
            { await fieldInput.UncheckAsync(); }
        }
        else
        {
            // Try as select option first
            try
            {
                await fieldInput.SelectOptionAsync(value, new() { Timeout = 500 });
            }
            catch
            {
                // Fall back to text input
                await fieldInput.FillAsync(value);
            }
        }

        // Commit the change (Tab or Enter to trigger model update)
        await fieldInput.PressAsync("Tab");
        await page.WaitForTimeoutAsync(300);
    }

    /// <summary>
    /// Adds a link (entryLink, infoLink, categoryLink) to the given parent.
    /// Opens the link creation dialog in the NR Editor and selects the target entry.
    /// </summary>
    public static async Task<GameDataActionOutputs> AddLinkAsync(
        IPage page, string parentId, string linkType, string targetId)
    {
        var parentNode = await FindTreeNodeByIdAsync(page, parentId);
        await parentNode.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync(300);

        var menuLabel = GetAddMenuLabel(linkType);
        var menuItem = page.GetByRole(AriaRole.Menuitem, new() { Name = menuLabel })
            .Or(page.GetByText(menuLabel, new PageGetByTextOptions { Exact = false }));
        await menuItem.First.ClickAsync(new() { Timeout = 5_000 });
        await page.WaitForTimeoutAsync(500);

        // In the link dialog, select the target by its ID
        var targetInput = page.GetByPlaceholder("Search", new PageGetByPlaceholderOptions { Exact = false })
            .Or(page.Locator("[class*='search'] input, [class*='link-target'] input"));
        if (await targetInput.IsVisibleAsync())
        {
            await targetInput.First.FillAsync(targetId);
            await page.WaitForTimeoutAsync(500);
            // Click the matching result
            var resultItem = page.Locator($"[data-id='{targetId}'], [class*='result-item'], [class*='option']").First;
            if (await resultItem.IsVisibleAsync())
            {
                await resultItem.ClickAsync();
                await page.WaitForTimeoutAsync(300);
            }
        }

        // Confirm the dialog
        await DismissActiveDialogAsync(page);

        var newId = await ReadLastCreatedEntryIdAsync(page, parentId, linkType);
        return new GameDataActionOutputs { EntryId = newId };
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
                    const ctx = window.__bsspec_editor_ui;
                    const pinia = ctx?.pinia || document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    if (!pinia) return JSON.stringify({ error: 'Pinia not found' });

                    const editorStore = ctx?.editorStore
                        || pinia._s.get('editor') || pinia._s.get('editorStore')
                        || pinia._s.get('catalogue') || pinia._s.get('catalogues');

                    if (!editorStore) {
                        return JSON.stringify({ error: 'Editor store not found. Available: [' + [...pinia._s.keys()].join(', ') + ']' });
                    }

                    // Retrieve the catalogue from the editor store.
                    // The NR Editor may expose it as: catalogue, currentCatalogue, rootCatalogue, rootEntry, data
                    const catalogue = editorStore.catalogue || editorStore.currentCatalogue
                        || editorStore.rootCatalogue || editorStore.rootEntry;

                    if (!catalogue) {
                        return JSON.stringify({ error: 'No catalogue in editor store. Store keys: ' + Object.keys(editorStore).join(', ') });
                    }

                    const sysStore = ctx?.sysStore
                        || pinia._s.get('systemsStore') || pinia._s.get('systems');
                    const gameSystemData = sysStore?.currentSystem || sysStore?.system || null;

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
                                result.fields[key] = String(val);
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
    /// Finds a tree node by its data-id attribute or by traversing visible tree items.
    /// This is the primary entry point for locating entries in the NR Editor tree.
    /// </summary>
    private static async Task<ILocator> FindTreeNodeByIdAsync(IPage page, string entryId)
    {
        // Try data-id attribute first (most reliable if NR renders it)
        var byDataId = page.Locator($"[data-id='{entryId}']");
        if (await byDataId.IsVisibleAsync())
        {
            return byDataId.First;
        }

        // Fall back: find in the tree structure (NR Editor renders entries with ids in the DOM or as data attributes)
        var byTreeItem = page.Locator($"[class*='tree-node'][id='{entryId}'], [class*='entry'][id='{entryId}']");
        if (await byTreeItem.IsVisibleAsync())
        {
            return byTreeItem.First;
        }

        // Scroll the tree to find hidden items
        await page.EvaluateAsync("""
            (entryId) => {
                const el = document.querySelector(`[data-id='${entryId}']`);
                if (el) el.scrollIntoView({ behavior: 'instant', block: 'nearest' });
            }
            """, entryId);
        await page.WaitForTimeoutAsync(200);

        // Retry after scroll
        if (await byDataId.IsVisibleAsync())
        {
            return byDataId.First;
        }

        throw new InvalidOperationException(
            $"NR Editor UI: tree node for entry '{entryId}' not found in the visible tree. " +
            "Run --probe to inspect the editor's DOM structure and update FindTreeNodeByIdAsync selectors.");
    }

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
        "collective" => "Collective",
        "defaultAmount" => "Default Amount",
        "page" => "Page",
        "publicationId" => "Publication",
        "defaultSelectionEntryId" => "Default Selection",
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
                    const editorStore = pinia?._s?.get('editor') || pinia?._s?.get('editorStore')
                        || pinia?._s?.get('catalogue') || pinia?._s?.get('catalogues');
                    const catalogue = editorStore?.catalogue || editorStore?.currentCatalogue
                        || editorStore?.rootCatalogue;
                    if (!catalogue) return null;

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

                    const parent = parentId === catalogue.id ? catalogue : findById(catalogue, parentId);
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
