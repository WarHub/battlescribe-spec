using System.Text.Json;
using System.Text.Json.Nodes;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Orchestrates the BS UI probe workflow: export XML, launch BS, connect agent,
/// handle startup, and provide an interactive probe session.
/// </summary>
public sealed class BsUiProbe : IAsyncDisposable
{
    private readonly BsRosterApp _app;
    private AgentClient? _client;

    public AgentClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    public BsUiProbe(BsUiOptions options)
    {
        _app = new BsRosterApp(
            options.JavaPath,
            options.RosterEditorJarPath,
            options.AgentJarPath,
            options.IsolatedHomePath);
    }

    /// <summary>
    /// Stages game system/catalogue XML files into the BS data directory,
    /// launches BS with the agent, connects, and handles startup dialogs.
    /// </summary>
    public async Task LaunchAsync(
        IReadOnlyList<(string FileName, string Content)> xmlFiles,
        TextWriter? log = null)
    {
        log ??= TextWriter.Null;

        // Stage XML files into the BS data directory
        var dataDir = _app.DataDirectoryPath;
        Directory.CreateDirectory(dataDir);
        foreach (var (fileName, content) in xmlFiles)
        {
            var filePath = Path.Combine(dataDir, fileName);
            await File.WriteAllTextAsync(filePath, content);
            log.WriteLine($"  Staged: {fileName}");
        }

        // Launch BS
        log.WriteLine("Launching BattleScribe Roster Editor...");
        await _app.StartAsync();
        log.WriteLine($"  Agent listening on port {_app.AgentPort}");

        // Connect
        _client = await _app.ConnectAsync();
        var pong = await _client.PingAsync();
        log.WriteLine($"  Agent connected: {pong}");

        // Wait for the main window to appear
        log.WriteLine("Waiting for Roster Editor window...");
        var hasWindow = await _client.WaitForWindowAsync("Roster Editor", timeoutMs: 30000);
        if (!hasWindow)
        {
            throw new TimeoutException("Roster Editor window did not appear within 30s.");
        }

        log.WriteLine("  Window ready.");

        // Handle startup dialogs (dismiss "download data?" confirmation)
        await HandleStartupDialogsAsync(log);
    }

    /// <summary>Dumps the scene graph tree to the writer.</summary>
    public async Task DumpTreeAsync(TextWriter output, int maxDepth = 15)
    {
        var tree = await Client.DumpTreeAsync(maxDepth);
        if (tree is not null)
        {
            var formatted = tree.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await output.WriteLineAsync(formatted);
        }
    }

    /// <summary>Lists all open windows.</summary>
    public async Task DumpWindowsAsync(TextWriter output)
    {
        var windows = await Client.GetWindowsAsync();
        if (windows is not null)
        {
            var formatted = windows.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await output.WriteLineAsync(formatted);
        }
    }

    private async Task HandleStartupDialogsAsync(TextWriter log)
    {
        // BS shows a "Confirm" dialog on first launch asking to download data.
        // Dialog structure: btnPositive ("YES"), btnNegative ("NO"), btnNeutral (hidden).
        // We fire btnNegative to dismiss it.
        await Task.Delay(2000); // Give BS a moment to show the dialog

        var windows = await Client.GetWindowsAsync();
        if (windows is not JsonArray windowArray)
        {
            return;
        }

        foreach (var w in windowArray)
        {
            var title = w?["title"]?.GetValue<string>();
            if (title is not null && title.Contains("Confirm"))
            {
                log.WriteLine("  Dismissing startup confirmation dialog...");
                await Client.FireButtonAsync("#btnNegative", windowTitle: "Confirm");
                log.WriteLine("  Dialog dismissed.");
                await Task.Delay(500);
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Configuration for launching the BS UI probe.
/// </summary>
public record BsUiOptions
{
    /// <summary>Path to the Java executable (must include JavaFX).</summary>
    public required string JavaPath { get; init; }

    /// <summary>Path to RosterEditor.jar.</summary>
    public required string RosterEditorJarPath { get; init; }

    /// <summary>Path to bs-ui-java-agent.jar.</summary>
    public required string AgentJarPath { get; init; }

    /// <summary>Optional isolated home directory. If null, a temp directory is created.</summary>
    public string? IsolatedHomePath { get; init; }
}
