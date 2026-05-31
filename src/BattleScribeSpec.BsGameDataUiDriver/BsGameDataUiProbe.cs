using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.BsGameDataUiDriver;

/// <summary>
/// Orchestrates a BS GameData UI probe session: export XML, launch BS with agent,
/// connect, and provide an interactive probe workflow.
///
/// <para>
/// <b>Usage</b> (from bs-spec-debug):
/// <code>
/// bs-spec-debug --engine battlescribe-ui --probe [spec-id]
/// </code>
/// This launches BS with the agent, stages game system files (if a spec is provided),
/// connects to the agent, and enters an interactive JSON-RPC REPL where you can
/// explore the data editor scene graph.
/// </para>
///
/// <para>
/// <b>Probe workflow for DataEditorActions.java implementation</b>:
/// <list type="number">
///   <item>Run with a spec that loads a simple game system (e.g., <c>gamedata/basic/entry-add</c>)</item>
///   <item>Use <c>getWindows</c> to see open window titles</item>
///   <item>Navigate to the BattleScribe data editor window (title TBD after probing)</item>
///   <item>Use <c>dumpTree</c> with the data editor window title to inspect the scene graph</item>
///   <item>Identify the tree view, context menu structure, and property panel</item>
///   <item>Document selectors and window titles in <c>DataEditorActions.java</c></item>
///   <item>Implement each stub method using the <see cref="BsUiProbe"/> pattern from
///     <c>RosterActions.java</c></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Key probe RPC commands</b>:
/// <list type="bullet">
///   <item><c>getWindows</c> — list all open JavaFX windows</item>
///   <item><c>dumpTree {"maxDepth": 5, "windowTitle": "..."}</c> — inspect scene graph</item>
///   <item><c>clickNode {"selector": "...", "windowTitle": "..."}</c> — click a node</item>
///   <item><c>findNodeByText {"text": "...", "windowTitle": "..."}</c> — find node by label</item>
///   <item><c>captureScreenshot</c> — capture PNG screenshot</item>
///   <item><c>editorAddEntryAction {...}</c> — test a data editor action stub</item>
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

        log.WriteLine("Waiting for Roster Editor window...");
        var hasWindow = await _client.WaitForWindowAsync("Roster Editor", timeoutMs: 30_000);
        if (!hasWindow)
        {
            throw new TimeoutException("Roster Editor window did not appear within 30s.");
        }

        log.WriteLine("  Window ready.");
        await HandleStartupDialogsAsync(log);
        log.WriteLine();
        log.WriteLine("BS is running. Use the agent REPL to probe the data editor.");
        log.WriteLine("Tip: Try 'getWindows' first, then 'dumpTree' to see the scene graph.");
        log.WriteLine("Tip: To open the data editor: explore the menu structure with dumpTree.");
    }

    private async Task HandleStartupDialogsAsync(TextWriter log)
    {
        await Task.Delay(2000);
        var windows = await _client!.GetWindowsAsync();
        if (windows is not System.Text.Json.Nodes.JsonArray arr)
        {
            return;
        }

        foreach (var w in arr)
        {
            var title = w?["title"]?.GetValue<string>();
            if (title is not null && title.Contains("Confirm"))
            {
                log.WriteLine("  Dismissing startup dialog...");
                await _client.FireButtonAsync("#btnNegative", windowTitle: "Confirm");
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
