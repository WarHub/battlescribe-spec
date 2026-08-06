using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.BsGameDataUiDriver;

/// <summary>
/// Orchestrates a BS GameData UI probe session: stage XML, launch the BattleScribe Data Editor
/// with the agent, connect, and leave the app open for inspection.
///
/// <para>
/// <b>Usage</b> (from bs-spec):
/// <code>
/// bs-spec probe --engine battlescribe --ui [spec-id]
/// </code>
/// This stages the spec's game system / catalogue files, launches the Data Editor
/// (<c>DataEditor.jar</c> — the same artifact <see cref="BsGameDataUiEngine"/> drives), connects
/// to the agent, and waits for the "Data Editor" window. There is <b>no interactive REPL</b>: the
/// app is left running for manual inspection until the caller presses Enter. To drive it
/// programmatically, attach a JSON-RPC client to the agent port.
/// </para>
///
/// <para>
/// <b>Useful agent RPC commands</b> (for an attached client):
/// <list type="bullet">
///   <item><c>getWindows</c> — list all open JavaFX windows</item>
///   <item><c>dumpTree {"maxDepth": 5, "windowTitle": "Data Editor"}</c> — inspect scene graph</item>
///   <item><c>findNodeByText {"text": "...", "windowTitle": "..."}</c> — find node by label</item>
///   <item><c>captureScreenshot</c> — capture PNG screenshot</item>
///   <item><c>gamedataGetDataState</c> — read the loaded data model as JSON</item>
/// </list>
/// </para>
/// </summary>
public sealed class BsGameDataUiProbe : IAsyncDisposable
{
    private readonly BsRosterApp _app;
    private AgentClient? _client;

    public AgentClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    public BsGameDataUiProbe(BsUiOptions options)
    {
        _app = new BsRosterApp(
            options.JavaPath,
            options.RosterEditorJarPath,
            options.AgentJarPath,
            options.IsolatedHomePath);
    }

    /// <summary>
    /// Stages game system/catalogue XML files, launches BS with the agent,
    /// connects, and handles startup dialogs.
    /// </summary>
    public async Task LaunchAsync(
        ProtocolGameSystem? gameSystem,
        IReadOnlyList<ProtocolCatalogue>? catalogues,
        TextWriter? log = null)
    {
        log ??= TextWriter.Null;

        if (gameSystem is not null && catalogues is not null)
        {
            var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
            var catXmls = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, [.. catalogues]);
            var files = new List<(string FileName, string Content)> { ("system.gst", gstXml) };
            files.AddRange(catXmls.Select(c => (c.FileName, c.Xml)));

            await BsUiDataStaging.StageDataFilesAsync(
                _app.DataDirectoryPath, gameSystem, [.. catalogues], files);

            foreach (var (fileName, _) in files)
            {
                log.WriteLine($"  Staged: {fileName}");
            }
        }
        else
        {
            log.WriteLine("  No game system provided — launching with empty data directory.");
        }

        log.WriteLine("Launching BattleScribe (data editor probe)...");
        await _app.StartAsync();
        log.WriteLine($"  Agent listening on port {_app.AgentPort}");

        _client = await _app.ConnectAsync();
        var pong = await _client.PingAsync();
        log.WriteLine($"  Agent connected: {pong}");

        log.WriteLine("Waiting for Data Editor window...");
        var hasWindow = await _client.WaitForWindowAsync("Data Editor", timeoutMs: 30_000);
        if (!hasWindow)
        {
            throw new TimeoutException("Data Editor window did not appear within 30s.");
        }

        log.WriteLine("  Window ready.");
        await HandleStartupDialogsAsync(log);
        log.WriteLine();
        log.WriteLine("BattleScribe Data Editor is running. Inspect it by hand, or attach your own");
        log.WriteLine($"JSON-RPC client to the agent on port {_app.AgentPort} (e.g. getWindows, dumpTree).");
    }

    private async Task HandleStartupDialogsAsync(TextWriter log)
    {
        // One shared, condition-driven implementation — see AgentClient.DismissStartupConfirmAsync.
        log.WriteLine("  Checking for the startup dialog...");
        await _client!.DismissStartupConfirmAsync();
        log.WriteLine("  Startup dialog handled.");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _app.DisposeAsync();
    }
}
