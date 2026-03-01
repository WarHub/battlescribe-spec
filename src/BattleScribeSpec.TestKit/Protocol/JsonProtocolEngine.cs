namespace BattleScribeSpec.Protocol;

/// <summary>
/// IRosterEngine implementation that communicates with an external adapter process
/// via the JSON-line protocol. This enables testing engines written in any language.
/// </summary>
public sealed class JsonProtocolEngine : IRosterEngine
{
    private readonly AdapterProcess _adapter;

    public JsonProtocolEngine(AdapterProcess adapter)
    {
        _adapter = adapter;
    }

    public IReadOnlyList<string> Setup(GameSystemSpec gameSystem, CatalogueSpec catalogue)
    {
        var cmd = ProtocolConverter.ToSetupCommand(gameSystem, catalogue);
        var response = _adapter.SendCommandAsync(cmd).GetAwaiter().GetResult();
        return response switch
        {
            SetupResult sr => sr.Errors,
            ProtocolError pe => [pe.Message],
            _ => [$"Unexpected response type: {response.Type}"],
        };
    }

    public void AddForce(int forceEntryIndex)
    {
        SendAction(new ActionCommand { Action = "addForce", ForceEntryIndex = forceEntryIndex });
    }

    public void RemoveForce(int forceIndex)
    {
        SendAction(new ActionCommand { Action = "removeForce", ForceIndex = forceIndex });
    }

    public void SelectEntry(int forceIndex, int entryIndex)
    {
        SendAction(new ActionCommand { Action = "selectEntry", ForceIndex = forceIndex, EntryIndex = entryIndex });
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
        var response = _adapter.SendCommandAsync(new GetStateCommand()).GetAwaiter().GetResult();
        return response switch
        {
            StateResponse sr => ProtocolConverter.ToRosterState(sr),
            ProtocolError pe => throw new InvalidOperationException($"Adapter error: {pe.Message}"),
            _ => throw new InvalidOperationException($"Unexpected response type: {response.Type}"),
        };
    }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var response = _adapter.SendCommandAsync(new GetErrorsCommand()).GetAwaiter().GetResult();
        return response switch
        {
            ErrorsResponse er => er.Errors,
            ProtocolError pe => [pe.Message],
            _ => [$"Unexpected response type: {response.Type}"],
        };
    }

    public bool HasValidationErrors()
    {
        return GetValidationErrors().Count > 0;
    }

    public void Dispose()
    {
        try
        {
            _adapter.SendCommandAsync(new TeardownCommand()).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort teardown
        }
    }

    private void SendAction(ActionCommand cmd)
    {
        var response = _adapter.SendCommandAsync(cmd).GetAwaiter().GetResult();
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
}
