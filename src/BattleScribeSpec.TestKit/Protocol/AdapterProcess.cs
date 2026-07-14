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
    /// <summary>
    /// Cap on <see cref="_stderrLines"/>: <see cref="GetStderrTail"/> only ever reads the last 10,
    /// and every line is ALSO forwarded live to the parent's stderr (see <see cref="Start"/>) — so
    /// beyond a small tail, the queue is pure unbounded retention for the process's whole lifetime.
    /// Over a warm batch of hundreds of specs against a chatty JVM that would grow without bound.
    /// 200 is generous headroom over the 10-line tail actually read.
    /// </summary>
    private const int MaxStderrLines = 200;

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
        ResourceMetrics.Acquired("adapter-process");
        var stderrLines = new ConcurrentQueue<string>();

        // BSSPEC_WORKER_INDEX is set on the CHILD's environment (by RunBatch's AdapterFactory), not
        // the parent's — Environment.GetEnvironmentVariable here would read the parent's own env and
        // find nothing. Read it out of the environment dictionary the caller handed us instead, so
        // the tag reflects the worker this child actually belongs to.
        var workerTag = environment is not null
            && environment.TryGetValue("BSSPEC_WORKER_INDEX", out var workerIndex)
            && workerIndex.Length > 0
            ? $"[host:{workerIndex}] "
            : "[host] ";
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                stderrLines.Enqueue(e.Data);
                // Bounded retention: only the last MaxStderrLines are kept for GetStderrTail's
                // benefit. Every line is still forwarded live below regardless, so trimming here
                // loses nothing a human could otherwise see.
                while (stderrLines.Count > MaxStderrLines && stderrLines.TryDequeue(out var discarded))
                {
                    _ = discarded;
                }

                // Host-side diagnostics were previously invisible during a run: nothing ever drained
                // this queue except GetStderrTail(10), and only on failure. Forward every line live
                // (#303) so N parallel workers' host processes stay legible via the tag prefix.
                Console.Error.WriteLine(workerTag + e.Data);
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
    /// <b>The environment the child will actually receive</b> if it is started with
    /// <paramref name="environment"/> as its overlay: this process's own environment with the overlay
    /// applied, exactly as <see cref="BuildStartInfo"/> composes it — same code, same dictionary, same
    /// comparer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists so that "what will the child see?" has exactly one answer.</b> A caller that needs
    /// to reason about the child's environment BEFORE spawning it — <c>EngineSelection.LoadTarget</c>
    /// derives from <c>NR_ENGINE_URL</c> whether the run points at a third party's website, and therefore
    /// how many browsers it may open — must read the same dictionary the child is handed, not a second
    /// dictionary that happens to hold the same pairs.
    /// </para>
    /// <para>
    /// <b>Because a variable NAME does not mean the same thing on both platforms.</b>
    /// <see cref="ProcessStartInfo.Environment"/> is built with <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// on Windows and <see cref="StringComparer.Ordinal"/> on Unix — the OS's own rules. A parent that
    /// looked the endpoint up in its own <c>Ordinal</c> dictionary therefore disagreed with its own child
    /// on Windows: <c>--config-a "nr_engine_url=https://www.newrecruit.eu"</c> was a MISS for the parent
    /// (which then planned <c>ceil(cpuCount × k)</c> workers, the machine's full width) and a HIT for the
    /// child (which went live) — the load limit evaporated on a single lowercased letter, and the banner
    /// that says "held to N concurrent sessions" never printed. Hard-coding <c>OrdinalIgnoreCase</c>
    /// instead would merely have moved the disagreement to Linux, where the child genuinely would not see
    /// the variable and would replay its frozen HAR while the parent throttled it.
    /// </para>
    /// <para>
    /// So neither comparer is hard-coded anywhere: the answer is <em>read back out of the dictionary the
    /// OS itself defines</em>, which cannot be wrong about its own semantics and cannot drift from what
    /// the spawn does.
    /// </para>
    /// </remarks>
    /// <param name="environment">The overlay a child would be started with; null = no overlay.</param>
    /// <returns>The composed child environment, keyed by the platform's own variable-name rules.</returns>
    public static IReadOnlyDictionary<string, string?> ComposeChildEnvironment(
        IReadOnlyDictionary<string, string>? environment) =>
        BuildStartInfo(executable: "", arguments: null, environment).Environment.AsReadOnly();

    /// <summary>
    /// Bounded wait (see <see cref="SendCommandAsync"/>'s catch clause) for a just-failed process to
    /// report its exit before it is classified as still alive. On Windows <c>Process.HasExited</c>
    /// flips essentially immediately when a child dies, so this never actually waits the full
    /// duration in practice there. On Linux, a just-exited child is not reaped synchronously — the
    /// stdio pipe can close (which is what makes <see cref="NdjsonLineConnection.SendCommandAsync"/>
    /// throw in the first place) slightly BEFORE <c>Process.HasExited</c> flips true. 2 seconds is
    /// generous headroom over that reaping race without materially slowing down the one path that
    /// pays it (a transport failure — rare outside an actual crash).
    /// </summary>
    private const int DeathConfirmationWaitMs = 2000;

    /// <summary>
    /// True once the underlying process has exited (a crash, or the adapter's own self-termination —
    /// the motivating case: the BattleScribe app kept alive across hundreds of warm-reused specs
    /// intermittently self-terminates). <see cref="BattleScribeSpec.Batch.SpecSuiteRunner"/> checks
    /// this AFTER every spec attempt — rather than pattern-matching the exception text — to distinguish a genuine
    /// adapter death (this is true) from a transport error that leaves the process itself alive
    /// (a bad response, a hung call): only the former warrants retry-with-replacement.
    /// </summary>
    /// <remarks>
    /// This must NOT be the only place death is detected: see the bounded wait in
    /// <see cref="SendCommandAsync"/>'s catch clause, which is what makes THIS property correct by
    /// the time a caller reads it right after a transport failure (rather than depending on how fast
    /// the OS happens to have reaped the child).
    /// </remarks>
    public bool HasExited => _disposed || _process.HasExited;

    /// <summary>
    /// Classifies a process as dead after its transport has just failed: already-reaped-exited, OR
    /// exits within a short bounded wait. Extracted as a pure function of two already-known-shape
    /// inputs (a snapshot bool and a wait delegate) so the Linux reaping race that motivates it —
    /// <c>hasExitedNow == false</c> at the moment of failure, with the process actually dead and
    /// about to be reaped — is deterministically testable without spawning a real process or
    /// depending on OS timing (see <c>AdapterProcessDeathDetectionTests</c>).
    /// </summary>
    /// <param name="hasExitedNow">The process's exited state at the instant the transport failed.</param>
    /// <param name="waitForExitShort">
    /// Blocks for a short bounded duration and returns whether the process exited within it. In
    /// production this is <c>_process.WaitForExit(DeathConfirmationWaitMs)</c>; a test supplies a
    /// fake to simulate either outcome without a real process.
    /// </param>
    internal static bool IsDeadAfterTransportFailure(bool hasExitedNow, Func<bool> waitForExitShort) =>
        hasExitedNow || waitForExitShort();

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
            // The transport just failed — almost always because the process died and its stdio
            // closed (see NdjsonLineConnection's read loop). Give the OS a bounded chance to reap
            // the child and flip Process.HasExited BEFORE returning, so SpecSuiteRunner's HasExited
            // check right after this call is correct regardless of Linux's reaping timing (#308 CI
            // failure: without this wait, HasExited could still read false at that point, and the
            // death recovery path never engaged). A process that is genuinely still alive despite
            // the transport error is unaffected: WaitForExit simply returns false once the bounded
            // wait elapses, and HasExited correctly stays false.
            try
            {
                _ = IsDeadAfterTransportFailure(_process.HasExited, () => _process.WaitForExit(DeathConfirmationWaitMs));
            }
            catch
            {
                // Best-effort: never let this confirmation step mask the real transport exception below.
            }

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
        try
        {
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
        finally
        {
            // In a finally so a throwing teardown can't leak the counter — a counter that drifts
            // upward is worse than no counter, because it silently invents resources that don't exist.
            ResourceMetrics.Released("adapter-process");
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
