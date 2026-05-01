using System.Text.RegularExpressions;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests for spec YAML files — validates formatting, required fields,
/// and conventions across the entire spec suite.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecLintTests
{
    private static readonly string? SpecsDir = SpecLoader.FindRosterSpecsDirectory();

    private static IEnumerable<(string path, string relPath, SpecFile spec)> AllSpecFiles()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            yield break;
        }

        foreach (var (path, _, _) in SpecLoader.DiscoverSpecs(SpecsDir))
        {
            var spec = SpecLoader.Load(path);
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
        var spec = SpecLoader.Load(specPath);
        Assert.False(string.IsNullOrWhiteSpace(spec.Id), $"{specName}: missing 'id'");
        Assert.False(string.IsNullOrWhiteSpace(spec.Category), $"{specName}: missing 'category'");
        Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{specName}: missing 'description'");
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void IdMatchesFilename(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        var filename = Path.GetFileNameWithoutExtension(specPath);
        Assert.True(filename == spec.Id,
            $"{specName}: expected id '{filename}' but got '{spec.Id}'");
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void CategoryMatchesDirectory(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
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
            $"Duplicate spec IDs found:\n  {string.Join("\n  ", duplicates)}");
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

            // Top-level step item (2-space indent + "- ")
            if (Regex.IsMatch(lines[i], @"^  - (action|expectedState):"))
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

    // ── Formatting: blank line between header and setup ──────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void BlankLineBeforeSetup(string specPath, string specName)
    {
        var lines = File.ReadAllLines(specPath);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "setup:")
            {
                Assert.True(i > 0 && lines[i - 1].Trim() == "",
                    $"{specName}: line {i + 1}: expected blank line before 'setup:'");
                break;
            }
        }
    }

    // ── Valid actions ─────────────────────────────────────────────────

    // ── addChildForce is a known action ─────────────────────────────
    // (added as part of the ID-based protocol redesign)

    private static readonly HashSet<string> KnownActions =
    [
        "addForce", "addChildForce", "removeForce",
        "selectEntry", "selectChildEntry",
        "deselectSelection", "setSelectionCount",
        "duplicateSelection", "duplicateForce", "setCostLimit",
        "setCustomization",
        "dump"
    ];

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void ActionsAreKnown(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Steps is null)
        {
            return;
        }

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

    // ── Valid tags ────────────────────────────────────────────────────

    private static readonly HashSet<string> KnownTags =
    [
        // Engine difference classification
        "battlescribe-bug", "newrecruit-bug",
        "newrecruit-missing-feature", "design-difference",
        "undefined-behavior", "engine-limitation",
        // Feature tags
        "auto-select", "constraint", "min", "max",
        "field-forces", "nested", "cost",
        "defaultCostLimit", "multi-type", "default",
        "wh40k-10e", "real-world", "space-marines",
        "profile", "selection", "number",
        "defaultSelectionEntryId", "entryLink",
        "edge-case",
        "duplicate-ids",
        "invalid-data",
        "validation-errors", "structured-errors",
        "modifierGroup", "infoGroup", "infoLink",
        "forceEntry", "categoryEntry", "categoryLink",
        "childForce",
        "publication", "rule", "catalogue",
        "costType", "profileType",
        "deep-nesting",
        "entry-id", "entryGroup",
    ];

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void TagsAreKnown(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Tags is null)
        {
            return;
        }

        var unknown = spec.Tags.Where(t => !KnownTags.Contains(t)).ToList();
        Assert.True(unknown.Count == 0,
            $"{specName}: unknown tag(s): {string.Join(", ", unknown.Select(t => $"'{t}'"))}. " +
            $"Add to KnownTags in SpecLintTests if intentional.");
    }

    // ── Valid engine expectation values ───────────────────────────────

    private static readonly HashSet<string> KnownExpectations = ["pass", "fail", "skip"];

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void EngineExpectationsAreValid(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Engines is null)
        {
            return;
        }

        foreach (var (engine, expectation) in spec.Engines)
        {
            Assert.True(KnownExpectations.Contains(expectation),
                $"{specName}: engine '{engine}' has invalid expectation '{expectation}' " +
                $"(expected: {string.Join(", ", KnownExpectations)})");
        }
    }

    // ── Steps have either action or expectedState (not both, not neither) ──

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void StepsAreActionOrExpectedState(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Steps is null)
        {
            return;
        }

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

    // ── setSelectionCount must specify selectionId ─────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void SetSelectionCountHasSelectionId(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Steps is null)
        {
            return;
        }

        var violations = new List<string>();
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            if (step.Action != "setSelectionCount")
            {
                continue;
            }

            if (step.SelectionId is null or { Length: 0 })
            {
                violations.Add($"step {i + 1}: setSelectionCount requires selectionId");
            }
        }
        Assert.True(violations.Count == 0,
            $"{specName}: setSelectionCount issues:\n  {string.Join("\n  ", violations)}");
    }

    // ── addForce/addChildForce require catalogueId when multi-catalogue ──

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void AddForceRequiresCatalogueIdWhenMultiCatalogue(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        var catalogueCount = spec.Setup.Catalogues?.Count ?? 0;
        // For DataSource specs, catalogueId is always required (protocol can't auto-resolve)
        var isDataSource = spec.Setup.DataSource is { Length: > 0 };
        if (catalogueCount < 2 && !isDataSource)
        {
            return;
        }

        if (spec.Steps is null)
        {
            return;
        }

        var violations = new List<string>();
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            if (step.Action is not ("addForce" or "addChildForce"))
            {
                continue;
            }

            if (step.CatalogueId is null or { Length: 0 })
            {
                var reason = isDataSource
                    ? "dataSource specs always require catalogueId"
                    : $"setup has {catalogueCount} catalogues";
                violations.Add($"step {i + 1}: {step.Action} requires catalogueId ({reason})");
            }
        }
        Assert.True(violations.Count == 0,
            $"{specName}: missing catalogueId on force actions:\n  {string.Join("\n  ", violations)}");
    }

    // ── No trailing whitespace ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void NoTrailingWhitespace(string specPath, string specName)
    {
        var lines = File.ReadAllLines(specPath);
        var violations = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && lines[i] != lines[i].TrimEnd())
            {
                violations.Add($"line {i + 1}");
            }
        }
        Assert.True(violations.Count == 0,
            $"{specName}: trailing whitespace on {string.Join(", ", violations)}");
    }

    // ── File ends with newline ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void FileEndsWithNewline(string specPath, string specName)
    {
        var text = File.ReadAllText(specPath);
        Assert.True(text.EndsWith('\n'),
            $"{specName}: file does not end with a newline");
    }

    // ── No explicit defaults in setup ────────────────────────────────

    private static readonly (string Pattern, string Description)[] DefaultValuePatterns =
    [
        ("primary: false", "primary defaults to false"),
        ("defaultCostLimit: -1", "defaultCostLimit defaults to -1"),
        ("import: true", "import defaults to true"),
        ("importRootEntries: true", "importRootEntries defaults to true"),
    ];

    // Patterns that are redundant everywhere (setup AND expectedState).
    private static readonly (string Pattern, string Description)[] GlobalDefaultPatterns =
    [
        ("hidden: false", "hidden defaults to false"),
    ];

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void NoExplicitDefaultsInSetup(string specPath, string specName)
    {
        var lines = File.ReadAllLines(specPath);
        var inExpectedState = false;
        var violations = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var stripped = lines[i].Trim();
            if (stripped.StartsWith("- expectedState:", StringComparison.Ordinal))
            {
                inExpectedState = true;
            }
            else if (stripped.StartsWith("- action:", StringComparison.Ordinal))
            {
                inExpectedState = false;
            }

            // Global defaults are redundant everywhere.
            foreach (var (pattern, description) in GlobalDefaultPatterns)
            {
                if (stripped == pattern)
                {
                    violations.Add($"line {i + 1}: '{pattern}' ({description})");
                }
            }

            if (inExpectedState)
            {
                continue;
            }

            // Setup-only defaults.
            foreach (var (pattern, description) in DefaultValuePatterns)
            {
                if (stripped == pattern)
                {
                    violations.Add($"line {i + 1}: '{pattern}' ({description})");
                }
            }
        }
        Assert.True(violations.Count == 0,
            $"{specName}: explicit default values (omit them):\n  {string.Join("\n  ", violations)}");
    }
}
