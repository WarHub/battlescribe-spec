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
            violations.AddRange(SpecLintTests.CheckEngineExpectations(spec));
            violations.AddRange(SpecLintTests.CheckNoRestatedFamilyEntries(spec));
            violations.AddRange(DataStepRestatements(spec.Steps));
            violations.AddRange(CheckKnownActions(spec.Steps));
            violations.AddRange(CheckStepsAreActionOrExpectedState(spec.Steps));
            violations.AddRange(CheckSetupHasGameSystem(spec.Setup));
            violations.AddRange(CheckSetupHasEdit(spec.Setup));
            violations.AddRange(CheckActionParameters(spec.Steps));
        }

        return violations;
    }

    /// <summary>
    /// The GameData steps' override maps restating their family's (<see cref="SpecLintTests.RestatedOverrides{T}"/>),
    /// each named by its 1-based step number after <paramref name="prefix"/>. A scenario's data blocks call it with
    /// the item they sit in.
    /// </summary>
    internal static IEnumerable<string> DataStepRestatements(IReadOnlyList<GameDataStepDef>? steps, string prefix = "")
    {
        if (steps is null)
        {
            yield break;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var at = $"{prefix}step {i + 1}";
            foreach (var finding in SpecLintTests.RestatedOverrides(step.ExpectedState?.Engines, $"{at} expectedState")
                .Concat(SpecLintTests.RestatedOverrides(step.ExpectFailure?.Engines, $"{at} expectFailure"))
                .Concat(SpecLintTests.RestatedOverrides(step.ExpectedFile?.Engines, $"{at} expectedFile")))
            {
                yield return finding;
            }
        }
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
        ("description: Every rule accepts this spec\n", "description: Every rule accepts this spec\nengines:\n  newrecruit: flaky\n",
            "engine 'newrecruit' has invalid expectation 'flaky'"),
        ("description: Every rule accepts this spec\n",
            "description: Every rule accepts this spec\nengines:\n  battlescribe: skip\n  battlescribe-ui: skip\n",
            "engines: 'battlescribe-ui: skip' restates 'battlescribe: skip' — battlescribe-ui inherits its family's entry"),
        ("                targetId: sse-bolter\n",
            "                targetId: sse-bolter\n      engines:\n        newrecruit:\n          catalogues:\n            - id: cat-1\n"
            + "        newrecruit-ui:\n          catalogues:\n            - id: cat-1\n",
            "step 2 expectedState: engines 'newrecruit-ui' restates 'newrecruit'"),
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

    /// <summary>The one list (<see cref="GameDataActions.All"/>), which the runner dispatches on and the GameData schema is held to.</summary>
    private static readonly HashSet<string> KnownActions = [.. GameDataActions.All];

    private static IEnumerable<string> CheckKnownActions(IReadOnlyList<GameDataStepDef> steps)
    {
        foreach (var step in steps)
        {
            if (step.Action is { } action && !KnownActions.Contains(action))
            {
                yield return $"unknown action '{action}'";
            }
        }
    }

    /// <summary>
    /// Every per-step rule a GameData spec's steps are held to, over a list of steps that is not a GameData
    /// spec's own — a scenario's <c>data:</c> block (<see cref="ScenarioSpecLintTests"/>): known actions, one kind
    /// per step, and each action's required parameters. The step numbers in the findings start at
    /// <paramref name="firstStep"/>, so they count across the whole spec.
    /// </summary>
    internal static IEnumerable<string> CheckGameDataSteps(IReadOnlyList<GameDataStepDef> steps, int firstStep) =>
    [
        .. CheckKnownActions(steps),
        .. CheckStepsAreActionOrExpectedState(steps, firstStep),
        .. CheckActionParameters(steps, firstStep),
    ];

    // ── Steps have action or expectedState ───────────────────────────

    private static IEnumerable<string> CheckStepsAreActionOrExpectedState(IReadOnlyList<GameDataStepDef> steps, int firstStep = 1)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var hasAction = step.Action is not null;
            var hasExpected = step.ExpectedState is not null;
            var hasExpectedFile = step.ExpectedFile is not null;
            if (!hasAction && !hasExpected && !hasExpectedFile)
            {
                yield return $"step {firstStep + i} has none of 'action', 'expectedState' or 'expectedFile'";
            }

            if (hasAction && (hasExpected || hasExpectedFile))
            {
                yield return $"step {firstStep + i} has both an action and an assertion (expectedState/expectedFile)";
            }

            // A side-file expectedFile (no inline content) is keyed by the step id.
            if (hasExpectedFile && step.ExpectedFile!.Content is null && step.Id is not { Length: > 0 })
            {
                yield return $"step {firstStep + i}: expectedFile without inline 'content' requires the step to have an 'id'";
            }
        }
    }

    // ── Setup has gameSystem ─────────────────────────────────────────

    /// <summary>Shared with <see cref="ScenarioSpecLintTests"/>, whose setup is this one verbatim.</summary>
    internal static IEnumerable<string> CheckSetupHasGameSystem(GameDataSetupDef? setup)
    {
        if (setup?.GameSystem is null)
        {
            yield return "setup.gameSystem is required";
        }
    }

    // ── Setup declares the file opened for editing ───────────────────
    // Engines disagree on which loaded file is "active" by default, so every spec must declare the
    // file it edits (a catalogue id or the game system id). An openFile step may switch it later.

    /// <summary>Shared with <see cref="ScenarioSpecLintTests"/>, whose setup is this one verbatim.</summary>
    internal static IEnumerable<string> CheckSetupHasEdit(GameDataSetupDef? setup)
    {
        var edit = setup?.Edit;
        if (string.IsNullOrWhiteSpace(edit))
        {
            yield return "setup.edit is required (the id of the catalogue or game system to open for editing)";
            yield break;
        }

        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        if (setup?.GameSystem?.Id is { Length: > 0 } gsId)
        {
            knownIds.Add(gsId);
        }
        foreach (var cat in setup?.Catalogues ?? [])
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

    private static IEnumerable<string> CheckActionParameters(IReadOnlyList<GameDataStepDef> steps, int firstStep = 1)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var n = firstStep + i;
            if (step.Action is null)
            {
                continue;
            }

            switch (step.Action)
            {
                case GameDataActions.AddEntry:
                    if (step.ParentId is null)
                    {
                        yield return $"step {n}: addEntry requires 'parentId'";
                    }

                    if (step.EntryType is null)
                    {
                        yield return $"step {n}: addEntry requires 'entryType'";
                    }

                    break;
                case GameDataActions.RemoveEntry:
                    if (step.EntryId is null)
                    {
                        yield return $"step {n}: removeEntry requires 'entryId'";
                    }

                    break;
                case GameDataActions.SetFields:
                    if (step.EntryId is null)
                    {
                        yield return $"step {n}: setFields requires 'entryId'";
                    }

                    if (step.Fields is null && step.Characteristics is null && step.Costs is null)
                    {
                        yield return $"step {n}: setFields requires at least one of 'fields', 'characteristics' or 'costs'";
                    }

                    break;
                case GameDataActions.AddLink:
                    if (step.ParentId is null)
                    {
                        yield return $"step {n}: addLink requires 'parentId'";
                    }

                    if (step.LinkType is null)
                    {
                        yield return $"step {n}: addLink requires 'linkType'";
                    }

                    if (step.TargetId is null)
                    {
                        yield return $"step {n}: addLink requires 'targetId'";
                    }

                    break;
                case GameDataActions.OpenFile:
                    // openFile opens a loaded file (entryId), loads inline XML (content), or loads a
                    // side-file keyed by the step id.
                    if (step.EntryId is null && step.Content is null && step.Id is not { Length: > 0 })
                    {
                        yield return $"step {n}: openFile requires 'entryId', 'content', or a step 'id' for a side-file";
                    }

                    break;
            }
        }
    }
}
