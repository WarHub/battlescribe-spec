using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using BattleScribeSpec.BsRosterUiDriver;

namespace BattleScribeSpec.Tests.Features;

public sealed class AgentClientTests
{
    [Fact]
    public async Task LateResponseAfterTimeout_DoesNotDesyncNextCall()
    {
        // Server: for id=1 ("slow"), wait past the client's CallTimeout before replying;
        // for id=2 ("fast"), reply immediately. Both replies echo their request id.
        await using var server = FakeAgentServer.Start(async (req, respond, ct) =>
        {
            var id = req["id"]!.GetValue<int>();
            var method = req["method"]!.GetValue<string>();
            if (method == "slow")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), ct);   // > CallTimeout below
                await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"slow-result"}""");
            }
            else
            {
                await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"fast-result"}""");
            }
        });

        using var client = new AgentClient(server.Connect()) { CallTimeout = TimeSpan.FromMilliseconds(200) };
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TimeoutException>(() => client.CallAsync("slow", cancellationToken: ct));

        // Give the late "slow" response (id=1, written ~600ms after the request) time to actually
        // land on the wire and sit unread before we issue the next call. This makes the repro
        // deterministic: a positional reader would consume this stale line first, regardless of
        // small scheduling jitter between the timeout firing and the next call being issued.
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

        // The next call (id=2) must get ITS OWN result, not the stale id=1 result.
        var fast = await client.CallAsync("fast", cancellationToken: ct);
        Assert.Equal("fast-result", fast!.GetValue<string>());
    }

    [Fact]
    public async Task NormalCall_ReturnsResult()
    {
        await using var server = FakeAgentServer.Start(async (req, respond, _) =>
        {
            var id = req["id"]!.GetValue<int>();
            await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"ok-result"}""");
        });

        using var client = new AgentClient(server.Connect());
        var ct = TestContext.Current.CancellationToken;

        var result = await client.CallAsync("ping", cancellationToken: ct);
        Assert.Equal("ok-result", result!.GetValue<string>());
    }

    [Fact]
    public async Task ErrorResponse_ThrowsAgentException()
    {
        await using var server = FakeAgentServer.Start(async (req, respond, _) =>
        {
            var id = req["id"]!.GetValue<int>();
            await respond($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-1,"message":"boom"}}""");
        });

        using var client = new AgentClient(server.Connect());
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.CallAsync("explode", cancellationToken: ct));
        Assert.Equal(-1, ex.Code);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task ConnectionClosed_FaultsPendingCall()
    {
        // Never respond; just wait until the test disposes the server, which closes the socket.
        await using var server = FakeAgentServer.Start(async (_, _, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct));

        using var client = new AgentClient(server.Connect()) { CallTimeout = Timeout.InfiniteTimeSpan };
        var ct = TestContext.Current.CancellationToken;

        var callTask = client.CallAsync("hang", cancellationToken: ct);

        // Give the request time to be written and registered as pending, then close the connection.
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        await server.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => callTask);
    }
}

/// <summary>
/// In-process fake JSON-RPC server for <see cref="AgentClientTests"/>. Accepts a single TCP
/// connection, reads newline-delimited JSON requests, and invokes a scripted handler for each.
/// The handler runs without blocking subsequent reads, so it can reply late / out of band —
/// this is what makes the desync regression test possible.
/// </summary>
public sealed class FakeAgentServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<JsonNode, Func<string, Task>, CancellationToken, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private TcpClient? _serverSideClient;

    private FakeAgentServer(TcpListener listener, Func<JsonNode, Func<string, Task>, CancellationToken, Task> handler)
    {
        _listener = listener;
        _handler = handler;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public static FakeAgentServer Start(Func<JsonNode, Func<string, Task>, CancellationToken, Task> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeAgentServer(listener, handler);
    }

    /// <summary>Connects a new client socket to this fake server.</summary>
    public TcpClient Connect()
    {
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        return client;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            using var serverClient = await _listener.AcceptTcpClientAsync(_cts.Token);
            _serverSideClient = serverClient;
            var stream = serverClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
            var writeLock = new SemaphoreSlim(1, 1);

            Task Respond(string line) => RespondAsync(writer, writeLock, line, _cts.Token);

            while (!_cts.IsCancellationRequested)
            {
                string? requestLine;
                try
                {
                    requestLine = await reader.ReadLineAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (requestLine is null)
                {
                    break;
                }

                var request = JsonNode.Parse(requestLine);
                if (request is null)
                {
                    continue;
                }

                // Fire-and-forget: don't await the handler here, so it can reply late without
                // blocking the read loop from picking up the next request.
                _ = InvokeHandlerAsync(request, Respond, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (ObjectDisposedException)
        {
            // listener/socket disposed during shutdown
        }
    }

    private async Task InvokeHandlerAsync(JsonNode request, Func<string, Task> respond, CancellationToken ct)
    {
        try
        {
            await _handler(request, respond, ct);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown for handlers awaiting indefinitely
        }
        catch
        {
            // swallow: a misbehaving handler shouldn't crash the accept loop
        }
    }

    private static async Task RespondAsync(StreamWriter writer, SemaphoreSlim writeLock, string line, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (ObjectDisposedException)
        {
            // stream disposed during shutdown
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _serverSideClient?.Dispose();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch
        {
            // already handled inside the loop; ignore any residual exception
        }
    }
}
