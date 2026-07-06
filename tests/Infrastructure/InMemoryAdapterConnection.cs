using System.Threading.Channels;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Infrastructure;

/// <summary>
/// Runs an adapter handler loop in-process and exposes it as an
/// <see cref="IAdapterConnection"/> — no child process, fully deterministic.
/// </summary>
public sealed class InMemoryAdapterConnection : IAdapterConnection, IAsyncDisposable
{
    private readonly Channel<string> _toAdapter = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _fromAdapter = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public InMemoryAdapterConnection(
        Func<TextReader, TextWriter, CancellationToken, Task> runHandler)
    {
        _loop = Task.Run(() => runHandler(
            new ChannelTextReader(_toAdapter.Reader),
            new ChannelTextWriter(_fromAdapter.Writer),
            _cts.Token));
    }

    public async Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default)
    {
        await _toAdapter.Writer.WriteAsync(ProtocolSerializer.SerializeCommand(command), ct);
        var line = await _fromAdapter.Reader.ReadAsync(ct);
        return ProtocolSerializer.DeserializeResponse(line)
            ?? throw new InvalidOperationException($"Bad response: {line}");
    }

    public async ValueTask DisposeAsync()
    {
        _toAdapter.Writer.TryComplete(); // handler loop sees end-of-input and exits
        await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        _cts.Dispose();
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
                return null; // simulates stdin closing
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
