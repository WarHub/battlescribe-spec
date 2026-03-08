namespace BattleScribeSpec.Protocol;

/// <summary>
/// IRosterEngine implementation that communicates with an external adapter process
/// via the JSON-line protocol. This enables testing engines written in any language.
/// </summary>
public sealed class JsonProtocolEngine : IRosterEngine
{
    private readonly AdapterProcess _adapter;
    private readonly TimeSpan _requestTimeout;

    public JsonProtocolEngine(AdapterProcess adapter, TimeSpan? requestTimeout = null)
    {
        _adapter = adapter;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var cmd = new SetupCommand
        {
            GameSystem = gameSystem,
            Catalogues = catalogues.ToList(),
        };
        var response = SendCommand(cmd);
        return response switch
        {
            SetupResult sr => sr.Errors,
            ProtocolError pe => [pe.Message],
            _ => [$"Unexpected response type: {response.Type}"],
        };
    }

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
    {
        var cmd = new SetupFromFilesCommand
        {
            Files = files.Select(f => new ProtocolDataFile { FileName = f.FileName, Content = f.Content }).ToList()
        };
        var response = SendCommand(cmd, TimeSpan.FromMinutes(5));
        return response switch
        {
            SetupResult sr => sr.Errors,
            ProtocolError pe => [pe.Message],
            _ => [$"Unexpected response type: {response.Type}"],
        };
    }

    public void AddForce(int forceEntryIndex, int catalogueIndex = 0)
    {
        SendAction(new ActionCommand { Action = "addForce", ForceEntryIndex = forceEntryIndex, CatalogueIndex = catalogueIndex });
    }

    public void AddForceByName(string forceName, string? catalogueName = null, int catalogueIndex = 0)
    {
        SendAction(new ActionCommand { Action = "addForce", ForceEntryName = forceName, CatalogueName = catalogueName, CatalogueIndex = catalogueIndex });
    }

    public void RemoveForce(int forceIndex)
    {
        SendAction(new ActionCommand { Action = "removeForce", ForceIndex = forceIndex });
    }

    public void SelectEntry(int forceIndex, int entryIndex)
    {
        SendAction(new ActionCommand { Action = "selectEntry", ForceIndex = forceIndex, EntryIndex = entryIndex });
    }

    public void SelectEntryByName(int forceIndex, string entryName)
    {
        SendAction(new ActionCommand { Action = "selectEntry", ForceIndex = forceIndex, EntryName = entryName });
    }

    public void SelectChildEntry(int forceIndex, int selectionIndex, int childEntryIndex)
    {
        SendAction(new ActionCommand
        {
            Action = "selectChildEntry",
            ForceIndex = forceIndex,
            SelectionIndex = selectionIndex,
            ChildEntryIndex = childEntryIndex,
        });
    }

    public void SelectChildEntryByName(int forceIndex, int selectionIndex, string childEntryName)
    {
        SendAction(new ActionCommand
        {
            Action = "selectChildEntry",
            ForceIndex = forceIndex,
            SelectionIndex = selectionIndex,
            ChildEntryName = childEntryName,
        });
    }

    public void DeselectSelection(int forceIndex, int selectionIndex)
    {
        SendAction(new ActionCommand
        {
            Action = "deselectSelection",
            ForceIndex = forceIndex,
            SelectionIndex = selectionIndex,
        });
    }

    public void SetSelectionCount(int forceIndex, int entryIndex, int count)
    {
        SendAction(new ActionCommand
        {
            Action = "setSelectionCount",
            ForceIndex = forceIndex,
            EntryIndex = entryIndex,
            Count = count,
        });
    }

    public void DuplicateSelection(int forceIndex, int selectionIndex)
    {
        SendAction(new ActionCommand
        {
            Action = "duplicateSelection",
            ForceIndex = forceIndex,
            SelectionIndex = selectionIndex,
        });
    }

    public void SetCostLimit(string costTypeId, double value)
    {
        SendAction(new ActionCommand
        {
            Action = "setCostLimit",
            CostTypeId = costTypeId,
            Value = value,
        });
    }

    public RosterState GetRosterState()
    {
        var response = SendCommand(new GetStateCommand());
        return response switch
        {
            StateResponse sr => new RosterState(
                sr.Name,
                sr.GameSystemId,
                sr.Forces,
                sr.Costs,
                sr.ValidationErrors),
            ProtocolError pe => throw new InvalidOperationException($"Adapter error: {pe.Message}"),
            _ => throw new InvalidOperationException($"Unexpected response type: {response.Type}"),
        };
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
    {
        var response = SendCommand(new GetErrorsCommand());
        return response switch
        {
            ErrorsResponse er => er.Errors,
            ProtocolError pe => [new ValidationErrorState(pe.Message)],
            _ => [new ValidationErrorState($"Unexpected response type: {response.Type}")],
        };
    }

    public void Dispose()
    {
        try
        {
            SendCommand(new TeardownCommand());
        }
        catch
        {
            // Best-effort teardown
        }
    }

    private void SendAction(ActionCommand cmd)
    {
        var response = SendCommand(cmd);
        switch (response)
        {
            case ActionResult { Ok: true }:
                return;
            case ActionResult { Ok: false, Error: var error }:
                throw new InvalidOperationException($"Action '{cmd.Action}' failed: {error}");
            case ProtocolError pe:
                throw new InvalidOperationException($"Adapter error: {pe.Message}");
            default:
                throw new InvalidOperationException($"Unexpected response type: {response.Type}");
        }
    }

    private ProtocolResponse SendCommand(ProtocolCommand command, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? _requestTimeout;
        using var cts = new CancellationTokenSource(effectiveTimeout);
        try
        {
            return _adapter.SendCommandAsync(command, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Adapter timed out after {effectiveTimeout.TotalSeconds:0}s while handling '{command.Type}'.", ex);
        }
    }
}
