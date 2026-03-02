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
        var (gs, catalogues) = ProtocolConverter.FromSetupCommand(cmd);
        var errors = engine.Setup(gs, catalogues);
        return new SetupResult { Errors = errors.ToList() };
    }

    private static ProtocolResponse HandleAction(ActionCommand cmd, IRosterEngine? engine)
    {
        if (engine is null)
            return new ActionResult { Ok = false, Error = "Engine not initialized (call setup first)" };

        try
        {
            switch (cmd.Action)
            {
                case "addForce":
                    engine.AddForce(cmd.ForceEntryIndex ?? 0, cmd.CatalogueIndex ?? 0);
                    break;
                case "removeForce":
                    engine.RemoveForce(cmd.ForceIndex ?? 0);
                    break;
                case "selectEntry":
                    engine.SelectEntry(cmd.ForceIndex ?? 0, cmd.EntryIndex ?? 0);
                    break;
                case "selectChildEntry":
                    engine.SelectChildEntry(cmd.ForceIndex ?? 0, cmd.SelectionIndex ?? 0, cmd.ChildEntryIndex ?? 0);
                    break;
                case "deselectSelection":
                    engine.DeselectSelection(cmd.ForceIndex ?? 0, cmd.SelectionIndex ?? 0);
                    break;
                case "setSelectionCount":
                    engine.SetSelectionCount(cmd.ForceIndex ?? 0, cmd.EntryIndex ?? 0, cmd.Count ?? 1);
                    break;
                case "duplicateSelection":
                    engine.DuplicateSelection(cmd.ForceIndex ?? 0, cmd.SelectionIndex ?? 0);
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
        return ProtocolConverter.ToStateResponse(state);
    }

    private static ProtocolResponse HandleGetErrors(IRosterEngine? engine)
    {
        if (engine is null)
            return new ProtocolError { Message = "Engine not initialized" };

        return new ErrorsResponse { Errors = engine.GetValidationErrors().ToList() };
    }

    private static ProtocolResponse HandleTeardown(ref IRosterEngine? engine)
    {
        engine?.Dispose();
        engine = null;
        return new TeardownResult();
    }
}
