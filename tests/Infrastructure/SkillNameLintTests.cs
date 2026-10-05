namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>Every agent skill under <c>.agents/skills/</c> carries a <c>SKILL.md</c> whose front matter names it
/// for its directory.</b> The skill format requires it, and a loader that keys skills by name finds a
/// different one, or none.
/// </summary>
[Trait("Category", "Lint")]
public sealed class SkillNameLintTests
{
    [Fact]
    public void EverySkill_HasASkillMd_NamedForItsDirectory()
    {
        var root = Path.Combine(TestPaths.Root, ".agents", "skills");
        var skills = Directory.EnumerateDirectories(root).Select(static d => Path.GetFileName(d)).Order(StringComparer.Ordinal).ToList();
        Assert.True(skills.Count > 0, $"Found no skill directory under {root}; the skills moved, or this lint reads the wrong place.");

        var problems = new List<string>();
        foreach (var skill in skills)
        {
            var file = Path.Combine(root, skill, "SKILL.md");
            if (!File.Exists(file))
            {
                problems.Add($"  .agents/skills/{skill}/: no SKILL.md");
                continue;
            }

            var lines = File.ReadAllLines(file);
            var close = lines.Length > 0 && lines[0] == "---" ? Array.IndexOf(lines, "---", 1) : -1;
            var name = close < 0 ? null : lines[1..close].FirstOrDefault(static l => l.StartsWith("name:", StringComparison.Ordinal))?["name:".Length..].Trim();
            if (name != skill)
            {
                problems.Add($"  .agents/skills/{skill}/SKILL.md: front-matter name is {(name is null ? "missing" : $"'{name}'")}; it must be '{skill}'");
            }
        }

        Assert.True(problems.Count == 0, "Skills that do not carry the name of their directory:\n" + string.Join("\n", problems));
    }
}
