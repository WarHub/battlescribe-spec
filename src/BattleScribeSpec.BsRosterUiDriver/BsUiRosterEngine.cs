using System.Text.Json;
using System.Text.Json.Nodes;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Drives the BattleScribe Roster Editor desktop application via a Java agent.
/// <para>
/// <b>Timeout architecture</b> — operations pass through multiple timeout layers:
/// <list type="bullet">
///   <item><b>AgentClient.CallTimeout (90s for actions)</b> — Max time for a single JSON-RPC
///     round-trip. High-level *Action methods get 90s; other calls use 30s default.</item>
///   <item><b>FX thread dispatch (60s)</b> — Java agent's <c>executeOnFxThread</c> timeout.
///     If a UI command doesn't complete on the JavaFX Application Thread within 60s,
///     the agent returns an error (likely deadlock).</item>
///   <item><b>Window wait (30s)</b> — Java agent's <c>waitForWindow</c>/<c>waitForWindowClose</c>
///     timeout for dialog transitions during high-level actions.</item>
///   <item><b>State poll (10s)</b> — Java agent's <c>waitForStateChange</c> polls
///     until the roster state reflects the expected change.</item>
///   <item><b>Startup timeout (30s)</b> — <c>BsRosterApp.StartAsync</c> waits for
///     the Java process to print the agent port line.</item>
///   <item><b>Diagnostic timeout (5s)</b> — <c>BsUiDiagnostics</c> temporarily reduces
///     CallTimeout to 5s to capture state even when the agent is partially stuck.</item>
/// </list>
/// For a typical high-level action, the effective max wait is roughly:
/// window wait (30s) + state poll (10s) = 40s on the Java side, well within the 90s RPC timeout.
/// </para>
/// </summary>
public sealed class BsUiRosterEngine : IRosterEngine
{
    private const string MainWindowTitle = "Roster Editor";
    private const string ConfirmWindowTitle = "Confirm";

    // Timeout constants — see class-level docs for the full timeout architecture.
    private const int EngineOpWaitMs = 15_000;
    private const int PollTimeoutMs = 10_000;
    private const int WindowWaitMs = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly BsUiOptions _options;
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
    /// Set when an action fails in a way that leaves the running app in an unknown state (an
    /// unexpected-modal <see cref="AgentException"/>, or any <see cref="TimeoutException"/>) — see
    /// <see cref="MarkPoisonedIfUnsafe"/>. A poisoned engine's next <see cref="CleanupAsync"/> tears
    /// the app down even under <see cref="KeepAlive"/>, so the NEXT spec cold-starts a fresh JVM
    /// instead of risking a warm-reused app that might still have a dialog open or be mid-corruption.
    /// One bad spec costs one cold restart; it cannot cascade into later specs.
    /// </summary>
    private bool _poisoned;

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
        => RunAsync(() => CallActionAsync<ActionOutputs>("rosterAddForceAction", new JsonObject
        {
            ["forceEntryId"] = forceEntryId,
            ["catalogueId"] = catalogueId,
        }, isFirstForce: true, forceEntryId: forceEntryId, gameSystemName: _gameSystem?.Name));

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
        => RunAsync(() => CallActionAsync<ActionOutputs>("rosterAddChildForceAction", new JsonObject
        {
            ["parentForceId"] = parentForceId,
            ["forceEntryId"] = forceEntryId,
            ["catalogueId"] = catalogueId,
        }));

    public void RemoveForce(string forceId)
        => RunAsync(() => CallActionAsync("rosterRemoveForceAction", new JsonObject { ["forceId"] = forceId }));

    public ActionOutputs SelectEntry(string forceId, string entryId)
        => RunAsync(() => CallActionAsync<ActionOutputs>("rosterSelectEntryAction", new JsonObject
        {
            ["forceId"] = forceId,
            ["entryId"] = entryId,
        }));

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
        => RunAsync(() => CallActionAsync<ActionOutputs>("rosterSelectChildEntryAction", new JsonObject
        {
            ["forceId"] = forceId,
            ["parentSelectionId"] = parentSelectionId,
            ["entryId"] = entryId,
            ["entryName"] = _entryNamesById.GetValueOrDefault(entryId) ?? entryId,
        }));

