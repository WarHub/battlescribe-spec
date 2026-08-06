using System.Threading.Channels;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Regression coverage for the NDJSON correlation fix (#271). Production symptom: a client
/// enforces its own per-command timeout (e.g. 30s) and abandons the read when it fires, but a
/// real adapter can legitimately still be working (BS-UI roster operations take up to ~122s).
/// Without correlation, the adapter's late response is read positionally as the answer to
/// whatever command the client sends *next* — every subsequent read is now off by one for the
/// rest of the connection (a real 102-spec run produced 4 passed / 98 failed, 96 of them
/// "Unexpected response type: teardownResult").
/// </summary>
/// <remarks>
/// Exercises <see cref="NdjsonLineConnection"/> — the reader-loop + id-correlation transport
/// that both <see cref="AdapterProcess"/> and <c>bs-engine-host</c>'s adapters run on — against a
/// scripted fake adapter that can reply out of order without blocking the next read. Modeled on
/// <c>AgentClientTests.FakeAgentServer</c> (same fire-and-forget-handler shape, but driving the
/// NDJSON transport over in-memory channels instead of a TCP JSON-RPC socket).
/// </remarks>
public sealed class AdapterProcessCorrelationTests
{
    [Fact]
    public async Task LateResponseAfterTimeout_DoesNotDesyncNextCommand()
    {
        var ct = TestContext.Current.CancellationToken;

        // The TEST decides when "slow" answers and when the caller gives up — no wall-clock race.
        //
        // This used to sleep 600ms in the adapter against a 200ms CancellationTokenSource, i.e. two
        // real timers racing. Note what the client timeout actually IS here: NdjsonLineConnection
        // has no timeout of its own, so the 200ms CTS was purely "the caller gave up". Cancelling
        // that token explicitly expresses the same thing without a clock.
        var slowRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowReplyWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var adapter = ScriptedAdapter.Start(async (command, respond, adapterCt) =>
        {
            if (command is GetStateCommand)
            {
                slowRequestReceived.TrySetResult();
                await releaseSlowReply.Task.WaitAsync(adapterCt);
                await respond(new StateResponse { Name = "stale-late-response", GameSystemId = "gs" }, true);
                slowReplyWritten.TrySetResult();
            }
            else
            {
                await respond(new SetupResult(), true);
            }
        });

        // cmd1 is abandoned by the caller while the adapter is still "working" on it.
        using (var callerGaveUp = new CancellationTokenSource())
        {
            var cmd1 = adapter.Connection.SendCommandAsync(new GetStateCommand(), callerGaveUp.Token);
            await slowRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await callerGaveUp.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cmd1);
        }

        // Release cmd1's late response and wait for the fact that it was written, so it is on the
        // wire and unread before cmd2 goes out. That ordering IS the regression under test —
        // without it a positional reader is never offered the stale line and the test would pass
        // while proving nothing.
        releaseSlowReply.SetResult();
        await slowReplyWritten.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // cmd2 must get ITS OWN response. A positional reader would instead hand it the stale
        // StateResponse meant for cmd1 — exactly the production cascade ("Unexpected response type").
        var r2 = await adapter.Connection.SendCommandAsync(
            new SetupCommand { GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" } }, ct);
        Assert.IsType<SetupResult>(r2); // NOT the stale StateResponse meant for cmd1
    }

    [Fact]
    public async Task ResponseWithNoCorrId_ResolvesOldestOutstandingRequest_LegacyFallback()
    {
        // Legacy adapter: never echoes corrId at all. The client must still get an answer,
        // preserving today's strict positional behavior for adapters predating this addition.
        await using var adapter = ScriptedAdapter.Start(
            async (_, respond, _) => await respond(new TeardownResult(), false));

        var ct = TestContext.Current.CancellationToken;
        var response = await adapter.Connection.SendCommandAsync(new TeardownCommand(), ct);
        Assert.IsType<TeardownResult>(response);
    }
}

/// <summary>
/// In-process fake NDJSON adapter for <see cref="AdapterProcessCorrelationTests"/>. Runs a real
/// <see cref="NdjsonLineConnection"/> (the client-side transport under test) over two in-memory
/// channels, and dispatches each incoming command to a scripted handler WITHOUT awaiting it — so
/// the handler can reply late/out of order while the connection's read loop keeps servicing
/// other in-flight commands, exactly like <c>AgentClientTests.FakeAgentServer</c>.
/// </summary>
public sealed class ScriptedAdapter : IAsyncDisposable
{
    private readonly Channel<string> _toAdapter = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _fromAdapter = Channel.CreateUnbounded<string>();
    private readonly Func<ProtocolCommand, Func<ProtocolResponse, bool, Task>, CancellationToken, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatchLoop;

    /// <summary>The real correlation transport under test, wired to this fake adapter.</summary>
    public NdjsonLineConnection Connection { get; }

    private ScriptedAdapter(Func<ProtocolCommand, Func<ProtocolResponse, bool, Task>, CancellationToken, Task> handler)
    {
        _handler = handler;
        Connection = new NdjsonLineConnection(
            new ChannelTextReader(_fromAdapter.Reader),
            new ChannelTextWriter(_toAdapter.Writer));
        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    /// <param name="handler">
    /// (command, respond, ct) — call <c>respond</c> whenever ready; the dispatch loop does not
    /// wait for this to complete before reading the next command line.
    /// </param>
    public static ScriptedAdapter Start(
        Func<ProtocolCommand, Func<ProtocolResponse, bool, Task>, CancellationToken, Task> handler)
        => new(handler);

    private async Task DispatchLoopAsync()
    {
        try
        {
            while (await _toAdapter.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_toAdapter.Reader.TryRead(out var line))
                {
                    if (ProtocolSerializer.DeserializeCommand(line) is { } command)
                    {
                        _ = InvokeHandlerAsync(command, _cts.Token); // fire-and-forget: allows late replies
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task InvokeHandlerAsync(ProtocolCommand command, CancellationToken ct)
    {
        Task Respond(ProtocolResponse response, bool echoCorrId) => RespondAsync(command, response, echoCorrId);
        try
        {
            await _handler(command, Respond, ct);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown for handlers awaiting indefinitely
        }
        catch
        {
            // swallow: a misbehaving handler shouldn't crash the dispatch loop
        }
    }

    private async Task RespondAsync(ProtocolCommand command, ProtocolResponse response, bool echoCorrId)
    {
        if (echoCorrId)
        {
            response.CorrId = command.CorrId; // real AdapterHandler behavior
        }

        await _fromAdapter.Writer.WriteAsync(ProtocolSerializer.SerializeResponse(response));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _toAdapter.Writer.TryComplete();
        _fromAdapter.Writer.TryComplete();
        Connection.Dispose();
        try
        {
            await _dispatchLoop;
        }
        catch
        {
            // already handled inside the loop; ignore any residual exception
        }
    }

    private sealed class ChannelTextReader(ChannelReader<string> reader) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            try
            {
                return await reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null; // simulates the adapter closing its output
            }
        }
    }

    private sealed class ChannelTextWriter(ChannelWriter<string> writer) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override async Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken ct = default)
            => await writer.WriteAsync(value.ToString(), ct);

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task FlushAsync() => Task.CompletedTask;
    }
}
