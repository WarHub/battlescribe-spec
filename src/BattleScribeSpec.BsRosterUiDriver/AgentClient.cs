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
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly CancellationTokenSource _readLoopCts = new();
    private readonly Task _readLoop;
    private int _nextId;
    private volatile Exception? _fault;

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
        _readLoop = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_readLoopCts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_readLoopCts.Token);
                if (line is null)
                {
                    break; // stream closed
                }

                JsonNode? response;
                try
                {
                    response = JsonNode.Parse(line);
                }
                catch
                {
                    continue; // ignore unparseable line
                }

                if (response?["id"] is not JsonNode idNode)
                {
                    continue; // e.g. parse-error response (id null) — discard
                }

                int id;
                try
                {
                    id = idNode.GetValue<int>();
                }
                catch
                {
                    continue;
                }

                if (_pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
                // else: late/abandoned response for a timed-out call — discard. THIS is the desync fix.
            }
        }
        catch (OperationCanceledException)
        {
            // disposing
        }
        catch (Exception ex)
        {
            _fault = ex;
        }
        finally
        {
            FaultAllPending(_fault ?? new InvalidOperationException("Agent connection closed."));
        }
    }

    private void FaultAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    /// <summary>Sends a JSON-RPC request and returns the result.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        if (_fault is { } fault)
        {
            throw new InvalidOperationException("Agent connection is faulted.", fault);
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
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

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
                await _writer.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeoutCts = CallTimeout != Timeout.InfiniteTimeSpan
                ? new CancellationTokenSource(CallTimeout)
                : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            JsonNode response;
            try
            {
                response = await tcs.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Agent did not respond to '{method}' within {CallTimeout.TotalSeconds:F0}s. " +
                    "The JavaFX thread may be blocked (deadlock).");
            }

            if (response["error"] is JsonNode error)
            {
                var code = error["code"]?.GetValue<int>() ?? -1;
                var message = error["message"]?.GetValue<string>() ?? "Unknown error";
                throw new AgentException(code, message);
            }

            return response["result"];
        }
        finally
        {
            _pending.TryRemove(id, out _); // no waiter leak on timeout/cancel/error
        }
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

    public void Dispose()
    {
        _readLoopCts.Cancel();
        try
        {
            _writer.Dispose();
        }
        catch
        {
        }

        try
        {
            _reader.Dispose();
        }
        catch
        {
        }

        _client.Dispose();
        FaultAllPending(new ObjectDisposedException(nameof(AgentClient)));
    }

    // --- Engine access commands ---

    /// <summary>Reads the current roster state from the engine.</summary>
    public async Task<JsonNode?> GetRosterStateAsync()
    {
        return await CallAsync("getRosterState");
    }

    /// <summary>Exports the current roster as BattleScribe XML (.ros format).</summary>
    public async Task<string?> ExportRosterXmlAsync()
    {
        var result = await CallAsync("exportRosterXml");
        return result?["xml"]?.GetValue<string>();
    }

    /// <summary>Captures a screenshot of the current JavaFX scene as PNG bytes.</summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        var result = await CallAsync("captureScreenshot");
        var base64 = result?["png"]?.GetValue<string>();
        return base64 is not null ? Convert.FromBase64String(base64) : null;
    }

    /// <summary>Reads the visible UI state (tree, costs, roster name) from the Roster Editor.</summary>
    public async Task<JsonNode?> GetUiStateAsync()
    {
        return await CallAsync("getUiState");
    }

    /// <summary>Gets current validation errors from the engine.</summary>
    public async Task<JsonNode?> GetValidationErrorsAsync()
    {
        return await CallAsync("getValidationErrors");
    }

    /// <summary>Starts recording user interactions in the Roster Editor UI.</summary>
    public async Task StartRecordingAsync()
    {
        await CallAsync("startRecording");
    }

    /// <summary>Stops recording and returns the recorded actions as JSON.</summary>
    public async Task<JsonNode?> StopRecordingAsync()
    {
        return await CallAsync("stopRecording");
    }

    /// <summary>Returns currently recorded actions without stopping.</summary>
    public async Task<JsonNode?> GetRecordedActionsAsync()
    {
        return await CallAsync("getRecordedActions");
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
