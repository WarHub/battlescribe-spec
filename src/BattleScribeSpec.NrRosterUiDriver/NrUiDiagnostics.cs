using Microsoft.Playwright;

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// Captures diagnostic information when an NR UI action times out or fails.
/// Collects: Playwright screenshot, browser console logs, DOM snapshot, and Pinia state dump.
/// </summary>
public sealed class NrUiDiagnostics
{
    private readonly IPage _page;
    private readonly List<string> _consoleMessages = [];

    public NrUiDiagnostics(IPage page)
    {
        _page = page;
        // Subscribe to console messages for capture on failure
        page.Console += (_, msg) => _consoleMessages.Add($"[{msg.Type}] {msg.Text}");
    }

    /// <summary>
    /// Captures a PNG screenshot of the current page state.
    /// Returns null if the page is closed or capture fails.
    /// </summary>
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

    /// <summary>
    /// Returns all browser console messages collected since diagnostics were attached.
    /// </summary>
    public IReadOnlyList<string> GetConsoleLog() => _consoleMessages;

    /// <summary>
    /// Captures a simplified DOM snapshot (outer HTML of the body element).
    /// </summary>
    public async Task<string?> CaptureDomSnapshotAsync()
    {
        try
        {
            return await _page.EvaluateAsync<string?>(
                "() => document.body?.outerHTML ?? null");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Dumps the current Pinia store state relevant to roster editing:
    /// currentList (army forces, selections), systemsStore (loaded systems).
    /// </summary>
    public async Task<string?> CapturePiniaStateAsync()
    {
        try
        {
            return await _page.EvaluateAsync<string?>("""
                () => {
                    try {
                        const pinia = document.querySelector('#__nuxt')
                            ?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return null;
                        const lists = pinia._s.get('lists');
                        const army = lists?.currentList?.army;
                        if (!army) return JSON.stringify({ error: 'no army loaded' });

                        const forces = (army.getForces?.() || []).map(f => ({
                            uid: f.uid,
                            name: f.getName?.() ?? f.name,
                            selections: (f.getSelections?.() || []).map(s => ({
                                uid: s.uid,
                                name: s.getName?.() ?? s.name,
                                amount: s.getAmount?.() ?? s.amount,
                                entryId: s.getId?.() ?? s.id
                            }))
                        }));

                        const maxCosts = army.getMaxCosts?.() || [];
                        return JSON.stringify({ forces, maxCosts }, null, 2);
                    } catch(e) {
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
    /// Useful for attaching to test failure messages or writing to disk.
    /// </summary>
    public async Task<DiagnosticReport> CaptureFullReportAsync()
    {
        var screenshot = await CaptureScreenshotAsync();
        var dom = await CaptureDomSnapshotAsync();
        var pinia = await CapturePiniaStateAsync();
        var console = GetConsoleLog().ToList();

        return new DiagnosticReport(screenshot, console, dom, pinia);
    }
}

/// <summary>
/// Contains all diagnostic data captured on failure.
/// </summary>
public sealed record DiagnosticReport(
    byte[]? Screenshot,
    IReadOnlyList<string> ConsoleLog,
    string? DomSnapshot,
    string? PiniaState)
{
    /// <summary>
    /// Formats the non-binary diagnostics as a human-readable string.
    /// </summary>
    public string FormatText()
    {
        var parts = new List<string>();

        if (ConsoleLog.Count > 0)
        {
            parts.Add("=== Console Log ===");
            parts.AddRange(ConsoleLog);
        }

        if (PiniaState is not null)
        {
            parts.Add("\n=== Pinia State ===");
            parts.Add(PiniaState);
        }

        if (Screenshot is not null)
        {
            parts.Add($"\n=== Screenshot === ({Screenshot.Length} bytes PNG captured)");
        }

        if (DomSnapshot is not null)
        {
            // Truncate DOM to avoid overwhelming output
            var truncated = DomSnapshot.Length > 5000
                ? DomSnapshot[..5000] + "\n... (truncated)"
                : DomSnapshot;
            parts.Add("\n=== DOM Snapshot ===");
            parts.Add(truncated);
        }

        return string.Join("\n", parts);
    }
}
