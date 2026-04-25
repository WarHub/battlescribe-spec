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
        IReadOnlyList<string> catalogueIds = [];

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
                        SetupCommand setup => HandleSetup(setup, engineFactory, ref engine, out catalogueIds),
                        SetupFromFilesCommand setupFiles => HandleSetupFromFiles(setupFiles, engineFactory, ref engine, out catalogueIds),
                        ActionCommand action => HandleAction(action, engine, catalogueIds),
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
        SetupCommand cmd, Func<IRosterEngine> factory, ref IRosterEngine? engine, out IReadOnlyList<string> catalogueIds)
    {
        engine?.Dispose();
        engine = factory();
        catalogueIds = cmd.Catalogues.Select(c => c.Id).ToArray();
        if (cmd.SpecId is { Length: > 0 })
            engine.SetTestContext(cmd.SpecId);
        var errors = engine.Setup(cmd.GameSystem, cmd.Catalogues.ToArray());
        return new SetupResult { Errors = errors.ToList() };
    }

    private static ProtocolResponse HandleSetupFromFiles(
        SetupFromFilesCommand cmd, Func<IRosterEngine> factory, ref IRosterEngine? engine, out IReadOnlyList<string> catalogueIds)
    {
        engine?.Dispose();
        engine = factory();
        // File-based setup: catalogue IDs unknown — actions must provide catalogueId explicitly
        catalogueIds = [];
        if (cmd.SpecId is { Length: > 0 })
            engine.SetTestContext(cmd.SpecId);
        var files = cmd.Files.Select(f => (f.FileName, f.Content)).ToList();
        var errors = engine.SetupFromFiles(files);
        return new SetupResult { Errors = errors.ToList() };
    }

    private static ProtocolResponse HandleAction(ActionCommand cmd, IRosterEngine? engine, IReadOnlyList<string> catalogueIds)
    {
        if (engine is null)
            return new ActionResult { Ok = false, Error = "Engine not initialized (call setup first)" };

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
                        cmd.Count ?? 1);
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
                    engine.SetCostLimit(cmd.CostTypeId ?? "", cmd.Value ?? 0);
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
            return new ProtocolError { Message = "Engine not initialized" };

        var state = engine.GetRosterState();
        return new StateResponse
        {
            Name = state.Name,
            GameSystemId = state.GameSystemId,
            GameSystemName = state.GameSystemName,
            Forces = state.Forces.ToList(),
            Costs = state.Costs.ToList(),
            CostLimits = state.CostLimits?.ToList(),
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
