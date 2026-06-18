using BattleScribeSpec.GameData;
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
        await NrGameDataUiSetup.NavigateToFileAsync(_page, id);
        _selectedToken = null;
        _parentOf.Clear();
    }

    public async Task<GameDataActionOutputs> AddEntryAsync(string parentId, string entryType, string? name)
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
            await RightClickSelectedAsync();
            if (SubmenuAddTypes.Contains(entryType))
            {
                await OpenSubmenuAndPickFirstAsync(AddChildLabel(entryType));
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

        _parentOf[token] = parentId;
        _selectedToken = token;
        return new GameDataActionOutputs { EntryId = token };
    }

    public async Task<GameDataActionOutputs> AddLinkAsync(string parentId, string linkType, string targetId)
    {
        var rootId = await NrGameDataUiActions.GetCurrentCatalogueIdAsync(_page);

        if (parentId == rootId)
        {
            var outputs = await NrGameDataUiActions.AddLinkToRootSectionAsync(_page, linkType, targetId);
            var rootToken = outputs.EntryId ?? NewSyntheticToken();
            _parentOf[rootToken] = parentId;
            _selectedToken = rootToken;
            return new GameDataActionOutputs { EntryId = rootToken };
        }

        // Nested link: right-click the parent node and pick the "Link" item, then set the target.
        await SelectAsync(parentId);
        await RightClickSelectedAsync();
        await OpenSubmenuAndPickFirstAsync("Link");
        await WaitEditorReadyAsync();
        var uid = await ReadUniqueIdAsync();
        var token = uid ?? NewSyntheticToken();
        if (uid is null)
        {
            _idless.Add(token);
        }
        await SetTargetAutocompleteAsync(targetId);
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
            // NR's root editor does not expose every BattleScribe root field. Skip the ones it
            // genuinely lacks (the spec asserts these only on the BS anchors via a per-engine
            // expectedState override) rather than failing to find a widget.
            if (RootFieldsNrCannotEdit.Contains(field))
            {
                Console.Error.WriteLine(
                    $"[nr-gamedata-ui] root field '{field}' is not editable in NR's UI — skipping.");
                return;
            }

            await EditOpenFieldAsync(field, value);
            return;
        }

        if (field is "comment" or "description" || advancedEditor)
        {
            await EditOpenFieldAsync(field, value);
            return;
        }

        await NrGameDataUiActions.SetFieldAsync(_page, entryId, field, value);
    }

    /// <summary>Root (catalogue/game-system) fields BattleScribe has but NR's editor UI doesn't expose.</summary>
    private static readonly HashSet<string> RootFieldsNrCannotEdit = new(StringComparer.Ordinal)
    {
        "battleScribeVersion",
    };

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
            await WaitEditorReadyAsync();
            _selectedToken = token;
            return;
        }

        // A named entity carries a real id and renders as a tree node.
        if (!_idless.Contains(token) && !token.StartsWith("__nr", StringComparison.Ordinal))
        {
            var node = await NrGameDataUiActions.FindTreeNodeByIdAsync(_page, token);
            await node.ClickAsync();
            await WaitEditorReadyAsync();
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
        await _page.WaitForTimeoutAsync(300);
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
        await crumbs.Nth(index).ClickAsync();
        await WaitEditorReadyAsync();
    }

    /// <summary>Entry types whose context-menu item opens a submenu (e.g. Profile → profile types).</summary>
    private static readonly HashSet<string> SubmenuAddTypes = new(StringComparer.Ordinal)
    {
        "profile",
    };

    /// <summary>
    /// Some "add child" menu items (Profile, Link) are submenu triggers — the item carries a
    /// "❯" and a <c>context-menu-id</c>, and hovering it opens a second <c>.context-menu</c>
    /// listing the concrete options (e.g. the profile types). Hovers the trigger and clicks the
    /// first option. NR requires the choice at creation time; a later <c>setFields</c> can still
    /// adjust it where the editor exposes the field.
    /// </summary>
    private async Task OpenSubmenuAndPickFirstAsync(string parentLabel)
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
        await _page.WaitForTimeoutAsync(400);

        // The submenu is the visible menu that lacks the main menu's "Remove" item.
        var submenu = _page.Locator(".context-menu:visible")
            .Filter(new LocatorFilterOptions { HasNotText = "Remove" });
        await submenu.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await submenu.First.Locator("> div").First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
        await _page.WaitForTimeoutAsync(400);
    }

    private async Task ClickContextItemAsync(string label)
    {
        await _page.Locator(".context-menu > div")
            .Filter(new LocatorFilterOptions
            {
                HasTextRegex = new System.Text.RegularExpressions.Regex($"^\\s*{System.Text.RegularExpressions.Regex.Escape(label)}\\s*$"),
            })
            .First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
        await _page.WaitForTimeoutAsync(400);
    }

    private async Task WaitEditorReadyAsync()
    {
        await _page.Locator(".rightPanel fieldset").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await _page.WaitForTimeoutAsync(150);
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
        await _page.WaitForTimeoutAsync(150);
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
        "categoryLink" => "Link",
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
            await div.PressAsync("Tab");
            await _page.WaitForTimeoutAsync(200);
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
                await _page.WaitForTimeoutAsync(150);
                return true;
            case "value":
                {
                    var num = constraint.Locator("input[type='number']").First;
                    await num.FillAsync(value ?? "");
                    await num.PressAsync("Tab");
                    await _page.WaitForTimeoutAsync(150);
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

    private async Task<bool> EditModifierFieldAsync(ILocator modifier, string field, string? value)
    {
        switch (field)
        {
            case "type":
                // The modifier type select carries object option values, so select by visible label.
                await modifier.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Label = ModifierTypeLabel(value) });
                await _page.WaitForTimeoutAsync(150);
                return true;
            case "field":
                await SetIconSelectAsync(modifier.Locator(".select-container").First, value);
                return true;
            case "value":
                {
                    // After choosing the field, the value control is either a select (booleans) or input.
                    var sel = modifier.Locator("select").Nth(1);
                    if (await sel.CountAsync() > 0 && await sel.IsVisibleAsync())
                    {
                        try
                        {
                            await sel.SelectOptionAsync(new SelectOptionValue { Value = value });
                            await _page.WaitForTimeoutAsync(150);
                            return true;
                        }
                        catch
                        {
                            // fall through to input
                        }
                    }
                    var input = modifier.Locator("input").Last;
                    await input.FillAsync(value ?? "");
                    await input.PressAsync("Tab");
                    await _page.WaitForTimeoutAsync(150);
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
                        await _page.WaitForTimeoutAsync(150);
                        return true;
                    }
                    return false;
                }
            case "value":
                {
                    var num = q.Locator("input[type='number'], input[type='text']").First;
                    await num.FillAsync(value ?? "");
                    await num.PressAsync("Tab");
                    await _page.WaitForTimeoutAsync(150);
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
        await _page.WaitForTimeoutAsync(200);
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
        await _page.WaitForTimeoutAsync(200);
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
        await _page.WaitForTimeoutAsync(200);
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
            "targetId" => "Target",
            "typeName" => "Type",
            _ => FieldLabel(field),
        };
        var display = await ResolveDisplayNameAsync(field, value);
        await SetAutocompleteByRowAsync(rp, rowLabel, display ?? value);
    }

    private async Task SetAutocompleteByRowAsync(ILocator rp, string rowLabel, string? match)
    {
        var row = rp.Locator("table tr").Filter(new LocatorFilterOptions { HasText = rowLabel }).First;
        var container = row.Locator(".autocomplete").First;
        if (await container.CountAsync() == 0)
        {
            container = rp.Locator(".autocomplete").First;
        }
        await container.Locator(".autocomplete-input").First.ClickAsync();
        await _page.WaitForTimeoutAsync(300);
        var suggestions = container.Locator(".suggestions:not(.hidden) > div");
        await suggestions.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        var pick = suggestions.Filter(new LocatorFilterOptions { HasText = match });
        await pick.First.ClickAsync(new LocatorClickOptions { Timeout = 4_000 });
        await _page.WaitForTimeoutAsync(250);
    }

    private async Task SetIconSelectAsync(ILocator container, string? value)
    {
        if (value is null)
        {
            return;
        }
        await container.Locator(".iconselect-input").First.ClickAsync();
        await _page.WaitForTimeoutAsync(300);
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
        await _page.WaitForTimeoutAsync(250);
    }

    private async Task SetCheckboxAsync(ILocator checkbox, string? value)
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
        await _page.WaitForTimeoutAsync(150);
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
        "battleScribeVersion" => "BattleScribe Version",
        _ => char.ToUpperInvariant(field[0]) + field[1..],
    };
}
