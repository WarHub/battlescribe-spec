using BattleScribeSpec.Protocol;
using Microsoft.Playwright;

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// Interactive probe mode for the NR UI driver. Launches a visible Playwright browser
/// session with game data loaded, allowing ad-hoc DOM exploration and JS evaluation.
/// Used for discovering UI element selectors and testing interaction patterns.
///
/// Usage via Debugger:
///   dotnet run --project src/BattleScribeSpec.Debugger -- --probe --engine nr-ui spec-id
/// </summary>
public sealed class NrUiProbe : IAsyncDisposable
{
    private NrRosterUiEngine? _engine;

    public IPage Page => _engine?.Browser.Page
        ?? throw new InvalidOperationException("Probe not started.");

    /// <summary>
    /// Launches NR in a visible browser window with game data loaded from the spec.
    /// The roster is NOT created — use the browser to interact manually.
    /// </summary>
    public async Task LaunchAsync(
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        string baseUrl = "https://newrecruit.eu",
        TextWriter? log = null)
    {
        log ??= TextWriter.Null;

        log.WriteLine("Launching NR UI probe (visible browser)...");
        _engine = await NrRosterUiEngine.CreateAsync(baseUrl, headless: false, slowMo: 100);

        log.WriteLine("Loading game data...");
        _engine.Setup(gameSystem, [.. catalogues]);

        log.WriteLine("Ready! Browser is open. Use EvalAsync() to run JS or interact manually.");
        log.WriteLine("Press Ctrl+C to exit.");
    }

    /// <summary>
    /// Launches in frozen (HAR replay) mode for offline probing.
    /// </summary>
    public async Task LaunchFrozenAsync(
        string harFilePath,
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        string baseUrl = "https://newrecruit.eu",
        TextWriter? log = null)
    {
        log ??= TextWriter.Null;

        log.WriteLine("Launching NR UI probe (frozen/HAR mode, visible browser)...");
        _engine = await NrRosterUiEngine.CreateFrozenAsync(harFilePath, baseUrl, headless: false, slowMo: 100);

        log.WriteLine("Loading game data...");
        _engine.Setup(gameSystem, [.. catalogues]);

        log.WriteLine("Ready! Browser is open with HAR replay.");
    }

    /// <summary>
    /// Evaluate arbitrary JavaScript in the page context.
    /// Useful for probing NR's DOM structure, Pinia state, and Vue components.
    /// </summary>
    public async Task<T> EvalAsync<T>(string expression)
    {
        return await Page.EvaluateAsync<T>(expression);
    }

    /// <summary>
    /// Evaluate JavaScript returning a string result (convenience wrapper).
    /// </summary>
    public async Task<string?> EvalStringAsync(string expression)
    {
        return await Page.EvaluateAsync<string?>(expression);
    }

    /// <summary>
    /// Take a screenshot and save to disk.
    /// </summary>
    public async Task ScreenshotAsync(string path)
    {
        await Page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = true,
        });
    }

    /// <summary>
    /// Interactive REPL loop: reads JS expressions from stdin, evaluates them,
    /// prints results. Type 'exit' or 'quit' to stop.
    /// </summary>
    public async Task RunReplAsync(TextReader input, TextWriter output)
    {
        output.WriteLine("\nNR UI Probe REPL — enter JS expressions (exit/quit to stop):");
        output.Write("> ");

        while (true)
        {
            var line = await Task.Run(() => input.ReadLine());
            if (line is null or "exit" or "quit")
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                output.Write("> ");
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
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine?.Dispose();
        _engine = null;
        await ValueTask.CompletedTask;
    }
}
