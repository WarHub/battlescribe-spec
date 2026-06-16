using System.Text.RegularExpressions;
using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests for GameData spec YAML files — validates formatting, required fields,
/// and conventions across the entire GameData spec suite.
///
/// Each spec file is loaded exactly once per test run. All per-spec rules are
/// aggregated into a single <see cref="AllLintChecks"/> theory so a failing spec
/// reports all its violations in one message. Cross-spec checks remain separate
/// <see cref="FactAttribute"/> methods.
/// </summary>
[Trait("Category", "Lint")]
public sealed class GameDataSpecLintTests
{
    private static readonly string? SpecsDir = SpecLoader.FindGameDataSpecsDirectory();

    private sealed record SpecEntry(string Path, string RelPath, GameDataSpecFile? Spec, string? LoadError);

    // File discovery only — no YAML parsing
    private static IEnumerable<(string path, string relPath)> DiscoverSpecFiles()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            yield break;
        }

        foreach (var (path, _, _) in SpecLoader.DiscoverGameDataSpecs(SpecsDir))
        {
            yield return (path, Path.GetRelativePath(SpecsDir, path).Replace('\\', '/'));
        }
    }

    // Load helper: parse YAML and capture any error without throwing
    private static SpecEntry TryLoadSpec(string path, string relPath)
    {
        try
        {
            return new SpecEntry(path, relPath, SpecLoader.LoadGameData(path), null);
        }
        catch (Exception ex)
        {
            return new SpecEntry(path, relPath, null, ex.Message);
        }
    }

    // All specs loaded exactly once per test session
    private static readonly Lazy<IReadOnlyList<SpecEntry>> AllSpecsLazy =
        new(() => [.. DiscoverSpecFiles().Select(x => TryLoadSpec(x.path, x.relPath))]);

    // O(1) per-path lookup for AllLintChecks
    private static readonly Lazy<Dictionary<string, SpecEntry>> SpecsByPath =
        new(() => AllSpecsLazy.Value.ToDictionary(x => x.Path));

    public static IEnumerable<object[]> AllSpecs() =>
        DiscoverSpecFiles().Select(x => new object[] { x.path, x.relPath });

    // ── Single aggregated lint check per spec ────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void AllLintChecks(string specPath, string specName)
    {
        var lines = File.ReadAllLines(specPath);

        var violations = new List<string>();
        violations.AddRange(CheckBlankLineBetweenSteps(lines));

        // Look up the cached spec (loaded once per test session)
        var entry = SpecsByPath.Value[specPath];

        if (entry.LoadError is not null)
        {
            violations.Add($"Failed to load spec: {entry.LoadError}");
        }

        if (entry.Spec is not null)
        {
            var filename = Path.GetFileNameWithoutExtension(specPath);
            var dirName = Path.GetFileName(Path.GetDirectoryName(specPath));

            violations.AddRange(CheckRequiredFields(entry.Spec));
            violations.AddRange(CheckIdMatchesFilename(entry.Spec, filename));
            violations.AddRange(CheckCategoryMatchesDirectory(entry.Spec, dirName!));
            violations.AddRange(CheckKnownActions(entry.Spec));
            violations.AddRange(CheckStepsAreActionOrExpectedState(entry.Spec));
            violations.AddRange(CheckSetupHasGameSystem(entry.Spec));
            violations.AddRange(CheckActionParameters(entry.Spec));
        }

        Assert.True(violations.Count == 0,
            $"{specName}:\n  {string.Join("\n  ", violations)}");
    }

    // ── No duplicate IDs (cross-spec check) ─────────────────────────

    [Fact]
    public void NoDuplicateSpecIds()
    {
        var duplicates = AllSpecsLazy.Value
            .Where(x => x.Spec is not null)
            .GroupBy(x => x.Spec!.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' in: {string.Join(", ", g.Select(x => x.RelPath))}")
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"Duplicate GameData spec IDs found:\n  {string.Join("\n  ", duplicates)}");
    }

    // ── Formatting: blank lines between steps ────────────────────────

    private static IEnumerable<string> CheckBlankLineBetweenSteps(string[] lines)
    {
        var inSteps = false;
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

            if (Regex.IsMatch(lines[i], @"^  - (action|expectedState):") && i > 0)
            {
                var prev = lines[i - 1].Trim();
                if (prev != "" && prev != "steps:" && !prev.StartsWith('#'))
                {
                    yield return $"line {i + 1}: missing blank line before '{stripped[..Math.Min(40, stripped.Length)]}'";
                }
            }
        }
    }

    // ── Required fields ──────────────────────────────────────────────

    private static IEnumerable<string> CheckRequiredFields(GameDataSpecFile spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            yield return "missing 'id'";
        }

        if (string.IsNullOrWhiteSpace(spec.Category))
        {
            yield return "missing 'category'";
        }

        if (string.IsNullOrWhiteSpace(spec.Description))
        {
            yield return "missing 'description'";
        }
    }

    private static IEnumerable<string> CheckIdMatchesFilename(GameDataSpecFile spec, string filename)
    {
        if (filename != spec.Id)
        {
            yield return $"expected id '{filename}' but got '{spec.Id}'";
        }
    }

    private static IEnumerable<string> CheckCategoryMatchesDirectory(GameDataSpecFile spec, string dirName)
    {
        if (dirName != spec.Category)
        {
            yield return $"expected category '{dirName}' but got '{spec.Category}'";
        }
    }

    // ── Valid actions ─────────────────────────────────────────────────

    private static readonly HashSet<string> KnownActions =
    [
        "addEntry", "removeEntry",
        "setField", "addLink",
        "dump"
    ];

    private static IEnumerable<string> CheckKnownActions(GameDataSpecFile spec)
    {
        foreach (var step in spec.Steps)
        {
            if (step.Action is { } action && !KnownActions.Contains(action))
            {
                yield return $"unknown action '{action}'";
            }
        }
    }

    // ── Steps have action or expectedState ───────────────────────────

    private static IEnumerable<string> CheckStepsAreActionOrExpectedState(GameDataSpecFile spec)
    {
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            var hasAction = step.Action is not null;
            var hasExpected = step.ExpectedState is not null;
            if (!hasAction && !hasExpected)
            {
                yield return $"step {i + 1} has neither 'action' nor 'expectedState'";
            }

            if (hasAction && hasExpected)
            {
                yield return $"step {i + 1} has both 'action' and 'expectedState'";
            }
        }
    }

    // ── Setup has gameSystem ─────────────────────────────────────────

    private static IEnumerable<string> CheckSetupHasGameSystem(GameDataSpecFile spec)
    {
        if (spec.Setup?.GameSystem is null)
        {
            yield return "setup.gameSystem is required";
        }
    }

    // ── Action parameter validation ──────────────────────────────────

    private static IEnumerable<string> CheckActionParameters(GameDataSpecFile spec)
    {
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
                        yield return $"step {i + 1}: addEntry requires 'parentId'";
                    }

                    if (step.EntryType is null)
                    {
                        yield return $"step {i + 1}: addEntry requires 'entryType'";
                    }

                    break;
                case "removeEntry":
                    if (step.EntryId is null)
                    {
                        yield return $"step {i + 1}: removeEntry requires 'entryId'";
                    }

                    break;
                case "setField":
                    if (step.EntryId is null)
                    {
                        yield return $"step {i + 1}: setField requires 'entryId'";
                    }

                    if (step.Field is null)
                    {
                        yield return $"step {i + 1}: setField requires 'field'";
                    }

                    break;
                case "addLink":
                    if (step.ParentId is null)
                    {
                        yield return $"step {i + 1}: addLink requires 'parentId'";
                    }

                    if (step.LinkType is null)
                    {
                        yield return $"step {i + 1}: addLink requires 'linkType'";
                    }

                    if (step.TargetId is null)
                    {
                        yield return $"step {i + 1}: addLink requires 'targetId'";
                    }

                    break;
            }
        }
    }
}
