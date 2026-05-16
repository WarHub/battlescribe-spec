using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.BsRosterUiDriver;

public sealed class BsUiRosterEngine : IRosterEngine
{
    private const string MainWindowTitle = "Roster Editor";
    private const string ConfirmWindowTitle = "Confirm";
    private const string NewRosterWindowTitle = "New Roster";
    private const string EditRosterWindowTitle = "Edit Roster";
    private const string AddForceWindowTitle = "Add Force";
    private const string CountSpinnerSelector = "Spinner";
    private const string BattleScribeVersion = "2.03";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly BsUiOptions _options;
    private readonly Dictionary<string, ProtocolCatalogue> _cataloguesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _forceNamesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _entryNamesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _costNamesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _pendingCostLimits = new(StringComparer.Ordinal);

    private BsRosterApp? _app;
    private AgentClient? _client;
    private ProtocolGameSystem? _gameSystem;
    private string? _specId;
    private bool _engineLocated;
    private bool _disposed;

    /// <summary>
    /// When true, <see cref="Cleanup"/> preserves the running app and agent connection
    /// so subsequent <see cref="Setup"/> calls reuse the same JVM instance (warm start).
    /// Useful for iterative debugging where JVM startup time is significant.
    /// </summary>
    public bool KeepAlive { get; set; }

    public BsUiRosterEngine(BsUiOptions options)
    {
        _options = options;
    }

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        => RunAsync(() => SetupAsync(gameSystem, catalogues));

    public ActionOutputs AddForce(string forceEntryId, string catalogueId)
        => RunAsync(() => AddForceAsync(forceEntryId, catalogueId));

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
        => RunAsync(() => AddChildForceAsync(parentForceId, forceEntryId, catalogueId));

    public void RemoveForce(string forceId)
        => RunAsync(() => RemoveForceAsync(forceId));

    public ActionOutputs SelectEntry(string forceId, string entryId)
        => RunAsync(() => SelectEntryAsync(forceId, entryId));

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
        => RunAsync(() => SelectChildEntryAsync(forceId, parentSelectionId, entryId));

    public void DeselectSelection(string forceId, string selectionId)
        => RunAsync(() => DeselectSelectionAsync(forceId, selectionId));

    public void SetSelectionCount(string forceId, string selectionId, int count)
        => RunAsync(() => SetSelectionCountAsync(forceId, selectionId, count));

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
        => RunAsync(() => DuplicateSelectionAsync(forceId, selectionId));

    public ActionOutputs DuplicateForce(string forceId)
        => RunAsync(() => DuplicateForceAsync(forceId));

    public void SetCostLimit(string costTypeId, decimal value)
        => RunAsync(() => SetCostLimitAsync(costTypeId, value));

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
        => RunAsync(() => SetCustomizationAsync(forceId, selectionId, categoryEntryId, customName, customNotes));

    public RosterState GetRosterState()
        => RunAsync(GetRosterStateAsync);

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
        => RunAsync(GetValidationErrorsAsync);

