using System.Diagnostics;
using System.Net.Sockets;

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

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start BattleScribe process.");

        // Drain stderr in background to prevent pipe buffer from filling
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync()
                    is not null)
                { }
            }
            catch { /* process exited */ }
        });

        // Wait for the agent to print its port
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var line = await _process.StandardOutput.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("BSUI_AGENT_PORT=", StringComparison.Ordinal))
            {
                AgentPort = int.Parse(line["BSUI_AGENT_PORT=".Length..]);
                // Continue draining stdout in background to prevent pipe buffer deadlock
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (await _process.StandardOutput.ReadLineAsync()
                            is not null)
                        { }
                    }
                    catch { /* process exited */ }
                });
                return;
            }
        }

        throw new TimeoutException($"Agent did not report port within {timeoutSeconds}s.");
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