    public void DeselectSelection(string forceId, string selectionId)
        => RunAsync(() => CallActionAsync("rosterDeselectSelectionAction", new JsonObject
        {
            ["forceId"] = forceId,
            ["selectionId"] = selectionId,
        }));

    public void SetSelectionCount(string forceId, string selectionId, int count)
        => RunAsync(() => CallActionAsync("rosterSetSelectionCountAction", new JsonObject
        {
            ["forceId"] = forceId,
            ["selectionId"] = selectionId,
            ["count"] = count,
        }));

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
        => RunAsync(() => CallActionAsync<ActionOutputs>("rosterDuplicateSelectionAction", new JsonObject
        {
            ["forceId"] = forceId,
            ["selectionId"] = selectionId,
        }));

    public ActionOutputs DuplicateForce(string forceId)
        => RunAsync(() => CallActionAsync<ActionOutputs>("rosterDuplicateForceAction", new JsonObject
        {
            ["forceId"] = forceId,
        }));

    public void SetCostLimit(string costTypeId, decimal value)
    {
        _pendingCostLimits[costTypeId] = value;
        if (!_engineLocated)
        {
            // Roster not yet created; cost limit will be applied during createRosterAction.
            return;
        }
        var costName = _costNamesById.GetValueOrDefault(costTypeId) ?? costTypeId;
        RunAsync(() => CallActionAsync("rosterSetCostLimitAction", new JsonObject
        {
            ["costTypeId"] = costTypeId,
            ["costName"] = costName,
            ["value"] = (int)value,
        }));
    }

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
        => RunAsync(() => CallActionAsync("rosterSetCustomizationAction", new JsonObject
        {
            ["forceId"] = forceId,
            ["selectionId"] = selectionId,
            ["categoryEntryId"] = categoryEntryId,
            ["customName"] = customName,
            ["customNotes"] = customNotes,
        }));

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
        _entryNamesById.Clear();
        _costNamesById.Clear();

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

                    // Close any open roster to return to clean main window state.
                    // This dismisses unsaved changes without saving.
                    await CloseCurrentRosterIfOpenAsync();

                    // Restage data files for the new run.
                    // NOTE: The app's loaded game data is from the previous startup.
                    // Warm start is only reliable for re-running the same game system.
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

            if (!await ConnectedClient.WaitForWindowAsync(MainWindowTitle, timeoutMs: WindowWaitMs))
            {
                throw new TimeoutException("Roster Editor window did not appear within 30 seconds.");
            }

