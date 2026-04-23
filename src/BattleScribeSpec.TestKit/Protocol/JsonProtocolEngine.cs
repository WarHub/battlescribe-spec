namespace BattleScribeSpec.Protocol;

/// <summary>
/// IRosterEngine implementation that communicates with an external adapter process
/// via the JSON-line protocol. This enables testing engines written in any language.
/// </summary>
public sealed class JsonProtocolEngine : IRosterEngine
{
    private readonly AdapterProcess _adapter;
    private readonly TimeSpan _requestTimeout;
    private string? _specId;

    public JsonProtocolEngine(AdapterProcess adapter, TimeSpan? requestTimeout = null)
    {
        _adapter = adapter;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var cmd = new SetupCommand
        {
            SpecId = _specId,
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
            SpecId = _specId,
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

    public ActionOutputs AddForce(string forceEntryId, string catalogueId)
    {
        return SendAction(new ActionCommand { Action = "addForce", ForceEntryId = forceEntryId, CatalogueId = catalogueId });
    }

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
    {
        return SendAction(new ActionCommand { Action = "addChildForce", ForceId = parentForceId, ForceEntryId = forceEntryId, CatalogueId = catalogueId });
    }

    public void RemoveForce(string forceId)
    {
        SendAction(new ActionCommand { Action = "removeForce", ForceId = forceId });
    }

    public ActionOutputs SelectEntry(string forceId, string entryId)
    {
        return SendAction(new ActionCommand { Action = "selectEntry", ForceId = forceId, EntryId = entryId });
    }

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
    {
        return SendAction(new ActionCommand
        {
            Action = "selectChildEntry",
            ForceId = forceId,
            SelectionId = parentSelectionId,
            EntryId = entryId,
        });
    }

    public void DeselectSelection(string forceId, string selectionId)
    {
        SendAction(new ActionCommand
        {
            Action = "deselectSelection",
            ForceId = forceId,
            SelectionId = selectionId,
        });
    }

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        SendAction(new ActionCommand
        {
            Action = "setSelectionCount",
            ForceId = forceId,
            SelectionId = selectionId,
            Count = count,
        });
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
    {
        return SendAction(new ActionCommand
        {
            Action = "duplicateSelection",
            ForceId = forceId,
            SelectionId = selectionId,
        });
    }

    public ActionOutputs DuplicateForce(string forceId)
    {
        return SendAction(new ActionCommand
        {
            Action = "duplicateForce",
            ForceId = forceId,
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

    private ActionOutputs SendAction(ActionCommand cmd)
    {
        var response = SendCommand(cmd);
        switch (response)
        {
            case ActionResult { Ok: true } ar:
                return ar.Outputs ?? new ActionOutputs();
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