    public void Cleanup()
        => RunAsync(() => CleanupAsync());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            CleanupAsync(force: true).GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort during dispose
        }
        GC.SuppressFinalize(this);
    }

    private async Task<IReadOnlyList<string>> SetupAsync(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        ThrowIfDisposed();
        await CleanupAsync();

        _gameSystem = gameSystem;
        _engineLocated = false;
        _pendingCostLimits.Clear();
        _cataloguesById.Clear();
        _forceNamesById.Clear();
        _entryNamesById.Clear();
        _costNamesById.Clear();

        foreach (var catalogue in catalogues)
        {
            _cataloguesById[catalogue.Id] = catalogue;
        }

        IndexDefinitions(gameSystem, catalogues);

        try
        {
            // Warm start: reuse running app if available
            if (KeepAlive && _app is not null && _client is not null)
            {
                try
                {
                    _ = await ConnectedClient.PingAsync();
                    Console.Error.WriteLine("[bs-ui] Warm start: reusing existing BattleScribe instance.");

                    // Restage data files and reload
                    var warmFiles = BuildXmlFiles(gameSystem, catalogues);
                    await StageDataFilesAsync(_app.DataDirectoryPath, gameSystem, catalogues, warmFiles);

                    return [];
                }
                catch
                {
                    Console.Error.WriteLine("[bs-ui] Warm start: existing instance unresponsive, starting fresh.");
                    // Fall through to cold start
                    ConnectedClient.Dispose();
                    _client = null;
                    await _app.DisposeAsync();
                    _app = null;
                }
            }

            _app = new BsRosterApp(
                _options.JavaPath,
                _options.RosterEditorJarPath,
                _options.AgentJarPath,
                _options.IsolatedHomePath);

            var files = BuildXmlFiles(gameSystem, catalogues);
            await StageDataFilesAsync(_app.DataDirectoryPath, gameSystem, catalogues, files);

            await _app.StartAsync();
            _client = await _app.ConnectAsync();
            _ = await ConnectedClient.PingAsync();

            if (!await ConnectedClient.WaitForWindowAsync(MainWindowTitle, timeoutMs: 30000))
            {
                throw new TimeoutException("Roster Editor window did not appear within 30 seconds.");
            }

            await HandleStartupDialogsAsync();
            return [];
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            return [ex.Message];
        }
    }

    private async Task<ActionOutputs> AddForceAsync(string forceEntryId, string catalogueId)
    {
        EnsureSetup();
        var before = await ReadRosterStateOrEmptyAsync();

        if (before.Forces.Count == 0)
        {
            await CreateRosterAsync(forceEntryId, catalogueId);
        }
        else
        {
            try
            {
                await OpenEditRosterAsync();
                await AddForceInDialogAsync(forceEntryId, catalogueId, EditRosterWindowTitle);
                await CloseRosterDialogAsync(EditRosterWindowTitle);
            }
            catch
            {
                // Edit Roster dialog unavailable — fall back to engine API
                await AddForceViaEngineAsync(forceEntryId, catalogueId);
            }
        }

        var expectedForceName = FindForceEntryName(forceEntryId);
        var after = await WaitForRosterStateAsync(state =>
            FindAddedForce(before, state, parentForceId: null, expectedForceName, catalogueId) is not null);

        var createdForce = FindAddedForce(before, after, parentForceId: null, expectedForceName, catalogueId)
            ?? throw new InvalidOperationException($"Unable to locate force created for '{forceEntryId}'.");

        return BuildForceOutputs(createdForce);
    }

    private async Task<ActionOutputs> AddChildForceAsync(string parentForceId, string forceEntryId, string catalogueId)
    {
        EnsureRosterLoaded();
        var before = await ReadRosterStateAsync();

        try
        {
            await OpenEditRosterAsync();
            await SelectTreeItemAsync(["#treeRoster", "#treeForces"], TreeIdToken(parentForceId), EditRosterWindowTitle);
            await AddForceInDialogAsync(forceEntryId, catalogueId, EditRosterWindowTitle);
            await CloseRosterDialogAsync(EditRosterWindowTitle);
        }
        catch
        {
            // Edit Roster dialog unavailable — fall back to engine API
            await AddForceViaEngineAsync(forceEntryId, catalogueId, parentForceId);
        }

        var expectedForceName = FindForceEntryName(forceEntryId);
        var after = await WaitForRosterStateAsync(state =>
            FindAddedForce(before, state, parentForceId, expectedForceName, catalogueId) is not null);

        var createdForce = FindAddedForce(before, after, parentForceId, expectedForceName, catalogueId)
            ?? throw new InvalidOperationException($"Unable to locate child force created for '{forceEntryId}'.");

        return BuildForceOutputs(createdForce);
    }

    private async Task RemoveForceAsync(string forceId)
    {
        EnsureRosterLoaded();
        var before = await ReadRosterStateAsync();

        try
        {
            await OpenEditRosterAsync();
            await SelectTreeItemAsync(["#treeRoster", "#treeForces"], TreeIdToken(forceId), EditRosterWindowTitle);
            if (!await TryFireButtonAsync("#btnRemoveForce", EditRosterWindowTitle) &&
                !await TryClickTextAsync("Remove Force", EditRosterWindowTitle, "Button") &&
                !await TryClickTextAsync("Remove", EditRosterWindowTitle, "Button"))
            {
                await PressKeyAsync("DELETE", "#treeRoster", EditRosterWindowTitle);
            }
            await CloseRosterDialogAsync(EditRosterWindowTitle);
        }
        catch
        {
            // Edit Roster dialog unavailable — fall back to engine API
            await RemoveForceViaEngineAsync(forceId);
        }

        _ = await WaitForRosterStateAsync(state => FindForceById(state.Forces, forceId) is null);
        _ = before;
    }

    private async Task<ActionOutputs> SelectEntryAsync(string forceId, string entryId)
    {
        EnsureRosterLoaded();
        var before = await ReadRosterStateAsync();

        // UI approach: select force in roster tree, then double-click entry in catalogue tree
        await SelectTreeItemAsync(["#treeRoster"], TreeIdToken(forceId), MainWindowTitle);
        await ClickTreeItemAsync(["#treeCatalogue"], TreeIdToken(entryId), MainWindowTitle, doubleClick: true);

        return await WaitForSelectionOutputsAsync(before, forceId, parentSelectionId: null, entryId);
    }

    private async Task<ActionOutputs> SelectChildEntryAsync(string forceId, string parentSelectionId, string entryId)
    {
        EnsureRosterLoaded();
        var before = await ReadRosterStateAsync();

        // In BS Desktop, child entries appear in the edit panel as Spinners/CheckBoxes/Buttons
        // when the parent Selection is selected in the roster tree (not in the catalogue tree).
        await ClickTreeItemAsync(["#treeRoster"], TreeIdToken(parentSelectionId), MainWindowTitle, doubleClick: false);
        await Task.Delay(500);

        // Find the child entry's name for label matching in the edit panel
        var entryName = _entryNamesById.GetValueOrDefault(entryId) ?? entryId;

        // Click the control (Spinner increment / CheckBox toggle / Button fire) by label text
        var result = await ConnectedClient.CallAsync("clickControlByLabel", new System.Text.Json.Nodes.JsonObject
        {
            ["text"] = entryName,
            ["windowTitle"] = MainWindowTitle
        });
        var clicked = result?["clicked"]?.GetValue<bool>() ?? false;
        if (!clicked)
        {
            // Hidden entries aren't shown in UI — fall back to engine API
            result = await ConnectedClient.CallAsync("selectEntryViaEngine", new System.Text.Json.Nodes.JsonObject
            {
                ["forceId"] = forceId,
                ["entryId"] = entryId,
                ["parentSelectionId"] = parentSelectionId
            });
            var selected = result?["selected"]?.GetValue<bool>() ?? false;
            if (!selected)
            {
                var error = result?["error"]?.GetValue<string>() ?? result?.ToJsonString() ?? "unknown";
                throw new InvalidOperationException(
                    $"selectChildEntry failed for '{entryName}' (id={entryId}): {error}");
            }
            await ConnectedClient.CallAsync("waitForEngine", new System.Text.Json.Nodes.JsonObject
            {
                ["timeoutMs"] = 15000
            });
        }

        return await WaitForSelectionOutputsAsync(before, forceId, parentSelectionId, entryId);
    }

    private async Task DeselectSelectionAsync(string forceId, string selectionId)
    {
        EnsureRosterLoaded();
        // Use dedicated deselectEntry command — bypasses getNumChanges/isDuplicate check
        // which returns 0 for shared entries, causing deselect to silently fail
        var result = await ConnectedClient.CallAsync("deselectEntryViaEngine", new System.Text.Json.Nodes.JsonObject
        {
            ["forceId"] = forceId,
            ["selectionId"] = selectionId
        });
        var deselected = result?["deselected"]?.GetValue<bool>() ?? false;
        if (!deselected)
        {
            var error = result?["error"]?.GetValue<string>() ?? "unknown";
            throw new InvalidOperationException($"deselectSelection failed for '{selectionId}': {error}");
        }
        await ConnectedClient.CallAsync("waitForEngine", new System.Text.Json.Nodes.JsonObject
        {
            ["timeoutMs"] = 15000
        });
        _ = await WaitForRosterStateAsync(state => FindSelectionById(state.Forces, selectionId) is null);
    }

    private async Task SetSelectionCountAsync(string forceId, string selectionId, int count)
    {
        EnsureRosterLoaded();
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // Use engine API directly on a bg thread (safe with threadCount=1: no pool deadlock).
        // The UI spinner approach deadlocks because the controller's change listener calls
        // engine.setNumSelections → t() which blocks the FX thread indefinitely.
        var result = await ConnectedClient.CallAsync("setSelectionCount", new System.Text.Json.Nodes.JsonObject
        {
            ["forceId"] = forceId,
            ["selectionId"] = selectionId,
            ["count"] = count
        });
        var set = result?["set"]?.GetValue<bool>() ?? false;
        if (!set)
        {
            var error = result?["error"]?.GetValue<string>() ?? "unknown";
            throw new InvalidOperationException(
                $"setSelectionCount engine API failed: {error}");
        }

        // Wait for the bg thread engine operation to complete
        await ConnectedClient.CallAsync("waitForEngine", new System.Text.Json.Nodes.JsonObject
        {
            ["timeoutMs"] = 15000
        });
        _ = await WaitForRosterStateAsync(state => FindSelectionById(state.Forces, selectionId)?.Number == count);
    }

    private async Task<ActionOutputs> DuplicateSelectionAsync(string forceId, string selectionId)
    {
        EnsureRosterLoaded();
        _ = forceId;
        var before = await ReadRosterStateAsync();
        var original = FindSelectionById(before.Forces, selectionId)
            ?? throw new InvalidOperationException($"Selection '{selectionId}' not found.");

        await SelectTreeItemAsync(["#treeRoster"], TreeIdToken(selectionId), MainWindowTitle);
        await PressKeyAsync("D", "#treeRoster", MainWindowTitle, ctrl: true);

        var after = await WaitForRosterStateAsync(state =>
            FindDuplicatedSelection(before, state, original) is not null);

        var duplicated = FindDuplicatedSelection(before, after, original)
            ?? throw new InvalidOperationException($"Unable to locate duplicated selection for '{selectionId}'.");

        return new ActionOutputs { SelectionId = duplicated.Id };
    }

    private async Task<ActionOutputs> DuplicateForceAsync(string forceId)
    {
        EnsureRosterLoaded();
        var before = await ReadRosterStateAsync();
        var original = FindForceById(before.Forces, forceId)
            ?? throw new InvalidOperationException($"Force '{forceId}' not found.");

        await SelectTreeItemAsync(["#treeRoster"], TreeIdToken(forceId), MainWindowTitle);
        await PressKeyAsync("D", "#treeRoster", MainWindowTitle, ctrl: true);

        var after = await WaitForRosterStateAsync(state =>
            FindDuplicatedForce(before, state, original) is not null);

        var duplicated = FindDuplicatedForce(before, after, original)
            ?? throw new InvalidOperationException($"Unable to locate duplicated force for '{forceId}'.");

        return new ActionOutputs { ForceId = duplicated.Id };
    }

    private async Task SetCostLimitAsync(string costTypeId, decimal value)
    {
        EnsureSetup();
        _pendingCostLimits[costTypeId] = value;

        var current = await ReadRosterStateOrEmptyAsync();
        if (current.Forces.Count == 0)
        {
            return;
        }

        // Use engine API directly (BS Desktop has no post-creation cost limit UI)
        var result = await ConnectedClient.CallAsync("setCostLimit", new System.Text.Json.Nodes.JsonObject
        {
            ["costTypeId"] = costTypeId,
            ["value"] = (double)value
        });
        var set = result?["set"]?.GetValue<bool>() ?? false;
        if (!set)
        {
            var error = result?["error"]?.GetValue<string>() ?? "unknown";
            throw new InvalidOperationException($"setCostLimit failed: {error}");
        }
        await ConnectedClient.CallAsync("waitForEngine", new System.Text.Json.Nodes.JsonObject
        {
            ["timeoutMs"] = 15000
        });
    }

    private async Task AddForceViaEngineAsync(string forceEntryId, string catalogueId, string? parentForceId = null)
    {
        var args = new System.Text.Json.Nodes.JsonObject
        {
            ["forceEntryId"] = forceEntryId,
            ["catalogueId"] = catalogueId
        };
        if (parentForceId is not null)
        {
            args["parentForceId"] = parentForceId;
        }
        var result = await ConnectedClient.CallAsync("addForceViaEngine", args);
        var added = result?["added"]?.GetValue<bool>() ?? false;
        if (!added)
        {
            var error = result?["error"]?.GetValue<string>() ?? result?.ToJsonString() ?? "unknown";
            throw new InvalidOperationException($"addForceViaEngine failed: {error}");
        }
        await ConnectedClient.CallAsync("waitForEngine", new System.Text.Json.Nodes.JsonObject
        {
            ["timeoutMs"] = 15000
        });
    }

    private async Task RemoveForceViaEngineAsync(string forceId)
    {
        var result = await ConnectedClient.CallAsync("removeForceViaEngine", new System.Text.Json.Nodes.JsonObject
        {
            ["forceId"] = forceId
        });
        var removed = result?["removed"]?.GetValue<bool>() ?? false;
        if (!removed)
        {
            var error = result?["error"]?.GetValue<string>() ?? result?.ToJsonString() ?? "unknown";
            throw new InvalidOperationException($"removeForceViaEngine failed: {error}");
        }
        await ConnectedClient.CallAsync("waitForEngine", new System.Text.Json.Nodes.JsonObject
        {
            ["timeoutMs"] = 15000
        });
    }

    private async Task SetCustomizationAsync(
        string forceId,
        string? selectionId,
        string? categoryEntryId,
        string? customName,
        string? customNotes)
    {
        EnsureRosterLoaded();

        if (categoryEntryId is not null)
        {
            // Category customization isn't available via UI in BS desktop.
            // Use engine API directly to set customNotes on the category.
            var catResult = await ConnectedClient.CallAsync("setCategoryCustomNotes", new JsonObject
            {
                ["forceId"] = forceId,
                ["categoryEntryId"] = categoryEntryId,
                ["customName"] = customName ?? "",
                ["customNotes"] = customNotes ?? ""
            });
            Console.Error.WriteLine($"  [DEBUG] setCategoryCustomNotes result: {catResult}");
            return;
        }

        var targetId = selectionId ?? forceId;
        await SelectTreeItemAsync(["#treeRoster"], TreeIdToken(targetId), MainWindowTitle);

        if (!await TryFireButtonAsync("#btnCustomiseName", MainWindowTitle, async: true) &&
            !await TryClickTextAsync("Customise Name", MainWindowTitle, "Button"))
        {
            throw new InvalidOperationException("Could not open customization dialog.");
        }

        // If the supporter popup appears instead of the customization dialog, dismiss it and retry
        var windowTitle = await WaitForFirstWindowAsync(["Customise", "Customize", "Name", "Support BattleScribe"]);
        Console.Error.WriteLine($"  [DEBUG] SetCustomization: window found = '{windowTitle}'");
        if (windowTitle is not null && windowTitle.Contains("Support", StringComparison.OrdinalIgnoreCase))
        {
            // Dismiss the supporter popup
            if (!await TryClickTextAsync("MAYBE LATER", windowTitle, "Button") &&
                !await TryFireButtonAsync("#btnNegative", windowTitle))
            {
                await TryClickTextAsync("Maybe Later", windowTitle);
            }
            await WaitForWindowToCloseAsync(windowTitle);

            // Retry opening customization - the supporter check may still block
            throw new InvalidOperationException(
                "Customise Name requires a supporter pass and the supporter patch did not take effect. " +
                "The supporter popup was dismissed but the feature is unavailable.");
        }

        if (windowTitle is null)
        {
            throw new TimeoutException("Customization dialog did not appear.");
        }

        if (customName is not null)
        {
            await SetTextAsync(["#txtName", "#txtCustomName", "TextField"], customName, windowTitle);
            Console.Error.WriteLine($"  [DEBUG] SetCustomization: set customName='{customName}'");
        }

        if (customNotes is not null)
        {
            await SetTextAsync(["#txtNotes", "#txtCustomNotes", "TextArea"], customNotes, windowTitle);
            Console.Error.WriteLine($"  [DEBUG] SetCustomization: set customNotes='{customNotes}'");
        }

        Console.Error.WriteLine($"  [DEBUG] SetCustomization: clicking Done...");
        if (!await TryFireButtonAsync("#btnDone", windowTitle) &&
            !await TryClickTextAsync("Done", windowTitle, "Button") &&
            !await TryClickTextAsync("OK", windowTitle, "Button"))
        {
            throw new InvalidOperationException("Could not confirm customization dialog.");
        }

        await WaitForWindowToCloseAsync(windowTitle);
    }

    private async Task<RosterState> GetRosterStateAsync()
        => await ReadRosterStateOrEmptyAsync();

    private async Task<IReadOnlyList<ValidationErrorState>> GetValidationErrorsAsync()
    {
        EnsureSetup();
        if (!_engineLocated)
        {
            return [];
        }

        return await ReadValidationErrorsAsync();
    }

    private async Task CleanupAsync(bool force = false)
    {
        _engineLocated = false;

        if (KeepAlive && !force)
        {
            // Warm start: keep app/_client alive, just reset engine state
            return;
        }

        _client?.Dispose();
        _client = null;

        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private async Task CreateRosterAsync(string forceEntryId, string catalogueId)
    {
        await FireButtonAsync("#btnNewRoster", MainWindowTitle, async: true);
        await WaitForWindowAsync(NewRosterWindowTitle);

        // Select game system — even with one option, BS may need an explicit selection event
        await SelectComboBoxItemAsync("#cboGameSystem", _gameSystem!.Name, NewRosterWindowTitle, fallbackToFirst: true);
        await Task.Delay(300); // Allow UI to enable Add Force button

        await ApplyPendingCostLimitsAsync(NewRosterWindowTitle);

        await FireButtonAsync("#btnAddForce", NewRosterWindowTitle, async: true);
        await WaitForWindowAsync(AddForceWindowTitle);
        await AddForceInDialogAsync(forceEntryId, catalogueId, AddForceWindowTitle);

        await FireButtonAsync("#btnDone", NewRosterWindowTitle, async: true);
        await WaitForWindowToCloseAsync(NewRosterWindowTitle);
        await EnsureEngineLocatedAsync();
    }

    private async Task AddForceInDialogAsync(string forceEntryId, string catalogueId, string hostWindowTitle)
    {
        if (!string.Equals(hostWindowTitle, AddForceWindowTitle, StringComparison.Ordinal))
        {
            await FireButtonAsync("#btnAddForce", hostWindowTitle, async: true);
            await WaitForWindowAsync(AddForceWindowTitle);
        }

        var catalogueName = ResolveCatalogueName(catalogueId);
        await SelectComboBoxItemAsync("#cboCatalogue", catalogueName, AddForceWindowTitle, fallbackToFirst: true);
        await Task.Delay(300); // Allow force entries to populate after catalogue selection
        await SelectComboBoxItemAsync("#cboForceEntry", FindForceEntryName(forceEntryId), AddForceWindowTitle);
        await FireButtonAsync("#btnDone", AddForceWindowTitle);
        await WaitForWindowToCloseAsync(AddForceWindowTitle);
    }

    private async Task OpenEditRosterAsync()
    {
        if (!await TryFireButtonAsync("#btnEditRoster", MainWindowTitle, async: true) &&
            !await TryClickTextAsync("Edit Roster", MainWindowTitle, "Button"))
        {
            throw new InvalidOperationException("Could not open Edit Roster dialog.");
        }

        await WaitForWindowAsync(EditRosterWindowTitle);
    }

    private async Task CloseRosterDialogAsync(string windowTitle)
    {
        if (!await TryFireButtonAsync("#btnDone", windowTitle, async: true) &&
            !await TryClickTextAsync("Done", windowTitle, "Button") &&
            !await TryClickTextAsync("OK", windowTitle, "Button"))
        {
            throw new InvalidOperationException($"Could not close '{windowTitle}' dialog.");
        }

        await WaitForWindowToCloseAsync(windowTitle);
        await EnsureEngineLocatedAsync();
    }

    private async Task<ActionOutputs> WaitForSelectionOutputsAsync(
        RosterState before,
        string forceId,
        string? parentSelectionId,
        string entryId)
    {
        var after = await WaitForRosterStateAsync(state =>
            BuildSelectionOutputs(before, state, forceId, parentSelectionId, entryId) is not null);

        return BuildSelectionOutputs(before, after, forceId, parentSelectionId, entryId)
            ?? throw new InvalidOperationException($"Unable to locate selection created for '{entryId}'.");
    }

    private static ActionOutputs? BuildSelectionOutputs(
        RosterState before,
        RosterState after,
        string forceId,
        string? parentSelectionId,
        string entryId)
    {
        var beforeForce = FindForceById(before.Forces, forceId);
        var afterForce = FindForceById(after.Forces, forceId);
        if (afterForce is null)
        {
            return null;
        }

        var beforeParentSelections = GetParentSelections(beforeForce, parentSelectionId);
        var afterParentSelections = GetParentSelections(afterForce, parentSelectionId);
        if (afterParentSelections is null)
        {
            return null;
        }

        var created = FindCreatedSelection(beforeParentSelections, afterParentSelections, entryId);
        if (created is null)
        {
            return null;
        }

        var outputs = new ActionOutputs
        {
            SelectionId = created.Id,
        };

        var beforeSelf = created.Id is null || beforeParentSelections is null
            ? null
            : beforeParentSelections.FirstOrDefault(x => string.Equals(x.Id, created.Id, StringComparison.Ordinal));
        var selectionMap = CollectNewChildSelectionIds(beforeSelf, created);
        if (selectionMap.Count > 0)
        {
            outputs.Selections = selectionMap;
        }

        return outputs;
    }

    private static IReadOnlyList<SelectionState>? GetParentSelections(ForceState? force, string? parentSelectionId)
    {
        if (force is null)
        {
            return null;
        }

        if (parentSelectionId is null)
        {
            return force.Selections;
        }

        return FindSelectionById(force, parentSelectionId)?.Children;
    }

    private static SelectionState? FindCreatedSelection(
        IReadOnlyList<SelectionState>? beforeSelections,
        IReadOnlyList<SelectionState> afterSelections,
        string entryId)
    {
        var beforeById = beforeSelections?
            .Where(s => s.Id is not null)
            .ToDictionary(s => s.Id!, s => s, StringComparer.Ordinal)
            ?? new Dictionary<string, SelectionState>(StringComparer.Ordinal);

        var newById = afterSelections
            .Where(s => s.Id is not null && !beforeById.ContainsKey(s.Id!))
            .Where(s => string.Equals(s.EntryId, entryId, StringComparison.Ordinal))
            .ToList();
        if (newById.Count > 0)
        {
            return newById[0];
        }

        return afterSelections.FirstOrDefault(s =>
            string.Equals(s.EntryId, entryId, StringComparison.Ordinal) &&
            s.Id is not null &&
            beforeById.TryGetValue(s.Id, out var previous) &&
            s.Number != previous.Number);
    }

    private static Dictionary<string, string> CollectNewChildSelectionIds(SelectionState? before, SelectionState after)
    {
        var beforeIds = new HashSet<string>(EnumerateSelections(before).Select(s => s.Id).OfType<string>(), StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var child in EnumerateSelections(after))
        {
            if (child.Id is null || beforeIds.Contains(child.Id) || child.EntryId is null)
            {
                continue;
            }

            result.TryAdd(child.EntryId, child.Id);
        }

        return result;
    }

    private static IEnumerable<SelectionState> EnumerateSelections(SelectionState? selection)
    {
        if (selection is null)
        {
            yield break;
        }

        yield return selection;
        foreach (var child in selection.Children)
        {
            foreach (var nested in EnumerateSelections(child))
            {
                yield return nested;
            }
        }
    }

    private static ForceState? FindAddedForce(
        RosterState before,
        RosterState after,
        string? parentForceId,
        string expectedForceName,
        string catalogueId)
    {
        var beforeForces = parentForceId is null
            ? before.Forces
            : FindForceById(before.Forces, parentForceId)?.ChildForces;
        var afterForces = parentForceId is null
            ? after.Forces
            : FindForceById(after.Forces, parentForceId)?.ChildForces;

        if (afterForces is null)
        {
            return null;
        }

        var beforeIds = new HashSet<string>(FlattenForces(beforeForces).Select(f => f.Id).OfType<string>(), StringComparer.Ordinal);
        var newForces = FlattenForces(afterForces)
            .Where(f => f.Id is not null && !beforeIds.Contains(f.Id!))
            .ToList();

        return newForces.FirstOrDefault(f =>
                   string.Equals(f.CatalogueId, catalogueId, StringComparison.Ordinal) &&
                   string.Equals(f.Name, expectedForceName, StringComparison.Ordinal))
               ?? newForces.FirstOrDefault(f => string.Equals(f.CatalogueId, catalogueId, StringComparison.Ordinal))
               ?? newForces.FirstOrDefault();
    }

    private static ForceState? FindDuplicatedForce(RosterState before, RosterState after, ForceState original)
    {
        var beforeIds = new HashSet<string>(FlattenForces(before.Forces).Select(f => f.Id).OfType<string>(), StringComparer.Ordinal);
        return FlattenForces(after.Forces)
            .Where(f => f.Id is not null && !beforeIds.Contains(f.Id!))
            .FirstOrDefault(f =>
                string.Equals(f.Name, original.Name, StringComparison.Ordinal) &&
                string.Equals(f.CatalogueId, original.CatalogueId, StringComparison.Ordinal));
    }

    private static SelectionState? FindDuplicatedSelection(RosterState before, RosterState after, SelectionState original)
    {
        var beforeIds = new HashSet<string>(FlattenSelections(before.Forces).Select(s => s.Id).OfType<string>(), StringComparer.Ordinal);
        return FlattenSelections(after.Forces)
            .Where(s => s.Id is not null && !beforeIds.Contains(s.Id!))
            .FirstOrDefault(s =>
                string.Equals(s.EntryId, original.EntryId, StringComparison.Ordinal) &&
                string.Equals(s.Name, original.Name, StringComparison.Ordinal));
    }

    private static IEnumerable<ForceState> FlattenForces(IEnumerable<ForceState>? forces)
    {
        if (forces is null)
        {
            yield break;
        }

        foreach (var force in forces)
        {
            yield return force;
            if (force.ChildForces is null)
            {
                continue;
            }

            foreach (var child in FlattenForces(force.ChildForces))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<SelectionState> FlattenSelections(IEnumerable<ForceState> forces)
    {
        foreach (var force in forces)
        {
            foreach (var selection in FlattenSelections(force.Selections))
            {
                yield return selection;
            }

            if (force.ChildForces is null)
            {
                continue;
            }

            foreach (var selection in FlattenSelections(force.ChildForces))
            {
                yield return selection;
            }
        }
    }

    private static IEnumerable<SelectionState> FlattenSelections(IEnumerable<SelectionState> selections)
    {
        foreach (var selection in selections)
        {
            yield return selection;
            foreach (var child in FlattenSelections(selection.Children))
            {
                yield return child;
            }
        }
    }

    private static ActionOutputs BuildForceOutputs(ForceState force)
    {
        var outputs = new ActionOutputs { ForceId = force.Id };
        var selections = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectForceSelectionIds(force, selections);
        if (selections.Count > 0)
        {
            outputs.Selections = selections;
        }

        return outputs;
    }

    private static void CollectForceSelectionIds(ForceState force, Dictionary<string, string> selections)
    {
        foreach (var selection in force.Selections)
        {
            CollectSelectionIds(selection, selections);
        }

        if (force.ChildForces is null)
        {
            return;
        }

        foreach (var childForce in force.ChildForces)
        {
            CollectForceSelectionIds(childForce, selections);
        }
    }

    private static void CollectSelectionIds(SelectionState selection, Dictionary<string, string> selections)
    {
        if (selection.Id is not null && selection.EntryId is not null)
        {
            selections.TryAdd(selection.EntryId, selection.Id);
        }

        foreach (var child in selection.Children)
        {
            CollectSelectionIds(child, selections);
        }
    }

    private async Task<RosterState> WaitForRosterStateAsync(Func<RosterState, bool> predicate, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await ReadRosterStateAsync();
                if (predicate(state))
                {
                    return state;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(lastError?.Message ?? "Timed out waiting for roster mutation.");
    }

    private async Task<RosterState> ReadRosterStateAsync()
    {
        EnsureRosterLoaded();
        var result = await ConnectedClient.GetRosterStateAsync();
        var json = ExtractJson(result);
        if (TryExtractError(result, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var dto = JsonSerializer.Deserialize<AgentRosterState>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize roster state from agent.");

        var validationErrors = await ReadValidationErrorsAsync();
        return MapRosterState(dto, validationErrors);
    }

    private async Task<RosterState> ReadRosterStateOrEmptyAsync()
    {
        if (_client is null || _gameSystem is null)
        {
            return EmptyRosterState();
        }

        try
        {
            if (!_engineLocated)
            {
                return EmptyRosterState();
            }

            return await ReadRosterStateAsync();
        }
        catch
        {
            return EmptyRosterState();
        }
    }

    private async Task<IReadOnlyList<ValidationErrorState>> ReadValidationErrorsAsync()
    {
        var result = await ConnectedClient.GetValidationErrorsAsync();
        if (TryExtractError(result, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var json = ExtractJson(result);
        return JsonSerializer.Deserialize<List<ValidationErrorState>>(json, JsonOptions) ?? [];
    }

    private RosterState EmptyRosterState()
    {
        if (_gameSystem is null)
        {
            return new RosterState("", "", [], [], []);
        }

        var rosterName = string.IsNullOrWhiteSpace(_specId) ? "Spec Test" : _specId;
        var costLimits = _pendingCostLimits.Count == 0
            ? null
            : _pendingCostLimits.Select(x => new CostState(_costNamesById.GetValueOrDefault(x.Key, x.Key), x.Key, x.Value)).ToList();

        return new RosterState(
            rosterName,
            _gameSystem.Id,
            [],
            [],
            [],
            CostLimits: costLimits,
            GameSystemName: _gameSystem.Name);
    }

    private async Task EnsureEngineLocatedAsync()
    {
        if (_engineLocated)
        {
            return;
        }

        var result = await ConnectedClient.FindEngineAsync();
        if (TryExtractError(result, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var found = result?["found"]?.GetValue<bool>() == true;
        if (!found)
        {
            throw new InvalidOperationException(result?["error"]?.GetValue<string>() ?? "BattleScribe engine instance not found.");
        }

        _engineLocated = true;

        // Patch supporter pass to unlock premium features (customise name, etc.)
        await PatchSupporterPassAsync();

        // Set roster name to spec ID (BS UI defaults to "New Roster")
        if (!string.IsNullOrWhiteSpace(_specId))
        {
            await SetRosterNameAsync(_specId);
        }
    }

    private async Task PatchSupporterPassAsync()
    {
        try
        {
            var result = await ConnectedClient.CallAsync("patchSupporterPass");
            var patched = result?["patched"]?.GetValue<bool>() == true;
            if (!patched)
            {
                Console.Error.WriteLine($"[bs-ui] Warning: could not patch supporter pass: {result?.ToJsonString()}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-ui] Warning: patchSupporterPass failed: {ex.Message}");
        }
    }

    private async Task SetRosterNameAsync(string name)
    {
        try
        {
            await ConnectedClient.CallAsync("setRosterName", new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = name
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-ui] Warning: setRosterName failed: {ex.Message}");
        }
    }

    /// <summary>Exports the current roster as BattleScribe XML (.ros format).</summary>
    public async Task<string?> ExportRosterXmlAsync()
    {
        ThrowIfDisposed();
        if (_client is null)
        {
            throw new InvalidOperationException("Not connected to BattleScribe app.");
        }
        return await ConnectedClient.ExportRosterXmlAsync();
    }

    /// <summary>Captures a screenshot of the current JavaFX scene as PNG bytes.</summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        ThrowIfDisposed();
        if (_client is null)
        {
            Console.Error.WriteLine("[bs-ui] Warning: cannot capture screenshot — not connected.");
            return null;
        }
        return await ConnectedClient.CaptureScreenshotAsync();
    }

    /// <summary>Reads the visible UI state from the Roster Editor window.</summary>
    public async Task<JsonNode?> GetUiStateAsync()
    {
        ThrowIfDisposed();
        if (_client is null)
        {
            return null;
        }
        return await ConnectedClient.GetUiStateAsync();
    }

    private async Task HandleStartupDialogsAsync()
    {
        await Task.Delay(1500);
        if (await HasWindowAsync(ConfirmWindowTitle) && await TryFireButtonAsync("#btnNegative", ConfirmWindowTitle))
        {
            await WaitForWindowToCloseAsync(ConfirmWindowTitle);
        }
    }

    private async Task ApplyPendingCostLimitsAsync(string windowTitle)
    {
        if (_pendingCostLimits.Count == 0)
        {
            return;
        }

        if (_pendingCostLimits.Count > 1)
        {
            throw new NotSupportedException(
                "Multiple cost limits are not supported by the current BS UI driver setup flow.");
        }

        var limit = _pendingCostLimits.Single();
        _ = limit;
        await ConnectedClient.SetSpinnerValueAsync(CountSpinnerSelector, value: DecimalToSpinnerValue(limit.Value), windowTitle: windowTitle);
    }

    private static async Task StageDataFilesAsync(
        string dataDirectoryPath,
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        Directory.CreateDirectory(dataDirectoryPath);
        var gameSystemDirectory = Path.Combine(dataDirectoryPath, gameSystem.Id);
        if (Directory.Exists(gameSystemDirectory))
        {
            Directory.Delete(gameSystemDirectory, recursive: true);
        }

        Directory.CreateDirectory(gameSystemDirectory);

        foreach (var (fileName, content) in files)
        {
            var filePath = Path.Combine(gameSystemDirectory, fileName);
            await File.WriteAllTextAsync(filePath, content);
        }

        var indexPath = Path.Combine(gameSystemDirectory, "index.bsi");
        await File.WriteAllTextAsync(indexPath, BuildIndexXml(gameSystem, catalogues, files));
    }

    private static string BuildIndexXml(
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        var catalogueFiles = files.Where(x => x.FileName.EndsWith(".cat", StringComparison.Ordinal)).ToList();
        XNamespace ns = "http://www.battlescribe.net/schema/dataIndexSchema";
        var entries = new List<XElement>
        {
            new(
                ns + "dataIndexEntry",
                new XAttribute("filePath", "system.gst"),
                new XAttribute("dataType", "gamesystem"),
                new XAttribute("dataId", gameSystem.Id),
                new XAttribute("dataName", gameSystem.Name),
                new XAttribute("dataBattleScribeVersion", BattleScribeVersion),
                new XAttribute("dataRevision", 1)),
        };

        for (var i = 0; i < catalogues.Count; i++)
        {
            var fileName = i < catalogueFiles.Count ? catalogueFiles[i].FileName : $"catalogue{i}.cat";
            entries.Add(
                new XElement(
                    ns + "dataIndexEntry",
                    new XAttribute("filePath", fileName),
                    new XAttribute("dataType", "catalogue"),
                    new XAttribute("dataId", catalogues[i].Id),
                    new XAttribute("dataName", catalogues[i].Name),
                    new XAttribute("dataBattleScribeVersion", BattleScribeVersion),
                    new XAttribute("dataRevision", 1)));
        }

        var root = new XElement(
            ns + "dataIndex",
            new XAttribute("battleScribeVersion", BattleScribeVersion),
            new XAttribute("name", gameSystem.Name),
            new XElement(ns + "dataIndexEntries", entries));

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).ToString();
    }

    private static IReadOnlyList<(string FileName, string Content)> BuildXmlFiles(
        ProtocolGameSystem gameSystem,
        ProtocolCatalogue[] catalogues)
    {
        var files = new List<(string FileName, string Content)>
        {
            ("system.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)),
        };

        foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
        {
            files.Add((fileName, xml));
        }

        return files;
    }

    private void IndexDefinitions(ProtocolGameSystem gameSystem, IEnumerable<ProtocolCatalogue> catalogues)
    {
        if (gameSystem.CostTypes is not null)
        {
            foreach (var costType in gameSystem.CostTypes)
            {
                _costNamesById[costType.Id] = costType.Name;
            }
        }

        IndexForceEntries(gameSystem.ForceEntries);
        IndexSelectionContainer(gameSystem.SelectionEntries, gameSystem.EntryLinks, gameSystem.SharedSelectionEntries, gameSystem.SharedSelectionEntryGroups);

        foreach (var catalogue in catalogues)
        {
            IndexForceEntries(catalogue.ForceEntries);
            IndexSelectionContainer(catalogue.SelectionEntries, catalogue.EntryLinks, catalogue.SharedSelectionEntries, catalogue.SharedSelectionEntryGroups);
            if (catalogue.CostTypes is null)
            {
                continue;
            }

            foreach (var costType in catalogue.CostTypes)
            {
                _costNamesById[costType.Id] = costType.Name;
            }
        }
    }

    private void IndexForceEntries(IEnumerable<ProtocolForceEntry>? forceEntries)
    {
        if (forceEntries is null)
        {
            return;
        }

        foreach (var forceEntry in forceEntries)
        {
            _forceNamesById[forceEntry.Id] = forceEntry.Name;
            IndexForceEntries(forceEntry.ForceEntries);
        }
    }

    private void IndexSelectionContainer(
        IEnumerable<ProtocolSelectionEntry>? selectionEntries,
        IEnumerable<ProtocolEntryLink>? entryLinks,
        IEnumerable<ProtocolSelectionEntry>? sharedSelectionEntries,
        IEnumerable<ProtocolSelectionEntryGroup>? sharedSelectionEntryGroups)
    {
        IndexSelectionEntries(selectionEntries);
        IndexEntryLinks(entryLinks);
        IndexSelectionEntries(sharedSelectionEntries);
        IndexSelectionEntryGroups(sharedSelectionEntryGroups);
    }

    private void IndexSelectionEntries(IEnumerable<ProtocolSelectionEntry>? selectionEntries)
    {
        if (selectionEntries is null)
        {
            return;
        }

        foreach (var selectionEntry in selectionEntries)
        {
            _entryNamesById[selectionEntry.Id] = selectionEntry.Name;
            IndexSelectionEntries(selectionEntry.SelectionEntries);
            IndexEntryLinks(selectionEntry.EntryLinks);
            IndexSelectionEntryGroups(selectionEntry.SelectionEntryGroups);
        }
    }

    private void IndexSelectionEntryGroups(IEnumerable<ProtocolSelectionEntryGroup>? groups)
    {
        if (groups is null)
        {
            return;
        }

        foreach (var group in groups)
        {
            _entryNamesById[group.Id] = group.Name;
            IndexSelectionEntries(group.SelectionEntries);
            IndexEntryLinks(group.EntryLinks);
            IndexSelectionEntryGroups(group.SelectionEntryGroups);
        }
    }

    private void IndexEntryLinks(IEnumerable<ProtocolEntryLink>? entryLinks)
    {
        if (entryLinks is null)
        {
            return;
        }

        foreach (var entryLink in entryLinks)
        {
            _entryNamesById[entryLink.Id] = entryLink.Name;
        }
    }

    private string FindForceEntryName(string forceEntryId)
        => _forceNamesById.TryGetValue(forceEntryId, out var name)
            ? name
            : throw new InvalidOperationException($"Force entry '{forceEntryId}' not found in setup data.");

    private string ResolveCatalogueName(string catalogueId)
        => _cataloguesById.TryGetValue(catalogueId, out var catalogue)
            ? catalogue.Name
            : throw new InvalidOperationException($"Catalogue '{catalogueId}' not found in setup data.");

    private async Task SelectComboBoxItemAsync(
        string selector,
        string desiredText,
        string windowTitle,
        bool fallbackToFirst = false)
    {
        var items = await ConnectedClient.GetComboBoxItemsAsync(selector, windowTitle) as JsonObject
            ?? throw new InvalidOperationException($"ComboBox '{selector}' not found in '{windowTitle}'.");

        var available = items["items"] as JsonArray ?? [];
        var best = available
            .Select(x => new
            {
                Text = x?["text"]?.GetValue<string>(),
                Index = x?["index"]?.GetValue<int>() ?? -1,
            })
            .FirstOrDefault(x => string.Equals(x.Text, desiredText, StringComparison.Ordinal))
            ?? available
                .Select(x => new
                {
                    Text = x?["text"]?.GetValue<string>(),
                    Index = x?["index"]?.GetValue<int>() ?? -1,
                })
                .FirstOrDefault(x => x.Text?.Contains(desiredText, StringComparison.Ordinal) == true)
            ?? (fallbackToFirst && available.Count > 0
                ? new { Text = available[0]?["text"]?.GetValue<string>(), Index = available[0]?["index"]?.GetValue<int>() ?? 0 }
                : null);

        if (best is null || best.Index < 0)
        {
            throw new InvalidOperationException(
                $"Item '{desiredText}' not found in combo '{selector}' ({string.Join(", ", available.Select(x => x?["text"]?.GetValue<string>()))}).");
        }

        _ = await ConnectedClient.SelectComboBoxItemAsync(selector, index: best.Index, windowTitle: windowTitle);
    }

    private async Task SelectTreeItemAsync(IEnumerable<string> selectors, string text, string windowTitle, int retries = 3)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            foreach (var selector in selectors)
            {
                try
                {
                    _ = await ConnectedClient.SelectTreeItemAsync(selector, text, windowTitle);
                    return;
                }
                catch
                {
                    // try next selector
                }
            }
            if (attempt < retries)
            {
                await Task.Delay(500 * (attempt + 1));
            }
        }

        throw new InvalidOperationException($"Tree item '{text}' not found in '{windowTitle}'.");
    }

    private async Task ClickTreeItemAsync(IEnumerable<string> selectors, string text, string windowTitle, bool doubleClick, int retries = 3)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            foreach (var selector in selectors)
            {
                try
                {
                    _ = await ConnectedClient.ClickTreeItemAsync(selector, text, doubleClick, windowTitle);
                    return;
                }
                catch
                {
                    // try next selector
                }
            }
            if (attempt < retries)
            {
                await Task.Delay(500 * (attempt + 1));
            }
        }

        throw new InvalidOperationException($"Tree item '{text}' not found in '{windowTitle}'.");
    }

    private async Task SetTextAsync(IEnumerable<string> selectors, string text, string windowTitle)
    {
        foreach (var selector in selectors)
        {
            try
            {
                await ConnectedClient.SetNodeTextAsync(selector, text, windowTitle);
                Console.Error.WriteLine($"  [DEBUG] SetTextAsync: set '{selector}' = '{text}' in '{windowTitle}'");
                return;
            }
            catch
            {
                // try next selector
            }
        }

        throw new InvalidOperationException($"No editable text field found in '{windowTitle}'.");
    }

    private async Task FireButtonAsync(string selector, string windowTitle, bool async = false)
    {
        if (!await TryFireButtonAsync(selector, windowTitle, async))
        {
            throw new InvalidOperationException($"Button '{selector}' not found in '{windowTitle}'.");
        }
    }

    private async Task<bool> TryFireButtonAsync(string selector, string windowTitle, bool async = false)
    {
        try
        {
            var node = await ConnectedClient.FindNodeAsync(selector, windowTitle);
            if (node is null)
            {
                return false;
            }

            var parameters = new JsonObject
            {
                ["selector"] = selector,
                ["windowTitle"] = windowTitle,
            };

            if (async)
            {
                parameters["async"] = "true";
            }

            _ = await ConnectedClient.CallAsync("fireButton", parameters);
            return true;
        }
        catch (AgentException)
        {
            // Node found but not a ButtonBase (e.g., Label) — treat as not found
            return false;
        }
    }

    private async Task<bool> TryClickTextAsync(string text, string windowTitle, string? nodeType = null)
    {
        var parameters = new JsonObject
        {
            ["text"] = text,
            ["windowTitle"] = windowTitle,
        };

        if (nodeType is not null)
        {
            parameters["nodeType"] = nodeType;
        }

        var found = await ConnectedClient.CallAsync("findNodeByText", parameters);
        if (found is null)
        {
            return false;
        }

        _ = await ConnectedClient.CallAsync("clickNode", new JsonObject
        {
            ["text"] = text,
            ["windowTitle"] = windowTitle,
        });
        return true;
    }

    private async Task PressKeyAsync(string key, string selector, string windowTitle, bool ctrl = false)
    {
        var parameters = new JsonObject
        {
            ["key"] = key,
            ["selector"] = selector,
            ["windowTitle"] = windowTitle,
        };

        if (ctrl)
        {
            parameters["ctrl"] = true;
        }

        _ = await ConnectedClient.CallAsync("pressKey", parameters);
    }

    private async Task WaitForWindowAsync(string title)
    {
        if (!await ConnectedClient.WaitForWindowAsync(title, timeoutMs: 30000))
        {
            throw new TimeoutException($"Window '{title}' did not appear.");
        }
    }

    private async Task<string?> WaitForFirstWindowAsync(IEnumerable<string> titleFragments, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var windows = await ConnectedClient.GetWindowsAsync() as JsonArray;
            if (windows is not null)
            {
                foreach (var title in windows.Select(w => w?["title"]?.GetValue<string>()))
                {
                    if (title is null)
                    {
                        continue;
                    }

                    if (titleFragments.Any(fragment => title.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    {
                        return title;
                    }
                }
            }

            await Task.Delay(200);
        }

        return null;
    }

    private async Task WaitForWindowToCloseAsync(string title, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!await HasWindowAsync(title))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Window '{title}' did not close.");
    }

    private async Task<bool> HasWindowAsync(string title)
    {
        if (await ConnectedClient.GetWindowsAsync() is not JsonArray windows)
        {
            return false;
        }

        // Use exact match or "starts with" to avoid matching main window title
        // (e.g. "New Roster" dialog vs "Roster Editor 2.03.21 - New Roster (GS v1)")
        return windows.Any(w =>
        {
            var windowTitle = w?["title"]?.GetValue<string>();
            return windowTitle is not null &&
                   (string.Equals(windowTitle, title, StringComparison.Ordinal) ||
                    windowTitle.StartsWith(title + " ", StringComparison.Ordinal));
        });
    }

    private static RosterState MapRosterState(AgentRosterState dto, IReadOnlyList<ValidationErrorState> validationErrors)
        => new(
            dto.Name ?? string.Empty,
            dto.GameSystemId ?? string.Empty,
            [.. (dto.Forces ?? []).Select(MapForceState)],
            [.. (dto.Costs ?? []).Select(MapCostState)],
            validationErrors,
            CostLimits: dto.CostLimits is null ? null : [.. dto.CostLimits.Select(MapCostState)],
            GameSystemName: dto.GameSystemName);

    private static ForceState MapForceState(AgentForceState dto)
        => new(
            dto.Id,
            dto.Name ?? string.Empty,
            dto.CatalogueId,
            [.. (dto.Selections ?? []).Select(MapSelectionState)],
            ChildForces: dto.ChildForces is null ? null : [.. dto.ChildForces.Select(MapForceState)],
            Hidden: dto.Hidden,
            PublicationId: string.IsNullOrEmpty(dto.PublicationId) ? null : dto.PublicationId,
            Page: dto.Page,
            Rules: dto.Rules is null or [] ? null : [.. dto.Rules.Select(MapRuleState)],
            EntryId: dto.EntryId,
            CatalogueName: dto.CatalogueName,
            CustomName: dto.CustomName,
            CustomNotes: dto.CustomNotes,
            Categories: dto.Categories is null or [] ? null : [.. dto.Categories.Select(MapCategoryState)],
            Publications: dto.Publications is null or [] ? null : [.. dto.Publications.Select(MapPublicationState)]);

    private static SelectionState MapSelectionState(AgentSelectionState dto)
        => new(
            dto.Id,
            dto.Name ?? string.Empty,
            dto.EntryId,
            Type: dto.Type,
            dto.Number,
            dto.Hidden,
            [.. (dto.Costs ?? []).Select(MapCostState)],
            [.. (dto.Children ?? []).Select(MapSelectionState)],
            Page: dto.Page,
            PublicationId: dto.PublicationId,
            PublicationName: dto.PublicationName,
            EntryGroupId: dto.EntryGroupId,
            CustomName: dto.CustomName,
            CustomNotes: dto.CustomNotes,
            Categories: dto.Categories is null or [] ? null : [.. dto.Categories.Select(MapCategoryState)],
            Profiles: dto.Profiles is null or [] ? null : [.. dto.Profiles.Select(MapProfileState)],
            Rules: dto.Rules is null or [] ? null : [.. dto.Rules.Select(MapRuleState)]);

    private static CostState MapCostState(AgentCostState dto)
        => new(dto.Name ?? string.Empty, dto.TypeId ?? string.Empty, dto.Value);

    private static CategoryState MapCategoryState(AgentCategoryState dto)
        => new(dto.Name ?? string.Empty, dto.EntryId, dto.Primary,
            CustomNotes: string.IsNullOrEmpty(dto.CustomNotes) ? null : dto.CustomNotes,
            PublicationId: string.IsNullOrEmpty(dto.PublicationId) ? null : dto.PublicationId,
            Page: dto.Page);

    private static PublicationState MapPublicationState(AgentPublicationState dto)
        => new(dto.Id ?? string.Empty, dto.Name ?? string.Empty);

    private static ProfileState MapProfileState(AgentProfileState dto)
        => new(
            dto.Name ?? string.Empty,
            dto.TypeId,
            dto.TypeName,
            dto.Hidden,
            [.. (dto.Characteristics ?? []).Select(c => new CharacteristicState(c.Name ?? string.Empty, c.TypeId, c.Value ?? string.Empty))],
            Page: dto.Page,
            PublicationId: dto.PublicationId);

    private static RuleState MapRuleState(AgentRuleState dto)
        => new(dto.Name ?? string.Empty, dto.Description ?? string.Empty, dto.Hidden, Page: dto.Page, PublicationId: dto.PublicationId);

    private static string ExtractJson(JsonNode? result)
        => result switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            null => "null",
            _ => result.ToJsonString(),
        };

    private static bool TryExtractError(JsonNode? result, out string error)
    {
        error = string.Empty;
        if (result is JsonObject obj && obj["error"] is JsonValue errorNode)
        {
            error = errorNode.GetValue<string>();
            return true;
        }

        return false;
    }

    private static string TreeIdToken(string id) => $":{id}:";

    private static int DecimalToSpinnerValue(decimal value)
    {
        if (decimal.Truncate(value) != value)
        {
            throw new NotSupportedException("Spinner-based cost limit editing only supports integer values.");
        }

        return decimal.ToInt32(value);
    }

    private static ForceState? FindForceById(IEnumerable<ForceState> forces, string forceId)
    {
        foreach (var force in forces)
        {
            if (string.Equals(force.Id, forceId, StringComparison.Ordinal))
            {
                return force;
            }

            if (force.ChildForces is null)
            {
                continue;
            }

            var child = FindForceById(force.ChildForces, forceId);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static SelectionState? FindSelectionById(IEnumerable<ForceState> forces, string selectionId)
    {
        foreach (var force in forces)
        {
            var selection = FindSelectionById(force, selectionId);
            if (selection is not null)
            {
                return selection;
            }
        }

        return null;
    }

    private static SelectionState? FindSelectionById(ForceState force, string selectionId)
    {
        foreach (var selection in force.Selections)
        {
            var found = FindSelectionById(selection, selectionId);
            if (found is not null)
            {
                return found;
            }
        }

        if (force.ChildForces is null)
        {
            return null;
        }

        return FindSelectionById(force.ChildForces, selectionId);
    }

    private static SelectionState? FindSelectionById(SelectionState selection, string selectionId)
    {
        if (string.Equals(selection.Id, selectionId, StringComparison.Ordinal))
        {
            return selection;
        }

        foreach (var child in selection.Children)
        {
            var found = FindSelectionById(child, selectionId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindParentSelectionId(IEnumerable<ForceState> forces, string selectionId)
    {
        foreach (var force in forces)
        {
            var result = FindParentSelectionId(force.Selections, selectionId);
            if (result is not null)
            {
                return result;
            }

            if (force.ChildForces is not null)
            {
                result = FindParentSelectionId(force.ChildForces, selectionId);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static string? FindParentSelectionId(IEnumerable<SelectionState> selections, string selectionId)
    {
        foreach (var sel in selections)
        {
            foreach (var child in sel.Children)
            {
                if (string.Equals(child.Id, selectionId, StringComparison.Ordinal))
                {
                    return sel.Id;
                }

                var deeper = FindParentSelectionId(child.Children, selectionId);
                if (deeper is not null)
                {
                    return deeper;
                }
            }
        }

        return null;
    }

    private void EnsureSetup()
    {
        ThrowIfDisposed();
        if (_gameSystem is null || _client is null)
        {
            throw new InvalidOperationException("Engine has not been set up.");
        }
    }

    private void EnsureRosterLoaded()
    {
        EnsureSetup();
        if (!_engineLocated)
        {
            throw new InvalidOperationException("Roster has not been created yet.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Maximum time allowed for a single engine action (e.g. addForce, selectEntry).
    /// If an action exceeds this, a diagnostic dump is captured and a TimeoutException is thrown.
    /// Default is 60 seconds. Set via <c>BS_UI_ACTION_TIMEOUT</c> env var (in seconds).
    /// </summary>
    public static TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("BS_UI_ACTION_TIMEOUT"), out var envTimeout) && envTimeout > 0
            ? envTimeout
            : 60);

    private AgentClient ConnectedClient => _client ?? throw new InvalidOperationException("Agent client is not connected.");

    /// <summary>
    /// Maximum number of retry attempts for transient failures (timeout, agent communication).
    /// Set to 0 to disable retries. Default is 1 (one retry after initial failure).
    /// </summary>
    public static int MaxRetries { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("BS_UI_MAX_RETRIES"), out var envRetries) && envRetries >= 0
            ? envRetries
            : 1;

    /// <summary>Delay between retry attempts.</summary>
    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    private T RunAsync<T>(Func<Task<T>> func, [System.Runtime.CompilerServices.CallerMemberName] string? actionName = null)
    {
        return RunWithRetryAsync(func, actionName ?? "unknown").GetAwaiter().GetResult();
    }

    private void RunAsync(Func<Task> func, [System.Runtime.CompilerServices.CallerMemberName] string? actionName = null)
    {
        RunWithRetryAsync(async () => { await func(); return 0; }, actionName ?? "unknown").GetAwaiter().GetResult();
    }

    private async Task<T> RunWithRetryAsync<T>(Func<Task<T>> func, string actionName)
    {
        // NOTE: Retries are only safe because a timeout/transient failure typically means
        // the app is unresponsive and needs restart. The full setup flow will re-initialize
        // if the connection is lost. Non-transient errors (InvalidOperationException) are
        // never retried.
        var attempts = MaxRetries + 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await RunWithTimeoutAsync(func, actionName);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < attempts)
            {
                Console.Error.WriteLine(
                    $"[bs-ui] Action '{actionName}' failed (attempt {attempt}/{attempts}): " +
                    $"{ex.GetType().Name}: {ex.Message}. Retrying in {RetryDelay.TotalSeconds:F0}s...");
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException or AgentException)
            {
                CaptureAndRethrow(ex, actionName);
                throw; // unreachable but required
            }
        }
        throw new InvalidOperationException("Unreachable");
    }

    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException or OperationCanceledException or AgentException;

    private static async Task<T> RunWithTimeoutAsync<T>(Func<Task<T>> func, string actionName)
    {
        using var cts = new CancellationTokenSource(ActionTimeout);
        var task = func();
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
        if (completed != task)
        {
            throw new TimeoutException(
                $"Action '{actionName}' exceeded the {ActionTimeout.TotalSeconds:F0}s timeout. " +
                "The BattleScribe UI may be unresponsive.");
        }
        return await task;
    }

    private void CaptureAndRethrow(Exception ex, string actionName)
    {
        // Best-effort diagnostic capture — don't let it mask the original exception
        try
        {
            BsUiDiagnostics.CaptureAsync(_client, _specId ?? "unknown", actionName, ex)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore diagnostic capture failures
        }
    }

    private sealed class AgentRosterState
    {
        public string? Name { get; set; }
        public string? GameSystemId { get; set; }
        public string? GameSystemName { get; set; }
        public List<AgentCostState>? Costs { get; set; }
        public List<AgentCostState>? CostLimits { get; set; }
        public List<AgentForceState>? Forces { get; set; }
    }

    private sealed class AgentForceState
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? CatalogueId { get; set; }
        public string? EntryId { get; set; }
        public string? CatalogueName { get; set; }
        public string? CustomName { get; set; }
        public string? CustomNotes { get; set; }
        public string? PublicationId { get; set; }
        public string? Page { get; set; }
        public List<AgentSelectionState>? Selections { get; set; }
        public List<AgentForceState>? ChildForces { get; set; }
        public bool Hidden { get; set; }
        public List<AgentCategoryState>? Categories { get; set; }
        public List<AgentPublicationState>? Publications { get; set; }
        public List<AgentRuleState>? Rules { get; set; }
    }

    private sealed class AgentSelectionState
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? EntryId { get; set; }
        public string? EntryGroupId { get; set; }
        public string? Type { get; set; }
        public int Number { get; set; }
        public bool Hidden { get; set; }
        public string? Page { get; set; }
        public string? PublicationId { get; set; }
        public string? PublicationName { get; set; }
        public string? CustomName { get; set; }
        public string? CustomNotes { get; set; }
        public List<AgentCostState>? Costs { get; set; }
        public List<AgentSelectionState>? Children { get; set; }
        public List<AgentCategoryState>? Categories { get; set; }
        public List<AgentProfileState>? Profiles { get; set; }
        public List<AgentRuleState>? Rules { get; set; }
    }

    private sealed class AgentCostState
    {
        public string? Name { get; set; }
        public string? TypeId { get; set; }
        public decimal Value { get; set; }
    }

    private sealed class AgentCategoryState
    {
        public string? Name { get; set; }
        public string? EntryId { get; set; }
        public bool Primary { get; set; }
        public string? CustomNotes { get; set; }
        public string? PublicationId { get; set; }
        public string? Page { get; set; }
    }

    private sealed class AgentPublicationState
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class AgentProfileState
    {
        public string? Name { get; set; }
        public string? TypeId { get; set; }
        public string? TypeName { get; set; }
        public bool Hidden { get; set; }
        public List<AgentCharacteristicState>? Characteristics { get; set; }
        public string? Page { get; set; }
        public string? PublicationId { get; set; }
    }

    private sealed class AgentCharacteristicState
    {
        public string? Name { get; set; }
        public string? TypeId { get; set; }
        public string? Value { get; set; }
    }

    private sealed class AgentRuleState
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool Hidden { get; set; }
        public string? Page { get; set; }
        public string? PublicationId { get; set; }
    }
}
