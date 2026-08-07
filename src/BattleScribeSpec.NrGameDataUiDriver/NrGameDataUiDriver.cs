using BattleScribeSpec.GameData;
using BattleScribeSpec.NewRecruit;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Stateful, purely UI-driven mutation driver for the NR Editor GameData engine.
///
/// Every data change goes through the rendered NR Editor interface — context menus, the
/// right-panel property widgets, autocompletes and contenteditable fields. The Pinia store is
/// only ever <b>read</b> (to resolve display names / catalogue ids), never written.
///
/// Node identity: named entities expose a "Unique ID" field in their editor; advanced query
/// entities (modifier/condition/repeat/groups) are id-less in NR, so they are tracked by the
/// fact that the editor auto-selects a node when it is created or edited. The driver therefore
/// keeps the "currently selected" token and a child→parent map, and selects a target via:
///   1. it is already the selected node, or
///   2. it is a named entity (located + clicked in the tree), or
///   3. it is the parent of the selected node (clicked via the right-panel breadcrumb).
/// </summary>
public sealed class NrGameDataUiDriver
{
    private readonly IPage _page;
    private string? _selectedToken;
    private readonly Dictionary<string, string?> _parentOf = new(StringComparer.Ordinal);
    private readonly HashSet<string> _idless = new(StringComparer.Ordinal);
    private int _ctr;

    public NrGameDataUiDriver(IPage page) => _page = page;

    /// <summary>Clears per-spec selection state (the engine/page are reused across specs).</summary>
    public void Reset()
    {
        _selectedToken = null;
        _parentOf.Clear();
        _idless.Clear();
        _ctr = 0;
    }

    private string NewSyntheticToken() => $"__nr{++_ctr}";

    /// <summary>Entry types whose NR editor has no Unique ID and uses a bespoke query editor.</summary>
    private static readonly HashSet<string> Idless = new(StringComparer.Ordinal)
    {
        "modifier", "modifierGroup", "condition", "conditionGroup", "repeat",
    };

    // ===== Public operations =====

    public async Task OpenFileAsync(string id)
    {
        await NrEditorStore.NavigateToFileAsync(_page, id);
        _selectedToken = null;
        _parentOf.Clear();
    }

    public async Task<GameDataActionOutputs> AddEntryAsync(string parentId, string entryType, string? name, string? declaredId = null)
    {
        var rootId = await NrGameDataUiActions.GetCurrentCatalogueIdAsync(_page);

        string token;
        if (parentId == rootId)
        {
            // Root section add through the section-header context menu.
            var outputs = await NrGameDataUiActions.AddEntryToRootSectionAsync(_page, entryType, name);
            token = outputs.EntryId ?? NewSyntheticToken();
        }
        else
        {
            await SelectAsync(parentId);
            // Record what is selected BEFORE the add, so the wait below can assert the selection
            // moved to the new node rather than that a panel is on screen.
            await MarkSelectionAsync();
            await RightClickSelectedAsync();
            if (entryType == "profile")
            {
                // "Profile ❯" submenu lists the profile types; pick the first (only one in specs).
                await OpenSubmenuAndPickAsync("Profile", null);
            }
            else if (LinkAddTypes.Contains(entryType))
            {
                // "Link ❯" submenu lists target kinds (Entry/Group/Profile/Rule/InfoGroup/Association).
                // The choice sets the container (entryLinks vs infoLinks); a later targetId aligns the
                // exact type. Pick a representative kind for the requested link family.
                await OpenSubmenuAndPickAsync("Link", LinkSubmenuItemForType(entryType));
            }
            else
            {
                await ClickContextItemAsync(AddChildLabel(entryType));
            }
            await WaitEditorReadyAsync();
            var uid = await ReadUniqueIdAsync();
            token = uid ?? NewSyntheticToken();
            if (uid is null)
            {
                _idless.Add(token);
            }
            if (name is not null)
            {
                await SetNameInEditorAsync(name);
            }
        }

        token = await ApplyDeclaredIdAsync(token, declaredId);
        _parentOf[token] = parentId;
        _selectedToken = token;
        return new GameDataActionOutputs { EntryId = token };
    }

    public async Task<GameDataActionOutputs> AddLinkAsync(string parentId, string linkType, string targetId, string? declaredId = null)
    {
        var rootId = await NrGameDataUiActions.GetCurrentCatalogueIdAsync(_page);

        if (parentId == rootId)
        {
            var outputs = await NrGameDataUiActions.AddLinkToRootSectionAsync(_page, linkType, targetId);
            var rootToken = outputs.EntryId ?? NewSyntheticToken();
            rootToken = await ApplyDeclaredIdAsync(rootToken, declaredId);
            _parentOf[rootToken] = parentId;
            _selectedToken = rootToken;
            return new GameDataActionOutputs { EntryId = rootToken };
        }

        // Nested link: right-click the parent, open the "Link ❯" submenu and pick the item matching
        // the target's kind (so the right link container is created), then set the target.
        await SelectAsync(parentId);
        await MarkSelectionAsync();
        await RightClickSelectedAsync();
        var kind = await NrGameDataUiActions.LinkTargetKindAsync(_page, targetId);
        await OpenSubmenuAndPickAsync("Link", LinkSubmenuItemForKind(kind) ?? LinkSubmenuItemForType(linkType));
        await WaitEditorReadyAsync();
        var uid = await ReadUniqueIdAsync();
        var token = uid ?? NewSyntheticToken();
        if (uid is null)
        {
            _idless.Add(token);
        }
        await SetTargetAutocompleteAsync(targetId);
        token = await ApplyDeclaredIdAsync(token, declaredId);
        _parentOf[token] = parentId;
        _selectedToken = token;
        return new GameDataActionOutputs { EntryId = token };
    }

    public async Task SetFieldAsync(string entryId, string field, string? value)
    {
        await SelectAsync(entryId);

        // Rich-text/composite fields, the bespoke query editors, and the root metadata panel go
        // through the driver (which operates on the already-selected node and makes no "Unique ID"
        // assumption). Plain named-entity scalar fields use the proven static table/checkbox/
        // autocomplete path, which re-selects the entry by tree node.
        var rp = _page.Locator(".rightPanel");
        var advancedEditor = await rp.Locator(".constraint, .modifier, .query").CountAsync() > 0;
        if (await IsRootAsync(entryId))
        {
            await EditOpenFieldAsync(field, value);
            return;
        }

        // Idless nodes (modifier/condition/repeat/groups) have no tree id, so the static path
        // (which re-selects via FindTreeNodeByIdAsync) can't target them — they're already selected,
        // so edit the open panel directly through the driver.
        var idless = _idless.Contains(entryId) || entryId.StartsWith("__nr", StringComparison.Ordinal);
        if (field is "comment" or "description" || advancedEditor || idless)
        {
            await EditOpenFieldAsync(field, value);
            return;
        }

        await NrGameDataUiActions.SetFieldAsync(_page, field, value);
    }

