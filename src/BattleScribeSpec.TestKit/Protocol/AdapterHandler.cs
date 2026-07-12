using System.Diagnostics;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Protocol;

/// <summary>Configuration for <see cref="AdapterHandler.RunAsync(AdapterOptions, TextReader, TextWriter, CancellationToken)"/>.</summary>
public sealed class AdapterOptions
{
    public required Func<IRosterEngine> RosterEngineFactory { get; init; }

    /// <summary>Optional gamedata engine factory; when null, gamedata commands answer with an error.</summary>
    public Func<GameData.IGameDataEngine>? GameDataEngineFactory { get; init; }

    /// <summary>Engine identity reported by describe (e.g. "battlescribe").</summary>
    public string Name { get; init; } = "unknown";

    public string? Version { get; init; }

    public AdapterCapabilities Capabilities { get; init; } = new();

    /// <summary>Protocol v1.1 (optional): capture the engine UI as a PNG. Null → unsupported.</summary>
    public Func<IRosterEngine, byte[]?>? ScreenshotProvider { get; init; }

    /// <summary>Protocol v1.1 (optional): export the current roster as .ros XML. Null → unsupported.</summary>
    public Func<IRosterEngine, string?>? RosterXmlExporter { get; init; }

    /// <summary>Protocol v1.1 (optional): start recording UI actions. Null → unsupported.</summary>
    public Action<IRosterEngine>? RecordStarter { get; init; }

    /// <summary>Protocol v1.1 (optional): stop recording and return the recorded actions. Null → unsupported.</summary>
    public Func<IRosterEngine, string?>? RecordStopper { get; init; }

    /// <summary>
    /// When true, keep the roster engine alive across setup/teardown cycles, resetting it via
    /// <see cref="IRosterEngine.Cleanup"/> between specs (self-heal to dispose+recreate on failure)
    /// instead of disposing and recreating. See <see cref="ReuseGameDataEngineAcrossSetups"/>.
    /// </summary>
    public bool ReuseRosterEngineAcrossSetups { get; init; }

    /// <summary>
    /// Gamedata counterpart of <see cref="ReuseRosterEngineAcrossSetups"/>. Independent because a
    /// single host process serves both domains with separate engines and their warm-reuse feasibility
    /// differs (e.g. battlescribe-ui: gamedata reusable, roster not). Default false.
    /// </summary>
    public bool ReuseGameDataEngineAcrossSetups { get; init; }
}

/// <summary>
/// Handles the adapter side of the JSON-line protocol.
/// Reads commands from stdin, dispatches to an IRosterEngine, writes responses to stdout.
/// Reusable by any .NET engine adapter.
/// </summary>
public static class AdapterHandler
{
    /// <summary>
    /// Run the adapter protocol loop, reading from input and writing to output.
    /// Handles multiple setup/teardown cycles.
    /// </summary>
    public static Task RunAsync(
        Func<IRosterEngine> engineFactory,
        TextReader input,
        TextWriter output,
        CancellationToken ct = default)
        => RunAsync(new AdapterOptions { RosterEngineFactory = engineFactory }, input, output, ct);

