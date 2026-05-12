using System.Text.RegularExpressions;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests for spec YAML files — validates formatting, required fields,
/// and conventions across the entire spec suite.
///
/// Each spec file is loaded exactly once per test run. All per-spec rules are
/// aggregated into a single <see cref="AllLintChecks"/> theory, so a failing spec
/// reports all its violations in one message. Cross-spec checks (e.g. duplicate IDs)
/// remain separate <see cref="FactAttribute"/> methods.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecLintTests
{
    private static readonly string? SpecsDir = SpecLoader.FindRosterSpecsDirectory();

    private sealed record SpecEntry(string Path, string RelPath, SpecFile? Spec, string? LoadError);

    // File discovery only — no YAML parsing
    private static IEnumerable<(string path, string relPath)> DiscoverSpecFiles()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            yield break;
        }

        foreach (var (path, _, _) in SpecLoader.DiscoverSpecs(SpecsDir))
        {
            yield return (path, Path.GetRelativePath(SpecsDir, path).Replace('\\', '/'));
        }
    }

    // Load helper: parse YAML and capture any error without throwing
    private static SpecEntry TryLoadSpec(string path, string relPath)
    {
        try
        {
            return new SpecEntry(path, relPath, SpecLoader.Load(path), null);
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
        var text = File.ReadAllText(specPath);
        var lines = File.ReadAllLines(specPath);

        // Run text-only checks first (don't need a successfully loaded model)
        var violations = new List<string>();
        violations.AddRange(CheckFormatting(text));
        violations.AddRange(CheckTrailingWhitespace(lines));
        violations.AddRange(CheckFileEndsWithNewline(text));
        violations.AddRange(CheckBlankLineBeforeSetup(lines));
        violations.AddRange(CheckBlankLineBetweenSteps(lines));
        violations.AddRange(CheckNoEmptyEnginesDeclaration(lines));
        violations.AddRange(CheckNoExplicitDefaults(lines));
        violations.AddRange(CheckExpectedStatePropertyOrdering(lines));
        violations.AddRange(CheckNoLegacyAssertSteps(text));
        violations.AddRange(CheckNoLegacyErrorFields(text));
        violations.AddRange(CheckNoEmptyTagFields(lines));

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
            violations.AddRange(CheckKnownTags(entry.Spec));
            violations.AddRange(CheckEngineExpectations(entry.Spec));
            violations.AddRange(CheckStepsAreActionOrExpectedState(entry.Spec));
            violations.AddRange(CheckSetSelectionCountHasSelectionId(entry.Spec));
            violations.AddRange(CheckAddForceRequiresCatalogueIdWhenMultiCatalogue(entry.Spec));
            violations.AddRange(CheckEverySpecHasSetup(entry.Spec));
            violations.AddRange(CheckLastStepIsExpectedState(entry.Spec));
            violations.AddRange(CheckAllErrorAssertionsHaveFrom(entry.Spec));
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
            $"Duplicate spec IDs found:\n  {string.Join("\n  ", duplicates)}");
    }

    // ── Formatting ───────────────────────────────────────────────────

    private static IEnumerable<string> CheckFormatting(string text)
    {
        // Normalize CRLF → LF before comparing: on Windows with autocrlf=true,
        // checked-out files have CRLF but the formatter (and repository) uses LF.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var formatted = SpecFormatter.FormatText(normalized);
        if (formatted != normalized)
        {
            yield return "file is not correctly formatted — run 'pwsh tools/format-specs.ps1' to fix";
        }
    }

    // ── Trailing whitespace ──────────────────────────────────────────

    private static IEnumerable<string> CheckTrailingWhitespace(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && lines[i] != lines[i].TrimEnd())
            {
                yield return $"line {i + 1}: trailing whitespace";
            }
        }
    }

    // ── File ends with newline ───────────────────────────────────────

    private static IEnumerable<string> CheckFileEndsWithNewline(string text)
    {
        if (!text.EndsWith('\n'))
        {
            yield return "file does not end with a newline";
        }
    }

    // ── Blank line before setup: ─────────────────────────────────────

    private static IEnumerable<string> CheckBlankLineBeforeSetup(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "setup:")
            {
                if (i == 0 || lines[i - 1].Trim() != "")
                {
                    yield return $"line {i + 1}: expected blank line before 'setup:'";
                }
                break;
            }
        }
    }

    // ── Blank lines between steps ────────────────────────────────────

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

    // ── No empty engines declaration ─────────────────────────────────

    private static IEnumerable<string> CheckNoEmptyEnginesDeclaration(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "engines: {}")
            {
                yield return $"line {i + 1}: remove empty 'engines: {{}}' (omit the field instead)";
            }
        }
    }

    // ── No explicit defaults ─────────────────────────────────────────

    private static readonly (string Pattern, string Description)[] SetupDefaultPatterns =
    [
        ("primary: false", "primary defaults to false"),
        ("defaultCostLimit: -1", "defaultCostLimit defaults to -1"),
        ("import: true", "import defaults to true"),
        ("importRootEntries: true", "importRootEntries defaults to true"),
    ];

    private static readonly (string Pattern, string Description)[] GlobalDefaultPatterns =
    [
        ("hidden: false", "hidden defaults to false"),
    ];

    private static IEnumerable<string> CheckNoExplicitDefaults(string[] lines)
    {
        var inExpectedState = false;
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

            foreach (var (pattern, description) in GlobalDefaultPatterns)
            {
                if (stripped == pattern)
                {
                    yield return $"line {i + 1}: '{pattern}' ({description} — omit it)";
                }
            }

            if (!inExpectedState)
            {
                foreach (var (pattern, description) in SetupDefaultPatterns)
                {
                    if (stripped == pattern)
                    {
                        yield return $"line {i + 1}: '{pattern}' ({description} — omit it)";
                    }
                }
            }
        }
    }

    // ── expectedState property ordering ─────────────────────────────

    private static int GetPropertyZone(string prop) => prop switch
    {
        "errors" or "errorsContain" => 0,
        "forces" => 2,
        "engines" => 3,
        _ => 1,
    };

    private static IEnumerable<string> CheckExpectedStatePropertyOrdering(string[] lines)
    {
        var violations = new List<string>();
        var inExpected = false;
        var stepStart = -1;
        var stepProps = new List<(string Name, int Line, int Zone)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var stripped = lines[i].TrimEnd();

            if (Regex.IsMatch(stripped, @"^  - expectedState:"))
            {
                FlushStepProps(stepProps, stepStart, violations);
                inExpected = true;
                stepStart = i + 1;
                stepProps.Clear();
                continue;
            }
            if (Regex.IsMatch(stripped, @"^  - action:"))
            {
                FlushStepProps(stepProps, stepStart, violations);
                inExpected = false;
                stepProps.Clear();
                continue;
            }

            if (inExpected
                && Regex.IsMatch(stripped, @"^      \w+:")
                && !stripped.StartsWith("        ", StringComparison.Ordinal))
            {
                var prop = stripped.TrimStart()[..stripped.TrimStart().IndexOf(':')];
                stepProps.Add((prop, i + 1, GetPropertyZone(prop)));
            }
        }
        FlushStepProps(stepProps, stepStart, violations);
        return violations;

        static void FlushStepProps(List<(string Name, int Line, int Zone)> props, int stepLine, List<string> violations)
        {
            if (props.Count < 2)
            {
                return;
            }

            var maxZoneSoFar = -1;
            string? maxZoneProp = null;
            foreach (var (name, line, zone) in props)
            {
                if (zone < maxZoneSoFar)
                {
                    violations.Add(
                        $"line {line}: '{name}' (zone {zone}) must come before '{maxZoneProp}' " +
                        $"(zone {maxZoneSoFar}) — run format-specs.ps1 to fix (step at line {stepLine})");
                }
                if (zone > maxZoneSoFar)
                {
                    maxZoneSoFar = zone;
                    maxZoneProp = name;
                }
            }
        }
    }

    // ── No legacy assert steps ───────────────────────────────────────

    private static IEnumerable<string> CheckNoLegacyAssertSteps(string text)
    {
        if (Regex.IsMatch(text, @"^[ \t]*- assert:", RegexOptions.Multiline))
        {
            yield return "contains legacy 'assert:' step (use 'expectedState:' instead)";
        }
    }

    // ── No legacy error fields ───────────────────────────────────────

    private static readonly string[] LegacyErrorFields =
        ["validationErrors", "validationErrorCount", "hasValidationErrors", "noValidationErrors"];

    private static IEnumerable<string> CheckNoLegacyErrorFields(string text)
    {
        foreach (var field in LegacyErrorFields)
        {
            if (Regex.IsMatch(text, $@"^[ \t]+{field}:", RegexOptions.Multiline))
            {
                yield return $"contains legacy field '{field}:' (use 'errors:' instead)";
            }
        }
    }

    // ── No empty tags declarations ───────────────────────────────────

    private static IEnumerable<string> CheckNoEmptyTagFields(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsEmptyTagsDeclaration(lines, i))
            {
                yield return $"line {i + 1}: remove empty 'tags' field (omit the field instead)";
            }
        }
    }

    private static bool IsEmptyTagsDeclaration(string[] lines, int index)
    {
        var stripped = lines[index].Trim();
        if (stripped is "tags: []" or "tags: ~")
        {
            return true;
        }

        if (stripped != "tags:")
        {
            return false;
        }

        var currentIndent = GetIndentationWidth(lines[index]);
        for (var i = index + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            return GetIndentationWidth(lines[i]) <= currentIndent;
        }

        return true;
    }

    private static int GetIndentationWidth(string line) =>
        line.TakeWhile(ch => ch is ' ' or '\t').Count();

    // ── Required fields ──────────────────────────────────────────────

    private static IEnumerable<string> CheckRequiredFields(SpecFile spec)
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

    private static IEnumerable<string> CheckIdMatchesFilename(SpecFile spec, string filename)
    {
        if (filename != spec.Id)
        {
            yield return $"expected id '{filename}' but got '{spec.Id}'";
        }
    }

    private static IEnumerable<string> CheckCategoryMatchesDirectory(SpecFile spec, string dirName)
    {
        if (dirName != spec.Category)
        {
            yield return $"expected category '{dirName}' but got '{spec.Category}'";
        }
    }

    // ── Valid actions ─────────────────────────────────────────────────

    private static readonly HashSet<string> KnownActions =
    [
        "addForce", "addChildForce", "removeForce",
        "selectEntry", "selectChildEntry",
        "deselectSelection", "setSelectionCount",
        "duplicateSelection", "duplicateForce", "setCostLimit",
        "setCustomization",
        "dump"
    ];

    private static IEnumerable<string> CheckKnownActions(SpecFile spec)
    {
        if (spec.Steps is null)
        {
            yield break;
        }

        foreach (var step in spec.Steps)
        {
            if (step.Action is { } action && !KnownActions.Contains(action))
            {
                yield return $"unknown action '{action}'";
            }
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
        "collective", "deselect", "validation",
        "entry-group",
        "same-constraint-id",
    ];

    private static IEnumerable<string> CheckKnownTags(SpecFile spec)
    {
        if (spec.Tags is null)
        {
            yield break;
        }

        var unknown = spec.Tags.Where(t => !KnownTags.Contains(t)).ToList();
        if (unknown.Count > 0)
        {
            yield return $"unknown tag(s): {string.Join(", ", unknown.Select(t => $"'{t}'"))} " +
                         "(add to KnownTags in SpecLintTests if intentional)";
        }
    }

    // ── Valid engine expectations ─────────────────────────────────────

    private static readonly HashSet<string> KnownExpectations = ["pass", "fail", "skip"];

    private static IEnumerable<string> CheckEngineExpectations(SpecFile spec)
    {
        if (spec.Engines is null)
        {
            yield break;
        }

        foreach (var (engine, expectation) in spec.Engines)
        {
            if (!KnownExpectations.Contains(expectation))
            {
                yield return $"engine '{engine}' has invalid expectation '{expectation}' " +
                             $"(expected: {string.Join(", ", KnownExpectations)})";
            }
        }
    }

    // ── Steps have action or expectedState ───────────────────────────

    private static IEnumerable<string> CheckStepsAreActionOrExpectedState(SpecFile spec)
    {
        if (spec.Steps is null)
        {
            yield break;
        }

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

    // ── setSelectionCount requires selectionId ───────────────────────

    private static IEnumerable<string> CheckSetSelectionCountHasSelectionId(SpecFile spec)
    {
        if (spec.Steps is null)
        {
            yield break;
        }

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            if (step.Action == "setSelectionCount" && step.SelectionId is null or { Length: 0 })
            {
                yield return $"step {i + 1}: setSelectionCount requires 'selectionId'";
            }
        }
    }

    // ── addForce/addChildForce require catalogueId when multi-catalogue ──

    private static IEnumerable<string> CheckAddForceRequiresCatalogueIdWhenMultiCatalogue(SpecFile spec)
    {
        var catalogueCount = spec.Setup?.Catalogues?.Count ?? 0;
        var isDataSource = spec.Setup?.DataSource is { Length: > 0 };
        if (catalogueCount < 2 && !isDataSource)
        {
            yield break;
        }

        if (spec.Steps is null)
        {
            yield break;
        }

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            if (step.Action is not ("addForce" or "addChildForce"))
            {
                continue;
            }

            if (step.CatalogueId is null or { Length: 0 })
            {
                var reason = isDataSource ? "dataSource specs always require catalogueId"
                    : $"setup has {catalogueCount} catalogues";
                yield return $"step {i + 1}: {step.Action} requires 'catalogueId' ({reason})";
            }
        }
    }

    // ── Structure: setup required ────────────────────────────────────

    private static IEnumerable<string> CheckEverySpecHasSetup(SpecFile spec)
    {
        if (spec.Setup is null)
        {
            yield return "missing 'setup' section";
        }
    }

    // ── Structure: last step is expectedState ────────────────────────

    private static IEnumerable<string> CheckLastStepIsExpectedState(SpecFile spec)
    {
        if (spec.Steps is null)
        {
            yield return "missing 'steps' section";
            yield break;
        }
        if (spec.Steps.Count == 0)
        {
            yield return "'steps' is empty";
            yield break;
        }
        if (spec.Steps[^1].ExpectedState is null)
        {
            yield return "last step must be 'expectedState'";
        }
    }

    // ── Structure: error assertions have 'from' ──────────────────────

    private static IEnumerable<string> CheckAllErrorAssertionsHaveFrom(SpecFile spec)
    {
        if (spec.Steps is null)
        {
            yield break;
        }

        foreach (var step in spec.Steps)
        {
            if (step.ExpectedState?.Errors is not { } errors)
            {
                continue;
            }

            foreach (var err in errors)
            {
                if (string.IsNullOrEmpty(err.From))
                {
                    yield return $"error assertion on='{err.On}' is missing 'from:' field";
                }
            }

            if (step.ExpectedState.Engines is { } engines)
            {
                foreach (var (_, over) in engines)
                {
                    if (over.Errors is not { } overErrors)
                    {
                        continue;
                    }

                    foreach (var err in overErrors)
                    {
                        if (err.On is null)
                        {
                            yield return $"engine override error assertion is missing 'on:' field";
                        }
                    }
                }
            }
        }
    }
}
