using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Captures diagnostic state from the BS UI agent when an action fails or times out.
/// Dumps window list, scene graph, and error details to a file for post-mortem analysis.
/// </summary>
public sealed class BsUiDiagnostics
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>
    /// Directory where diagnostic dumps are written. Defaults to ./artifacts/bs-ui-diagnostics/,
    /// suffixed per worker (e.g. <c>-w2</c>) when <c>BSSPEC_WORKER_INDEX</c> is set — otherwise N
    /// parallel adapter processes resolve the same directory and overwrite each other's dumps.
    /// Set via <c>BS_UI_DIAGNOSTICS_DIR</c> environment variable (bypasses the worker suffix).
    /// </summary>
    public static string DiagnosticsDirectory { get; set; } = ResolveDefaultDirectory();

    /// <summary>
    /// Computes the default diagnostics directory fresh from the current environment. Exposed
    /// separately from <see cref="DiagnosticsDirectory"/> (whose initializer only runs once, at
    /// static-init time) so per-worker resolution can be tested without depending on when this
    /// type happens to be first touched by the process.
    /// </summary>
    public static string ResolveDefaultDirectory() =>
        Environment.GetEnvironmentVariable("BS_UI_DIAGNOSTICS_DIR")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", $"bs-ui-diagnostics{WorkerSuffix}");

    private static string WorkerSuffix =>
        Environment.GetEnvironmentVariable("BSSPEC_WORKER_INDEX") is { Length: > 0 } index
            ? $"-w{index}"
            : "";

    /// <summary>
    /// Captures the current UI state and writes a diagnostic dump file.
    /// Returns the path to the dump file, or null if capture failed.
    /// </summary>
    public static async Task<string?> CaptureAsync(
        AgentClient? client,
        string specId,
        string actionDescription,
        Exception failure)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var safeSpecId = SanitizeFileName(specId);
        var fileName = $"{timestamp}_{safeSpecId}.txt";

        try
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
            var filePath = Path.Combine(DiagnosticsDirectory, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine("  BS UI DRIVER DIAGNOSTIC DUMP");
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine($"Timestamp:  {DateTime.UtcNow:O}");
            sb.AppendLine($"Spec:       {specId}");
            sb.AppendLine($"Action:     {actionDescription}");
            sb.AppendLine($"Error Type: {failure.GetType().Name}");
            sb.AppendLine($"Error:      {failure.Message}");
            sb.AppendLine();

            if (client is not null)
            {
                // Use a short timeout for diagnostic calls — if agent is hung, don't wait long
                var originalTimeout = client.CallTimeout;
                client.CallTimeout = TimeSpan.FromSeconds(5);
                try
                {
                    sb.AppendLine("─── OPEN WINDOWS ───────────────────────────────────────");
                    try
                    {
                        var windows = await client.GetWindowsAsync();
                        sb.AppendLine(FormatJson(windows));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  [Failed to get windows: {ex.GetType().Name}: {ex.Message}]");
                    }

                    sb.AppendLine();
                    sb.AppendLine("─── OPEN DIALOGS (title/modal/scraped text) ───────────");
                    try
                    {
                        var dialogs = await client.CallAsync("getOpenDialogs", null);
                        sb.AppendLine(FormatJson(dialogs));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  [Failed to get open dialogs: {ex.GetType().Name}: {ex.Message}]");
                    }

                    sb.AppendLine();
                    sb.AppendLine("─── ALL WINDOWS SCENE DUMP (depth=4) ────────────────────");
                    try
                    {
                        var allWindows = await client.CallAsync("dumpAllWindows", new JsonObject { ["maxDepth"] = 4 });
                        sb.AppendLine(FormatJson(allWindows));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  [Failed to dump all windows: {ex.GetType().Name}: {ex.Message}]");
                    }

                    sb.AppendLine();
                    sb.AppendLine("─── THREAD DUMP ────────────────────────────────────────");
                    try
                    {
                        var threadDump = await client.CallAsync("threadDump", null);
                        sb.AppendLine(FormatJson(threadDump));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  [Failed to get thread dump: {ex.GetType().Name}: {ex.Message}]");
                    }

                    sb.AppendLine();
                    sb.AppendLine("─── SCENE GRAPH (depth=5) ──────────────────────────────");
                    try
                    {
                        var tree = await client.DumpTreeAsync(maxDepth: 5);
                        sb.AppendLine(FormatJson(tree));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  [Failed to dump tree: {ex.GetType().Name}: {ex.Message}]");
                    }
                }
                finally
                {
                    client.CallTimeout = originalTimeout;
                }
            }
            else
            {
                sb.AppendLine("  [No agent client available — connection may have been lost]");
            }

            sb.AppendLine();
            sb.AppendLine("─── STACK TRACE ────────────────────────────────────────");
            sb.AppendLine(failure.ToString());

            await File.WriteAllTextAsync(filePath, sb.ToString());

            Console.Error.WriteLine($"[bs-ui-diag] Diagnostic dump written: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-ui-diag] Failed to write diagnostic dump: {ex.Message}");
            return null;
        }
    }

    private static string FormatJson(JsonNode? node)
    {
        if (node is null)
        {
            return "  null";
        }

        try
        {
            return node.ToJsonString(PrettyJson);
        }
        catch
        {
            return node.ToString();
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }
}
