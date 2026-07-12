using System.Collections.Concurrent;
using System.Diagnostics;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Protocol;

/// <summary>
/// Correlates NDJSON command/response pairs by an optional <see cref="ProtocolCommand.CorrId"/>
/// over any <see cref="TextReader"/>/<see cref="TextWriter"/> pair. A single background read
/// loop owns the reader and reads lines to completion (never cancelled mid-line); a caller's
/// timeout/cancellation only abandons its own wait on a <see cref="TaskCompletionSource{TResult}"/>
/// — it never touches the stream. This is what makes a late/abandoned response harmless: the
/// read loop discards it by id instead of it being misread as the answer to the next command
/// (the desync that used to cascade through an entire run once one call timed out).
/// </summary>
/// <remarks>
/// Legacy fallback: a response with no id completes the single oldest outstanding request
/// (today's positional behavior), so adapters that don't echo ids keep working.
/// </remarks>
public sealed class NdjsonLineConnection : IAdapterConnection, IDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<ProtocolResponse>> _pending = new();
    private readonly CancellationTokenSource _readLoopCts = new();
    private readonly Task _readLoop;
    private int _nextId;
    private volatile Exception? _fault;
    private bool _disposed;

    public NdjsonLineConnection(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Set once the read loop has exited because the stream closed or a read/parse failed.
    /// New sends fail fast referencing this instead of hanging on a response that will never arrive.
    /// </summary>
    public Exception? Fault => _fault;

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_readLoopCts.IsCancellationRequested)
            {
                var line = await _input.ReadLineAsync(_readLoopCts.Token);
                if (line is null)
                {
                    break; // stream closed
                }

                ProtocolResponse? response;
                try
                {
                    response = ProtocolSerializer.DeserializeResponse(line);
                }
                catch
                {
                    continue; // ignore unparseable line rather than killing the read loop
                }

                if (response is null)
                {
                    continue;
                }

                if (response.CorrId is { } corrId)
                {
                    if (_pending.TryRemove(corrId, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                    // else: late/abandoned response for a timed-out call — discard. THIS is the desync fix.
                }
                else
                {
                    // Legacy fallback: no corrId on the wire — complete the single oldest outstanding
                    // request (today's positional behavior), for adapters that don't echo it.
                    CompleteOldestPending(response);
                }
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
            FaultAllPending(_fault ?? new IOException("NDJSON stream closed."));
        }
    }

    /// <summary>
    /// Completes the pending request with the smallest corrId. Ids are assigned via a
    /// monotonically increasing counter, so the smallest still-outstanding id is always the
    /// oldest request.
    /// </summary>
    private void CompleteOldestPending(ProtocolResponse response)
    {
        int? oldest = null;
        foreach (var key in _pending.Keys)
        {
            if (oldest is null || key < oldest)
            {
                oldest = key;
            }
        }

        if (oldest is { } corrId && _pending.TryRemove(corrId, out var tcs))
        {
            tcs.TrySetResult(response);
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

    /// <summary>
    /// Send a protocol command and await its correlated response. Assigns the next id, registers
    /// a waiter, writes the command (writes are serialized by a semaphore), then awaits the
    /// waiter with the caller's token. On cancellation/timeout the waiter is removed and the
    /// exception propagates — the stream itself is never touched, so a late response is later
    /// discarded by id rather than desyncing the next call.
    /// </summary>
    public async Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_fault is { } fault)
        {
            throw new InvalidOperationException("NDJSON connection is faulted.", fault);
        }

        var corrId = Interlocked.Increment(ref _nextId);
        command.CorrId = corrId;

        // The sending side of a remote call is a CLIENT span. Jaeger's dependency graph and Tempo's
        // servicegraph processor derive edges EXCLUSIVELY from CLIENT->SERVER pairs — with Internal
        // on both sides there is no bs-spec -> bs-engine-host edge at all.
        using var activity = HarnessTelemetry.StartOp(command.Type, kind: ActivityKind.Client);

        command.Traceparent ??= HarnessTelemetry.CurrentTraceparent();
        command.Tracestate ??= Activity.Current?.TraceStateString;

        var tcs = new TaskCompletionSource<ProtocolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[corrId] = tcs;
        try
        {
            var json = ProtocolSerializer.SerializeCommand(command);

            await _writeLock.WaitAsync(ct);
            try
            {
                await _output.WriteLineAsync(json.AsMemory(), ct);
                await _output.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _pending.TryRemove(corrId, out _); // no waiter leak on timeout/cancel/error
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readLoopCts.Cancel();
        FaultAllPending(new ObjectDisposedException(nameof(NdjsonLineConnection)));
    }
}

/// <summary>
/// Manages an adapter child process, providing JSON-line communication over stdin/stdout.
/// Delegates the wire protocol (id correlation, read loop) to <see cref="NdjsonLineConnection"/>;
/// this type owns process lifecycle and stderr diagnostics.
/// </summary>
public sealed class AdapterProcess : IAdapterConnection, IDisposable
{
    private readonly Process _process;
    private readonly NdjsonLineConnection _connection;
    private readonly ConcurrentQueue<string> _stderrLines;
    private bool _disposed;

    private AdapterProcess(Process process, ConcurrentQueue<string> stderrLines)
    {
        _process = process;
        _stderrLines = stderrLines;
        _connection = new NdjsonLineConnection(process.StandardOutput, process.StandardInput);
    }

    /// <summary>
    /// Start an adapter process from the given executable path and optional arguments.
    /// </summary>
    /// <param name="executable">Executable or "dotnet" when launching a .dll.</param>
    /// <param name="arguments">Command-line arguments, verbatim.</param>
    /// <param name="environment">
    /// Extra environment variables for the child, layered on top of the inherited environment.
    /// This is how the OTLP collector endpoint and the worker index reach the child.
    /// </param>
    /// <remarks>
    /// Stderr is collected asynchronously via BeginErrorReadLine. When the process exits
    /// quickly, not all stderr lines may be captured before GetStderrTail() is called.
    /// The Dispose method calls WaitForExit to ensure stderr is fully drained.
    /// </remarks>
    public static AdapterProcess Start(
        string executable,
        string? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = BuildStartInfo(executable, arguments, environment);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start adapter process: {executable}");
        var stderrLines = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                stderrLines.Enqueue(e.Data);
            }
        };
        process.BeginErrorReadLine();

        return new AdapterProcess(process, stderrLines);
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for an adapter child, layering
    /// <paramref name="environment"/> on top of the inherited environment. Extracted from
    /// <see cref="Start"/> so the environment-wiring logic is directly testable without spawning
    /// a real process.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string executable,
        string? arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments ?? "",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        return psi;
    }

    /// <summary>
    /// Send a protocol command and deserialize the correlated response. On timeout/cancellation
    /// the caller's waiter is abandoned without touching the stream — the adapter's eventual late
    /// response is discarded by id (see <see cref="NdjsonLineConnection"/>).
    /// </summary>
    public async Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"Adapter process has exited with code {_process.ExitCode}. Stderr tail: {GetStderrTail()}");
        }

        try
        {
            return await _connection.SendCommandAsync(command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller-requested timeout/cancellation — propagate unchanged (JsonProtocolEngine maps this to TimeoutException)
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Adapter process communication failed. Stderr tail: {GetStderrTail()}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(5000))
                {
                    _process.Kill();
                }
            }
            _process.CancelErrorRead();
        }
        catch
        {
            // Best-effort cleanup
        }
        finally
        {
            _process.Dispose();
        }
    }

    private string GetStderrTail(int maxLines = 10)
    {
        if (_stderrLines.IsEmpty)
        {
            return "<empty>";
        }

        var lines = _stderrLines.ToArray();
        var tail = lines.Skip(Math.Max(0, lines.Length - maxLines));
        return string.Join(" | ", tail);
    }
}
