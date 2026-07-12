using System.Diagnostics;
using System.Net.Sockets;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Manages the lifecycle of a BattleScribe Roster Editor instance with
/// the bs-ui-java-agent loaded for remote scene graph access.
/// </summary>
public sealed class BsRosterApp : IAsyncDisposable
{
    private readonly string _javaPath;
    private readonly string _rosterEditorJarPath;
    private readonly string _agentJarPath;
    private readonly string _homePath;
    private readonly bool _ownsHome;

    private Process? _process;
    /// <summary>Cancels the long-lived agent-stderr pump on disposal.</summary>
    private readonly CancellationTokenSource _stderrCts = new();
    /// <summary>Set once <c>ResourceMetrics.Acquired("jvm")</c> has fired, so <see cref="DisposeAsync"/> releases exactly once.</summary>
    private bool _jvmAcquired;

    /// <summary>The TCP port the agent is listening on, or null if not started.</summary>
    public int? AgentPort { get; private set; }

    /// <summary>Path to the BattleScribe data directory within the isolated home.</summary>
    public string DataDirectoryPath => Path.Combine(_homePath, "BattleScribe", "data");

    public BsRosterApp(string javaPath, string rosterEditorJarPath, string agentJarPath, string? isolatedHomePath = null)
    {
        _javaPath = javaPath;
        _rosterEditorJarPath = rosterEditorJarPath;
        _agentJarPath = agentJarPath;
        _ownsHome = isolatedHomePath is null;
        _homePath = isolatedHomePath ?? CreateIsolatedHome();
    }

    /// <summary>Launches the BattleScribe process and waits for the agent to be ready.</summary>
    public async Task StartAsync(int timeoutSeconds = 30)
    {
        EnsureHomeStructure(_homePath);

        var args = $"-javaagent:\"{_agentJarPath}\" -Xms1024m \"-Duser.home={_homePath}\" -jar \"{_rosterEditorJarPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _javaPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start BattleScribe process.");
        _process = process;
        // The OS process is alive from here regardless of whether the agent handshake below
        // succeeds — DisposeAsync always tears down _process, so it must always release too.
        ResourceMetrics.Acquired("jvm");
        _jvmAcquired = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        // Pump the agent JVM's stderr for the WHOLE process lifetime (not just startup), teeing it to
        // a log file when BSUI_AGENT_STDERR_LOG is set. This is the harness's window into agent-side
        // diagnostics (System.err in the agent), which the request/response protocol can't surface.
        var stderrLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stderrLogPath = Environment.GetEnvironmentVariable("BSUI_AGENT_STDERR_LOG");
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(_stderrCts.Token)
                    is { } line)
                {
                    stderrLines.Enqueue(line);
                    Console.Error.WriteLine($"[BS stderr] {line}");
                    if (stderrLogPath is not null)
                    {
                        try
                        {
                            await File.AppendAllTextAsync(stderrLogPath, line + "\n");
                        }
                        catch
                        {
                            // best-effort logging
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                /* process exited */
            }
        });

        var exitTask = process.WaitForExitAsync();

        while (!cts.Token.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                var stderr = string.Join("\n", stderrLines);
                throw new InvalidOperationException(
                    $"BattleScribe process exited with code {process.ExitCode} before agent started.\nStderr: {stderr}");
            }

            var readTask = process.StandardOutput.ReadLineAsync(cts.Token).AsTask();
            var completedTask = await Task.WhenAny(readTask, exitTask);

            if (completedTask == exitTask)
            {
                var stderr = string.Join("\n", stderrLines);
                throw new InvalidOperationException(
                    $"BattleScribe process exited with code {process.ExitCode} before agent started.\nStderr: {stderr}");
            }

            string? line;
            try
            {
                line = await readTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                if (process.HasExited)
                {
                    var stderr = string.Join("\n", stderrLines);
                    throw new InvalidOperationException(
                        $"BattleScribe process exited with code {process.ExitCode} before agent started.\nStderr: {stderr}");
                }

                break;
            }

            Console.Error.WriteLine($"[bs-app] {line}");

            if (line.StartsWith("BSUI_AGENT_PORT=", StringComparison.Ordinal))
            {
                AgentPort = int.Parse(line["BSUI_AGENT_PORT=".Length..]);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (await process.StandardOutput.ReadLineAsync()
                            is not null)
                        { }
                    }
                    catch
                    {
                        /* process exited */
                    }
                });
                return;
            }
        }

        var stderrContent = string.Join("\n", stderrLines);
        throw new TimeoutException(
            $"BattleScribe agent did not start within {timeoutSeconds}s. Exit code: {(process.HasExited ? process.ExitCode : "still running")}. Stderr:\n{stderrContent}");
    }

    /// <summary>Creates a JSON-RPC client connected to the agent.</summary>
    public async Task<AgentClient> ConnectAsync()
    {
        if (AgentPort is null)
        {
            throw new InvalidOperationException("App not started or agent port not known.");
        }

        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", AgentPort.Value);
        return new AgentClient(client);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _stderrCts.Cancel();
            _stderrCts.Dispose();
            if (_process is not null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
                catch
                {
                    // Best effort
                }
            }
            _process?.Dispose();

            if (_ownsHome)
            {
                try
                {
                    Directory.Delete(_homePath, recursive: true);
                }
                catch
                {
                    // Best effort
                }
            }
        }
        finally
        {
            // In a finally so a throwing teardown can't leak the counter — a counter that drifts
            // upward is worse than no counter, because it silently invents resources that don't exist.
            if (_jvmAcquired)
            {
                _jvmAcquired = false;
                ResourceMetrics.Released("jvm");
            }
        }
    }

    private static string CreateIsolatedHome()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bs-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void EnsureHomeStructure(string homePath)
    {
        var bsDir = Path.Combine(homePath, "BattleScribe");
        Directory.CreateDirectory(Path.Combine(bsDir, "data"));
        Directory.CreateDirectory(Path.Combine(bsDir, "rosters"));
        Directory.CreateDirectory(Path.Combine(bsDir, "settings"));

        var settingsPath = Path.Combine(bsDir, "settings", "settings.xml");
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <configuration battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/configSchema"/>
                """);
        }

        var reposPath = Path.Combine(bsDir, "settings", "repositories.xml");
        if (!File.Exists(reposPath))
        {
            File.WriteAllText(reposPath, """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <repositoriesConfiguration battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/repositoriesSchema">
                  <repositorySources/>
                </repositoriesConfiguration>
                """);
        }
    }
}
