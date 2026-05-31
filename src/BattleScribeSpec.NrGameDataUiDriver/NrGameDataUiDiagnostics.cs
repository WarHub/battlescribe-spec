using Microsoft.Playwright;

namespace BattleScribeSpec.NrGameDataUiDriver;

/// <summary>
/// Captures diagnostic information when an NR Editor GameData UI action fails.
/// Collects: screenshot, browser console logs, DOM snapshot, and editorStore state dump.
/// </summary>
public sealed class NrGameDataUiDiagnostics
{
    private readonly IPage _page;
    private readonly List<string> _consoleMessages = [];

    /// <summary>
    /// Default directory for saving diagnostic artifacts.
    /// Override with NR_GAMEDATA_UI_DIAGNOSTICS_DIR environment variable.
    /// </summary>
    public static string DefaultArtifactsDir =>
        Environment.GetEnvironmentVariable("NR_GAMEDATA_UI_DIAGNOSTICS_DIR")
        ?? Path.Combine("artifacts", "nr-gamedata-ui-diagnostics");

    public NrGameDataUiDiagnostics(IPage page)
    {
        _page = page;
        page.Console += (_, msg) => _consoleMessages.Add($"[{msg.Type}] {msg.Text}");
    }

    /// <summary>Captures a full-page PNG screenshot. Returns null if capture fails.</summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        try
        {
            return await _page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns all browser console messages since diagnostics were attached.</summary>
    public IReadOnlyList<string> GetConsoleLog() => _consoleMessages;

    /// <summary>Captures the outer HTML of the body element (truncated to 8 KB).</summary>
    public async Task<string?> CaptureDomSnapshotAsync()
    {
        try
        {
            return await _page.EvaluateAsync<string?>(
                "() => document.body?.outerHTML?.substring(0, 8192) ?? null");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Dumps the NR Editor's Pinia editorStore state: currently open catalogue,
    /// available store keys, and any error information.
    /// </summary>
    public async Task<string?> CaptureEditorStateAsync()
    {
        try
        {
            return await _page.EvaluateAsync<string?>("""
                () => {
                    try {
                        const pinia = document.querySelector('#__nuxt')
                            ?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return JSON.stringify({ error: 'Pinia not found' });

                        const storeIds = [...pinia._s.keys()];
                        const editorStore = pinia._s.get('editor') || pinia._s.get('editorStore')
                            || pinia._s.get('catalogue') || pinia._s.get('catalogues');
                        const catalogue = editorStore?.catalogue || editorStore?.currentCatalogue
                            || editorStore?.rootCatalogue;

                        const state = {
                            storeIds,
                            hasEditorStore: !!editorStore,
                            editorStoreKeys: editorStore ? Object.keys(editorStore).filter(k => typeof editorStore[k] !== 'function') : [],
                            hasCatalogue: !!catalogue,
                            catalogueId: catalogue?.id,
                            catalogueName: catalogue?.name,
                            catalogueKeys: catalogue ? Object.keys(catalogue).filter(k => typeof catalogue[k] !== 'function') : [],
                            specEditorUiContext: window.__bsspec_editor_ui ? Object.keys(window.__bsspec_editor_ui) : null,
                        };
                        return JSON.stringify(state, null, 2);
                    } catch (e) {
                        return JSON.stringify({ error: e.message });
                    }
                }
                """);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Captures all diagnostic information and returns a formatted report.
    /// </summary>
    public async Task<NrGameDataDiagnosticReport> CaptureFullReportAsync()
    {
        var screenshot = await CaptureScreenshotAsync();
        var dom = await CaptureDomSnapshotAsync();
        var editorState = await CaptureEditorStateAsync();
        var console = GetConsoleLog().ToList();

        return new NrGameDataDiagnosticReport(screenshot, console, dom, editorState);
    }

    /// <summary>
    /// Saves the diagnostic report to the artifacts directory.
    /// Creates a timestamped subdirectory with screenshot.png, console.txt, state.json, dom.html.
    /// </summary>
    public static async Task SaveReportAsync(NrGameDataDiagnosticReport report, string? specId = null)
    {
        var dir = Path.Combine(
            DefaultArtifactsDir,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{specId ?? "unknown"}");
        Directory.CreateDirectory(dir);

        if (report.Screenshot is not null)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "screenshot.png"), report.Screenshot);
        }

        if (report.ConsoleLog.Count > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "console.txt"),
                string.Join("\n", report.ConsoleLog));
        }

        if (report.EditorState is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "editor-state.json"), report.EditorState);
        }

        if (report.DomSnapshot is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "dom.html"), report.DomSnapshot);
        }
    }
}

/// <summary>
/// Contains all diagnostic data captured on NR Editor GameData UI failure.
/// </summary>
public sealed record NrGameDataDiagnosticReport(
    byte[]? Screenshot,
    IReadOnlyList<string> ConsoleLog,
    string? DomSnapshot,
    string? EditorState)
{
    /// <summary>Formats the non-binary diagnostics as a human-readable string.</summary>
    public string FormatText()
    {
        var parts = new List<string>();

        if (ConsoleLog.Count > 0)
        {
            parts.Add("=== Console Log ===");
            parts.AddRange(ConsoleLog);
        }

        if (EditorState is not null)
        {
            parts.Add("\n=== NR Editor Store State ===");
            parts.Add(EditorState);
        }

        if (Screenshot is not null)
        {
            parts.Add($"\n=== Screenshot === ({Screenshot.Length} bytes PNG captured)");
        }

        if (DomSnapshot is not null)
        {
            parts.Add("\n=== DOM Snapshot (truncated) ===");
            parts.Add(DomSnapshot);
        }

        return string.Join("\n", parts);
    }
}