    /// <summary>True when the token is the open catalogue or its game system (the editable roots).</summary>
    private async Task<bool> IsRootAsync(string token)
    {
        var rootId = await NrGameDataUiActions.GetCurrentCatalogueIdAsync(_page);
        var systemId = await _page.EvaluateAsync<string?>(
            "() => new URLSearchParams(location.search).get('systemId')");
        return token == rootId || token == systemId;
    }

    public async Task SetCostAsync(string entryId, string costTypeId, string? value)
    {
        await SelectAsync(entryId);
        await EditCostAsync(costTypeId, value);
    }

    public async Task SetCharacteristicAsync(string entryId, string nameOrTypeId, string? value)
    {
        await SelectAsync(entryId);
        await EditCharacteristicAsync(nameOrTypeId, value);
    }

    // ===== Selection =====

    private async Task SelectAsync(string token)
    {
        if (token == _selectedToken)
        {
            return;
        }

        // Parent of the currently-selected node → use the right-panel breadcrumb.
        if (_selectedToken is not null && _parentOf.TryGetValue(_selectedToken, out var p) && p == token)
        {
            await ClickBreadcrumbAncestorAsync(1);
            _selectedToken = token;
            return;
        }

        // Selecting the catalogue/game-system root itself (root metadata fields): the root renders
        // as the first tree node inside `#editor-entries .head`.
        if (await IsRootAsync(token))
        {
            var rootNode = _page.Locator("#editor-entries .head h3:is(.normalTitle, .arrowTitle)").First;
            await rootNode.ClickAsync();
            // Selecting the root selects the catalogue itself, so assert THAT id rather than that
            // some panel is on screen — `CataloguePanel` renders the same Basics fieldset an entry
            // does, which is what made the old panel-only wait pass against the outgoing node.
            await WaitEditorReadyAsync(await NrGameDataUiActions.GetCurrentCatalogueIdAsync(_page));
            _selectedToken = token;
            return;
        }

        // A named entity carries a real id and renders as a tree node.
        if (!_idless.Contains(token) && !token.StartsWith("__nr", StringComparison.Ordinal))
        {
            var node = await NrGameDataUiActions.FindTreeNodeByIdAsync(_page, token);
            await node.ClickAsync();
            await WaitEditorReadyAsync(token);
            _selectedToken = token;
            return;
        }

        throw new InvalidOperationException(
            $"NR Editor UI: cannot re-select id-less node '{token}' (not currently selected and not a named entity).");
    }

    private async Task RightClickSelectedAsync()
    {
        var selected = _page.Locator("#editor-entries h3.selected");
        await selected.First.ScrollIntoViewIfNeededAsync();
        await selected.First.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        // Reactive: wait for the context menu to render rather than a fixed delay.
        await _page.WaitForSelectorAsync(".context-menu:visible", new PageWaitForSelectorOptions { Timeout = 5_000 });
    }

    /// <summary>Clicks the breadcrumb item <paramref name="fromEnd"/> positions before the current node (1 = parent).</summary>
    private async Task ClickBreadcrumbAncestorAsync(int fromEnd)
    {
        var crumbs = _page.Locator(".rightPanel .-indent-16px > div.cursor-pointer");
        var count = await crumbs.CountAsync();
        var index = count - 1 - fromEnd;
        if (index < 0)
        {
            throw new InvalidOperationException("NR Editor UI: breadcrumb ancestor out of range.");
        }
        await MarkSelectionAsync();
        await crumbs.Nth(index).ClickAsync();
        await WaitEditorReadyAsync();
    }

    /// <summary>Link entry types added through the "Link ❯" submenu (target-kind chooser). A
    /// categoryLink is NOT here — a force entry adds it via a direct "Category" context item.</summary>
    private static readonly HashSet<string> LinkAddTypes = new(StringComparer.Ordinal)
    {
        "entryLink", "infoLink",
    };

    /// <summary>The "Link ❯" submenu item to pick for a link family (sets entryLinks vs infoLinks).</summary>
    private static string LinkSubmenuItemForType(string entryType) => entryType switch
    {
        "entryLink" => "Entry",
        "infoLink" => "Rule",
        "categoryLink" => "Association",
        _ => "Entry",
    };

    /// <summary>The "Link ❯" submenu item matching a resolved target kind, or null if unknown.</summary>
    private static string? LinkSubmenuItemForKind(string? kind) => kind switch
    {
        "selectionEntry" => "Entry",
        "selectionEntryGroup" => "Group",
        "rule" => "Rule",
        "profile" => "Profile",
        "infoGroup" => "InfoGroup",
        _ => null,
    };

    /// <summary>
    /// Some "add child" menu items (Profile, Link) are submenu triggers — the item carries a
    /// "❯" and a <c>context-menu-id</c>, and hovering it opens a second <c>.context-menu</c>
    /// listing the concrete options. Hovers the trigger and clicks the option matching
    /// <paramref name="itemMatch"/> (or the first option when null). NR requires the choice at
    /// creation time; a later <c>setFields</c> can still adjust it where the editor exposes it.
    /// </summary>
    private async Task OpenSubmenuAndPickAsync(string parentLabel, string? itemMatch)
    {
        // Hover the trigger in the (visible) main menu; the submenu is a pre-rendered
        // .context-menu that becomes visible on hover.
        var trigger = _page.Locator(".context-menu:visible > div")
            .Filter(new LocatorFilterOptions
            {
                HasTextRegex = new System.Text.RegularExpressions.Regex(
                    $"^\\s*{System.Text.RegularExpressions.Regex.Escape(parentLabel)}\\b"),
            });
        await trigger.First.HoverAsync();

        // The submenu is the visible menu that lacks the main menu's "Remove" item. Waiting for it
        // to become visible below is the reactive signal — no fixed post-hover delay needed.
        var submenu = _page.Locator(".context-menu:visible")
            .Filter(new LocatorFilterOptions { HasNotText = "Remove" });
        await submenu.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var item = submenu.First.Locator("> div");
        if (itemMatch is not null)
        {
            item = submenu.First.Locator("> div").Filter(new LocatorFilterOptions
            {
                HasTextRegex = new System.Text.RegularExpressions.Regex(
                    $"^\\s*{System.Text.RegularExpressions.Regex.Escape(itemMatch)}\\s*$"),
            });
        }
        await item.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
        // The caller waits for the editor panel (WaitEditorReadyAsync) right after picking, which is
        // the reactive signal that the created node's editor opened — no fixed delay needed here.
    }

