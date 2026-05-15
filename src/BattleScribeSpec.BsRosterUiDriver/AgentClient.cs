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

    /// <summary>
    /// Default timeout for a single JSON-RPC call. Set to <see cref="Timeout.InfiniteTimeSpan"/>
    /// to disable. Default is 30 seconds.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public AgentClient(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
    }

    /// <summary>Sends a JSON-RPC request and returns the result.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
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
        await _writer.FlushAsync(cancellationToken);

        // Apply per-call timeout unless caller supplies their own cancellation
        using var timeoutCts = CallTimeout != Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource(CallTimeout)
            : new CancellationTokenSource();
        using var linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken)
            : null;
        var effectiveToken = linked?.Token ?? timeoutCts.Token;

        string? responseLine;
        try
        {
            responseLine = await _reader.ReadLineAsync(effectiveToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Agent did not respond to '{method}' within {CallTimeout.TotalSeconds:F0}s. " +
                "The JavaFX thread may be blocked (deadlock).");
        }

        if (responseLine is null)
        {
            throw new InvalidOperationException("Agent connection closed.");
        }

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

    /// <summary>Fires a ButtonBase control directly (more reliable than click).</summary>
    public async Task FireButtonAsync(string selector, string? windowTitle = null, bool async = false)
    {
        var parameters = new JsonObject { ["selector"] = selector };
        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        if (async)
        {
            parameters["async"] = "true";
        }

        await CallAsync("fireButton", parameters);
    }

    /// <summary>Finds all nodes matching a CSS selector.</summary>
    public async Task<JsonNode?> FindAllNodesAsync(string selector, string? windowTitle = null)
    {
        var parameters = new JsonObject { ["selector"] = selector };
        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        return await CallAsync("findAllNodes", parameters);
    }

    /// <summary>Finds a node by its text content, optionally filtered by type.</summary>
    public async Task<JsonNode?> FindNodeByTextAsync(string text, string? nodeType = null, string? windowTitle = null)
    {
        var parameters = new JsonObject { ["text"] = text };
        if (nodeType is not null)
        {
            parameters["nodeType"] = nodeType;
        }

        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        return await CallAsync("findNodeByText", parameters);
    }

    /// <summary>Sets text content of a TextInputControl.</summary>
    public async Task SetNodeTextAsync(string selector, string text, string? windowTitle = null)
    {
        var parameters = new JsonObject { ["selector"] = selector, ["text"] = text };
        if (windowTitle is not null)
        {
            parameters["windowTitle"] = windowTitle;
        }

        await CallAsync("setNodeText", parameters);
    }

    /// <summary>Polls for a node to appear, retrying until timeout.</summary>
    public async Task<JsonNode?> WaitForNodeAsync(string selector, string? windowTitle = null, int timeoutMs = 10000, int pollIntervalMs = 250)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var result = await FindNodeAsync(selector, windowTitle);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(pollIntervalMs);
        }

        return null;
    }

    /// <summary>Polls for a window with the given title to appear.</summary>
    public async Task<bool> WaitForWindowAsync(string titleFragment, int timeoutMs = 30000, int pollIntervalMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var windows = await GetWindowsAsync();
            if (windows is JsonArray arr)
            {
                foreach (var w in arr)
                {
                    var title = w?["title"]?.GetValue<string>();
                    if (title is not null && title.Contains(titleFragment))
                    {
                        return true;
                    }
                }
            }

            await Task.Delay(pollIntervalMs);
        }

        return false;
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

    // --- Engine access commands ---

    /// <summary>Lists all loaded net.battlescribe.* classes in the JVM.</summary>
    public async Task<JsonNode?> ListBsClassesAsync()
    {
        return await CallAsync("listBsClasses");
    }

    /// <summary>Inspects a class by name, listing fields and methods.</summary>
    public async Task<JsonNode?> InspectClassAsync(string className)
    {
        return await CallAsync("inspectClass", new JsonObject { ["className"] = className });
    }

    /// <summary>Attempts to find the BS engine instance in the JVM.</summary>
    public async Task<JsonNode?> FindEngineAsync()
    {
        return await CallAsync("findEngine");
    }

    /// <summary>Reads the current roster state from the engine.</summary>
    public async Task<JsonNode?> GetRosterStateAsync()
    {
        return await CallAsync("getRosterState");
    }

    /// <summary>Gets current validation errors from the engine.</summary>
    public async Task<JsonNode?> GetValidationErrorsAsync()
    {
        return await CallAsync("getValidationErrors");
    }

    // --- ComboBox / TreeView commands ---

    /// <summary>Gets items from a ComboBox.</summary>
    public async Task<JsonNode?> GetComboBoxItemsAsync(string selector, string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector };
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("getComboBoxItems", p);
    }

    /// <summary>Selects an item in a ComboBox by text match or index.</summary>
    public async Task<JsonNode?> SelectComboBoxItemAsync(
        string selector,
        string? text = null,
        int? index = null,
        string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector };
        if (text is not null)
        {
            p["text"] = text;
        }
        if (index is not null)
        {
            p["index"] = index.Value;
        }
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("selectComboBoxItem", p);
    }

    /// <summary>Gets items from a TreeView.</summary>
    public async Task<JsonNode?> GetTreeItemsAsync(
        string selector,
        int maxDepth = 3,
        string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector, ["maxDepth"] = maxDepth };
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("getTreeItems", p);
    }

    /// <summary>Selects an item in a TreeView by text match.</summary>
    public async Task<JsonNode?> SelectTreeItemAsync(
        string selector,
        string text,
        string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector, ["text"] = text };
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("selectTreeItem", p);
    }

    /// <summary>Expands a TreeItem by text match.</summary>
    public async Task<JsonNode?> ExpandTreeItemAsync(
        string selector,
        string text,
        string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector, ["text"] = text };
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("expandTreeItem", p);
    }

    /// <summary>Clicks (or double-clicks) a tree item by text. Double-click on catalogue tree adds entry.</summary>
    public async Task<JsonNode?> ClickTreeItemAsync(
        string selector,
        string text,
        bool doubleClick = false,
        string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector, ["text"] = text };
        if (doubleClick)
        {
            p["doubleClick"] = "true";
        }
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("clickTreeItem", p);
    }

    /// <summary>Presses a key on the focused or specified node.</summary>
    public async Task<JsonNode?> PressKeyAsync(
        string key,
        string? selector = null,
        string? windowTitle = null,
        bool ctrl = false,
        bool alt = false,
        bool shift = false,
        bool meta = false)
    {
        var p = new JsonObject { ["key"] = key };
        if (selector is not null)
        {
            p["selector"] = selector;
        }
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        if (ctrl)
        {
            p["ctrl"] = true;
        }
        if (alt)
        {
            p["alt"] = true;
        }
        if (shift)
        {
            p["shift"] = true;
        }
        if (meta)
        {
            p["meta"] = true;
        }
        return await CallAsync("pressKey", p);
    }

    /// <summary>Gets the current value of a Spinner control.</summary>
    public async Task<JsonNode?> GetSpinnerValueAsync(string selector, string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector };
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("getSpinnerValue", p);
    }

    /// <summary>Sets a Spinner value by steps (increment/decrement) or direct value.</summary>
    public async Task<JsonNode?> SetSpinnerValueAsync(
        string selector,
        int? steps = null,
        int? value = null,
        string? windowTitle = null)
    {
        var p = new JsonObject { ["selector"] = selector };
        if (steps is not null)
        {
            p["steps"] = steps.Value;
        }
        if (value is not null)
        {
            p["value"] = value.Value;
        }
        if (windowTitle is not null)
        {
            p["windowTitle"] = windowTitle;
        }
        return await CallAsync("setSpinnerValue", p);
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
