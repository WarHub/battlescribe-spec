using System.Diagnostics;

namespace BattleScribeSpec.Protocol;

/// <summary>
/// Manages an adapter child process, providing JSON-line communication over stdin/stdout.
/// </summary>
public sealed class AdapterProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private bool _disposed;

    private AdapterProcess(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _stdin.AutoFlush = true;
    }

    /// <summary>
    /// Start an adapter process from the given executable path and optional arguments.
    /// </summary>
    public static AdapterProcess Start(string executable, string? arguments = null)
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

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start adapter process: {executable}");

        return new AdapterProcess(process);
    }

    /// <summary>
    /// Send a JSON command line and read the JSON response line.
    /// </summary>
    public async Task<string> SendAsync(string jsonLine, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process.HasExited)
            throw new InvalidOperationException($"Adapter process has exited with code {_process.ExitCode}.");

        await _stdin.WriteLineAsync(jsonLine.AsMemory(), ct);

        var response = await _stdout.ReadLineAsync(ct)
            ?? throw new InvalidOperationException("Adapter process closed stdout unexpectedly.");

        return response;
    }

    /// <summary>
    /// Send a protocol command and deserialize the response.
    /// </summary>
    public async Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default)
    {
        var json = ProtocolSerializer.SerializeCommand(command);
        var responseJson = await SendAsync(json, ct);
        return ProtocolSerializer.DeserializeResponse(responseJson)
            ?? throw new InvalidOperationException($"Failed to deserialize adapter response: {responseJson}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _stdin.Close();
                if (!_process.WaitForExit(5000))
                    _process.Kill();
            }
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
}
