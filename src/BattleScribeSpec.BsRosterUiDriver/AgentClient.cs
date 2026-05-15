using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// JSON-RPC 2.0 client for communicating with the bs-ui-java-agent
/// running inside the BattleScribe JVM.
/// </summary>
public sealed class AgentClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private int _nextId;

    public AgentClient(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
    }

    /// <summary>Sends a JSON-RPC request and returns the result.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null)
    {
        var id = Interlocked.Increment(ref _nextId);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["id"] = id,
        };

        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        var json = request.ToJsonString();
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();

        var responseLine = await _reader.ReadLineAsync()
            ?? throw new InvalidOperationException("Agent connection closed.");

        var response = JsonNode.Parse(responseLine)
            ?? throw new InvalidOperationException("Invalid JSON response from agent.");

        if (response["error"] is JsonNode error)
        {
            var code = error["code"]?.GetValue<int>() ?? -1;
            var message = error["message"]?.GetValue<string>() ?? "Unknown error";
            throw new AgentException(code, message);
        }

        return response["result"];
    }

    /// <summary>Sends a ping and verifies the agent is responsive.</summary>
    public async Task<string> PingAsync()
    {
        var result = await CallAsync("ping");
        return result?.GetValue<string>() ?? throw new InvalidOperationException("Unexpected ping response.");
    }

    /// <summary>Dumps the JavaFX scene graph tree.</summary>
    public async Task<JsonNode?> DumpTreeAsync(int maxDepth = 10, string? windowTitle = null)
    {
        var parameters = new JsonObject { ["maxDepth"] = maxDepth };
        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        return await CallAsync("dumpTree", parameters);
    }

    /// <summary>Gets information about all open JavaFX windows.</summary>
    public async Task<JsonNode?> GetWindowsAsync()
    {
        return await CallAsync("getWindows");
    }

    /// <summary>Finds a node by CSS selector.</summary>
    public async Task<JsonNode?> FindNodeAsync(string selector, string? windowTitle = null)
    {
        var parameters = new JsonObject { ["selector"] = selector };
        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        return await CallAsync("findNode", parameters);
    }

    /// <summary>Clicks a node found by CSS selector.</summary>
    public async Task<JsonNode?> ClickNodeAsync(string selector, string? windowTitle = null)
    {
        var parameters = new JsonObject { ["selector"] = selector };
        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        return await CallAsync("clickNode", parameters);
    }

    /// <summary>Gets the text content of a node.</summary>
    public async Task<string?> GetNodeTextAsync(string selector, string? windowTitle = null)
    {
        var parameters = new JsonObject { ["selector"] = selector };
        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        var result = await CallAsync("getNodeText", parameters);
        return result?.GetValue<string>();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _reader.Dispose();
        _client.Dispose();
    }
}

/// <summary>Exception thrown when the agent returns a JSON-RPC error.</summary>
public class AgentException : Exception
{
    public int Code { get; }

    public AgentException(int code, string message) : base(message)
    {
        Code = code;
    }
}
