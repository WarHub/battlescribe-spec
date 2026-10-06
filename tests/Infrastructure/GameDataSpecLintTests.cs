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

    private sealed record SpecEntry(string Path, string Name, string RelPath, GameDataSpecFile? Spec, string? LoadError);

    // File discovery only — no YAML parsing. Name is the spec's 'category/id', what its row carries.
    private static IEnumerable<(string path, string name, string relPath)> DiscoverSpecFiles()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            yield break;
        }

        foreach (var (path, id, category) in SpecLoader.DiscoverGameDataSpecs(SpecsDir))
        {
            yield return (path, SpecLoader.SpecName(category, id), Path.GetRelativePath(SpecsDir, path).Replace('\\', '/'));
        }
    }

    // Load helper: parse YAML and capture any error without throwing
    private static SpecEntry TryLoadSpec(string path, string name, string relPath)
    {
        try
        {
            return new SpecEntry(path, name, relPath, SpecLoader.LoadGameData(path), null);
        }
        catch (Exception ex)
        {
            return new SpecEntry(path, name, relPath, null, ex.Message);
        }
    }

    // All specs loaded exactly once per test session
    private static readonly Lazy<IReadOnlyList<SpecEntry>> AllSpecsLazy =
        new(() => [.. DiscoverSpecFiles().Select(x => TryLoadSpec(x.path, x.name, x.relPath))]);

    // O(1) per-name lookup for AllLintChecks
    private static readonly Lazy<Dictionary<string, SpecEntry>> SpecsByName =
        new(() => AllSpecsLazy.Value.ToDictionary(x => x.Name, StringComparer.Ordinal));

    /// <summary>
    /// One row per spec, carrying and labelled with its name (<c>category/id</c>), never its path —
    /// see <see cref="TheoryRowIdentityTests"/>.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string>> AllSpecs() =>
        DiscoverSpecFiles().Select(x => new TheoryDataRow<string>(x.name) { Label = x.name });

    // ── Single aggregated lint check per spec ────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void AllLintChecks(string specName)
    {
        // Look up the cached spec (loaded once per test session)
        var entry = SpecsByName.Value[specName];
        var violations = Lint(File.ReadAllText(entry.Path), Path.GetFileNameWithoutExtension(entry.Path),
            Path.GetFileName(Path.GetDirectoryName(entry.Path))!, entry.Spec, entry.LoadError);

        Assert.True(violations.Count == 0,
            $"{entry.RelPath}:\n  {string.Join("\n  ", violations)}");
    }

    /// <summary>
    /// Every per-spec check over one spec: its text, the file name and directory it must match, and the
    /// model it loaded to (or why it did not). <see cref="EveryBrokenSample_IsRejected"/> feeds it samples.
    /// </summary>
    private static List<string> Lint(string text, string filename, string dirName, GameDataSpecFile? spec, string? loadError)
    {
        var violations = new List<string>();
        violations.AddRange(CheckFormatting(text));

        if (loadError is not null)
        {
            violations.Add($"Failed to load spec: {loadError}");
        }

        if (spec is not null)
        {
            violations.AddRange(CheckRequiredFields(spec));
            violations.AddRange(CheckIdMatchesFilename(spec, filename));
            violations.AddRange(CheckCategoryMatchesDirectory(spec, dirName));
            violations.AddRange(CheckKnownActions(spec));
            violations.AddRange(CheckStepsAreActionOrExpectedState(spec));
            violations.AddRange(CheckSetupHasGameSystem(spec));
            violations.AddRange(CheckSetupHasEdit(spec));
            violations.AddRange(CheckActionParameters(spec));
        }

        return violations;
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

    // ── The rules can fail ───────────────────────────────────────────

    /// <summary>A spec every rule accepts, which each of <see cref="BrokenSamples"/> breaks in one place.</summary>
    private const string Sample = """
        id: sample
        category: sample
        description: Every rule accepts this spec

        setup:
          edit: cat-1
          gameSystem:
            id: gs-1
            name: Test System
          catalogues:
            - id: cat-1
              name: Test Catalogue
              gameSystemId: gs-1
              sharedSelectionEntries:
                - id: sse-bolter
                  name: Bolter
                  type: upgrade

        steps:
          - action: addLink
            id: add-link
            parentId: cat-1
            linkType: entryLink
            targetId: sse-bolter

          - expectedState:
              catalogues:
                - id: cat-1
                  entryLinks:
                    - entryType: entryLink
                      fields:
                        targetId: sse-bolter
        """ + "\n";

    /// <summary>One rule broken per row: the text of <see cref="Sample"/> replaced, its replacement, and what the lint must say.</summary>
    private static readonly (string Text, string BrokenBy, string Says)[] BrokenSamples =
    [
        ("targetId: sse-bolter\n\n", "targetId: sse-bolter\n", "file is not correctly formatted"),
        ("edit: cat-1", "edit: cat-2", "setup.edit 'cat-2' must be the game system id or one of the catalogue ids"),
        ("action: addLink", "action: addLinks", "unknown action 'addLinks'"),
        ("linkType: entryLink\n    targetId: sse-bolter\n", "linkType: entryLink\n", "step 1: addLink requires 'targetId'"),
    ];

    /// <summary>
    /// <b>Every sampled rule can fail.</b> <see cref="AllLintChecks"/> passing every spec says nothing about
    /// a rule that no longer matches what it looks for, so each row breaks <see cref="Sample"/> in one place
    /// and the lint must name the break. The sample itself must pass, so what a row sees is its own break.
    /// </summary>
    [Fact]
    public void EveryBrokenSample_IsRejected() => LintSamples.AssertEachBreakIsRejected(Sample, BrokenSamples, LintSample);

    private static List<string> LintSample(string yaml)
    {
        GameDataSpecFile? spec = null;
        string? loadError = null;
        try
        {
            spec = SpecLoader.LoadGameDataFromYaml(yaml);
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }

        return Lint(yaml, "sample", "sample", spec, loadError);
    }

    // ── Formatting ───────────────────────────────────────────────────
    // Same rule set as roster specs (SpecLintTests): a file must equal its
    // SpecFormatter output. Covers blank line before setup:, blank lines between
    // steps, trailing whitespace, expectedState ordering, and final newline.

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
        "setFields", "addLink",
        "openFile",
        "reload",
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
            var hasExpectedFile = step.ExpectedFile is not null;
            if (!hasAction && !hasExpected && !hasExpectedFile)
            {
                yield return $"step {i + 1} has none of 'action', 'expectedState' or 'expectedFile'";
            }

            if (hasAction && (hasExpected || hasExpectedFile))
            {
                yield return $"step {i + 1} has both an action and an assertion (expectedState/expectedFile)";
            }

            // A side-file expectedFile (no inline content) is keyed by the step id.
            if (hasExpectedFile && step.ExpectedFile!.Content is null && step.Id is not { Length: > 0 })
            {
                yield return $"step {i + 1}: expectedFile without inline 'content' requires the step to have an 'id'";
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

    // ── Setup declares the file opened for editing ───────────────────
    // Engines disagree on which loaded file is "active" by default, so every spec must declare the
    // file it edits (a catalogue id or the game system id). An openFile step may switch it later.

    private static IEnumerable<string> CheckSetupHasEdit(GameDataSpecFile spec)
    {
        var edit = spec.Setup?.Edit;
        if (string.IsNullOrWhiteSpace(edit))
        {
            yield return "setup.edit is required (the id of the catalogue or game system to open for editing)";
            yield break;
        }

        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        if (spec.Setup?.GameSystem?.Id is { Length: > 0 } gsId)
        {
            knownIds.Add(gsId);
        }
        foreach (var cat in spec.Setup?.Catalogues ?? [])
        {
            if (cat.Id is { Length: > 0 } catId)
            {
                knownIds.Add(catId);
            }
        }

        if (!knownIds.Contains(edit))
        {
            yield return $"setup.edit '{edit}' must be the game system id or one of the catalogue ids " +
                $"({string.Join(", ", knownIds)})";
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
                case "setFields":
                    if (step.EntryId is null)
                    {
                        yield return $"step {i + 1}: setFields requires 'entryId'";
                    }

                    if (step.Fields is null && step.Characteristics is null && step.Costs is null)
                    {
                        yield return $"step {i + 1}: setFields requires at least one of 'fields', 'characteristics' or 'costs'";
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
                case "openFile":
                    // openFile opens a loaded file (entryId), loads inline XML (content), or loads a
                    // side-file keyed by the step id.
                    if (step.EntryId is null && step.Content is null && step.Id is not { Length: > 0 })
                    {
                        yield return $"step {i + 1}: openFile requires 'entryId', 'content', or a step 'id' for a side-file";
                    }

                    break;
            }
        }
    }
}
