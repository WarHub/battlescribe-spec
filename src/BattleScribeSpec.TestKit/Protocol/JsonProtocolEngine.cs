using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Protocol;

/// <summary>
/// IRosterEngine implementation that communicates with an external adapter process
/// via the JSON-line protocol. This enables testing engines written in any language.
/// </summary>
public sealed class JsonProtocolEngine : IRosterEngine
{
    private readonly IAdapterConnection _adapter;
    private readonly TimeSpan _requestTimeout;
    private string? _specId;

    public JsonProtocolEngine(IAdapterConnection adapter, TimeSpan? requestTimeout = null)
    {
        _adapter = adapter;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void SetTestContext(string specId) => _specId = specId;

    /// <summary>
    /// Configure the engine with game system and catalogue data. Sent with an explicit
    /// 2-minute timeout (longer than the default 30s): engine construction happens
    /// server-side during setup, and UI engines hosted by bs-engine-host do a Playwright
    /// cold-start there, which can exceed the default window.
    /// </summary>
    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var cmd = new SetupCommand
        {
            SpecId = _specId,
            GameSystem = gameSystem,
            Catalogues = [.. catalogues],
        };
        var response = SendCommand(cmd, TimeSpan.FromMinutes(2));
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
            Files = [.. files.Select(f => new ProtocolDataFile { FileName = f.FileName, Content = f.Content })]
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

    public void SetCostLimit(string costTypeId, decimal value)
    {
        SendAction(new ActionCommand
        {
            Action = "setCostLimit",
            CostTypeId = costTypeId,
            Value = value,
        });
    }

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
    {
        SendAction(new ActionCommand
        {
            Action = "setCustomization",
            ForceId = forceId,
            SelectionId = selectionId,
            CategoryEntryId = categoryEntryId,
            CustomName = customName,
            CustomNotes = customNotes,
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
                sr.ValidationErrors,
                CostLimits: sr.CostLimits,
                GameSystemName: sr.GameSystemName),
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

    /// <summary>Protocol v1.1: capture a UI screenshot; throws NotSupportedException if the adapter can't.</summary>
    public byte[] CaptureScreenshot() => SendCommand(new ScreenshotCommand()) switch
    {
        ScreenshotResult sr => Convert.FromBase64String(sr.PngBase64),
        ProtocolError pe => throw new NotSupportedException(pe.Message),
        var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
    };

    /// <summary>Protocol v1.1: export the roster as .ros XML; throws NotSupportedException if unsupported.</summary>
    public string ExportRosterXml() => SendCommand(new ExportRosterXmlCommand()) switch
    {
        RosterXmlResult r => r.Xml,
        ProtocolError pe => throw new NotSupportedException(pe.Message),
        var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
    };

    /// <summary>Protocol v1.1: start UI action recording; throws NotSupportedException if unsupported.</summary>
    public void StartRecording()
    {
        var response = SendCommand(new RecordStartCommand());
        if (response is ProtocolError pe)
        {
            throw new NotSupportedException(pe.Message);
        }
    }

    /// <summary>Protocol v1.1: stop recording; returns the actions JSON (null if none). Throws NotSupportedException if unsupported.</summary>
    public string? StopRecording() => SendCommand(new RecordStopCommand()) switch
    {
        RecordResult r => r.ActionsJson,
        ProtocolError pe => throw new NotSupportedException(pe.Message),
        var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
    };

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
        return response switch
        {
            ActionResult { Ok: true } ar => ar.Outputs ?? new ActionOutputs(),
            ActionResult { Ok: false, Error: var error } => throw new InvalidOperationException($"Action '{cmd.Action}' failed: {error}"),
            ProtocolError pe => throw new InvalidOperationException($"Adapter error: {pe.Message}"),
            _ => throw new InvalidOperationException($"Unexpected response type: {response.Type}"),
        };
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
