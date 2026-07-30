using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests that keep the spec counts quoted in the repository's prose in sync with what is
/// actually on disk. These counts drifted badly once already — README.md and AGENTS.md disagreed
/// with each other and with <c>specs/</c>, and the README's per-category table was missing five
/// whole categories — so every documented number is now asserted here.
///
/// Two kinds of check:
/// <list type="bullet">
///   <item><see cref="DocumentedCountMatchesDisk"/> — one case per documented sentence, heading or
///   table row that quotes a count. Each is located by a regex anchored on distinctive wording
///   rather than on a line number, so reflowing or reformatting the markdown is harmless.</item>
///   <item><see cref="ReadmeCategoryTableMatchesDisk"/> — the README's per-category tables must
///   list exactly the category directories that exist, with exactly the right per-category
///   counts.</item>
/// </list>
///
/// Failures name the file, the offending text and the replacement number, so a contributor who
/// adds a spec can fix the docs without first reverse-engineering this test.
/// </summary>
[Trait("Category", "Lint")]
public sealed class DocumentedSpecCountLintTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ThisFile = ThisFileRelativePath();

    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        if (!Path.IsPathRooted(callerFilePath))
        {
            throw new InvalidOperationException(
                $"[CallerFilePath] returned a non-rooted path '{callerFilePath}'. " +
                "Ensure the project is not built with a PathMap that strips the absolute path.");
        }

        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.slnx").Any())
            {
                return dir.Replace('\\', '/');
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root (no *.slnx marker found) while traversing parents of '{callerFilePath}'.");
    }

    private static string ThisFileRelativePath([CallerFilePath] string callerFilePath = "")
    {
        return Path.GetRelativePath(RepoRoot, callerFilePath).Replace('\\', '/');
    }

    // ── On-disk truth ────────────────────────────────────────────────

    /// <summary>Per-category spec file counts for one spec domain (roster or gamedata).</summary>
    private sealed record SpecInventory(string Domain, IReadOnlyDictionary<string, int> ByCategory)
    {
        public int Total => ByCategory.Values.Sum();

        public int CategoryCount => ByCategory.Count;

        public string Summary =>
            $"specs/{Domain}/ holds {Total} .yaml files across {CategoryCount} category directories";
    }

    // Spec directories are resolved the same way the sibling spec lint tests resolve them
    // (SpecLintTests / GameDataSpecLintTests).
    private static readonly Lazy<SpecInventory> Roster =
        new(() => TakeInventory("roster", SpecLoader.FindRosterSpecsDirectory(), SpecLoader.DiscoverSpecs));

    private static readonly Lazy<SpecInventory> GameData =
        new(() => TakeInventory("gamedata", SpecLoader.FindGameDataSpecsDirectory(), SpecLoader.DiscoverGameDataSpecs));

    private static SpecInventory TakeInventory(
        string domain,
        string? specsDir,
        Func<string, IEnumerable<(string Path, string Id, string Category)>> discover)
    {
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            throw new DirectoryNotFoundException(
                $"Documented spec count lint could not find the specs/{domain}/ directory.");
        }

        var byCategory = discover(specsDir)
            .GroupBy(x => x.Category, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        if (byCategory.Count == 0)
        {
            throw new InvalidOperationException($"No spec files were discovered under '{specsDir}'.");
        }

        return new(domain, byCategory);
    }

    private static int TotalSpecs => Roster.Value.Total + GameData.Value.Total;

    private static string DiskTruth =>
        $"{Roster.Value.Summary}; {GameData.Value.Summary}; {TotalSpecs} spec files in total";

    // ── Documented claims ────────────────────────────────────────────

    /// <summary>One number inside a documented sentence, and what it is supposed to equal.</summary>
    private sealed record DocumentedNumber(string Group, string Means, Func<int> Actual);

    /// <summary>
    /// A sentence, heading or table row in a doc file that quotes one or more spec counts.
    /// <paramref name="Pattern"/> is anchored on distinctive wording and tolerates whitespace and
    /// line wrapping, so it survives harmless markdown reformatting.
    /// </summary>
    private sealed record DocumentedClaim(
        string Name,
        string RelativePath,
        string Describes,
        Regex Pattern,
        params DocumentedNumber[] Numbers);

    private static DocumentedNumber TotalCount()
    {
        return new("total", "total spec files", () => TotalSpecs);
    }

    private static DocumentedNumber RosterCount(string group)
    {
        return new(group, "roster spec files", () => Roster.Value.Total);
    }

    private static DocumentedNumber GameDataCount(string group)
    {
        return new(group, "gamedata spec files", () => GameData.Value.Total);
    }

    private static DocumentedNumber RosterCategories()
    {
        return new("categories", "roster category directories", () => Roster.Value.CategoryCount);
    }

    private static DocumentedNumber GameDataCategories()
    {
        return new("categories", "gamedata category directories", () => GameData.Value.CategoryCount);
    }

    private static Regex Pattern(string pattern)
    {
        return new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static readonly DocumentedClaim[] DocumentedClaims =
    [
        new(
            "README.md intro sentence",
            "README.md",
            "the opening sentence's spec file totals",
            Pattern(@"against\s+(?<total>\d+)\s+spec\s+files\s*[—-]\s*(?<roster>\d+)\s+roster\s+specs\s+and\s+(?<gamedata>\d+)\s+GameData"),
            TotalCount(),
            RosterCount("roster"),
            GameDataCount("gamedata")),

        new(
            "README.md architecture table",
            "README.md",
            "the 'YAML Specs' row of the architecture layer table",
            Pattern(@"\*\*YAML\s+Specs\*\*\s*\|\s*(?<total>\d+)\s+declarative\s+spec\s+files\s*\(\s*(?<roster>\d+)\s+roster\s*\+\s*(?<gamedata>\d+)\s+GameData\s*\)"),
            TotalCount(),
            RosterCount("roster"),
            GameDataCount("gamedata")),

        new(
            "README.md roster coverage heading",
            "README.md",
            "the 'Roster specs' heading above the roster category table",
            Pattern(@"#+\s*Roster\s+specs\s*[—-]\s*(?<total>\d+)\s+across\s+(?<categories>\d+)\s+categories"),
            RosterCount("total"),
            RosterCategories()),

        new(
            "README.md gamedata coverage heading",
            "README.md",
            "the 'GameData specs' heading above the gamedata category table",
            Pattern(@"#+\s*GameData\s+specs\s*[—-]\s*(?<total>\d+)\s+across\s+(?<categories>\d+)\s+categories"),
            GameDataCount("total"),
            GameDataCategories()),

        new(
            "README.md project structure tree",
            "README.md",
            "the 'specs/' comment in the project structure tree",
            Pattern(@"specs/\s*#\s*(?<total>\d+)\s+YAML\s+spec\s+files\s*\(\s*(?<roster>\d+)\s+roster\s*\+\s*(?<gamedata>\d+)\s+gamedata\s*\)"),
            TotalCount(),
            RosterCount("roster"),
            GameDataCount("gamedata")),

        new(
            "AGENTS.md roster key-files row",
            "AGENTS.md",
            "the specs/roster/ row of the key files table",
            Pattern(@"Roster\s+spec\s+files\s*\(\s*(?<total>\d+)\s+total,\s*(?<categories>\d+)\s+categories\s*\)"),
            RosterCount("total"),
            RosterCategories()),

        new(
            "AGENTS.md gamedata key-files row",
            "AGENTS.md",
            "the specs/gamedata/ row of the key files table",
            Pattern(@"GameData\s+spec\s+files\s*\(\s*(?<total>\d+)\s+total,\s*(?<categories>\d+)\s+categories\s*\)"),
            GameDataCount("total"),
            GameDataCategories()),

        // This doc describes the NR *roster* engine's data loading path (loadSystemFromFs, used by
        // NewRecruitRosterEngine). GameData specs drive the NR Editor instead, so the claim is
        // deliberately scoped to roster specs and is checked against the roster count only.
        new(
            "docs/nr-synthetic-data-loading.md coverage claim",
            "docs/nr-synthetic-data-loading.md",
            "the 'all N roster specs run through this path' claim",
            Pattern(@"all\s+(?<total>\d+)\s+roster\s+specs\s+run\s+through\s+this\s+path"),
            RosterCount("total")),
    ];

    public static TheoryData<string> AllClaims()
    {
        var data = new TheoryData<string>();
        foreach (var claim in DocumentedClaims)
        {
            data.Add(claim.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllClaims))]
    public void DocumentedCountMatchesDisk(string claimName)
    {
        var claim = DocumentedClaims.Single(c => string.Equals(c.Name, claimName, StringComparison.Ordinal));
        var text = File.ReadAllText(Path.Combine(RepoRoot, claim.RelativePath));
        var match = claim.Pattern.Match(text);

        Assert.True(match.Success,
            $"{claim.RelativePath}: could not find {claim.Describes}.\n" +
            $"  This guard matches on wording rather than line numbers, and looks for:\n" +
            $"    {claim.Pattern}\n" +
            $"  If that text was intentionally reworded or moved, update the '{claim.Name}' claim in " +
            $"{ThisFile} so it matches the new wording. Don't just delete the claim — it is what stops " +
            "the documented spec counts from going stale again.\n" +
            $"  On disk right now: {DiskTruth}.");

        var line = text.Take(match.Index).Count(c => c == '\n') + 1;
        var violations = new List<string>();

        foreach (var number in claim.Numbers)
        {
            var documented = int.Parse(match.Groups[number.Group].Value, CultureInfo.InvariantCulture);
            var actual = number.Actual();
            if (documented != actual)
            {
                violations.Add(
                    $"{number.Means}: documented {documented}, actual {actual} — replace {documented} with {actual}");
            }
        }

        Assert.True(violations.Count == 0,
            $"{claim.RelativePath} line {line} quotes stale spec counts in {claim.Describes}:\n" +
            $"  {string.Join("\n  ", violations)}\n" +
            $"  The text to edit reads: \"{Collapse(match.Value)}\"\n" +
            $"  On disk right now: {DiskTruth}.\n" +
            $"  Every documented count is asserted by {ThisFile}; if one is stale the others usually are too.");
    }

    // ── README per-category tables ───────────────────────────────────

    [Theory]
    [InlineData("roster", "Roster specs")]
    [InlineData("gamedata", "GameData specs")]
    public void ReadmeCategoryTableMatchesDisk(string domain, string heading)
    {
        var inventory = string.Equals(domain, "roster", StringComparison.Ordinal) ? Roster.Value : GameData.Value;
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, "README.md"));

        var headingIndex = Array.FindIndex(lines, l =>
            l.TrimStart().StartsWith('#') && l.Contains(heading, StringComparison.OrdinalIgnoreCase));

        Assert.True(headingIndex >= 0,
            $"README.md: could not find a '{heading}' heading, so the {domain} category table could not " +
            $"be located. If the Spec Coverage section was restructured, update {ThisFile} to find it " +
            $"again.\n  On disk right now: {DiskTruth}.");

        var documented = ParseCategoryTable(lines, headingIndex);

        Assert.True(documented.Count > 0,
            $"README.md: found the '{heading}' heading on line {headingIndex + 1}, but no markdown table " +
            $"of '| category | count | description |' rows beneath it. Expected {inventory.CategoryCount} " +
            $"rows, one per directory under specs/{domain}/.");

        var violations = new List<string>();

        foreach (var (category, actual) in inventory.ByCategory.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!documented.TryGetValue(category, out var claimed))
            {
                violations.Add(
                    $"no row for '{category}' — specs/{domain}/{category}/ exists and holds {actual} spec(s); " +
                    $"add a row: | {category} | {actual} | <what these specs cover> |");
            }
            else if (claimed != actual)
            {
                violations.Add(
                    $"'{category}' row: documented {claimed}, actual {actual} — replace {claimed} with {actual}");
            }
        }

        foreach (var (category, claimed) in documented.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!inventory.ByCategory.ContainsKey(category))
            {
                violations.Add(
                    $"stale row for '{category}' (documented {claimed}) — there is no " +
                    $"specs/{domain}/{category}/ directory; delete that row");
            }
        }

        Assert.True(violations.Count == 0,
            $"README.md's '{heading}' category table is out of sync with specs/{domain}/:\n" +
            $"  {string.Join("\n  ", violations)}\n" +
            $"  Remember the heading's own totals too — {inventory.Summary}.\n" +
            $"  Table read from line {headingIndex + 1} onwards: {documented.Count} row(s) found, " +
            $"{inventory.CategoryCount} expected.");
    }

    /// <summary>
    /// Reads the first markdown table after <paramref name="headingIndex"/> and returns
    /// category → count from its first two columns. The header and <c>|---|</c> separator rows are
    /// skipped, and every cell is trimmed, so column padding and alignment changes are harmless.
    /// </summary>
    private static Dictionary<string, int> ParseCategoryTable(string[] lines, int headingIndex)
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        var started = false;

        for (var i = headingIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (!line.StartsWith('|'))
            {
                // The table ends at the first non-table line after it, and it must belong to this
                // section — stop at the next heading if we never found one.
                if (started || line.StartsWith('#'))
                {
                    break;
                }

                continue;
            }

            started = true;

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2)
            {
                continue;
            }

            // Skips the header row ("Specs") and the |---|---:|---| separator row alike.
            if (!int.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }

            rows[cells[0]] = count;
        }

        return rows;
    }

    private static string Collapse(string text)
    {
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
