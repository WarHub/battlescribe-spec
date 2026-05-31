using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BattleScribeSpec.BsRosterUiDriver;

namespace BattleScribeSpec.BsGameDataUiDriver;

/// <summary>
/// Captures diagnostic state from the BS GameData UI agent when an action fails or times out.
/// Extends <see cref="BsUiDiagnostics"/> with data-editor-specific sections (data state, editor window).
/// </summary>
public static class BsGameDataUiDiagnostics
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>
    /// Directory where diagnostic dumps are written. Defaults to ./artifacts/bs-gamedata-ui-diagnostics/.
    /// Set via <c>BS_GAMEDATA_UI_DIAGNOSTICS_DIR</c> environment variable.
    /// </summary>
    public static string DiagnosticsDirectory { get; set; } =
        Environment.GetEnvironmentVariable("BS_GAMEDATA_UI_DIAGNOSTICS_DIR")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "bs-gamedata-ui-diagnostics");

    /// <summary>
    /// Captures the current data editor UI state and writes a diagnostic dump file.
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
            sb.AppendLine("  BS GAMEDATA UI DRIVER DIAGNOSTIC DUMP");
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
                    sb.AppendLine("─── DATA STATE ──────────────────────────────────────────");
                    try
                    {
                        var dataState = await client.CallAsync("editorGetDataState");
                        sb.AppendLine(FormatJson(dataState));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  [Failed to get data state: {ex.GetType().Name}: {ex.Message}]");
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

            Console.Error.WriteLine($"[bs-gamedata-ui-diag] Diagnostic dump written: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-gamedata-ui-diag] Failed to write diagnostic dump: {ex.Message}");
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
