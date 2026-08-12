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

    /// <summary>Ceiling for BattleScribe's first-launch "download data?" prompt to appear.</summary>
    /// <remarks>
    /// A ceiling, not a cost: the loop exits the moment the dialog shows. It cannot be replaced by a
    /// condition because "this dialog will never appear" has no positive signal — and a dialog that
    /// arrives after it is not lost, because DialogInspector.assertNoUnexpectedModals runs on every
    /// Java-side wait and fails the next action loudly.
    /// </remarks>
    private const int StartupDialogCeilingMs = 3_000;

    /// <summary>
    /// Poll interval for window-state questions. Matches the Java agent's own POLL_INTERVAL_MS.
    /// </summary>
    /// <remarks>
    /// Do not shrink this casually: every iteration is an FX-thread round trip contending with
    /// BattleScribe's own dialog handling on that single thread — an observer effect DialogInspector's
    /// javadoc records as measurably slowing real dialog transitions.
    /// </remarks>
    private const int StartupDialogPollMs = 200;
    private const int WindowWaitMs = 30_000;

    /// <summary>
    /// How long one <see cref="AgentClient.ProbeFxThreadAsync"/> call is given before the instance
    /// is called wedged — both to gate warm-start reuse and to end the retry backoff.
    /// </summary>
    private static readonly TimeSpan FxProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly BsUiOptions _options;
    private readonly Dictionary<string, string> _entryNamesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _costNamesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _pendingCostLimits = new(StringComparer.Ordinal);

    /// <summary>Ids that name a GROUP — a heading in the edit panel, never a control.</summary>
    private readonly HashSet<string> _groupEntryIds = new(StringComparer.Ordinal);

    /// <summary>Entry-link id to the id it targets, for resolving a link to what it labels as.</summary>
    private readonly Dictionary<string, string> _linkTargetsById = new(StringComparer.Ordinal);

    /// <summary>
    /// Children of one container, in declaration order — one list per container. Collected while
    /// indexing and consumed once by <see cref="IndexLabelOccurrences"/>, which needs every name
    /// resolved before it can tell which siblings share a label.
    /// </summary>
    private readonly List<List<string>> _siblingGroups = [];

    /// <summary>
    /// How many EARLIER siblings carry the same edit-panel label as this entry.
    /// <para>
    /// Two entry links onto one shared entry render as two rows spelled identically — BattleScribe
    /// labels a control with what the link RESOLVES to, so `link-alpha` and `link-alpha-2` onto a
    /// shared `Trigger` both read `'Trigger'`, and the panel exposes no id to tell them apart.
    /// Addressing by label alone always drove the first of them, so asking for the second
    /// incremented the first and the wait timed out reporting a child that was never added.
    /// </para>
    /// <para>
    /// Position is the key that is left: the panel renders one row per child, and rows sharing a
    /// label appear in the order their entries are declared. Both orders come from the same
    /// catalogue, so they agree by construction rather than by observation.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, int> _labelOccurrenceById = new(StringComparer.Ordinal);

    /// <summary>
    /// Per engine, not per <see cref="BsRosterApp"/>: a cold start under an explicit
    /// <see cref="BsUiOptions.IsolatedHomePath"/> gets a new app on the same data directory.
    /// </summary>
    private readonly BsUiDataStaging _dataStaging = new();

    private BsRosterApp? _app;
    private AgentClient? _client;
    private ProtocolGameSystem? _gameSystem;

    /// <summary>
    /// The loaded game system's identity, set by BOTH setup paths — the file-based one has no
    /// <see cref="ProtocolGameSystem"/> to read it from.
    /// </summary>
    private string? _gameSystemId;
    private string? _gameSystemName;
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
        }, isFirstForce: true, forceEntryId: forceEntryId, gameSystemName: _gameSystemName));

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
            ["entryName"] = ResolveEntryLabel(entryId),
            ["labelOccurrence"] = LabelOccurrenceOf(entryId),
        }));

    /// <summary>
    /// Which of the identically-labelled rows this entry is, or 0 when its label is its own.
    /// </summary>
    private int LabelOccurrenceOf(string entryId)
        => _labelOccurrenceById.TryGetValue(entryId, out var occurrence) ? occurrence : 0;

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

        // The same refusal the New Roster dialog makes, for the same reason and through the same
        // rule — see BsUiCostLimits. This path used to cast instead, so a value the dialog declined
        // to invent was invented here the moment a roster already existed.
        if (BsUiCostLimits.SpinnerValueFor(value) is not { } spinnerValue)
        {
            // Said out loud: the observable consequence is a limit that is simply absent, which
            // reads as BattleScribe ignoring one rather than as never having been given one.
            Console.Error.WriteLine(
                $"[bs-ui] Cost limit {value} for '{costTypeId}' is not a whole number and cannot be "
                + "entered in BattleScribe's integer spinner — leaving the roster's limit unset "
                + "rather than truncating it.");
            return;
        }

        var costName = _costNamesById.GetValueOrDefault(costTypeId) ?? costTypeId;
        RunAsync(() => CallActionAsync("rosterSetCostLimitAction", new JsonObject
        {
            ["costTypeId"] = costTypeId,
            ["costName"] = costName,
            ["value"] = spinnerValue,
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

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
        => RunAsync(() => SetupFromFilesAsync(files));

    /// <summary>
    /// Sets the engine up from raw BattleScribe XML — a <c>dataSource</c> spec's real data files —
    /// rather than from Protocol objects.
    /// </summary>
    /// <remarks>
    /// The app lifecycle is identical to <see cref="SetupAsync"/>; only where the data and the
    /// game system's identity come from differs. Identity is read off the <c>.gst</c> root, and the
    /// entry-name index that labels edit-panel controls is read out of the same XML — without it
    /// every control on this path would be addressed by its raw id.
    /// <para>
    /// No Protocol objects means no <c>CostTypes</c>, so the New Roster dialog's cost-limit spinner
    /// is left alone here. Real data carries its own limits and a spec on this path asks about the
    /// roster it loaded, not about a default we would have to invent.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> SetupFromFilesAsync(
        IReadOnlyList<(string FileName, string Content)> files)
    {
        ThrowIfDisposed();
        await CleanupAsync();
        ResetSetupState();

        // Inside the guard for the same reason as the generated path, and with one more of its own:
        // these files come off DISK, so `XDocument.Parse` here is reading data this engine did not
        // produce and cannot assume is well-formed.
        return await GuardSetupAsync(async () =>
        {
            var gameSystemFile = files.FirstOrDefault(
                f => f.FileName.EndsWith(".gst", StringComparison.OrdinalIgnoreCase));
            if (gameSystemFile.Content is null)
            {
                return ["SetupFromFiles: no .gst game system file among the supplied files."];
            }

            var root = System.Xml.Linq.XDocument.Parse(gameSystemFile.Content).Root;
            _gameSystemId = (string?)root?.Attribute("id");
            _gameSystemName = (string?)root?.Attribute("name");
            if (string.IsNullOrEmpty(_gameSystemId) || string.IsNullOrEmpty(_gameSystemName))
            {
                return [$"SetupFromFiles: '{gameSystemFile.FileName}' has no id/name on its root element."];
            }

            IndexDefinitionsFromXml(files);
            await StartOrReuseAsync(files);
            return [];
        });
    }

    private void ResetSetupState()
    {
        _engineLocated = false;
        _pendingCostLimits.Clear();
        _entryNamesById.Clear();
        _groupEntryIds.Clear();
        _linkTargetsById.Clear();
        _siblingGroups.Clear();
        _labelOccurrenceById.Clear();
        _costNamesById.Clear();
        _gameSystem = null;
        _gameSystemId = null;
        _gameSystemName = null;
    }

    private async Task<IReadOnlyList<string>> SetupAsync(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        ThrowIfDisposed();
        await CleanupAsync();
        ResetSetupState();

        return await GuardSetupAsync(async () =>
        {
            _gameSystem = gameSystem;
            _gameSystemId = gameSystem.Id;
            _gameSystemName = gameSystem.Name;

            IndexDefinitions(gameSystem, catalogues);

            await StartOrReuseAsync(BuildXmlFiles(gameSystem, catalogues));
            return [];
        });
    }

    /// <summary>
    /// Warm-reuses the running BattleScribe instance or cold-starts one, with
    /// <paramref name="files"/> staged either way.
    /// </summary>
    /// <remarks>
    /// Throws on failure rather than returning errors: <see cref="GuardSetupAsync"/> is the one
    /// place that turns a setup-phase failure into returned errors, and it covers the whole phase
    /// including the data generation that runs before this.
    /// </remarks>
    private async Task StartOrReuseAsync(
        IReadOnlyList<(string FileName, string Content)> files)
    {
        // Warm start: reuse running app if available
        if (KeepAlive && _app is not null && _client is not null)
        {
            try
            {
                // Not PingAsync: a wedged FX thread still answers `ping`, so that gate declared
                // undrivable instances reusable and every action against them then failed.
                await ConnectedClient.ProbeFxThreadAsync(FxProbeTimeout);
                Console.Error.WriteLine("[bs-ui] Warm start: reusing existing BattleScribe instance.");

                // No roster-close step here. There used to be a call to
                // CloseCurrentRosterIfOpenAsync(), and it could never do anything: `_engineLocated`
                // is set false a few lines above, and that method's first act is a state read
                // which short-circuits on exactly that flag and returns an EMPTY roster — so it
                // always saw zero forces and returned before touching the app.
                //
                // Warm-start roster closing is really handled on the Java side, by
                // RosterActions.waitForNewRosterWindowDismissingContinuePrompt, which answers
                // BattleScribe's "Continue? Roster has not been saved" prompt with NO.
                //
                // If this is ever reinstated here it MUST end in a throw, never a return: a
                // close that silently fails leaves the previous spec's roster open, and
                // CallActionAsync then skips rosterCreateRosterAction because forces already
                // exist — appending this spec's force to the PREVIOUS spec's roster. A spec
                // asserting only on its own selection would pass on polluted data.

                // Restage data files for the new run.
                // NOTE: The app's loaded game data is from the previous startup.
                // Warm start is only reliable for re-running the same game system.
                await _dataStaging.StageDataFilesAsync(_app.DataDirectoryPath, _gameSystemId!, files);

                return;
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

        await _dataStaging.StageDataFilesAsync(_app.DataDirectoryPath, _gameSystemId!, files);

        await _app.StartAsync();
        _client = await _app.ConnectAsync();
        _ = await ConnectedClient.PingAsync();

        if (!await ConnectedClient.WaitForWindowAsync(MainWindowTitle, timeoutMs: WindowWaitMs))
        {
            throw new TimeoutException("Roster Editor window did not appear within 30 seconds.");
        }

        await HandleStartupDialogsAsync();
    }

    /// <summary>
    /// Runs a setup phase so that ANY failure in it is reported the way <c>Setup</c> promises —
    /// returned errors, with the app torn down — instead of an exception escaping the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>force: true</c> — a setup-phase failure leaves no usable app (it may have died mid-start,
    /// or never reached a stable window), so KeepAlive/warm-reuse is meaningless here. Without
    /// force, <c>CleanupAsync</c> would no-op (KeepAlive defaults to true and <c>_poisoned</c> is
    /// only set by action-phase failures), <c>_app</c> would be silently overwritten by the next
    /// cold-start attempt, and the orphaned JVM process would leak.
    /// </para>
    /// <para>
    /// This wraps the WHOLE phase rather than app startup alone, and that is the point. It used to
    /// guard only <see cref="StartOrReuseAsync"/>, while entry indexing and XML generation ran
    /// ahead of it — so a spec whose data the generator rejects threw out of <c>Setup</c> instead
    /// of failing as a reported setup error, and skipped the teardown entirely. The gamedata engine
    /// generates inside its handler and never had the gap; these two were given the same fix and
    /// drifted on where the boundary sat.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> GuardSetupAsync(Func<Task<IReadOnlyList<string>>> setup)
    {
        try
        {
            return await setup();
        }
        catch (Exception ex)
        {
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
        if (_client is null || _gameSystemId is null)
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
        var errors = JsonSerializer.Deserialize<List<ValidationErrorState>>(json, JsonOptions) ?? [];

        // The agent reports each error on the node BattleScribe hung it on. The spec corpus reports
        // an over-limit violation on the SELECTION responsible, and the in-process adapter has
        // always moved it there — so without this the two BattleScribe engines answer the same
        // question differently, and this one produces the right `from` on the wrong `on`.
        //
        // The link-target map matters here for the same reason it does in-process: placement moves
        // an error onto the selection named by `from`, and `from` is deliberately the DECLARING
        // element — which for a per-link constraint is the link, not the entry. Without this the
        // error lands on `selection link-1`, naming the route rather than the thing.
        BattleScribeErrorPlacement.ApplyTo(
            errors,
            linkId => _linkTargetsById.GetValueOrDefault(linkId));
        return errors;
    }

    private RosterState EmptyRosterState()
    {
        if (_gameSystemId is null)
        {
            return new RosterState("", "", [], [], []);
        }

        var rosterName = string.IsNullOrWhiteSpace(_specId) ? "Spec Test" : _specId;
        var costLimits = _pendingCostLimits.Count == 0
            ? null
            : _pendingCostLimits.Select(x => new CostState(_costNamesById.GetValueOrDefault(x.Key, x.Key), x.Key, x.Value)).ToList();

        return new RosterState(
            rosterName,
            _gameSystemId,
            [],
            [],
            [],
            CostLimits: costLimits,
            GameSystemName: _gameSystemName);
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

    /// <summary>
    /// Dismisses BattleScribe's first-launch "download data?" prompt if it appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to sleep 1500ms and then check ONCE. Two things were wrong with that: the check is
    /// a snapshot, so a dialog arriving at 1600ms was missed entirely; and when the dialog arrived
    /// early, the remaining second was pure cost on every cold start.
    /// </para>
    /// <para>
    /// The ceiling stays, because "this dialog will never appear" has no positive signal — but it is
    /// now a CEILING rather than a cost: the loop exits the instant the dialog shows. Nothing is
    /// silently lost if it appears later either, since DialogInspector.assertNoUnexpectedModals runs
    /// on every Java-side wait and fails the next action loudly.
    /// </para>
    /// <para>
    /// It is dismissed with #btnNegative deliberately: answering positively would make BattleScribe
    /// fetch real game data over the staged spec data.
    /// </para>
    /// </remarks>
    private Task HandleStartupDialogsAsync()
        => ConnectedClient.DismissStartupConfirmAsync(ConfirmWindowTitle, StartupDialogCeilingMs);

    /// <summary>
    /// Waits until the agent's FX thread is pumping again, instead of assuming a fixed backoff.
    /// </summary>
    /// <remarks>
    /// The transient failures this backs off from are mostly "the FX thread was wedged", so the
    /// real condition is that it drains a queued task again — see
    /// <see cref="AgentClient.ProbeFxThreadAsync"/> for why that is asked with `getWindows` and
    /// not `ping`.
    /// </remarks>
    private async Task WaitForAgentResponsiveAsync(TimeSpan ceiling)
    {
        var deadline = DateTime.UtcNow + ceiling;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await ConnectedClient.ProbeFxThreadAsync(FxProbeTimeout);
                return;
            }
            catch
            {
                await Task.Delay(StartupDialogPollMs);
            }
        }
    }

    /// <summary>
    /// The value to put in the New Roster dialog's cost-limit spinner, or null to leave it alone —
    /// see <see cref="BsUiCostLimits.ForNewRoster"/>, which is the whole rule.
    /// </summary>
    private int? ResolveNewRosterCostLimit()
        => BsUiCostLimits.ForNewRoster(_pendingCostLimits, _gameSystem?.CostTypes);

    /// <summary>
    /// Builds the entry-name and group-id indexes by reading the staged XML, for the file-based
    /// setup path that has no Protocol objects to walk.
    /// </summary>
    /// <remarks>
    /// Element names, not ids, are what the edit panel labels its controls with — so without this
    /// every <c>selectChildEntry</c> on a <c>dataSource</c> spec would address controls by raw id
    /// and find nothing. Group ids are collected for the same reason they are on the other path:
    /// a group names a heading, never a control.
    /// </remarks>
    private void IndexDefinitionsFromXml(IReadOnlyList<(string FileName, string Content)> files)
    {
        foreach (var (fileName, content) in files)
        {
            // Data files only. A dataSource hands over whatever the repository holds — READMEs,
            // .gitignore, licence text — and parsing those as XML fails at line 1 with a message
            // about the root element that says nothing about which file it came from.
            if (!fileName.EndsWith(".gst", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = System.Xml.Linq.XDocument.Parse(content).Root;
            if (root is null)
            {
                continue;
            }

            foreach (var element in root.Descendants())
            {
                var id = (string?)element.Attribute("id");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "selectionEntry":
                    case "entryLink":
                        _entryNamesById[id] = (string?)element.Attribute("name") ?? id;
                        if ((string?)element.Attribute("targetId") is { } target)
                        {
                            _linkTargetsById[id] = target;
                        }

                        // An entryLink targeting a group is a group as far as labels go.
                        if ((string?)element.Attribute("type") == "selectionEntryGroup")
                        {
                            _groupEntryIds.Add(id);
                        }

                        break;

                    case "selectionEntryGroup":
                        _entryNamesById[id] = (string?)element.Attribute("name") ?? id;
                        _groupEntryIds.Add(id);
                        break;

                    case "costType":
                        _costNamesById[id] = (string?)element.Attribute("name") ?? id;
                        break;
                }
            }
        }
    }

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

        // Last, because it resolves labels and every name has to be indexed for that to answer.
        IndexLabelOccurrences();
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
            RecordSiblingOrder(
                selectionEntry.SelectionEntries?.Select(e => e.Id),
                selectionEntry.EntryLinks?.Select(l => l.Id),
                selectionEntry.SelectionEntryGroups?.Select(g => g.Id));
            IndexSelectionEntries(selectionEntry.SelectionEntries);
            IndexEntryLinks(selectionEntry.EntryLinks);
            IndexSelectionEntryGroups(selectionEntry.SelectionEntryGroups);
        }
    }

    /// <summary>
    /// Notes one container's children in the order the edit panel lists them: direct entries, then
    /// entry links, then groups — the order observed in BattleScribe's own panel.
    /// </summary>
    /// <remarks>
    /// Only the order among children that end up sharing a LABEL is load-bearing, and links sit
    /// with links, so the arrangement between the three collections never decides an occurrence
    /// on its own.
    /// </remarks>
    private void RecordSiblingOrder(
        IEnumerable<string>? entries,
        IEnumerable<string>? links,
        IEnumerable<string>? groups)
    {
        List<string> siblings = [.. entries ?? [], .. links ?? [], .. groups ?? []];
        if (siblings.Count > 1)
        {
            _siblingGroups.Add(siblings);
        }
    }

    /// <summary>
    /// Assigns each entry its <see cref="_labelOccurrenceById"/> index, once every name is known.
    /// </summary>
    private void IndexLabelOccurrences()
    {
        foreach (var siblings in _siblingGroups)
        {
            Dictionary<string, int> seenPerLabel = new(StringComparer.Ordinal);
            foreach (var childId in siblings)
            {
                var label = ResolveEntryLabel(childId);
                seenPerLabel.TryGetValue(label, out var seen);
                // Only a repeat is worth recording: a unique label needs no position, and storing
                // 0 for every entry would bury the handful that matter.
                if (seen > 0)
                {
                    _labelOccurrenceById[childId] = seen;
                }

                seenPerLabel[label] = seen + 1;
            }
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
            _groupEntryIds.Add(group.Id);
            RecordSiblingOrder(
                group.SelectionEntries?.Select(e => e.Id),
                group.EntryLinks?.Select(l => l.Id),
                group.SelectionEntryGroups?.Select(g => g.Id));
            IndexSelectionEntries(group.SelectionEntries);
            IndexEntryLinks(group.EntryLinks);
            IndexSelectionEntryGroups(group.SelectionEntryGroups);
        }
    }

    /// <summary>
    /// The text the edit panel labels this entry with, which is how its control is addressed.
    /// </summary>
    /// <remarks>
    /// A composite id — <c>el-relics-group::el-relic::sse-relic</c>, a group link through an entry
    /// link to a shared entry — is not a key in the name index, because each SEGMENT is. Falling
    /// back to the raw id made the driver hunt the panel for a label spelling out the composite,
    /// and report "Control not found for label: el-relics-group::el-relic::sse-relic" about a row
    /// that is sitting there under its name.
    /// <para>
    /// Segments are tried innermost-first — the TARGET, not the link. BattleScribe labels the
    /// control with the entry a link resolves to, even when the link carries a name of its own:
    /// a link named "Alpha Trigger" onto a shared entry named "Trigger" renders as `'Trigger' ->
    /// Spinner`. Trying the outer name first found nothing and reported the entry as missing.
    /// Segments with no name of their own are skipped rather than treated as a match.
    /// </para>
    /// </remarks>
    private string ResolveEntryLabel(string entryId)
    {
        if (NameOfResolved(entryId) is { } direct)
        {
            return direct;
        }

        foreach (var segment in entryId.Split("::", StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            // A GROUP segment names the heading the control sits under, never the control:
            // `el-relics-group::el-relic::sse-relic` is labelled with the relic, under "Relics".
            // Taking the group's name found no control and reported the heading as missing.
            if (_groupEntryIds.Contains(segment))
            {
                continue;
            }

            if (NameOfResolved(segment) is { } name)
            {
                return name;
            }
        }

        return entryId;
    }

    /// <summary>
    /// The name BattleScribe labels <paramref name="id"/> with, following entry links to what they
    /// target, or null when nothing is indexed for it.
    /// </summary>
    /// <remarks>
    /// A link does NOT rename what it points at, as far as the edit panel is concerned: a link
    /// named "Alpha Trigger" onto a shared entry named "Trigger" renders as `'Trigger' -> Spinner`.
    /// Taking the link's own name asked for a control that is not there.
    /// </remarks>
    private string? NameOfResolved(string id)
    {
        var current = id;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current) && _linkTargetsById.TryGetValue(current, out var target)
               && !string.IsNullOrEmpty(target))
        {
            current = target;
        }

        if (_entryNamesById.TryGetValue(current, out var name) && !string.IsNullOrEmpty(name))
        {
            return name;
        }

        // The target may not be indexed (a link into data we did not walk); the link's own name is
        // the only thing left to try.
        return _entryNamesById.TryGetValue(id, out var own) && !string.IsNullOrEmpty(own) ? own : null;
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
            _linkTargetsById[entryLink.Id] = entryLink.TargetId;
            if (string.Equals(entryLink.Type, "selectionEntryGroup", StringComparison.Ordinal))
            {
                _groupEntryIds.Add(entryLink.Id);
            }
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
            Profiles: dto.Profiles is null or [] ? null : [.. dto.Profiles.Select(MapProfileState)],
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
        => new(dto.Name ?? string.Empty, dto.TypeId ?? string.Empty, dto.Value, dto.Hidden);

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
        if (_gameSystemId is null || _client is null)
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
                    // The id selects the system; gameSystemName only reaches failure messages.
                    ["gameSystemId"] = _gameSystemId!,
                    ["gameSystemName"] = gameSystemName ?? _gameSystemName!,
                    ["rosterName"] = _specId,
                };
                if (ResolveNewRosterCostLimit() is { } costLimit)
                {
                    parameters["costLimit"] = costLimit;
                }
            }
        }

        var result = await ConnectedClient.CallAsync(method, parameters, timeout: ActionCallTimeout);
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

    /// <summary>
    /// Calls a high-level Java-side RosterAction that returns no meaningful outputs.
    /// </summary>
    private async Task CallActionAsync(string method, JsonObject parameters)
    {
        EnsureSetup();
        await ConnectedClient.CallAsync(method, parameters, timeout: ActionCallTimeout);
    }

    /// <summary>
    /// How long one high-level Java-side action gets.
    /// </summary>
    /// <remarks>
    /// The action methods run on a background thread with their own internal timeouts — a 30s window
    /// wait plus a 10s state poll per step — so the call has to outlast them or it reports a
    /// deadlock where the Java side is about to report what actually went wrong.
    /// </remarks>
    private static readonly TimeSpan ActionCallTimeout = TimeSpan.FromSeconds(90);

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
                await WaitForAgentResponsiveAsync(RetryDelay);
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
        public List<AgentProfileState>? Profiles { get; set; }
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
        public bool Hidden { get; set; }
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