            await HandleStartupDialogsAsync();
            return [];
        }
        catch (Exception ex)
        {
            // force: true — a setup-phase failure leaves no usable app (it may have died mid-start,
            // or never reached a stable window), so KeepAlive/warm-reuse is meaningless here. Without
            // force, CleanupAsync would no-op (KeepAlive defaults to true and _poisoned is only set by
            // action-phase failures), _app would be silently overwritten by the next cold-start attempt,
            // and the orphaned JVM process would leak.
            await CleanupAsync(force: true);
            return [ex.Message];
        }
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

        if (KeepAlive && !force && !_poisoned)
        {
            // Warm start: keep app/_client alive, just reset engine state
            return;
        }

        if (_poisoned)
        {
            Console.Error.WriteLine("[bs-ui] Engine poisoned by a prior failure — tearing down for a fresh cold start.");
        }

        _client?.Dispose();
        _client = null;

        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }

        // The upcoming (re)start is a fresh instance — clear the flag so it isn't
        // needlessly torn down again if it warm-reuses successfully afterwards.
        _poisoned = false;
    }

    /// <summary>
    /// Closes any currently open roster to return to the clean main window.
    /// Dismisses "save changes" confirmation without saving.
    /// </summary>
    private async Task CloseCurrentRosterIfOpenAsync()
    {
        // Check if a roster is currently loaded by reading state
        var state = await ReadRosterStateOrEmptyAsync();
        if (state.Forces.Count == 0)
        {
            // No roster open
            return;
        }

        // Close via keyboard shortcut Ctrl+W or "Close" button
        Console.Error.WriteLine("[bs-ui] Warm start: closing current roster...");
        try
        {
            _ = await ConnectedClient.PressKeyAsync("W", windowTitle: MainWindowTitle, ctrl: true);
        }
        catch
        {
            // Ctrl+W not available, try Close button
            if (!await TryFireButtonAsync("#btnClose", MainWindowTitle, async: true) &&
                !await TryClickTextAsync("Close", MainWindowTitle, "Button"))
            {
                // No way to close — proceed anyway, AddForce handles existing rosters
                return;
            }
        }

        // Handle "save changes?" confirmation dialog
        await Task.Delay(500);
        if (await TryClickTextAsync("No", ConfirmWindowTitle, "Button") ||
            await TryClickTextAsync("Don't Save", ConfirmWindowTitle, "Button") ||
            await TryClickTextAsync("Discard", ConfirmWindowTitle, "Button"))
        {
            await Task.Delay(300);
        }
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

    /// <summary>
    /// Exports the current roster as BattleScribe XML (.ros format) — the <see cref="IRosterEngine"/>
    /// member, so this engine reports an export like the other three rather than falling through to
    /// the interface default that throws <see cref="NotSupportedException"/>. That default is how the
    /// stack says "genuinely unsupported", and since <c>RosterRunner.ExecuteFileAssertion</c> now
    /// fails on it, an engine that <em>can</em> export must never raise it: driving this engine
    /// in-process (any <c>new RosterRunner(engine)</c>, not just via <c>bs-engine-host</c>) would
    /// otherwise fail every <c>expectedFile</c> byte-compare with "engine reports no export".
    /// </summary>
    public string ExportRosterXml() => ExportRosterXmlAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Exports the current roster as BattleScribe XML (.ros format).
    /// <para>
    /// Throws rather than returning null when the agent answers without an <c>xml</c> field. Null was
    /// the adapter layer's "this engine does not support export" signal (see
    /// <c>AdapterHandler</c>/<c>ServeCommand</c>), so a malformed or empty agent reply used to arrive
    /// at the runner as a capability gap and — before the swallow was removed — silently passed the
    /// byte-compare. A broken reply is a fault, and faults must be loud.
    /// </para>
    /// </summary>
    public async Task<string> ExportRosterXmlAsync()
    {
        ThrowIfDisposed();
        if (_client is null)
        {
            throw new InvalidOperationException("Not connected to BattleScribe app.");
        }
        return await ConnectedClient.ExportRosterXmlAsync()
            ?? throw new InvalidOperationException(
                "[bs-ui] exportRosterXml returned no 'xml' field. The agent is reachable but did not " +
                "produce a roster — this is an agent/app fault, not a missing capability.");
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

    /// <summary>Starts recording user interactions in the Roster Editor UI.</summary>
    public async Task StartRecordingAsync()
    {
        ThrowIfDisposed();
        if (_client is null)
        {
            throw new InvalidOperationException("Not connected to BattleScribe app.");
        }
        await ConnectedClient.StartRecordingAsync();
    }

    /// <summary>Stops recording and returns the recorded actions as JSON.</summary>
    public async Task<JsonNode?> StopRecordingAsync()
    {
        ThrowIfDisposed();
        if (_client is null)
        {
            return null;
        }
        return await ConnectedClient.StopRecordingAsync();
    }

    private async Task HandleStartupDialogsAsync()
    {
        await Task.Delay(1500);
        if (await HasWindowAsync(ConfirmWindowTitle) && await TryFireButtonAsync("#btnNegative", ConfirmWindowTitle))
        {
            await WaitForWindowToCloseAsync(ConfirmWindowTitle);
        }
    }

    private static Task StageDataFilesAsync(
        string dataDirectoryPath,
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        IReadOnlyList<(string FileName, string Content)> files)
        => BsUiDataStaging.StageDataFilesAsync(dataDirectoryPath, gameSystem, catalogues, files);

    private static IReadOnlyList<(string FileName, string Content)> BuildXmlFiles(
        ProtocolGameSystem gameSystem,
        ProtocolCatalogue[] catalogues)
    {
        var files = new List<(string FileName, string Content)>
        {
            ($"{gameSystem.Id}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)),
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

        IndexSelectionContainer(gameSystem.SelectionEntries, gameSystem.EntryLinks, gameSystem.SharedSelectionEntries, gameSystem.SharedSelectionEntryGroups);

        foreach (var catalogue in catalogues)
        {
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

    private async Task WaitForWindowToCloseAsync(string title, int timeoutMs = PollTimeoutMs)
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
            CustomName: string.IsNullOrEmpty(dto.CustomName) ? null : dto.CustomName,
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

    // ═══════════════════════════════════════════════════════════════════
    // Roster action RPC
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calls a high-level Java-side RosterAction and deserializes the result.
    /// </summary>
    private async Task<T> CallActionAsync<T>(string method, JsonObject parameters,
        bool isFirstForce = false, string? forceEntryId = null, string? gameSystemName = null)
    {
        EnsureSetup();

        // Special case: first force uses createRosterAction instead of addForceAction
        if (isFirstForce && method == "rosterAddForceAction")
        {
            var state = await ReadRosterStateOrEmptyAsync();
            if (state.Forces.Count == 0)
            {
                method = "rosterCreateRosterAction";
                parameters = new JsonObject
                {
                    ["forceEntryId"] = forceEntryId,
                    ["catalogueId"] = parameters["catalogueId"]?.GetValue<string>(),
                    ["gameSystemName"] = gameSystemName ?? _gameSystem!.Name,
                    ["rosterName"] = _specId,
                };
                // Apply pending cost limit if any
                if (_pendingCostLimits.Count == 1)
                {
                    parameters["costLimit"] = (int)_pendingCostLimits.Values.First();
                }
            }
        }

        // The action methods on the Java side run on a background thread with their own timeouts.
        // Increase call timeout to match: Java has 30s window wait + 10s state poll per step.
        var originalTimeout = ConnectedClient.CallTimeout;
        ConnectedClient.CallTimeout = TimeSpan.FromSeconds(90);
        try
        {
            var result = await ConnectedClient.CallAsync(method, parameters);
            var json = result?.ToJsonString() ?? "{}";
            var output = JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidOperationException($"{method} returned null result");

            // Mark engine as located after first successful action that creates a roster
            if (!_engineLocated && output is ActionOutputs { ForceId: not null })
            {
                _engineLocated = true;
            }

            return output;
        }
        finally
        {
            ConnectedClient.CallTimeout = originalTimeout;
        }
    }

    /// <summary>
    /// Calls a high-level Java-side RosterAction that returns no meaningful outputs.
    /// </summary>
    private async Task CallActionAsync(string method, JsonObject parameters)
    {
        EnsureSetup();

        var originalTimeout = ConnectedClient.CallTimeout;
        ConnectedClient.CallTimeout = TimeSpan.FromSeconds(90);
        try
        {
            await ConnectedClient.CallAsync(method, parameters);
        }
        finally
        {
            ConnectedClient.CallTimeout = originalTimeout;
        }
    }

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
                MarkPoisonedIfUnsafe(ex);
                Console.Error.WriteLine(
                    $"[bs-ui] Action '{actionName}' failed (attempt {attempt}/{attempts}): " +
                    $"{ex.GetType().Name}: {ex.Message}. Retrying in {RetryDelay.TotalSeconds:F0}s...");
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException or AgentException)
            {
                MarkPoisonedIfUnsafe(ex);
                CaptureAndRethrow(ex, actionName);
                throw; // unreachable but required
            }
        }
        throw new InvalidOperationException("Unreachable");
    }

    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException or OperationCanceledException or AgentException;

    /// <summary>
    /// Marks the engine poisoned (see <see cref="_poisoned"/>) when <paramref name="ex"/> signals
    /// the app was left in an unknown state: any <see cref="TimeoutException"/> (the UI thread may
    /// be wedged/deadlocked — see the class-level timeout architecture docs), or an
    /// <see cref="AgentException"/> whose message reports an unexpected modal dialog left open by
    /// <c>DialogInspector.assertNoUnexpectedModals</c> on the Java side. Both mean the running app's
    /// state can no longer be trusted for warm-reuse by a later, unrelated spec.
    /// </summary>
    private void MarkPoisonedIfUnsafe(Exception ex)
    {
        if (ex is TimeoutException ||
            (ex is AgentException && ex.Message.Contains("Unexpected modal dialog", StringComparison.Ordinal)))
        {
            _poisoned = true;
        }
    }

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
        public string? CustomName { get; set; }
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
