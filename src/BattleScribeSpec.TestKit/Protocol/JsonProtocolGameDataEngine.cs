using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Protocol;

/// <summary>
/// IGameDataEngine implementation over the NDJSON adapter protocol (v1.1 gamedata commands).
/// Counterpart of <see cref="JsonProtocolEngine"/> for the data-editing domain.
/// </summary>
public sealed class JsonProtocolGameDataEngine : IGameDataEngine
{
    private readonly IAdapterConnection _adapter;
    private readonly TimeSpan _requestTimeout;
    private string? _specId;

    public JsonProtocolGameDataEngine(IAdapterConnection adapter, TimeSpan? requestTimeout = null)
    {
        _adapter = adapter;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var response = SendCommand(new GameDataSetupCommand
        {
            SpecId = _specId,
            GameSystem = gameSystem,
            Catalogues = [.. catalogues],
        });
        return response switch
        {
            SetupResult sr => sr.Errors,
            ProtocolError pe => [pe.Message],
            _ => [$"Unexpected response type: {response.Type}"],
        };
    }

    public void OpenFile(string id) => SendAction(new GameDataActionCommand { Action = "openFile", Id = id });

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null)
    {
        var result = SendAction(new GameDataActionCommand
        {
            Action = "addEntry",
            ParentId = parentId,
            EntryType = entryType,
            Name = name,
            Id = id,
        });
        return new GameDataActionOutputs { EntryId = result.EntryId };
    }

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null)
    {
        var result = SendAction(new GameDataActionCommand
        {
            Action = "addLink",
            ParentId = parentId,
            LinkType = linkType,
            TargetId = targetId,
            Id = id,
        });
        return new GameDataActionOutputs { EntryId = result.EntryId };
    }

    public void RemoveEntry(string entryId) =>
        SendAction(new GameDataActionCommand { Action = "removeEntry", EntryId = entryId });

    public void SetField(string entryId, string field, string? value) =>
        SendAction(new GameDataActionCommand { Action = "setField", EntryId = entryId, Field = field, Value = value });

    public void SetCost(string entryId, string costTypeId, string? value) =>
        SendAction(new GameDataActionCommand { Action = "setCost", EntryId = entryId, CostTypeId = costTypeId, Value = value });

    public void SetCharacteristic(string entryId, string nameOrTypeId, string? value) =>
        SendAction(new GameDataActionCommand { Action = "setCharacteristic", EntryId = entryId, NameOrTypeId = nameOrTypeId, Value = value });

    public void Reload() => SendAction(new GameDataActionCommand { Action = "reload" });

    public string ExportActiveFile() =>
        SendAction(new GameDataActionCommand { Action = "exportFile" }).Xml
            ?? throw new InvalidOperationException("exportFile returned no xml.");

    public string LoadFile(string xml) =>
        SendAction(new GameDataActionCommand { Action = "loadFile", Xml = xml }).Id
            ?? throw new InvalidOperationException("loadFile returned no id.");

    public GameDataState GetState() => SendCommand(new GameDataGetStateCommand()) switch
    {
        GameDataStateResponse sr => sr.State,
        ProtocolError pe => throw new InvalidOperationException($"Adapter error: {pe.Message}"),
        var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
    };

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() =>
        SendCommand(new GameDataGetErrorsCommand()) switch
        {
            ErrorsResponse er => er.Errors,
            ProtocolError pe => [new ValidationErrorState(pe.Message)],
            var other => [new ValidationErrorState($"Unexpected response type: {other.Type}")],
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

    private GameDataActionResult SendAction(GameDataActionCommand command) => SendCommand(command) switch
    {
        GameDataActionResult { Ok: true } result => result,
        GameDataActionResult { Ok: false, Error: var error } =>
            throw new InvalidOperationException($"Action '{command.Action}' failed: {error}"),
        ProtocolError pe => throw new InvalidOperationException($"Adapter error: {pe.Message}"),
        var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
    };

    private ProtocolResponse SendCommand(ProtocolCommand command)
    {
        using var cts = new CancellationTokenSource(_requestTimeout);
        try
        {
            return _adapter.SendCommandAsync(command, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Adapter timed out after {_requestTimeout.TotalSeconds:0}s while handling '{command.Type}'.", ex);
        }
    }
}
