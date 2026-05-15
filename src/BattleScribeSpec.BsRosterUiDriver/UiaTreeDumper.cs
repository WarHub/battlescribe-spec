using System.Diagnostics;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Launches the BattleScribe Roster Editor in an isolated environment and dumps
/// the UIA (Windows UI Automation) element tree for feasibility analysis.
/// </summary>
public static class UiaTreeDumper
{
    /// <summary>
    /// Launches BattleScribe Roster Editor, waits for the main window, and returns
    /// a text dump of the UIA element tree.
    /// </summary>
    public static async Task<string> DumpTreeAsync(
        string javaPath,
        string rosterEditorJarPath,
        string? isolatedHomePath = null,
        int waitForWindowSeconds = 15,
        int maxDepth = 10)
    {
        var tempHome = isolatedHomePath ?? CreateIsolatedHome();
        var shouldCleanup = isolatedHomePath is null;

        try
        {
            EnsureHomeStructure(tempHome);

            var args = $"-Xms1024m \"-Duser.home={tempHome}\" -jar \"{rosterEditorJarPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = args,
                UseShellExecute = false,
            };

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start BattleScribe process.");

            var processId = process.Id;

            try
            {
                using var automation = new UIA3Automation();
                var app = Application.Attach(processId);

                // Wait for main window
                Window? mainWindow = null;
                var deadline = DateTime.UtcNow.AddSeconds(waitForWindowSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        mainWindow = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
                        if (mainWindow is not null)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        // Window not ready yet
                    }
                    await Task.Delay(500);
                }

                if (mainWindow is null)
                {
                    return $"ERROR: Could not find main window within {waitForWindowSeconds}s.";
                }

                // Give the UI a moment to fully render
                await Task.Delay(2000);

                var sb = new StringBuilder();
                sb.AppendLine($"=== BattleScribe Roster Editor UIA Tree ===");
                sb.AppendLine($"Window Title: {mainWindow.Title}");
                sb.AppendLine($"Window AutomationId: {mainWindow.AutomationId}");
                sb.AppendLine($"Window ClassName: {mainWindow.ClassName}");
                sb.AppendLine($"Window BoundingRectangle: {mainWindow.BoundingRectangle}");
                sb.AppendLine();

                DumpElement(mainWindow, sb, depth: 0, maxDepth);

                return sb.ToString();
            }
            finally
            {
                try
                {
                    process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch
                {
                    // Process already exited
                }
            }
        }
        finally
        {
            if (shouldCleanup)
            {
                try
                {
                    Directory.Delete(tempHome, recursive: true);
                }
                catch
                {
                    // best effort cleanup
                }
            }
        }
    }

    private static void DumpElement(AutomationElement element, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            sb.AppendLine($"{Indent(depth)}... (max depth reached)");
            return;
        }

        var controlType = TryGet(element, e => e.ControlType);
        var name = TryGet(element, e => e.Name);
        var automationId = TryGet(element, e => e.AutomationId);
        var className = TryGet(element, e => e.ClassName);
        var isEnabled = TryGetBool(element, e => e.IsEnabled);
        var patterns = TryGetPatterns(element);

        sb.Append(Indent(depth));
        sb.Append($"[{controlType}]");
        if (!string.IsNullOrEmpty(name))
        {
            sb.Append($" Name=\"{name}\"");
        }
        if (!string.IsNullOrEmpty(automationId))
        {
            sb.Append($" AutomationId=\"{automationId}\"");
        }
        if (!string.IsNullOrEmpty(className))
        {
            sb.Append($" Class=\"{className}\"");
        }
        if (isEnabled is not null)
        {
            sb.Append($" Enabled={isEnabled}");
        }
        if (!string.IsNullOrEmpty(patterns))
        {
            sb.Append($" Patterns=[{patterns}]");
        }
        sb.AppendLine();

        AutomationElement[] children;
        try
        {
            children = element.FindAllChildren();
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            DumpElement(child, sb, depth + 1, maxDepth);
        }
    }

    private static string Indent(int depth) => new(' ', depth * 2);

    private static T? TryGet<T>(AutomationElement element, Func<AutomationElement, T> accessor)
    {
        try
        {
            return accessor(element);
        }
        catch
        {
            return default;
        }
    }

    private static bool? TryGetBool(AutomationElement element, Func<AutomationElement, bool> accessor)
    {
        try
        {
            return accessor(element);
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetPatterns(AutomationElement element)
    {
        try
        {
            var supported = element.GetSupportedPatterns();
            return string.Join(", ", supported.Select(p => p.Name));
        }
        catch
        {
            return "";
        }
    }

    private static string CreateIsolatedHome()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bs-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void EnsureHomeStructure(string homePath)
    {
        var bsDir = Path.Combine(homePath, "BattleScribe");
        Directory.CreateDirectory(Path.Combine(bsDir, "data"));
        Directory.CreateDirectory(Path.Combine(bsDir, "rosters"));
        Directory.CreateDirectory(Path.Combine(bsDir, "settings"));

        var settingsPath = Path.Combine(bsDir, "settings", "settings.xml");
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <configuration battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/configSchema"/>
                """);
        }

        var reposPath = Path.Combine(bsDir, "settings", "repositories.xml");
        if (!File.Exists(reposPath))
        {
            File.WriteAllText(reposPath, """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <repositoriesConfiguration battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/repositoriesSchema">
                  <repositorySources/>
                </repositoriesConfiguration>
                """);
        }
    }
}
