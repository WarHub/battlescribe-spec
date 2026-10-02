using System.Text.RegularExpressions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The agent skills under <c>.agents/skills/</c> are what the <c>managing-skills</c> skill says they
/// are.</b> Each skill directory carries a <c>SKILL.md</c> whose <c>name</c> is the directory's, and the
/// inventory table in <c>managing-skills/SKILL.md</c> lists every skill and exactly its reference files.
/// </summary>
/// <remarks>
/// <para>
/// The inventory is the map an agent reads before it creates or updates a skill, and it went stale with
/// nothing to notice: it listed a <c>managing-backlog</c> reference, <c>ISSUE-HIERARCHY.md</c>, that does
/// not exist, and left out the one that does, <c>QUERYING-ISSUES.md</c> — while the same file said no CI
/// validation of skills existed at all, which had stopped being true when the documented-command lints
/// started reading them. A name that does not match its directory is the other silent failure: the skill
/// format requires it, and a loader that keys skills by name finds a different one, or none.
/// </para>
/// <para>
/// A reference file in a subfolder of <c>references/</c> is listed by its path below it
/// (<c>sub/X.md</c>), so a file cannot sit outside the inventory by moving one level down.
/// </para>
/// <para>
/// Mutation-checked when written: a reference file added to a skill without a row change (directly in
/// <c>references/</c>, and in a subfolder of it), a row naming a file that does not exist, a skill
/// directory with no inventory row, an inventory row with no directory, a skill directory without a
/// <c>SKILL.md</c>, an empty <c>SKILL.md</c>, the inventory heading renamed, a duplicate row, and a
/// <c>name:</c> that differs from its directory — each turns one of these red, naming the skill and
/// printing the row to write.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class SkillInventoryLintTests
{
    private static string SkillsRoot => Path.Combine(TestPaths.Root, ".agents", "skills");

    private static string InventoryFile => Path.Combine(SkillsRoot, "managing-skills", "SKILL.md");

    /// <summary>The heading of the inventory section; its table runs to the next <c>## </c> heading.</summary>
    private const string InventoryHeading = "## Existing skills inventory";

    /// <summary>A row of the inventory table: <c>| `name` | domain | A.md, B.md |</c>.</summary>
    private static readonly Regex InventoryRow = new(@"^\|\s*`(?<name>[^`]+)`\s*\|[^|]*\|(?<refs>[^|]*)\|\s*$", RegexOptions.CultureInvariant);

    private static List<string> SkillDirectories()
    {
        var skills = Directory.EnumerateDirectories(SkillsRoot)
            .Select(static d => Path.GetFileName(d))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(skills.Count > 0, $"Found no skill directory under {SkillsRoot}; the skills moved, or this lint reads the wrong place.");
        return skills;
    }

    /// <summary>
    /// <b>Every skill directory has a <c>SKILL.md</c> that opens with front matter naming it</b>: a
    /// <c>name</c> equal to the directory's, and a <c>description</c>.
    /// </summary>
    [Fact]
    public void EverySkill_HasASkillMd_NamedForItsDirectory()
    {
        var problems = new List<string>();
        foreach (var skill in SkillDirectories())
        {
            var file = Path.Combine(SkillsRoot, skill, "SKILL.md");
            if (!File.Exists(file))
            {
                problems.Add($"  .agents/skills/{skill}/: no SKILL.md");
                continue;
            }

            var lines = File.ReadAllLines(file);
            var close = lines.Length > 0 && lines[0] == "---" ? Array.IndexOf(lines, "---", 1) : -1;
            if (close < 0)
            {
                problems.Add($"  .agents/skills/{skill}/SKILL.md: does not open with front matter between two '---' lines");
                continue;
            }

            var frontMatter = lines[1..close];
            var name = frontMatter.FirstOrDefault(static l => l.StartsWith("name:", StringComparison.Ordinal))?["name:".Length..].Trim();
            if (name != skill)
            {
                problems.Add($"  .agents/skills/{skill}/SKILL.md: name is {(name is null ? "missing" : $"'{name}'")}; it must be '{skill}', the directory's");
            }

            if (!frontMatter.Any(static l => l.StartsWith("description:", StringComparison.Ordinal)))
            {
                problems.Add($"  .agents/skills/{skill}/SKILL.md: no description in its front matter");
            }
        }

        Assert.True(problems.Count == 0, "Skills that do not carry the name of their directory:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>The inventory table in <c>managing-skills/SKILL.md</c> lists every skill directory once, and each
    /// row names exactly the files in that skill's <c>references/</c></b> — none missing, none that do not
    /// exist.
    /// </summary>
    [Fact]
    public void TheSkillsInventory_ListsEverySkill_AndExactlyItsReferenceFiles()
    {
        var lines = File.ReadAllLines(InventoryFile);
        var start = Array.IndexOf(lines, InventoryHeading);
        Assert.True(start >= 0, $"{InventoryFile} has no '{InventoryHeading}' heading; the inventory moved, or this lint reads the wrong place.");
        var section = lines.Skip(start + 1).TakeWhile(static l => !l.StartsWith("## ", StringComparison.Ordinal));
        var rows = section
            .Select(static l => InventoryRow.Match(l))
            .Where(static m => m.Success)
            .Select(static m => (Name: m.Groups["name"].Value, References: m.Groups["refs"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static r => r is not ("—" or "-"))
                .Order(StringComparer.Ordinal)
                .ToList()))
            .ToList();
        Assert.True(rows.Count > 0, $"Found no row (| `name` | domain | references |) under '{InventoryHeading}' in {InventoryFile}; the table changed shape.");

        var problems = new List<string>();
        foreach (var duplicate in rows.GroupBy(static r => r.Name, StringComparer.Ordinal).Where(static g => g.Count() > 1))
        {
            problems.Add($"  `{duplicate.Key}` has {duplicate.Count()} rows; give it one");
        }

        var skills = SkillDirectories();
        foreach (var row in rows.Where(r => !skills.Contains(r.Name, StringComparer.Ordinal)))
        {
            problems.Add($"  `{row.Name}` has a row, and there is no .agents/skills/{row.Name}/; delete the row");
        }

        foreach (var skill in skills)
        {
            var referencesDir = Path.Combine(SkillsRoot, skill, "references");
            var actual = Directory.Exists(referencesDir)
                ? Directory.EnumerateFiles(referencesDir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(referencesDir, f).Replace(Path.DirectorySeparatorChar, '/'))
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : [];
            var listed = rows.Where(r => r.Name == skill).Select(static r => r.References).FirstOrDefault();
            var expectedCell = actual.Count == 0 ? "—" : string.Join(", ", actual);
            if (listed is null)
            {
                problems.Add($"  `{skill}` has no row; add one: | `{skill}` | <domain> | {expectedCell} |");
            }
            else if (!listed.SequenceEqual(actual, StringComparer.Ordinal))
            {
                problems.Add($"  `{skill}` lists {(listed.Count == 0 ? "no reference" : string.Join(", ", listed))}, and its references/ holds "
                    + $"{(actual.Count == 0 ? "nothing" : string.Join(", ", actual))}; its references cell should read: {expectedCell}");
            }
        }

        Assert.True(problems.Count == 0,
            "The skills inventory in .agents/skills/managing-skills/SKILL.md disagrees with .agents/skills/:\n"
            + string.Join("\n", problems));
    }
}
