using BattleScribeSpec.NewRecruit;
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
    /// Installs the store-mutation tracer (<see cref="NrStoreTraceJs"/>) if it has been enabled.
    /// Idempotent; call after Pinia is ready and after every per-spec reset.
    /// </summary>
    public async Task InstallStoreTraceAsync(int limit = 200)
    {
        if (!NrStoreTraceJs.Enabled)
        {
            return;
        }

        try
        {
            await _page.EvaluateAsync<string>(NrStoreTraceJs.InstallJs, limit);
        }
        catch
        {
            // Diagnostics must never break the run they are diagnosing.
        }
    }

    /// <summary>
    /// Reads back the recorded store mutations — who changed the stores, and from where.
    /// Null when tracing is off or nothing was recorded.
    /// </summary>
    public async Task<string?> CaptureStoreTraceAsync()
    {
        try
        {
            return await _page.EvaluateAsync<string?>(NrStoreTraceJs.ReadJs);
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
        var storeTrace = await CaptureStoreTraceAsync();
        var console = GetConsoleLog().ToList();

        return new DiagnosticReport(screenshot, console, dom, pinia, storeTrace);
    }

    /// <summary>
    /// How a message this driver has already described starts — both the one
    /// <see cref="DescribeTimeoutAsync"/> builds and the one
    /// <c>NrUiSetup.WaitForSetupConditionAsync</c> builds.
    /// </summary>
    private const string DescribedPrefix = "NR UI ";

    /// <summary>
    /// Whether <paramref name="ex"/> already carries a description, and re-describing it would only
    /// bury the more specific observation under a less specific one.
    /// </summary>
    internal static bool IsDescribed(Exception ex)
        => ex.Message.StartsWith(DescribedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Turns an anonymous Playwright timeout into a sentence naming the action, the page it was on,
    /// what the editor held instead, and where the report landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The action half of the fix <c>NrUiSetup.WaitForSetupConditionAsync</c> made for setup.</b>
    /// That one exists because a bare timeout is anonymous — Playwright names the target of a
    /// <em>locator</em> wait, but has nothing to name for a <c>WaitForFunctionAsync</c>. So does this
    /// one, and an action step is where this lane's remaining intermittents land. The complete text
    /// CI produced for the 2026-08-12 failure on
    /// <c>constraint/constraint-forces-field-on-forceentry</c> — the one that blocked the v35.27 HAR
    /// bump — was:
    /// </para>
    /// <code>Step 4: TimeoutException: Timeout 20000ms exceeded.</code>
    /// <para>
    /// Not which of that step's half-dozen waits, not what page it was on, not whether the roster
    /// held the two forces it should have by then. A snapshot bump's whole question is "did NR change
    /// under us", and that message cannot separate a changed UI from a lost page — a driver fix from
    /// a re-run.
    /// </para>
    /// <para>
    /// The observation is the point, not the label: this reports what it read and asserts no cause.
    /// The type stays <see cref="TimeoutException"/> at the throw site because callers discriminate
    /// on it.
    /// </para>
    /// </remarks>
    internal static async Task<string> DescribeTimeoutAsync(
        IPage page, string label, Exception failure, string? reportDir)
    {
        string observed;
        try
        {
            observed = await page.EvaluateAsync<string>("""
                () => {
                    const count = sel => document.querySelectorAll(sel).length;
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const army = pinia?._s?.get('lists')?.currentList?.army ?? window.__bsspec?.army;
                    const forces = army ? (army.getForces?.() || []).length : 'no army';
                    // forcesPanel excludes #popups deliberately. v35.72's Create-List dialog owns a
                    // `.forces` of its own, and counting it here is what produced the
                    // "forcesPanel=1" that sent this investigation towards the add-force panel when
                    // the truth was in forceRows=0 — the dialog had never closed. `createDialog`
                    // reports that state outright instead of hiding inside another counter.
                    const panels = count('.forces') - count('#popups .forces');
                    return `forces=${forces} forcesPanel=${panels} `
                        + `createDialog=${count('#vueAddlist')} `
                        + `forceRows=${count('.unit-wrap.force')} unitRows=${count('.unitRow')} `
                        + `popups=${count('#popups > *')}`;
                }
                """) ?? "(no observation)";
        }
        catch (Exception observeFailure)
        {
            // The page can be gone entirely — that IS the observation, and it must not replace the
            // timeout with a confusing secondary failure.
            observed = $"(could not be read: {observeFailure.GetType().Name}: {observeFailure.Message})";
        }

        return $"{DescribedPrefix}{label}: {failure.Message} (page: {page.Url}). Observed: {observed}."
            + (reportDir is null ? "" : $" Report: {reportDir}.");
    }

    /// <summary>Where reports land. Mirrors <c>NrGameDataUiDiagnostics</c>, including its worker suffix.</summary>
    public static string DefaultArtifactsDir =>
        Environment.GetEnvironmentVariable("NR_UI_DIAGNOSTICS_DIR")
        ?? Path.Combine("artifacts", $"nr-ui-diagnostics{WorkerSuffix}");

    private static string WorkerSuffix =>
        Environment.GetEnvironmentVariable("BSSPEC_WORKER_ID") is { Length: > 0 } id ? $"-w{id}" : "";

    /// <summary>
    /// Writes a report to <see cref="DefaultArtifactsDir"/> as screenshot.png / console.txt /
    /// pinia-state.json / store-trace.json / dom.html.
    /// <para>
    /// This driver had a <c>CaptureFullReportAsync</c> and no way to persist it, and nothing called
    /// it — so an NR roster UI failure produced no artifacts at all. Seven identical 30s
    /// <c>addForce</c> timeouts while diagnosing #339 wrote nothing to disk.
    /// </para>
    /// <para>
    /// Returns the directory it wrote, so the caller can name it in the failure message. A report on
    /// disk that the failure does not mention is one the reader has to know to go looking for.
    /// </para>
    /// </summary>
    public static async Task<string> SaveReportAsync(DiagnosticReport report, string? specId = null)
    {
        var dir = Path.Combine(
            DefaultArtifactsDir,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{specId ?? "unknown"}{WorkerSuffix}");
        Directory.CreateDirectory(dir);

        if (report.Screenshot is not null)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "screenshot.png"), report.Screenshot);
        }

        if (report.ConsoleLog.Count > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "console.txt"), string.Join("\n", report.ConsoleLog));
        }

        if (report.PiniaState is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "pinia-state.json"), report.PiniaState);
        }

        if (report.StoreTrace is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "store-trace.json"), report.StoreTrace);
        }

        if (report.DomSnapshot is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "dom.html"), report.DomSnapshot);
        }

        return dir;
    }
}

/// <summary>
/// Contains all diagnostic data captured on failure.
/// </summary>
public sealed record DiagnosticReport(
    byte[]? Screenshot,
    IReadOnlyList<string> ConsoleLog,
    string? DomSnapshot,
    string? PiniaState,
    string? StoreTrace = null)
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

        // Deliberately above the DOM dump: when state is wrong, the question is almost always who
        // changed it, and this is the only section that answers that.
        if (StoreTrace is not null)
        {
            parts.Add("\n=== Store Mutations (who changed the stores, and from where) ===");
            parts.Add(StoreTrace);
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
