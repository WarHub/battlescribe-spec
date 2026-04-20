namespace BattleScribeSpec.Protocol;

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
    public static async Task RunAsync(
        Func<IRosterEngine> engineFactory,
        TextReader input,
        TextWriter output,
        CancellationToken ct = default)
    {
        IRosterEngine? engine = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(ct);
                if (line is null) break; // stdin closed

                ProtocolResponse response;
                try
                {
                    var command = ProtocolSerializer.DeserializeCommand(line);
                    response = command switch
                    {
                        SetupCommand setup => HandleSetup(setup, engineFactory, ref engine),
                        SetupFromFilesCommand setupFiles => HandleSetupFromFiles(setupFiles, engineFactory, ref engine),
                        ActionCommand action => HandleAction(action, engine),
                        GetStateCommand => HandleGetState(engine),
                        GetErrorsCommand => HandleGetErrors(engine),
                        TeardownCommand => HandleTeardown(ref engine),
                        _ => new ProtocolError { Message = $"Unknown command: {line}" },
                    };
                }
                catch (Exception ex)
                {
                    response = new ProtocolError { Message = ex.Message };
                }

                await output.WriteLineAsync(ProtocolSerializer.SerializeResponse(response).AsMemory(), ct);
                await output.FlushAsync(ct);
            }
        }
        finally
        {
            engine?.Dispose();
        }
    }

    private static ProtocolResponse HandleSetup(
        SetupCommand cmd, Func<IRosterEngine> factory, ref IRosterEngine? engine)
    {
        engine?.Dispose();
        engine = factory();
        var errors = engine.Setup(cmd.GameSystem, cmd.Catalogues.ToArray());
        return new SetupResult { Errors = errors.ToList() };
    }

    private static ProtocolResponse HandleSetupFromFiles(
        SetupFromFilesCommand cmd, Func<IRosterEngine> factory, ref IRosterEngine? engine)
    {
        engine?.Dispose();
        engine = factory();
        var files = cmd.Files.Select(f => (f.FileName, f.Content)).ToList();
        var errors = engine.SetupFromFiles(files);
        return new SetupResult { Errors = errors.ToList() };
    }

    private static ProtocolResponse HandleAction(ActionCommand cmd, IRosterEngine? engine)
    {
        if (engine is null)
            return new ActionResult { Ok = false, Error = "Engine not initialized (call setup first)" };

        try
        {
            var forcePath = cmd.ForcePath ?? (cmd.ForceIndex is { } fi ? [fi] : []);
            var selectionPath = cmd.SelectionPath ?? (cmd.SelectionIndex is { } si ? [si] : [0]);

            switch (cmd.Action)
            {
                case "addForce":
                    if (cmd.ForceEntryName is { Length: > 0 })
                        engine.AddForceByName(forcePath, cmd.ForceEntryName, cmd.CatalogueName, cmd.CatalogueIndex ?? 0);
                    else
                        engine.AddForce(forcePath, cmd.ForceEntryIndex ?? 0, cmd.CatalogueIndex ?? 0);
                    break;
                case "removeForce":
                    engine.RemoveForce(forcePath);
                    break;
                case "selectEntry":
                    if (cmd.EntryName is { Length: > 0 })
                        engine.SelectEntryByName(forcePath, cmd.EntryName);
                    else
                        engine.SelectEntry(forcePath, cmd.EntryIndex ?? 0);
                    break;
                case "selectChildEntry":
                    if (cmd.ChildEntryName is { Length: > 0 })
                        engine.SelectChildEntryByName(forcePath, selectionPath, cmd.ChildEntryName);
                    else
                        engine.SelectChildEntry(forcePath, selectionPath, cmd.ChildEntryIndex ?? 0);
                    break;
                case "deselectSelection":
                    engine.DeselectSelection(forcePath, selectionPath);
                    break;
                case "setSelectionCount":
                    engine.SetSelectionCount(forcePath, cmd.EntryIndex ?? 0, cmd.Count ?? 1);
                    break;
                case "duplicateSelection":
                    engine.DuplicateSelection(forcePath, selectionPath);
                    break;
                case "setCostLimit":
                    engine.SetCostLimit(cmd.CostTypeId ?? "", cmd.Value ?? 0);
                    break;
                default:
                    return new ActionResult { Ok = false, Error = $"Unknown action: {cmd.Action}" };
            }

            return new ActionResult { Ok = true };
        }
        catch (Exception ex)
        {
            return new ActionResult { Ok = false, Error = ex.Message };
        }
    }

    private static ProtocolResponse HandleGetState(IRosterEngine? engine)
    {
        if (engine is null)
            return new ProtocolError { Message = "Engine not initialized" };

        var state = engine.GetRosterState();
        return new StateResponse
        {
            Name = state.Name,
            GameSystemId = state.GameSystemId,
            Forces = state.Forces.ToList(),
            Costs = state.Costs.ToList(),
            ValidationErrors = state.ValidationErrors.ToList(),
        };
    }

    private static ProtocolResponse HandleGetErrors(IRosterEngine? engine)
    {
        if (engine is null)
            return new ProtocolError { Message = "Engine not initialized" };

        return new ErrorsResponse
        {
            Errors = engine.GetValidationErrors().ToList()
        };
    }

    private static ProtocolResponse HandleTeardown(ref IRosterEngine? engine)
    {
        engine?.Dispose();
        engine = null;
        return new TeardownResult();
    }
}