    private async Task ClickContextItemAsync(string label)
    {
        await _page.Locator(".context-menu > div")
            .Filter(new LocatorFilterOptions
            {
                HasTextRegex = new System.Text.RegularExpressions.Regex($"^\\s*{System.Text.RegularExpressions.Regex.Escape(label)}\\s*$"),
            })
            .First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
        // The caller waits for the editor panel (WaitEditorReadyAsync) right after — reactive signal.
    }

    /// <summary>
    /// Waits until the editor is actually showing the node the caller means to edit.
    /// </summary>
    /// <param name="expectedSelectedId">
    /// The node that should end up selected. Null means "whatever NR just created", which is
    /// asserted as a CHANGE of selection against a marker taken before the click.
    /// </param>
    /// <remarks>
    /// <para>
    /// This method used to wait for <c>.rightPanel fieldset</c> and then sleep 150ms, and the
    /// fieldset is not a signal at all: <c>RightPanel</c> is <c>v-if="item"</c> with <c>:key</c>, so
    /// when something was already selected the wait is satisfied by the OUTGOING panel. The 150ms
    /// was therefore the entire gap between clicking a context-menu item and reading the new entry's
    /// id out of the panel.
    /// </para>
    /// <para>
    /// That is a wrong-answer bug, not a slow one. NR's <c>create()</c> adds with
    /// <c>{select: true}</c>, and the flag is consumed from the NEW box's <c>mounted()</c> hook — a
    /// Vue mount cycle after the click. Read too early and <c>ReadUniqueIdAsync</c> returns the
    /// PREVIOUS entry's id, after which <c>SetNameInEditorAsync</c> renames the previous entry and
    /// every later setField for that spec lands on the wrong node. It reports as a data mismatch
    /// rather than an error.
    /// </para>
    /// <para>
    /// Order matters: the selection is asserted FIRST, because the panel lags it — checking the
    /// panel first can pass against the very panel we are trying to leave.
    /// </para>
    /// </remarks>
    private async Task WaitEditorReadyAsync(string? expectedSelectedId = null)
    {
        if (expectedSelectedId is not null)
        {
            await _page.WaitForFunctionAsync(
                """
                (id) => {
                    const st = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                    return st?.get_selected?.()?.id === id;
                }
                """,
                expectedSelectedId,
                new PageWaitForFunctionOptions { Timeout = 10_000 });
        }
        else
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                    const st = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                    const cur = st?.selectedItem;
                    return cur != null && cur !== window.__bsspec_prev_sel?.deref();
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 10_000 });
        }

        await _page.Locator(".rightPanel fieldset").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
    }

    /// <summary>
    /// Records which node is selected right now, so <see cref="WaitEditorReadyAsync"/> can later
    /// assert the selection MOVED rather than that something is selected.
    /// </summary>
    /// <remarks>
    /// A <c>WeakRef</c> so a spec's discarded component cannot be kept alive by the marker.
    /// </remarks>
    private Task MarkSelectionAsync()
        => _page.EvaluateAsync(
            """
            () => {
                const st = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                window.__bsspec_prev_sel = st?.selectedItem ? new WeakRef(st.selectedItem) : null;
            }
            """);

    /// <summary>
    /// Waits until the node the editor is showing carries <paramref name="expected"/> in
    /// <paramref name="property"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the post-condition of every widget edit in the right panel, and it replaces a family
    /// of 150-200ms sleeps that stood in for "NR has taken the value". `get_selected()` returns the
    /// same object the exporter serialises, so asserting on it is asserting on the ANSWER rather
    /// than on a proxy for it.
    /// </para>
    /// <para>
    /// Use NR's own property names, read from the code that uses them — not names inferred from the
    /// C# parameter. The sibling roster driver shipped a predicate asserting `getCustomNotes()`,
    /// which NR does not have (the property is `note`), turning a 300ms sleep that worked into a
    /// 10s timeout that never passed. A condition asserted against an invented property is strictly
    /// worse than the sleep it replaces.
    /// </para>
    /// </remarks>
    private async Task WaitSelectedFieldAsync(string property, string? expected, int timeoutMs = 5_000)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                ([property, expected]) => {
                    const st = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                    const sel = st?.get_selected?.();
                    if (!sel) { return false; }
                    const v = sel[property];
                    return expected === null
                        ? (v === undefined || v === null || v === '')
                        : String(v) === expected;
                }
                """,
                new object?[] { property, expected },
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            // Report what the node ACTUALLY carries rather than a bare timeout. A wait on the wrong
            // property is indistinguishable from a slow commit otherwise, and that mistake has
            // already cost this work a lane run — the roster driver waited on `getCustomNotes()`
            // when NR stores `note`.
            var actual = await _page.EvaluateAsync<string>(
                """
                (property) => {
                    const st = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                    const sel = st?.get_selected?.();
                    if (!sel) { return '(nothing selected)'; }
                    const own = Object.keys(sel).filter(k => typeof sel[k] !== 'function');
                    const show = k => {
                        let v; try { v = sel[k]; } catch (e) { return k + '=<throws>'; }
                        if (v === null || v === undefined) { return k + '=' + String(v); }
                        if (typeof v === 'object') { return k + '={' + Object.keys(v).length + ' keys}'; }
                        return k + '=' + String(v).slice(0, 40);
                    };
                    return 'editorTypeName=' + (sel.editorTypeName ?? '?')
                        + ' | requested ' + show(property)
                        + ' | has: ' + own.slice(0, 30).join(', ');
                }
                """,
                property);

            throw new TimeoutException(
                $"NR Editor UI: '{property}' did not become '{expected ?? "<null>"}' on the selected "
                + $"node within {timeoutMs}ms. Selected node: {actual}");
        }
    }

    private async Task<string?> ReadUniqueIdAsync()
    {
        var idRow = _page.Locator(".rightPanel tr").Filter(new LocatorFilterOptions { HasText = "Unique ID" });
        if (await idRow.CountAsync() == 0)
        {
            return null;
        }
        var input = idRow.First.Locator("td:last-child input[type='text']");
        if (await input.CountAsync() == 0)
        {
            return null;
        }
        var val = await input.First.InputValueAsync();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private async Task SetNameInEditorAsync(string name)
    {
        var nameRow = _page.Locator(".rightPanel tr").Filter(new LocatorFilterOptions { HasText = "Name" });
        var nameInput = nameRow.First.Locator("td:last-child input[type='text']").First;
        await nameInput.ClickAsync(new LocatorClickOptions { ClickCount = 3 });
        await nameInput.FillAsync(name);
        await nameInput.PressAsync("Tab");
        // The tree label is derived from this, and FindTreeNodeByIdAsync matches on it later.
        await WaitSelectedFieldAsync("name", name);
    }

    /// <summary>Writes the editor's "Unique ID" field for the currently-selected entry.</summary>
    private async Task SetUniqueIdInEditorAsync(string id)
    {
        var idRow = _page.Locator(".rightPanel tr").Filter(new LocatorFilterOptions { HasText = "Unique ID" });
        var idInput = idRow.First.Locator("td:last-child input[type='text']").First;
        await idInput.ClickAsync(new LocatorClickOptions { ClickCount = 3 });
        await idInput.FillAsync(id);
        await idInput.PressAsync("Tab");
        // Basics' id setter removes from and re-adds to the catalogue index, so asserting the id
        // landed also proves the index the next tree lookup needs was rebuilt.
        await WaitSelectedFieldAsync("id", id);
    }

    /// <summary>
    /// If a declared id is given, re-id the just-created (id-bearing) entry through the editor's
    /// "Unique ID" field so exports are byte-reproducible, and remap identity tracking. Id-less
    /// tokens (synthetic) are left untouched. Returns the effective token.
    /// </summary>
    private async Task<string> ApplyDeclaredIdAsync(string token, string? declaredId)
    {
        if (string.IsNullOrEmpty(declaredId) || declaredId == token || _idless.Contains(token))
        {
            return token;
        }
        await SelectAsync(token);
        await SetUniqueIdInEditorAsync(declaredId);
        if (_parentOf.TryGetValue(token, out var parent))
        {
            _parentOf.Remove(token);
            _parentOf[declaredId] = parent;
        }
        return declaredId;
    }

    private static string AddChildLabel(string entryType) => entryType switch
    {
        "selectionEntry" => "Entry",
        "selectionEntryGroup" => "Group",
        "forceEntry" => "Force",
        "categoryEntry" => "Category",
        "profile" => "Profile",
        "rule" => "Rule",
        "infoGroup" => "Info Group",
        "infoLink" => "Link",
        "constraint" => "Constraint",
        "modifier" => "Modifier",
        "modifierGroup" => "Modifier Group",
        "condition" => "Condition",
        "conditionGroup" => "Condition Group",
        "repeat" => "Repeat",
        "categoryLink" => "Category",
        "characteristicType" => "Characteristic Type",
        // NR-specific additions over original BattleScribe.
        "association" => "Association",
        "attributeType" => "Attribute Type",
        "localConditionGroup" => "Local Condition Group",
        _ => entryType,
    };

    // ===== Field editing (open right-panel editor) =====

    private async Task EditOpenFieldAsync(string field, string? value)
    {
        var rp = _page.Locator(".rightPanel");

        // 1. Comment / description are contenteditable rich-text fields.
        if (field is "comment" or "description")
        {
            var legend = field == "comment" ? "Comment" : "Description";
            var div = rp.Locator("fieldset")
                .Filter(new LocatorFilterOptions { Has = _page.Locator($"legend:text-is(\"{legend}\")") })
                .Locator(".editableDiv").First;
            if (await div.CountAsync() == 0 && field == "description")
            {
                // Rules render their text in the first contenteditable after the basics.
                div = rp.Locator(".editableDiv").Last;
            }
            await div.ClickAsync();
            await div.FillAsync(value ?? "");
            // EditableDiv emits update:modelValue from @input, so FillAsync already committed it;
            // the Tab only blurs.
            await div.PressAsync("Tab");
            await WaitSelectedFieldAsync(field == "comment" ? "comment" : "description", value);
            return;
        }

        // 2. Constraint editor.
        var constraint = rp.Locator(".constraint");
        if (await constraint.CountAsync() > 0)
        {
            if (await EditConstraintFieldAsync(rp, constraint.First, field, value))
            {
                return;
            }
        }

        // 2a. Repeat editor: a "Repeat" fieldset whose `.condition` block holds two number inputs
        // (the repeat count + the per-value) and "Percentage?/Round up?" checks, plus `.query`.
        var repeatFieldset = rp.Locator("fieldset").Filter(new LocatorFilterOptions
        {
            Has = _page.Locator("legend:text-is(\"Repeat\")"),
        });
        if (await repeatFieldset.CountAsync() > 0)
        {
            if (await EditRepeatFieldAsync(rp, field, value))
            {
                return;
            }
        }

        // 2b. Condition editor: a `.condition` block (type/value/percentValue) plus the shared
        // `.query` block (field/scope/childForces) and a "Filter By" section.
        var condition = rp.Locator(".condition");
        if (await condition.CountAsync() > 0)
        {
            if (await EditConditionFieldAsync(rp, condition.First, field, value))
            {
                return;
            }
        }

        // 3. Modifier editor.
        var modifier = rp.Locator(".modifier");
        if (await modifier.CountAsync() > 0)
        {
            if (await EditModifierFieldAsync(modifier.First, field, value))
            {
                return;
            }
        }

        // 4. Query fields shared by constraints/conditions/repeats (scope, field, childId, value, type).
        if (await EditQueryFieldAsync(rp, field, value))
        {
            return;
        }

        // 5. Autocomplete reference fields.
        if (field is "publicationId" or "defaultSelectionEntryId" or "targetId" or "typeName")
        {
            await SetReferenceAutocompleteAsync(rp, field, value);
            return;
        }

        // 6. Generic widget by label (checkbox / select / text in a table row or boolean block).
        await EditGenericFieldAsync(rp, field, value);
    }

    private async Task<bool> EditConstraintFieldAsync(ILocator rp, ILocator constraint, string field, string? value)
    {
        switch (field)
        {
            case "type":
                await constraint.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Value = value });
                await WaitSelectedFieldAsync("type", value);
                return true;
            case "value":
                {
                    var num = constraint.Locator("input[type='number']").First;
                    await num.FillAsync(value ?? "");
                    // NumberInput only emits update:modelValue on @change, so the Tab is
                    // load-bearing; the sleep after it was not.
                    await num.PressAsync("Tab");
                    await WaitSelectedFieldAsync("value", value);
                    return true;
                }
            case "percentValue":
                await SetCheckboxAsync(constraint.Locator("#percent"), value);
                return true;
            case "shared":
                await SetCheckboxAsync(rp.Locator("#shared"), value);
                return true;
            case "includeChildSelections":
                await SetCheckboxAsync(rp.Locator("#childSelections"), value);
                return true;
            case "includeChildForces":
                await SetCheckboxAsync(rp.Locator("#childForces"), value);
                return true;
            default:
                return false; // field/scope handled by the query editor
        }
    }

    private async Task<bool> EditConditionFieldAsync(ILocator rp, ILocator condition, string field, string? value)
    {
        switch (field)
        {
            case "type":
                await condition.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Value = value });
                await WaitSelectedFieldAsync("type", value);
                return true;
            case "value":
                {
                    var num = condition.Locator("input[type='number']").First;
                    await num.FillAsync(value ?? "");
                    await num.PressAsync("Tab");
                    await WaitSelectedFieldAsync("value", value);
                    return true;
                }
            case "percentValue":
                await SetCheckboxAsync(condition.Locator("#percent"), value);
                return true;
            case "field":
                await SetIconSelectAsync(rp.Locator(".query .select-container.modType, .query .modType").First, value);
                return true;
            case "scope":
                await PickAutocompleteContainerAsync(rp.Locator(".query .inQuery .autocomplete").First, ScopeLabel(value));
                return true;
            case "includeChildSelections":
                await SetCheckboxAsync(rp.Locator("#childSelections"), value);
                return true;
            case "includeChildForces":
                await SetCheckboxAsync(rp.Locator("#childForces"), value);
                return true;
            case "shared":
                // NR keeps `shared` checked and disables the checkbox ("recommended on conditions").
                return true;
            case "childId":
                {
                    // The "Filter By" section's "Child:" autocomplete targets an entry/category by name.
                    var name = await ResolveDisplayNameAsync("childId", value ?? "");
                    var row = rp.Locator("table.editorTable tr").Filter(new LocatorFilterOptions
                    {
                        Has = _page.Locator("td").Filter(new LocatorFilterOptions
                        {
                            HasTextRegex = new System.Text.RegularExpressions.Regex("^\\s*Child:?\\s*$"),
                        }),
                    });
                    await PickAutocompleteContainerAsync(row.Locator(".autocomplete").First, name ?? value);
                    return true;
                }
            default:
                return false;
        }
    }

    private async Task<bool> EditRepeatFieldAsync(ILocator rp, string field, string? value)
    {
        var condition = rp.Locator(".condition").First; // the "Repeat" fieldset's body
        switch (field)
        {
            case "repeats":
                {
                    var num = condition.Locator("input[type='number']").First;
                    await num.FillAsync(value ?? "");
                    await num.PressAsync("Tab");
                    await WaitSelectedFieldAsync("repeats", value);
                    return true;
                }
            case "value":
                {
                    // Second number input: "N times for every <value> …".
                    //
                    // A repeat carries BOTH `repeats` and `value`, and the two inputs sit in a
                    // fieldset whose markup is identical to the condition editor's. The first
                    // conversion of this file asserted them crossed — `repeats` checking `value`
                    // and vice versa — because a global find-and-replace matched the condition
                    // editor's shape here too. The case label is the property; keep them in step.
                    var num = condition.Locator("input[type='number']").Nth(1);
                    await num.FillAsync(value ?? "");
                    await num.PressAsync("Tab");
                    await WaitSelectedFieldAsync("value", value);
                    return true;
                }
            case "roundUp":
                // "Round up?" and "Percentage?" share id=percent, so target by the label text.
                await SetCheckboxAsync(LabeledCheckbox(rp, "Round up?"), value);
                return true;
            case "percentValue":
                await SetCheckboxAsync(LabeledCheckbox(rp, "Percentage?"), value);
                return true;
            case "field":
                await SetIconSelectAsync(rp.Locator(".query .select-container.modType, .query .modType").First, value);
                return true;
            case "scope":
                await PickAutocompleteContainerAsync(rp.Locator(".query .inQuery .autocomplete").First, ScopeLabel(value));
                return true;
            case "includeChildSelections":
                await SetCheckboxAsync(rp.Locator("#childSelections"), value);
                return true;
            case "includeChildForces":
                await SetCheckboxAsync(rp.Locator("#childForces"), value);
                return true;
            case "shared":
                await SetCheckboxAsync(rp.Locator("#shared"), value);
                return true;
            case "childId":
                {
                    var name = await ResolveDisplayNameAsync("childId", value ?? "");
                    var row = rp.Locator("table.editorTable tr").Filter(new LocatorFilterOptions
                    {
                        Has = _page.Locator("td").Filter(new LocatorFilterOptions
                        {
                            HasTextRegex = new System.Text.RegularExpressions.Regex("^\\s*Child:?\\s*$"),
                        }),
                    });
                    await PickAutocompleteContainerAsync(row.Locator(".autocomplete").First, name ?? value);
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>Locates a checkbox by its sibling label's exact text (for duplicate-id checkboxes).</summary>
    private static ILocator LabeledCheckbox(ILocator rp, string labelText) =>
        rp.Locator($"div:has(> label:text-is(\"{labelText}\")) > input[type='checkbox']").First;

    /// <summary>Maps a BattleScribe query scope value to its NR autocomplete display label.</summary>
    private static string ScopeLabel(string? value) => value switch
    {
        "self" => "Self",
        "parent" => "Parent",
        "ancestor" => "Ancestor",
        "force" => "Force",
        "roster" => "Roster",
        "primary-category" => "Primary Category",
        "primary-catalogue" => "Primary Catalogue",
        "root-entry" => "Root Entry",
        _ => value ?? "",
    };

    /// <summary>Picks an option from an NR autocomplete given its container locator (no row label).</summary>
    private static async Task PickAutocompleteContainerAsync(ILocator container, string? match)
    {
        if (match is null)
        {
            return;
        }
        // See NrGameDataUiActions: the next statement waits for the popup this sleep hoped for.
        await container.Locator(".autocomplete-input").First.ClickAsync();
        var suggestions = container.Locator(".suggestions:not(.hidden) > div");
        await suggestions.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        var pick = suggestions.Filter(new LocatorFilterOptions
        {
            HasTextRegex = new System.Text.RegularExpressions.Regex(
                $"^\\s*{System.Text.RegularExpressions.Regex.Escape(match)}\\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        });
        if (await pick.CountAsync() == 0)
        {
            pick = suggestions.Filter(new LocatorFilterOptions { HasText = match });
        }
        await pick.First.ClickAsync(new LocatorClickOptions { Timeout = 4_000 });
        await WaitPopupClosedAsync(container);
    }

    private async Task<bool> EditModifierFieldAsync(ILocator modifier, string field, string? value)
    {
        switch (field)
        {
            case "type":
                // The modifier type select carries object option values, so select by visible label.
                await modifier.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Label = ModifierTypeLabel(value) });
                // Modifier.changed() writes item.type from the picked operation, and the VALUE widget
                // is computed from it — so asserting the type also gates the re-render that the next
                // setField addresses positionally.
                await WaitSelectedFieldAsync("type", value);
                return true;
            case "field":
                await SetIconSelectAsync(modifier.Locator(".select-container").First, value);
                return true;
            case "value":
                {
                    // After choosing the field, the value control depends on the field's data type:
                    // an autocomplete (category — the value is a category picked by name), a select
                    // (boolean), an input (number), or a contenteditable .editableDiv (string).
                    var auto = modifier.Locator(".autocomplete");
                    if (await auto.CountAsync() > 0 && await auto.Last.IsVisibleAsync())
                    {
                        // The category value is stored as the category's id; the picker lists names.
                        var catName = await ResolveDisplayNameAsync("targetId", value ?? "") ?? value;
                        await PickAutocompleteContainerAsync(auto.Last, catName);
                        return true;
                    }
                    var sel = modifier.Locator("select").Nth(1);
                    if (await sel.CountAsync() > 0 && await sel.IsVisibleAsync())
                    {
                        try
                        {
                            await sel.SelectOptionAsync(new SelectOptionValue { Value = value });
                            await WaitSelectedFieldAsync("value", value);
                            return true;
                        }
                        catch
                        {
                            // fall through to input / editableDiv
                        }
                    }
                    var ce = modifier.Locator(".editableDiv");
                    if (await ce.CountAsync() > 0 && await ce.Last.IsVisibleAsync())
                    {
                        await ce.Last.ClickAsync();
                        await ce.Last.FillAsync(value ?? "");
                        await ce.Last.PressAsync("Tab");
                        await WaitSelectedFieldAsync("value", value);
                        return true;
                    }
                    var input = modifier.Locator("input").Last;
                    await input.FillAsync(value ?? "");
                    await input.PressAsync("Tab");
                    await WaitSelectedFieldAsync("value", value);
                    return true;
                }
            default:
                return false;
        }
    }

    private async Task<bool> EditQueryFieldAsync(ILocator rp, string field, string? value)
    {
        var query = rp.Locator(".query");
        if (await query.CountAsync() == 0)
        {
            return false;
        }
        var q = query.First;
        switch (field)
        {
            case "type":
                // condition/repeat type select.
                {
                    var sel = q.Locator("select").First;
                    if (await sel.CountAsync() > 0)
                    {
                        await SelectByValueOrLabelAsync(sel, value);
                        await WaitSelectedFieldAsync("type", value);
                        return true;
                    }
                    return false;
                }
            case "value":
                {
                    var num = q.Locator("input[type='number'], input[type='text']").First;
                    await num.FillAsync(value ?? "");
                    await num.PressAsync("Tab");
                    await WaitSelectedFieldAsync("value", value);
                    return true;
                }
            case "field":
                await SetIconSelectAsync(q.Locator(".modType, .select-container").First, value);
                return true;
            case "scope":
                await SetAutocompleteByRowAsync(rp, "Scope", value);
                return true;
            case "childId":
                await SetAutocompleteByRowAsync(rp, "Filter By", value);
                return true;
            case "roundUp":
                await SetCheckboxAsync(rp.Locator("#roundUp"), value);
                return true;
            default:
                return false;
        }
    }

    private async Task EditGenericFieldAsync(ILocator rp, string field, string? value)
    {
        var label = FieldLabel(field);

        // Checkbox via associated <label for> (e.g. Hidden, Library).
        var byLabel = rp.GetByLabel(label, new LocatorGetByLabelOptions { Exact = false });
        if (await byLabel.CountAsync() > 0 && await IsCheckboxAsync(byLabel.First))
        {
            await SetCheckboxAsync(byLabel.First, value);
            return;
        }

        // Table row whose label *cell* matches precisely (tolerant of a trailing colon). The value
        // cell may be a text/number input, a select, or a contenteditable .editableDiv.
        var input = rp.Locator("table.editorTable tr")
            .Filter(new LocatorFilterOptions
            {
                Has = _page.Locator("td").Filter(new LocatorFilterOptions
                {
                    HasTextRegex = new System.Text.RegularExpressions.Regex(
                        $"^\\s*{System.Text.RegularExpressions.Regex.Escape(label)}:?\\s*$"),
                }),
            })
            .Locator("td:last-child input, td:last-child select, td:last-child textarea, td:last-child .editableDiv")
            .First;
        if (await input.CountAsync() == 0)
        {
            throw new InvalidOperationException($"NR Editor UI: no widget found for field '{field}' (label '{label}').");
        }

        if (value is "true" or "false" && await IsCheckboxAsync(input))
        {
            await SetCheckboxAsync(input, value);
            return;
        }

        var tag = await input.EvaluateAsync<string>("el => el.tagName");
        if (string.Equals(tag, "SELECT", StringComparison.OrdinalIgnoreCase))
        {
            await SelectByValueOrLabelAsync(input, value);
        }
        else if (string.Equals(tag, "DIV", StringComparison.OrdinalIgnoreCase))
        {
            // contenteditable .editableDiv (e.g. Readme, Author Contact).
            await input.ClickAsync();
            await input.FillAsync(value ?? "");
            await input.PressAsync("Tab");
        }
        else
        {
            await input.FillAsync(value ?? "");
            await input.PressAsync("Tab");
        }

        // NOTE: editorStore.changed() has a heavy branch — for a profileType (or a descendant of
        // one) it awaits system.loadAll(), re-runs processForEditor on every loaded catalogue and
        // runs the "Fix profiles" script. Asserting the field landed does NOT mean NR has finished
        // that; the 200ms it replaces did not cover it either. Same guarantee, stated honestly.
        await WaitSelectedFieldAsync(NrPropertyName(field), value);
    }

    // ===== Costs / characteristics =====

    private async Task EditCostAsync(string costTypeId, string? value)
    {
        // Resolve the cost type's display name (read-only) to find its labelled input.
        var name = await _page.EvaluateAsync<string?>(
            """
            (typeId) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const ed = pinia?._s?.get('editor');
                const sId = new URLSearchParams(location.search).get('systemId');
                for (const c of Object.values(ed?.gameSystems?.[sId]?.loadedCatalogues ?? {})) {
                    const ct = (c.costTypes || []).find(t => t.id === typeId);
                    if (ct) return ct.name;
                }
                return typeId;
            }
            """, costTypeId) ?? costTypeId;

        var rp = _page.Locator(".rightPanel");
        // Each cost renders as `.costs > div` with a `<label>{name}: </label>` and a numeric input.
        // Match the cost div by its label so the value lands on the right cost type.
        var costDiv = rp.Locator(".costs > div")
            .Filter(new LocatorFilterOptions
            {
                Has = _page.Locator("label").Filter(new LocatorFilterOptions
                {
                    HasTextRegex = new System.Text.RegularExpressions.Regex(
                        $"^\\s*{System.Text.RegularExpressions.Regex.Escape(name)}:?\\s*$"),
                }),
            });
        var input = costDiv.Locator("input").First;
        if (await input.CountAsync() == 0)
        {
            throw new InvalidOperationException($"NR Editor UI: cost input for '{name}' not found.");
        }
        await input.FillAsync(value ?? "");
        await input.PressAsync("Tab");

        // Assert the cost landed, keyed by its type. Worth more than the time saved: Costs.changed()
        // filters on isFinite(value), so a value NR cannot parse silently DROPS the cost rather than
        // failing — nothing noticed until the state diff, two steps later.
        await _page.WaitForFunctionAsync(
            """
            ([label, expected]) => {
                const st = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                const sel = st?.get_selected?.();
                if (!sel) { return false; }
                const costs = sel.costs ?? [];
                const c = costs.find(x => x.name === label || x.typeId === label);
                return expected === null ? !c : (c != null && String(c.value) === expected);
            }
            """,
            new object?[] { name, value },
            new PageWaitForFunctionOptions { Timeout = 5_000 });
    }

    private async Task EditCharacteristicAsync(string name, string? value)
    {
        var rp = _page.Locator(".rightPanel");
        // Characteristics render in a table row whose label cell is the characteristic name
        // (e.g. "M: "). Match the label *cell* precisely so a one-letter name like "M" doesn't
        // also match "Name:" / "Comment".
        var input = rp.Locator("table.editorTable tr")
            .Filter(new LocatorFilterOptions
            {
                Has = _page.Locator("td").Filter(new LocatorFilterOptions
                {
                    HasTextRegex = new System.Text.RegularExpressions.Regex(
                        $"^\\s*{System.Text.RegularExpressions.Regex.Escape(name)}:?\\s*$"),
                }),
            })
            .Locator("td:last-child input, td:last-child textarea, td:last-child .editableDiv").First;
        if (await input.CountAsync() == 0)
        {
            throw new InvalidOperationException($"NR Editor UI: characteristic input '{name}' not found.");
        }
        var tag = await input.EvaluateAsync<string>("el => el.tagName");
        if (string.Equals(tag, "DIV", StringComparison.OrdinalIgnoreCase))
        {
            await input.ClickAsync();
            await input.FillAsync(value ?? "");
        }
        else
        {
            await input.FillAsync(value ?? "");
        }
        await input.PressAsync("Tab");
        await _page.WaitForFunctionAsync(
            """
            ([label, expected]) => {
                const st = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia?._s?.get('editor');
                const sel = st?.get_selected?.();
                const c = (sel?.characteristics ?? []).find(x => x.name === label || x.typeId === label);
                return c != null && String(c.$text ?? '') === String(expected ?? '');
            }
            """,
            new object?[] { name, value },
            new PageWaitForFunctionOptions { Timeout = 5_000 });
    }

    // ===== Widget primitives =====

    private async Task SetTargetAutocompleteAsync(string targetId)
    {
        var rp = _page.Locator(".rightPanel");
        // Align the link's type to the target's kind so the Target list includes it.
        await NrGameDataUiActions.SetLinkTypeFromTargetAsync(_page, rp, targetId);
        await SetReferenceAutocompleteAsync(rp, "targetId", targetId);
    }

    private async Task SetReferenceAutocompleteAsync(ILocator rp, string field, string? value)
    {
        if (value is null)
        {
            return;
        }
        var rowLabel = field switch
        {
            "publicationId" => "Publication",
            "defaultSelectionEntryId" => "Default",
            "targetId" => "Target:", // "Target:" disambiguates from the "Target ID:" row
            "typeName" => "Type",
            _ => FieldLabel(field),
        };
        var display = await ResolveDisplayNameAsync(field, value);
        await SetAutocompleteByRowAsync(rp, rowLabel, display ?? value);
    }

    private static async Task SetAutocompleteByRowAsync(ILocator rp, string rowLabel, string? match)
    {
        var row = rp.Locator("table tr").Filter(new LocatorFilterOptions { HasText = rowLabel }).First;
        var container = row.Locator(".autocomplete").First;
        if (await container.CountAsync() == 0)
        {
            container = rp.Locator(".autocomplete").First;
        }
        // The next statement waits for a suggestion, which is strictly stronger than this sleep was.
        await container.Locator(".autocomplete-input").First.ClickAsync();
        var suggestions = container.Locator(".suggestions:not(.hidden) > div");
        await suggestions.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        var pick = suggestions.Filter(new LocatorFilterOptions { HasText = match });
        await pick.First.ClickAsync(new LocatorClickOptions { Timeout = 4_000 });
        await WaitPopupClosedAsync(container);
    }

    private static async Task SetIconSelectAsync(ILocator container, string? value)
    {
        if (value is null)
        {
            return;
        }
        // IconSelect.startEditing() is async — it un-hides the popup, THEN awaits fetch() for the
        // options. So the popup exists before its children do, and the wait below (which requires a
        // child) is the only correct gate. The sleep merely delayed reaching it.
        await container.Locator(".iconselect-input").First.ClickAsync();
        var suggestions = container.Locator(".suggestions:not(.hidden) > div");
        await suggestions.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        var pick = suggestions.Filter(new LocatorFilterOptions
        {
            HasTextRegex = new System.Text.RegularExpressions.Regex($"^\\s*{System.Text.RegularExpressions.Regex.Escape(value)}\\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        });
        if (await pick.CountAsync() == 0)
        {
            pick = suggestions.Filter(new LocatorFilterOptions { HasText = value });
        }
        await pick.First.ClickAsync(new LocatorClickOptions { Timeout = 4_000 });
        await WaitPopupClosedAsync(container);
    }

    private static async Task SetCheckboxAsync(ILocator checkbox, string? value)
    {
        if (await checkbox.CountAsync() == 0)
        {
            throw new InvalidOperationException("NR Editor UI: checkbox not found.");
        }
        if (value == "false")
        {
            if (await checkbox.First.IsCheckedAsync())
            {
                await checkbox.First.UncheckAsync();
            }
        }
        else
        {
            if (!await checkbox.First.IsCheckedAsync())
            {
                await checkbox.First.CheckAsync();
            }
        }

        // No wait: every one of these boxes is a direct v-model (or an @change handler) that commits
        // on the click's own event, and Check/UncheckAsync already assert the resulting state.
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

    private static async Task SelectByValueOrLabelAsync(ILocator select, string? value)
    {
        try
        {
            await select.SelectOptionAsync(new SelectOptionValue { Value = value });
        }
        catch
        {
            await select.SelectOptionAsync(new SelectOptionValue { Label = value });
        }
    }

    private async Task<string?> ResolveDisplayNameAsync(string field, string id)
    {
        var js = field == "publicationId"
            ? "(id)=>{const p=document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;const ed=p?._s?.get('editor');const sId=new URLSearchParams(location.search).get('systemId');for(const c of Object.values(ed?.gameSystems?.[sId]?.loadedCatalogues??{})){const x=(c.publications||[]).find(q=>q.id===id);if(x)return x.name;}return null;}"
            : "(id)=>{const p=document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;const ed=p?._s?.get('editor');const sId=new URLSearchParams(location.search).get('systemId');const cols=['selectionEntries','selectionEntryGroups','sharedSelectionEntries','sharedSelectionEntryGroups','forceEntries','categoryEntries','rules','sharedRules','profiles','sharedProfiles'];const seen=new Set();const dig=(o)=>{if(!o||typeof o!=='object'||seen.has(o))return null;seen.add(o);if(o.id===id)return o.name;for(const k of Object.keys(o)){const v=o[k];if(Array.isArray(v))for(const it of v){const r=dig(it);if(r!=null)return r;}}return null;};for(const c of Object.values(ed?.gameSystems?.[sId]?.loadedCatalogues??{})){const r=dig(c);if(r!=null)return r;}return null;}";
        try
        {
            return await _page.EvaluateAsync<string?>(js, id);
        }
        catch
        {
            return null;
        }
    }

    private static string ModifierTypeLabel(string? value) => value switch
    {
        "set" => "Set",
        "increment" => "Increment",
        "decrement" => "Decrement",
        "append" => "Append",
        "add" => "Add",
        "remove" => "Remove",
        "set-primary" => "Set Primary",
        "unset-primary" => "Unset Primary",
        _ => value ?? "",
    };

    /// <summary>
    /// The spec field name as NR stores it on the node — the data-side twin of
    /// <see cref="FieldLabel"/>, which maps the same fields to their editor LABELS.
    /// </summary>
    /// <remarks>
    /// Only the spellings that actually differ are listed; everything else is already NR's name.
    /// Keep this honest by reading NR's model rather than assuming the spec name: the sibling
    /// roster driver lost a lane run to a predicate that asserted `getCustomNotes()` when the
    /// property is `note`.
    /// </remarks>
    private static string NrPropertyName(string field) => field switch
    {
        "imported" => "import",
        _ => field,
    };

    /// <summary>
    /// Waits for an autocomplete/icon-select popup to close after a pick.
    /// </summary>
    /// <remarks>
    /// Picking emits <c>update:modelValue</c> and the <c>v-click-outside</c> handler bound to the
    /// input fires on <c>document</c> — the suggestion is a SIBLING, not a child — setting
    /// <c>editing = false</c>, which re-hides the popup and swaps the input back to a div. So the
    /// popup closing is NR acknowledging the pick, and it replaces a 250ms guess.
    /// <para>
    /// This proves the click was SEEN, not that the value reached the node. Where the caller knows
    /// which property it just set, it should also assert that — see the modifier/query paths, where
    /// the value widget is re-rendered from the new type and the next setField addresses it
    /// positionally.
    /// </para>
    /// </remarks>
    private static Task WaitPopupClosedAsync(ILocator container)
        => container.Locator(".suggestions:not(.hidden)").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5_000 });

    private static string FieldLabel(string field) => field switch
    {
        "name" => "Name",
        "hidden" => "Hidden",
        "type" => "Type",
        "import" => "Import",
        "imported" => "Import",
        "collective" => "Collective",
        "page" => "Page",
        "shortName" => "Short Name",
        "publisher" => "Publisher",
        "publicationDate" => "Date",
        "publisherUrl" => "Url",
        "defaultCostLimit" => "Default Cost Limit",
        "primary" => "Primary",
        "publicationId" => "Publication",
        "defaultSelectionEntryId" => "Default Selection",
        "targetId" => "Target",
        // Root metadata (catalogue / game system) editor labels.
        "authorName" => "Author Name",
        "authorContact" => "Author Contact",
        "authorUrl" => "Author Website",
        "readme" => "Readme",
        "revision" => "Revision Number",
        "library" => "Library",
        _ => char.ToUpperInvariant(field[0]) + field[1..],
    };
}
