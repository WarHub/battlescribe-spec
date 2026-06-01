using BattleScribeSpec.Protocol;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Interactive probe mode for the NR Editor GameData UI driver.
/// Launches a visible Playwright browser session with game data loaded,
/// allowing ad-hoc DOM exploration, JS evaluation, and selector discovery.
///
/// Used to discover and validate CSS selectors for the NR Editor's tree UI
/// before they are codified in <see cref="NrGameDataUiActions"/>.
///
/// Usage via Debugger:
///   dotnet run --project src/BattleScribeSpec.Debugger -- --engine nr-editor-ui --probe spec-id
/// </summary>
public sealed class NrGameDataUiProbe : IAsyncDisposable
{
    private NrGameDataUiEngine? _engine;

    /// <summary>The Playwright page (requires <see cref="LaunchAsync"/> to be called first).</summary>
    public IPage Page => _engine?.Page
        ?? throw new InvalidOperationException("Probe not started.");

    /// <summary>
    /// Launches the NR Editor in a visible browser with game data loaded from the spec.
    /// The catalogue is loaded and opened but no mutations are applied.
    /// </summary>
    public async Task LaunchAsync(
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        string baseUrl = "https://giloushaker.github.io/nr-editor",
        TextWriter? log = null)
    {
        log ??= TextWriter.Null;

        log.WriteLine("Launching NR Editor GameData UI probe (visible browser)...");
        _engine = await NrGameDataUiEngine.CreateAsync(baseUrl, headless: false, slowMo: 100);

        log.WriteLine("Loading game data via NR Editor UI...");
        var errors = _engine.Setup(gameSystem, [.. catalogues]);
        if (errors.Count > 0)
        {
            foreach (var err in errors)
            { log.WriteLine($"  Setup warning: {err}"); }
        }

        log.WriteLine("Ready! Browser is open. Use EvalAsync() to run JS or interact manually.");
    }

    /// <summary>
    /// Launches in frozen (static file serving) mode for offline probing.
    /// </summary>
    public async Task LaunchFrozenAsync(
        string staticDir,
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        TextWriter? log = null)
    {
        log ??= TextWriter.Null;

        log.WriteLine("Launching NR Editor GameData UI probe (frozen static mode, visible browser)...");
        _engine = await NrGameDataUiEngine.CreateFrozenAsync(staticDir, headless: false, slowMo: 100);

        log.WriteLine("Loading game data...");
        var errors = _engine.Setup(gameSystem, [.. catalogues]);
        if (errors.Count > 0)
        {
            foreach (var err in errors)
            { log.WriteLine($"  Setup warning: {err}"); }
        }

        log.WriteLine("Ready! Browser is open with static NR Editor.");
    }

    /// <summary>Evaluate arbitrary JavaScript in the NR Editor page context.</summary>
    public async Task<T?> EvalAsync<T>(string expression)
        => await Page.EvaluateAsync<T>(expression);

    /// <summary>Convenience wrapper returning a string result.</summary>
    public async Task<string?> EvalStringAsync(string expression)
        => await Page.EvaluateAsync<string?>(expression);

    /// <summary>Take a screenshot and save to disk.</summary>
    public async Task ScreenshotAsync(string path)
        => await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });

    /// <summary>
    /// Interactive REPL loop: reads JS expressions from stdin, evaluates, prints results.
    /// Type 'exit' or 'quit' to stop.
    /// </summary>
    public async Task RunReplAsync(TextReader input, TextWriter output)
    {
        output.WriteLine("\nNR Editor GameData UI Probe REPL — enter JS expressions (exit/quit to stop):");
        output.Write("> ");
        output.Flush();

        while (true)
        {
            var line = await Task.Run(() => input.ReadLine());
            if (line is null or "exit" or "quit")
            { break; }

            if (string.IsNullOrWhiteSpace(line))
            {
                output.Write("> ");
                output.Flush();
                continue;
            }

            try
            {
                var result = await Page.EvaluateAsync<object?>(line);
                output.WriteLine(result?.ToString() ?? "(null)");
            }
            catch (Exception ex)
            {
                output.WriteLine($"Error: {ex.Message}");
            }

            output.Write("> ");
            output.Flush();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine?.Dispose();
        _engine = null;
        await ValueTask.CompletedTask;
    }
}