    /// <summary>
    /// Run the adapter protocol loop, reading from input and writing to output.
    /// Handles multiple setup/teardown cycles.
    /// </summary>
    public static async Task RunAsync(
        AdapterOptions options,
        TextReader input,
        TextWriter output,
        CancellationToken ct = default)
    {
        var engineFactory = options.RosterEngineFactory;
        IRosterEngine? engine = null;
        IReadOnlyList<string> catalogueIds = [];
        GameData.IGameDataEngine? gdEngine = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(ct);
                if (line is null)
                {
                    break; // stdin closed
                }

                ProtocolResponse response;
                int? commandCorrId = null;
                try
                {
                    var command = ProtocolSerializer.DeserializeCommand(line);
                    commandCorrId = command?.CorrId;

                    // SERVER: the handling side of a remote call. Pairs with the parent's CLIENT span
                    // to form the one edge a service graph can actually draw.
                    using var activity = command is null
                        ? null
                        : HarnessTelemetry.StartOp(
                            command.Type,
                            command.Traceparent,
                            ActivityKind.Server,
                            command.Tracestate);

                    response = command switch
                    {
                        SetupCommand setup => HandleSetup(setup, engineFactory, options.ReuseRosterEngineAcrossSetups, ref engine, out catalogueIds),
                        SetupFromFilesCommand setupFiles => HandleSetupFromFiles(setupFiles, engineFactory, options.ReuseRosterEngineAcrossSetups, ref engine, out catalogueIds),
                        ActionCommand action => HandleAction(action, engine, catalogueIds),
                        GetStateCommand => HandleGetState(engine),
                        GetErrorsCommand => HandleGetErrors(engine),
                        TeardownCommand => HandleTeardown(options.ReuseRosterEngineAcrossSetups, options.ReuseGameDataEngineAcrossSetups, ref engine, ref gdEngine),
                        DescribeCommand => new DescribeResult
                        {
                            Name = options.Name,
                            Version = options.Version,
                            Domains = options.GameDataEngineFactory is null ? ["roster"] : ["roster", "gamedata"],
                            Capabilities = options.Capabilities,
                        },
                        ScreenshotCommand => engine is not null && options.ScreenshotProvider?.Invoke(engine) is { } png
                            ? new ScreenshotResult { PngBase64 = Convert.ToBase64String(png) }
                            : new ProtocolError { Message = "screenshot is not supported by this adapter" },
                        ExportRosterXmlCommand => engine is not null && options.RosterXmlExporter?.Invoke(engine) is { } xml
                            ? new RosterXmlResult { Xml = xml }
                            : new ProtocolError { Message = "exportRosterXml is not supported by this adapter" },
                        RecordStartCommand => HandleRecordStart(options, engine),
                        RecordStopCommand => engine is not null && options.RecordStopper is not null
                            ? new RecordResult { ActionsJson = options.RecordStopper(engine) }
                            : new ProtocolError { Message = "recordStop is not supported by this adapter" },
                        GameDataSetupCommand gdSetup => HandleGameDataSetup(gdSetup, options, ref gdEngine),
                        GameDataActionCommand gdAction => HandleGameDataAction(gdAction, gdEngine),
                        GameDataGetStateCommand => gdEngine is null
                            ? new ProtocolError { Message = "gamedata engine not initialized (call gamedataSetup first)" }
                            : new GameDataStateResponse { State = gdEngine.GetState() },
                        GameDataGetErrorsCommand => gdEngine is null
                            ? new ProtocolError { Message = "gamedata engine not initialized (call gamedataSetup first)" }
                            : new ErrorsResponse { Errors = [.. gdEngine.GetValidationErrors()] },
                        _ => new ProtocolError { Message = $"Unknown command: {line}" },
                    };
                }
                catch (Exception ex)
                {
                    response = new ProtocolError { Message = ex.Message };
                }

                // Echo the command's corrId (if any) on every response path, including the error
                // fallback above. A command with no corrId leaves the response corrId null (legacy client).
                response.CorrId = commandCorrId;

                await output.WriteLineAsync(ProtocolSerializer.SerializeResponse(response).AsMemory(), ct);
                await output.FlushAsync(ct);
            }
        }
        finally
        {
            engine?.Dispose();
            gdEngine?.Dispose();
        }
    }

    /// <summary>
    /// Configures the engine for a spec. When reuse is enabled (browser-backed engines) an
    /// already-live engine is kept and reconfigured in place — avoiding a per-spec cold start;
    /// otherwise the engine is disposed and recreated. Reset between specs happens in
    /// <see cref="HandleTeardown"/> via the engine's Cleanup.
    /// </summary>
    private static ProtocolResponse HandleSetup(
        SetupCommand cmd, Func<IRosterEngine> factory, bool reuse, ref IRosterEngine? engine, out IReadOnlyList<string> catalogueIds)
    {
        if (!reuse || engine is null)
        {
            engine?.Dispose();
            engine = factory();
        }

        catalogueIds = [.. cmd.Catalogues.Select(c => c.Id)];
        if (cmd.SpecId is { Length: > 0 })
        {
            engine.SetTestContext(cmd.SpecId);
        }

        var errors = engine.Setup(cmd.GameSystem, [.. cmd.Catalogues]);
        return new SetupResult { Errors = [.. errors] };
    }

    private static ProtocolResponse HandleSetupFromFiles(
        SetupFromFilesCommand cmd, Func<IRosterEngine> factory, bool reuse, ref IRosterEngine? engine, out IReadOnlyList<string> catalogueIds)
    {
        if (!reuse || engine is null)
        {
            engine?.Dispose();
            engine = factory();
        }

        // File-based setup: catalogue IDs unknown — actions must provide catalogueId explicitly
        catalogueIds = [];
        if (cmd.SpecId is { Length: > 0 })
        {
            engine.SetTestContext(cmd.SpecId);
        }

        var files = cmd.Files.Select(f => (f.FileName, f.Content)).ToList();
        var errors = engine.SetupFromFiles(files);
        return new SetupResult { Errors = [.. errors] };
    }

    private static ProtocolResponse HandleAction(ActionCommand cmd, IRosterEngine? engine, IReadOnlyList<string> catalogueIds)
    {
        if (engine is null)
        {
            return new ActionResult { Ok = false, Error = "Engine not initialized (call setup first)" };
        }

        try
        {
            ActionOutputs? outputs = null;
            switch (cmd.Action)
            {
                case "addForce":
                    outputs = engine.AddForce(
                        cmd.ForceEntryId ?? throw new InvalidOperationException("addForce requires forceEntryId"),
                        ProtocolValidator.ResolveCatalogueId(cmd.CatalogueId, catalogueIds));
                    break;
                case "addChildForce":
                    outputs = engine.AddChildForce(
                        cmd.ForceId ?? throw new InvalidOperationException("addChildForce requires forceId"),
                        cmd.ForceEntryId ?? throw new InvalidOperationException("addChildForce requires forceEntryId"),
                        ProtocolValidator.ResolveCatalogueId(cmd.CatalogueId, catalogueIds));
                    break;
                case "removeForce":
                    engine.RemoveForce(
                        cmd.ForceId ?? throw new InvalidOperationException("removeForce requires forceId"));
                    break;
                case "selectEntry":
                    outputs = engine.SelectEntry(
                        cmd.ForceId ?? throw new InvalidOperationException("selectEntry requires forceId"),
                        cmd.EntryId ?? throw new InvalidOperationException("selectEntry requires entryId"));
                    break;
                case "selectChildEntry":
                    outputs = engine.SelectChildEntry(
                        cmd.ForceId ?? throw new InvalidOperationException("selectChildEntry requires forceId"),
                        cmd.SelectionId ?? throw new InvalidOperationException("selectChildEntry requires selectionId"),
                        cmd.EntryId ?? throw new InvalidOperationException("selectChildEntry requires entryId"));
                    break;
                case "deselectSelection":
                    engine.DeselectSelection(
                        cmd.ForceId ?? throw new InvalidOperationException("deselectSelection requires forceId"),
                        cmd.SelectionId ?? throw new InvalidOperationException("deselectSelection requires selectionId"));
                    break;
                case "setSelectionCount":
                    engine.SetSelectionCount(
                        cmd.ForceId ?? throw new InvalidOperationException("setSelectionCount requires forceId"),
                        cmd.SelectionId ?? throw new InvalidOperationException("setSelectionCount requires selectionId"),
                        cmd.Count ?? throw new InvalidOperationException("setSelectionCount requires count"));
                    break;
                case "duplicateSelection":
                    outputs = engine.DuplicateSelection(
                        cmd.ForceId ?? throw new InvalidOperationException("duplicateSelection requires forceId"),
                        cmd.SelectionId ?? throw new InvalidOperationException("duplicateSelection requires selectionId"));
                    break;
                case "duplicateForce":
                    outputs = engine.DuplicateForce(
                        cmd.ForceId ?? throw new InvalidOperationException("duplicateForce requires forceId"));
                    break;
                case "setCostLimit":
                    engine.SetCostLimit(
                        cmd.CostTypeId ?? throw new InvalidOperationException("setCostLimit requires costTypeId"),
                        cmd.Value ?? throw new InvalidOperationException("setCostLimit requires value"));
                    break;
                case "setCustomization":
                    engine.SetCustomization(
                        cmd.ForceId ?? throw new InvalidOperationException("setCustomization requires forceId"),
                        cmd.SelectionId,
                        cmd.CategoryEntryId,
                        cmd.CustomName,
                        cmd.CustomNotes);
                    break;
                default:
                    return new ActionResult { Ok = false, Error = $"Unknown action: {cmd.Action}" };
            }

            return new ActionResult { Ok = true, Outputs = outputs };
        }
        catch (Exception ex)
        {
            return new ActionResult { Ok = false, Error = ex.Message };
        }
    }

    private static ProtocolResponse HandleGetState(IRosterEngine? engine)
    {
        if (engine is null)
        {
            return new ProtocolError { Message = "Engine not initialized" };
        }

        var state = engine.GetRosterState();
        return new StateResponse
        {
            Name = state.Name,
            GameSystemId = state.GameSystemId,
            GameSystemName = state.GameSystemName,
            Forces = [.. state.Forces],
            Costs = [.. state.Costs],
            CostLimits = state.CostLimits?.ToList(),
            ValidationErrors = [.. state.ValidationErrors],
        };
    }

    private static ProtocolResponse HandleGetErrors(IRosterEngine? engine)
    {
        if (engine is null)
        {
            return new ProtocolError { Message = "Engine not initialized" };
        }

        return new ErrorsResponse
        {
            Errors = [.. engine.GetValidationErrors()]
        };
    }

    private static ProtocolResponse HandleTeardown(
        bool reuseRoster, bool reuseGameData, ref IRosterEngine? engine, ref GameData.IGameDataEngine? gdEngine)
    {
        engine = ResetOrDispose(engine, reuseRoster, e => e.Cleanup());
        gdEngine = ResetOrDispose(gdEngine, reuseGameData, e => e.Cleanup());
        return new TeardownResult();
    }

    /// <summary>
    /// End-of-spec engine handling. With <paramref name="reuse"/> true, resets the engine in place and
    /// keeps it warm for the next setup; if the reset throws, disposes it so the next setup recreates a
    /// fresh one (self-heal against a crashed browser). With reuse false, disposes and clears it.
    /// </summary>
    private static T? ResetOrDispose<T>(T? engine, bool reuse, Action<T> cleanup) where T : class, IDisposable
    {
        if (engine is null)
        {
            return null;
        }

        if (!reuse)
        {
            engine.Dispose();
            return null;
        }

        try
        {
            cleanup(engine);
            return engine;
        }
        catch
        {
            try
            {
                engine.Dispose();
            }
            catch
            {
                /* best-effort */
            }

            return null;
        }
    }

    private static ProtocolResponse HandleRecordStart(AdapterOptions options, IRosterEngine? engine)
    {
        if (engine is null || options.RecordStarter is null)
        {
            return new ProtocolError { Message = "recordStart is not supported by this adapter" };
        }

        options.RecordStarter(engine);
        return new ActionResult { Ok = true };
    }

    /// <summary>
    /// Configures the gamedata engine for a spec. When reuse is enabled (browser-backed engines) an
    /// already-live engine is kept and reconfigured in place — avoiding a per-spec cold start;
    /// otherwise the engine is disposed and recreated. Reset between specs happens in
    /// <see cref="HandleTeardown"/> via the engine's Cleanup.
    /// </summary>
    private static ProtocolResponse HandleGameDataSetup(
        GameDataSetupCommand cmd, AdapterOptions options, ref GameData.IGameDataEngine? engine)
    {
        if (options.GameDataEngineFactory is null)
        {
            return new ProtocolError { Message = "gamedata domain is not supported by this adapter" };
        }

        if (!options.ReuseGameDataEngineAcrossSetups || engine is null)
        {
            engine?.Dispose();
            engine = options.GameDataEngineFactory();
        }

        if (cmd.SpecId is { Length: > 0 })
        {
            engine.SetTestContext(cmd.SpecId);
        }

        var errors = engine.Setup(cmd.GameSystem, [.. cmd.Catalogues]);
        return new SetupResult { Errors = [.. errors] };
    }

    private static ProtocolResponse HandleGameDataAction(GameDataActionCommand cmd, GameData.IGameDataEngine? engine)
    {
        if (engine is null)
        {
            return new GameDataActionResult { Ok = false, Error = "gamedata engine not initialized (call gamedataSetup first)" };
        }

        try
        {
            var result = new GameDataActionResult { Ok = true };
            switch (cmd.Action)
            {
                case "openFile":
                    engine.OpenFile(cmd.Id ?? throw new InvalidOperationException("openFile requires id"));
                    break;
                case "addEntry":
                    result.EntryId = engine.AddEntry(
                        cmd.ParentId ?? throw new InvalidOperationException("addEntry requires parentId"),
                        cmd.EntryType ?? throw new InvalidOperationException("addEntry requires entryType"),
                        cmd.Name,
                        cmd.Id).EntryId;
                    break;
                case "addLink":
                    result.EntryId = engine.AddLink(
                        cmd.ParentId ?? throw new InvalidOperationException("addLink requires parentId"),
                        cmd.LinkType ?? throw new InvalidOperationException("addLink requires linkType"),
                        cmd.TargetId ?? throw new InvalidOperationException("addLink requires targetId"),
                        cmd.Id).EntryId;
                    break;
                case "removeEntry":
                    engine.RemoveEntry(cmd.EntryId ?? throw new InvalidOperationException("removeEntry requires entryId"));
                    break;
                case "setField":
                    engine.SetField(
                        cmd.EntryId ?? throw new InvalidOperationException("setField requires entryId"),
                        cmd.Field ?? throw new InvalidOperationException("setField requires field"),
                        cmd.Value);
                    break;
                case "setCost":
                    engine.SetCost(
                        cmd.EntryId ?? throw new InvalidOperationException("setCost requires entryId"),
                        cmd.CostTypeId ?? throw new InvalidOperationException("setCost requires costTypeId"),
                        cmd.Value);
                    break;
                case "setCharacteristic":
                    engine.SetCharacteristic(
                        cmd.EntryId ?? throw new InvalidOperationException("setCharacteristic requires entryId"),
                        cmd.NameOrTypeId ?? throw new InvalidOperationException("setCharacteristic requires nameOrTypeId"),
                        cmd.Value);
                    break;
                case "reload":
                    engine.Reload();
                    break;
                case "exportFile":
                    result.Xml = engine.ExportActiveFile();
                    break;
                case "loadFile":
                    result.Id = engine.LoadFile(cmd.Xml ?? throw new InvalidOperationException("loadFile requires xml"));
                    break;
                default:
                    return new GameDataActionResult { Ok = false, Error = $"Unknown gamedata action: {cmd.Action}" };
            }

            return result;
        }
        catch (Exception ex)
        {
            return new GameDataActionResult { Ok = false, Error = ex.Message };
        }
    }
}
