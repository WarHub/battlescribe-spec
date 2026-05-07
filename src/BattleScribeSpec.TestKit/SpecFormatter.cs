using System.Text;
using System.Text.RegularExpressions;

namespace BattleScribeSpec;

/// <summary>
/// Formats spec YAML files to conform to the lint conventions enforced by SpecLintTests.
/// All rules are applied in a single call to <see cref="FormatText"/> — the result is
/// idempotent (formatting an already-formatted file produces no changes).
///
/// Formatting rules (in application order):
///   1. Trailing whitespace stripped from every line
///   2. Redundant <c>engines: {}</c> lines removed
///   3. Redundant <c>hidden: false</c> lines removed
///   4. <c>expectedState:</c> property blocks zone-sorted: errors/errorsContain → … → forces → engines
///   5. Blank line inserted before <c>setup:</c> when preceded by non-blank content
///   6. Blank line inserted before each step item (<c>  - action:</c> / <c>  - expectedState:</c>)
///      unless preceded by a blank line, <c>steps:</c>, or a comment
///   7. File ends with exactly one newline
///
/// Approach: C# line-based formatter, same strategy as the original PowerShell script but
/// with proper unit-testable structure and single-pass idempotency (reordering happens before
/// blank-line insertion so that reordering never re-introduces blank-line violations).
/// </summary>
public static class SpecFormatter
{
    private static readonly Regex StepItemPattern =
        new(@"^  - (action|expectedState):", RegexOptions.Compiled);

    private static readonly Regex ExpectedStatePattern =
        new(@"^  - expectedState:$", RegexOptions.Compiled);

    // 6-space indent, first character is a lowercase word character (property name)
    private static readonly Regex EsTopPropPattern =
        new(@"^      [a-z]\w*:", RegexOptions.Compiled);

    // 8-space or more indent (deeper nesting inside a property value)
    private static readonly Regex EsDeepIndentPattern =
        new(@"^        ", RegexOptions.Compiled);

    /// <summary>
    /// Formats the given spec YAML text and returns the formatted result.
    /// The operation is idempotent: calling this method twice in a row on the same input
    /// yields the same result as calling it once.
    /// </summary>
    public static string FormatText(string content)
    {
        // Normalize line endings to LF for processing
        var text = content.Replace("\r\n", "\n").Replace('\r', '\n');

        // Pass 1 – per-line transforms (whitespace, redundant fields)
        text = StripTrailingWhitespaceAndRedundantLines(text);

        // Pass 2 – reorder expectedState property blocks (zone sort)
        text = ReorderExpectedStateProperties(text);

        // Pass 3 – blank lines around structural elements
        text = InsertBlankLines(text);

        // Pass 4 – trailing newline
        text = text.TrimEnd('\n', '\r') + "\n";

        return text;
    }

    /// <summary>
    /// Formats all *.yaml files under <paramref name="specsDir"/> in-place.
    /// </summary>
    /// <returns>Number of files changed (or that would change in check mode).</returns>
    public static int FormatDirectory(string specsDir, bool checkOnly, TextWriter? log = null)
    {
        if (!Directory.Exists(specsDir))
        {
            throw new DirectoryNotFoundException($"Specs directory not found: {specsDir}");
        }

        var files = Directory.GetFiles(specsDir, "*.yaml", SearchOption.AllDirectories);
        var changed = 0;

        foreach (var file in files)
        {
            var original = File.ReadAllText(file);
            var formatted = FormatText(original);
            if (formatted == original)
            {
                continue;
            }

            changed++;
            var rel = Path.GetRelativePath(specsDir, file).Replace('\\', '/');
            if (checkOnly)
            {
                log?.WriteLine($"  {rel} (needs formatting)");
            }
            else
            {
                File.WriteAllText(file, formatted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                log?.WriteLine($"  {rel}");
            }
        }

        return changed;
    }

    // ── Pass 1: trailing whitespace + redundant lines ─────────────────────

    private static string StripTrailingWhitespaceAndRedundantLines(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            var stripped = trimmed.Trim();

            // Skip redundant declarations
            if (stripped is "engines: {}" or "hidden: false")
            {
                continue;
            }

            result.Add(trimmed);
        }

        return string.Join("\n", result);
    }

