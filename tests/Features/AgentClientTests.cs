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
        var ct = TestContext.Current.CancellationToken;

        // The TEST owns "slow", not the clock.
        //
        // This used to race two wall-clock durations: the handler slept 600ms against a 200ms
        // CallTimeout. That tests the CLOCK rather than the logic — on a loaded worker the 600ms
        // can lose to the 200ms, or the settle below can land in the wrong order — and it spent
        // ~700ms of every run proving something that should be true by construction.
        //
        // Gating the reply on a TaskCompletionSource makes "slow" INFINITELY slow, so the client's
        // own CallTimeout is the only clock left and can be tiny. Every line of the real path still
        // runs: the WaitAsync on the linked token, the exception filter that distinguishes a
        // timeout from a caller cancellation, the real TimeoutException, the pending-call removal,
        // and the read loop's discard-an-abandoned-response branch. Nothing is faked or bypassed.
        var slowRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowReplyWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = FakeAgentServer.Start(async (req, respond, serverCt) =>
        {
            var id = req["id"]!.GetValue<int>();
            var method = req["method"]!.GetValue<string>();
            if (method == "slow")
            {
                slowRequestReceived.TrySetResult();
                await releaseSlowReply.Task.WaitAsync(serverCt);
                await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"slow-result"}""");
                slowReplyWritten.TrySetResult();
            }
            else
            {
                await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"fast-result"}""");
            }
        });

        using var client = new AgentClient(server.Connect()) { CallTimeout = TimeSpan.FromMilliseconds(10) };

        var slowCall = client.CallAsync("slow", cancellationToken: ct);

        // Wait for the FACT that the request reached the server — and is therefore registered as
        // pending — rather than for a duration. Without it the assertion below could pass vacuously.
        await slowRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Assert.ThrowsAsync<TimeoutException>(() => slowCall);

        // Now release the late response and wait for the fact that it was WRITTEN.
        //
        // This is the load-bearing half of the regression (#271): the stale line has to be on the
        // wire before the next request goes out, or a positional reader never gets the chance to
        // mis-consume it and the test passes for the wrong reason. Deleting this wait without
        // replacing the signal would leave a green test that proves nothing. One TCP stream, so
        // written-before-request implies read-before-response.
        releaseSlowReply.SetResult();
        await slowReplyWritten.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

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
    public async Task NonObjectLine_IsDiscarded_AndLoopSurvives()
    {
        // A stray non-object line (JSON array) precedes the real response. The reader loop must
        // discard it — not throw from the ["id"] indexer and tear down the transport.
        await using var server = FakeAgentServer.Start(async (req, respond, _) =>
        {
            var id = req["id"]!.GetValue<int>();
            await respond("[1,2,3]");                                              // stray non-object line
            await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"survived"}""");
        });

        using var client = new AgentClient(server.Connect()) { CallTimeout = TimeSpan.FromSeconds(5) };
        var ct = TestContext.Current.CancellationToken;

        var result = await client.CallAsync("ping", cancellationToken: ct);
        Assert.Equal("survived", result!.GetValue<string>());
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
    public async Task WedgedFxThread_AnswersPing_ButFailsTheFxProbe()
    {
        // The agent under a full JavaFX deadlock, modelled exactly as JsonRpcServer splits it:
        // `ping` is not in FX_THREAD_METHODS and is answered from the socket thread, so it replies
        // normally; `getWindows` IS dispatched onto the FX thread and therefore never returns.
        //
        // Both halves are asserted on purpose. The ping succeeding is not incidental colour — it is
        // the defect (#357): the warm-start reuse gate used to ask this exact question, get this
        // exact answer, and hand the next spec an instance that could not be driven at all.
        var ct = TestContext.Current.CancellationToken;
        var fxCallReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = FakeAgentServer.Start(async (req, respond, serverCt) =>
        {
            var id = req["id"]!.GetValue<int>();
            if (req["method"]!.GetValue<string>() == "getWindows")
            {
                fxCallReceived.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, serverCt); // the wedged FX thread
                return;
            }

            await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"pong"}""");
        });

        using var client = new AgentClient(server.Connect()) { CallTimeout = TimeSpan.FromSeconds(30) };

        Assert.Equal("pong", await client.PingAsync());

        // The probe supplies its own short timeout, so the 30s CallTimeout above is never the clock
        // here — which is the other half of the fix: a wedged instance is discarded promptly rather
        // than stalling the reuse path for the full call timeout.
        await Assert.ThrowsAsync<TimeoutException>(() => client.ProbeFxThreadAsync(TimeSpan.FromMilliseconds(50)));
        await fxCallReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // And it leaves CallTimeout as it found it: the probe must not silently re-tune the client
        // it borrowed, or every later call on this connection inherits the probe's 50ms.
        Assert.Equal(TimeSpan.FromSeconds(30), client.CallTimeout);
    }

    [Fact]
    public async Task ConnectionClosed_FaultsPendingCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Never respond; just wait until the test disposes the server, which closes the socket.
        //
        // The Task.Delay here is NOT a wait to be removed — Timeout.InfiniteTimeSpan never elapses,
        // so this is "never answer, until shutdown cancels me". A mechanical sweep of Task.Delay
        // would delete the very behaviour this test needs.
        await using var server = FakeAgentServer.Start(async (_, _, serverCt) =>
        {
            requestReceived.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCt);
        });

        using var client = new AgentClient(server.Connect()) { CallTimeout = Timeout.InfiniteTimeSpan };

        var callTask = client.CallAsync("hang", cancellationToken: ct);

        // Wait for the fact that the request arrived — which is what "registered as pending" needs —
        // instead of guessing 200ms at it. Closing early would test a different thing entirely.
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
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
