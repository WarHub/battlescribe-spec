using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests for GameData spec YAML files — validates formatting, required fields,
/// and conventions across the entire GameData spec suite.
/// Parallel to <see cref="SpecLintTests"/> for roster specs.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameDataSpecLintTests
{
    private static readonly string? SpecsDir = SpecLoader.FindGameDataSpecsDirectory();

    private static IEnumerable<(string path, string relPath, GameDataSpecFile spec)> AllSpecFiles()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            yield break;
        }

        foreach (var (path, _, _) in SpecLoader.DiscoverGameDataSpecs(SpecsDir))
        {
            var spec = SpecLoader.LoadGameData(path);
            var relPath = Path.GetRelativePath(SpecsDir, path).Replace('\\', '/');
            yield return (path, relPath, spec);
        }
    }

    public static IEnumerable<object[]> AllSpecs() =>
        AllSpecFiles().Select(x => new object[] { x.path, x.relPath });

    // ── Required fields ──────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void HasRequiredFields(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);
        Assert.False(string.IsNullOrWhiteSpace(spec.Id), $"{specName}: missing 'id'");
        Assert.False(string.IsNullOrWhiteSpace(spec.Category), $"{specName}: missing 'category'");
        Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{specName}: missing 'description'");
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void IdMatchesFilename(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);
        var filename = Path.GetFileNameWithoutExtension(specPath);
        Assert.True(filename == spec.Id,
            $"{specName}: expected id '{filename}' but got '{spec.Id}'");
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void CategoryMatchesDirectory(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);
        var dirName = Path.GetFileName(Path.GetDirectoryName(specPath));
        Assert.True(dirName == spec.Category,
            $"{specName}: expected category '{dirName}' but got '{spec.Category}'");
    }

    // ── No duplicate IDs ─────────────────────────────────────────────

    [Fact]
    public void NoDuplicateSpecIds()
    {
        var allFiles = AllSpecFiles().ToList();
        var duplicates = allFiles
            .GroupBy(x => x.spec.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' in: {string.Join(", ", g.Select(x => x.relPath))}")
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"Duplicate GameData spec IDs found:\n  {string.Join("\n  ", duplicates)}");
    }

    // ── Valid actions ─────────────────────────────────────────────────

    private static readonly HashSet<string> KnownActions =
    [
        "addEntry", "removeEntry", "moveEntry",
        "setField", "addLink",
        "dump"
    ];

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void ActionsAreKnown(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);
        foreach (var step in spec.Steps)
        {
            if (step.Action is null)
            {
                continue;
            }
            Assert.True(KnownActions.Contains(step.Action),
                $"{specName}: unknown action '{step.Action}'");
        }
    }

    // ── Steps have either action or expectedState ────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void StepsAreActionOrExpectedState(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            var hasAction = step.Action is not null;
            var hasExpected = step.ExpectedState is not null;
            Assert.True(hasAction || hasExpected,
                $"{specName}: step {i + 1} has neither 'action' nor 'expectedState'");
            Assert.False(hasAction && hasExpected,
                $"{specName}: step {i + 1} has both 'action' and 'expectedState'");
        }
    }

    // ── Formatting: blank lines between steps ────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void BlankLineBetweenSteps(string specPath, string specName)
    {
        var lines = File.ReadAllLines(specPath);
        var inSteps = false;
        var violations = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var stripped = lines[i].Trim();
            if (stripped == "steps:")
            {
                inSteps = true;
                continue;
            }
            if (!inSteps)
            {
                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^  - (action|expectedState):"))
            {
                if (i > 0)
                {
                    var prev = lines[i - 1].Trim();
                    if (prev != "" && prev != "steps:" && !prev.StartsWith('#'))
                    {
                        violations.Add($"line {i + 1}: missing blank line before '{stripped[..Math.Min(40, stripped.Length)]}'");
                    }
                }
            }
        }
        Assert.True(violations.Count == 0,
            $"{specName}: missing blank lines between steps:\n  {string.Join("\n  ", violations)}");
    }

    // ── Setup is required ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void SetupHasGameSystem(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);
        Assert.True(spec.Setup?.GameSystem is not null,
            $"{specName}: setup.gameSystem is required");
    }

    // ── Action parameter validation ──────────────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void ActionParametersAreValid(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);
        var errors = new List<string>();

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            if (step.Action is null)
            {
                continue;
            }

            switch (step.Action)
            {
                case "addEntry":
                    if (step.ParentId is null)
                    {
                        errors.Add($"step {i + 1}: addEntry requires 'parentId'");
                    }

                    if (step.EntryType is null)
                    {
                        errors.Add($"step {i + 1}: addEntry requires 'entryType'");
                    }

                    break;
                case "removeEntry":
                    if (step.EntryId is null)
                    {
                        errors.Add($"step {i + 1}: removeEntry requires 'entryId'");
                    }

                    break;
                case "moveEntry":
                    if (step.EntryId is null)
                    {
                        errors.Add($"step {i + 1}: moveEntry requires 'entryId'");
                    }

                    if (step.NewParentId is null)
                    {
                        errors.Add($"step {i + 1}: moveEntry requires 'newParentId'");
                    }

                    break;
                case "setField":
                    if (step.EntryId is null)
                    {
                        errors.Add($"step {i + 1}: setField requires 'entryId'");
                    }

                    if (step.Field is null)
                    {
                        errors.Add($"step {i + 1}: setField requires 'field'");
                    }

                    break;
                case "addLink":
                    if (step.ParentId is null)
                    {
                        errors.Add($"step {i + 1}: addLink requires 'parentId'");
                    }

                    if (step.LinkType is null)
                    {
                        errors.Add($"step {i + 1}: addLink requires 'linkType'");
                    }

                    if (step.TargetId is null)
                    {
                        errors.Add($"step {i + 1}: addLink requires 'targetId'");
                    }

                    break;
            }
        }

        Assert.True(errors.Count == 0,
            $"{specName}: action parameter errors:\n  {string.Join("\n  ", errors)}");
    }
}