    // ── Pass 2: reorder expectedState property blocks ─────────────────────

    private static int GetPropertyZone(string propName) => propName switch
    {
        "errors" or "errorsContain" => 0,   // first
        "forces" => 2,                       // second-to-last
        "engines" => 3,                      // last
        _ => 1,                              // middle (no mutual ordering within zone)
    };

    private static string ReorderExpectedStateProperties(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (!ExpectedStatePattern.IsMatch(line))
            {
                result.Add(line);
                i++;
                continue;
            }

            // Entered an expectedState block
            result.Add(line);
            i++;

            // Collect property blocks until the next step item, comment, or non-indented content
            var propBlocks = new List<(string Name, List<string> Lines, int OriginalIndex)>();
            string? currentPropName = null;
            var currentPropLines = new List<string>();

            while (i < lines.Length)
            {
                var l = lines[i];

                // Stop at next step item or top-level (non-indented non-blank) content
                if (StepItemPattern.IsMatch(l) || (l.Length > 0 && l[0] != ' '))
                {
                    break;
                }

                // Stop at blank lines that are followed by a next step / top-level / comment
                if (l == "")
                {
                    var peekIdx = i + 1;
                    while (peekIdx < lines.Length && lines[peekIdx] == "")
                    {
                        peekIdx++;
                    }

                    if (peekIdx >= lines.Length
                        || StepItemPattern.IsMatch(lines[peekIdx])
                        || lines[peekIdx].StartsWith("  #", StringComparison.Ordinal)
                        || (lines[peekIdx].Length > 0 && lines[peekIdx][0] != ' '))
                    {
                        break;
                    }
                }

                // New top-level property (6-space indent, not deeper)
                if (EsTopPropPattern.IsMatch(l) && !EsDeepIndentPattern.IsMatch(l))
                {
                    if (currentPropName is not null)
                    {
                        propBlocks.Add((currentPropName, currentPropLines, propBlocks.Count));
                    }

                    currentPropName = l.TrimStart().Split(':')[0];
                    currentPropLines = [l];
                }
                else
                {
                    currentPropLines.Add(l);
                }

                i++;
            }

            if (currentPropName is not null)
            {
                propBlocks.Add((currentPropName, currentPropLines, propBlocks.Count));
            }

            if (propBlocks.Count < 2)
            {
                // Nothing to reorder
                foreach (var (_, blockLines, _) in propBlocks)
                {
                    result.AddRange(blockLines);
                }
                continue;
            }

            // Check if reorder is needed
            var zones = propBlocks.Select(b => GetPropertyZone(b.Name)).ToArray();
            var needsReorder = false;
            var maxZone = -1;
            foreach (var z in zones)
            {
                if (z < maxZone)
                {
                    needsReorder = true;
                    break;
                }

                if (z > maxZone)
                {
                    maxZone = z;
                }
            }

            if (!needsReorder)
            {
                foreach (var (_, blockLines, _) in propBlocks)
                {
                    result.AddRange(blockLines);
                }
                continue;
            }

            // Stable sort by zone
            var sorted = propBlocks
                .Select((b, idx) => (b, idx))
                .OrderBy(x => GetPropertyZone(x.b.Name))
                .ThenBy(x => x.idx)
                .Select(x => x.b);

            foreach (var (_, blockLines, _) in sorted)
            {
                result.AddRange(blockLines);
            }
        }

        return string.Join("\n", result);
    }

    // ── Pass 3: blank lines ───────────────────────────────────────────────

    private static string InsertBlankLines(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length + 16);
        var inSteps = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var stripped = line.Trim();

            // Blank line before setup:
            if (stripped == "setup:" && result.Count > 0 && result[^1].Trim() != "")
            {
                result.Add("");
            }

            // Track steps section
            if (stripped == "steps:")
            {
                inSteps = true;
            }

            // Blank line before step items
            if (inSteps && StepItemPattern.IsMatch(line) && result.Count > 0)
            {
                var prev = result[^1].Trim();
                if (prev != "" && prev != "steps:" && !prev.StartsWith('#'))
                {
                    result.Add("");
                }
            }

            result.Add(line);
        }

        return string.Join("\n", result);
    }
}
