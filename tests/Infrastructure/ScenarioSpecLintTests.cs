using BattleScribeSpec.Scenario;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests for scenario spec YAML files (<c>specs/scenario/</c>, <c>docs/adr/002-scenario-specs.md</c>) —
/// formatting, required fields, and the shape a scenario has that neither a roster nor a GameData spec has: the
/// items of <c>steps</c>, the hand-offs between them, and the order the hand-offs make sense in.
/// </summary>
/// <remarks>
/// <para>
/// The steps inside a block are a roster or GameData spec's steps verbatim, so they are held to those lints'
/// own per-step rules (<see cref="SpecLintTests.CheckRosterSteps"/>, <see cref="GameDataSpecLintTests.CheckGameDataSteps"/>)
/// rather than to a copy of them. Step numbers in the findings count every step and hand-off down the file, as
/// the runner's do — from 1, like the other lints' numbers, where the runner's "Step N" counts from 0 like the
/// other runners'. So the runner's "Step 5" is the lint's "step 6".
/// </para>
/// <para>
/// What the lint catches before a run, the runner refuses during one (<see cref="ScenarioRunner"/>): an unknown
/// hand-off, a missing <c>file</c>, a roster block before any roster. Both say so, because a spec that only ever
/// runs on one lane would otherwise find out on the first lane that runs it.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class ScenarioSpecLintTests
{
    private static readonly string? SpecsDir = SpecLoader.FindScenarioSpecsDirectory();

    private sealed record SpecEntry(string Path, string Name, string RelPath, ScenarioSpecFile? Spec, string? LoadError);

    // File discovery only — no YAML parsing. Name is the spec's 'category/id', what its row carries.
    private static IEnumerable<(string path, string name, string relPath)> DiscoverSpecFiles()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            yield break;
        }

        foreach (var (path, id, category) in SpecLoader.DiscoverScenarioSpecs(SpecsDir))
        {
            yield return (path, SpecLoader.SpecName(category, id), Path.GetRelativePath(SpecsDir, path).Replace('\\', '/'));
        }
    }

    // Load helper: parse YAML and capture any error without throwing
    private static SpecEntry TryLoadSpec(string path, string name, string relPath)
    {
        try
        {
            return new SpecEntry(path, name, relPath, SpecLoader.LoadScenario(path), null);
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
    /// see <see cref="TheoryRowIdentityTests"/>. No corpus, no rows: xunit fails a theory without data, so a
    /// checkout that lost <c>specs/scenario/</c> is a red lint run, not a skipped one.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string>> AllSpecs() =>
        DiscoverSpecFiles().Select(x => new TheoryDataRow<string>(x.name) { Label = x.name });

    // ── Single aggregated lint check per spec ────────────────────────

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void AllLintChecks(string specName)
    {
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
    private static List<string> Lint(string text, string filename, string dirName, ScenarioSpecFile? spec, string? loadError)
    {
        var violations = new List<string>();
        violations.AddRange(SpecLintTests.CheckFormatting(text));

        if (loadError is not null)
        {
            violations.Add($"Failed to load spec: {loadError}");
        }

        if (spec is not null)
        {
            violations.AddRange(SpecLintTests.CheckRequiredFields(spec));
            violations.AddRange(SpecLintTests.CheckIdMatchesFilename(spec, filename));
            violations.AddRange(SpecLintTests.CheckCategoryMatchesDirectory(spec, dirName));
            violations.AddRange(SpecLintTests.CheckEngineExpectations(spec));
            violations.AddRange(SpecLintTests.CheckNoRestatedFamilyEntries(spec));
            violations.AddRange(CheckNoRestatedOverrides(spec));
            violations.AddRange(GameDataSpecLintTests.CheckSetupHasGameSystem(spec.Setup));
            violations.AddRange(GameDataSpecLintTests.CheckSetupHasEdit(spec.Setup));
            violations.AddRange(CheckItems(spec));
            violations.AddRange(CheckStepIdsAreUnique(spec));
        }

        return violations;
    }

    /// <summary>
    /// No override map in a block or on a hand-off restates its family's: each block by its own domain's walk, a
    /// hand-off's <c>expectFailure</c> directly (<see cref="SpecLintTests.CheckNoRestatedFamilyEntries"/>).
    /// </summary>
    private static IEnumerable<string> CheckNoRestatedOverrides(ScenarioSpecFile spec)
    {
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var item = spec.Steps[i];
            var where = $"item {i + 1}";
            foreach (var finding in GameDataSpecLintTests.DataStepRestatements(item.Data, $"{where} (data): ")
                .Concat(SpecLintTests.RosterStepRestatements(item.Roster, $"{where} (roster): "))
                .Concat(SpecLintTests.RestatedOverrides(item.ExpectFailure?.Engines, $"{where} ({item.Action}) expectFailure")))
            {
                yield return finding;
            }
        }
    }

    // ── No duplicate IDs (cross-spec check) ─────────────────────────

    [Fact]
    public void NoDuplicateSpecIds()
    {
        Assert.True(AllSpecsLazy.Value.Count > 0,
            $"Found no scenario spec under '{SpecsDir ?? "(no specs/scenario/ directory)"}', so this check checked nothing.");

        var duplicates = AllSpecsLazy.Value
            .Where(x => x.Spec is not null)
            .GroupBy(x => x.Spec!.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' in: {string.Join(", ", g.Select(x => x.RelPath))}")
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"Duplicate scenario spec IDs found:\n  {string.Join("\n  ", duplicates)}");
    }

    // ── The items of 'steps' ─────────────────────────────────────────

    /// <summary>
    /// Walks <c>steps</c> the way the runner does, with one step number counted across blocks and hand-offs:
    /// each item is exactly one kind; a block is not empty and its steps pass their own domain's per-step rules;
    /// a hand-off is known, carries <c>file</c> exactly where it takes one, and comes in an order that makes
    /// sense (no roster block or <c>saveRoster</c> before a roster exists, no <c>openRoster</c> of a roster
    /// nobody saved, no <c>reloadRoster</c> without a <c>saveData</c> since the roster editor last read the data or
    /// of a roster changed since its file); and the spec ends asserting something.
    /// </summary>
    private static IEnumerable<string> CheckItems(ScenarioSpecFile spec)
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

        var catalogueCount = spec.Setup?.Catalogues?.Count ?? 0;
        var rosterStarted = false;
        var saved = new List<string>();
        string? rosterFile = null;
        var rosterChangedSinceFile = false;
        var dataSavedSinceRosterRead = false;
        var step = 1;

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var item = spec.Steps[i];
            var where = $"item {i + 1}";
            var kinds = (item.Data is null ? 0 : 1) + (item.Roster is null ? 0 : 1) + (item.Action is null ? 0 : 1);
            if (kinds == 0)
            {
                yield return $"{where} has none of 'data', 'roster' or 'action'";
                continue;
            }

            if (kinds > 1)
            {
                yield return $"{where} has more than one of 'data', 'roster' and 'action' — a block and a hand-off are separate items";
                continue;
            }

            if (item.Action is null && (item.File is not null || item.ExpectFailure is not null))
            {
                yield return $"{where}: 'file' and 'expectFailure' belong to a hand-off; a data or roster block takes neither — put expectFailure on the step inside the block";
            }

            if (item.Data is { } data)
            {
                if (data.Count == 0)
                {
                    yield return $"{where}: the 'data' block is empty";
                }

                foreach (var finding in GameDataSpecLintTests.CheckGameDataSteps(data, step))
                {
                    yield return $"{where} (data): {finding}";
                }

                step += data.Count;
                continue;
            }

            if (item.Roster is { } roster)
            {
                if (!rosterStarted)
                {
                    yield return $"{where}: a roster block needs a roster — start one with 'newRoster' or 'openRoster' first";
                }

                if (roster.Count == 0)
                {
                    yield return $"{where}: the 'roster' block is empty";
                }

                rosterChangedSinceFile |= roster.Any(s => s.Action is { } rosterAction && rosterAction != "dump");

                foreach (var finding in SpecLintTests.CheckRosterSteps(roster, catalogueCount, step))
                {
                    yield return $"{where} (roster): {finding}";
                }

                step += roster.Count;
                continue;
            }

            var action = item.Action!;
            var here = $"{where} (step {step})";
            step++;
            if (!ScenarioHandoffs.All.Contains(action, StringComparer.Ordinal))
            {
                yield return $"{here}: unknown hand-off '{action}' (known: {string.Join(", ", ScenarioHandoffs.All)})";
                continue;
            }

            var file = item.File is { Length: > 0 } named ? named : null;
            if (ScenarioHandoffs.TakesFile(action) && file is null)
            {
                yield return $"{here}: {action} requires 'file', the name the roster is saved under";
            }
            else if (!ScenarioHandoffs.TakesFile(action) && item.File is not null)
            {
                yield return $"{here}: {action} takes no 'file' — the data editor saves its open file under its own id";
            }

            switch (action)
            {
                case ScenarioHandoffs.SaveData:
                    dataSavedSinceRosterRead |= rosterStarted;
                    break;

                case ScenarioHandoffs.NewRoster:
                    rosterStarted = true;
                    (rosterFile, rosterChangedSinceFile, dataSavedSinceRosterRead) = (null, false, false);
                    break;

                case ScenarioHandoffs.ReloadRoster:
                    if (!rosterStarted)
                    {
                        yield return $"{here}: reloadRoster needs an open roster — start one with 'newRoster' or 'openRoster' first";
                    }
                    else if (!dataSavedSinceRosterRead)
                    {
                        yield return $"{here}: reloadRoster without a saveData since the roster editor last read the data "
                            + "directory — no file under the open roster changed, so BattleScribe offers no reload";
                    }
                    else if (rosterFile is not null && rosterChangedSinceFile)
                    {
                        yield return $"{here}: reloadRoster reads the roster back from its file '{rosterFile}', which would "
                            + "drop the roster changes made since — saveRoster first";
                    }

                    (rosterChangedSinceFile, dataSavedSinceRosterRead) = (false, false);
                    break;

                case ScenarioHandoffs.SaveRoster:
                    if (!rosterStarted)
                    {
                        yield return $"{here}: saveRoster needs a roster — start one with 'newRoster' or 'openRoster' first";
                    }

                    if (file is not null)
                    {
                        saved.Add(file);
                        (rosterFile, rosterChangedSinceFile) = (file, false);
                    }

                    break;

                case ScenarioHandoffs.OpenRoster:
                    if (file is not null && !saved.Contains(file, StringComparer.Ordinal))
                    {
                        yield return $"{here}: openRoster names '{file}', which no earlier saveRoster saved "
                            + $"(saved: [{string.Join(", ", saved)}])";
                    }

                    rosterStarted = true;
                    (rosterFile, rosterChangedSinceFile, dataSavedSinceRosterRead) = (file, false, false);
                    break;
            }
        }

        var last = spec.Steps[^1];
        var endsAsserting = last switch
        {
            { Action: null, Data: { Count: > 0 } data } when data[^1] is { ExpectedState: not null } or { ExpectedFile: not null } => true,
            { Action: null, Roster: { Count: > 0 } roster } when roster[^1] is { ExpectedState: not null } or { ExpectedFile: not null } => true,
            _ => false,
        };
        if (!endsAsserting)
        {
            yield return "the last item must be a 'data' or 'roster' block whose last step is an assertion "
                + "('expectedState' or 'expectedFile') — a scenario that ends on an action or a hand-off asserts nothing about it";
        }
    }

    /// <summary>
    /// Step ids are one namespace across the whole spec (<c>docs/adr/002-scenario-specs.md</c>, "Runner"): an
    /// id a roster step reuses from a data step, or from an earlier roster step, makes <c>${{ steps.&lt;id&gt; }}</c>
    /// name whichever ran last.
    /// </summary>
    private static IEnumerable<string> CheckStepIdsAreUnique(ScenarioSpecFile spec)
    {
        var ids = (spec.Steps ?? [])
            .SelectMany(static item => (item.Data ?? []).Select(static s => s.Id).Concat((item.Roster ?? []).Select(static s => s.Id)))
            .OfType<string>()
            .Where(static id => id.Length > 0);

        foreach (var group in ids.GroupBy(static id => id, StringComparer.Ordinal).Where(static g => g.Count() > 1))
        {
            yield return $"step id '{group.Key}' is used by {group.Count()} steps — step ids are one namespace across the "
                + "whole scenario, data and roster blocks alike";
        }
    }

    // ── The rules can fail ───────────────────────────────────────────

    /// <summary>A scenario every rule accepts, which each of <see cref="BrokenSamples"/> breaks in one place.</summary>
    private const string Sample = """
        id: sample
        category: sample
        description: Every rule accepts this spec

        setup:
          edit: cat-1
          gameSystem:
            id: gs-1
            name: Test System
            costTypes:
              - id: ct-pts
                name: pts
            forceEntries:
              - id: fe-1
                name: Detachment
          catalogues:
            - id: cat-1
              name: Test Catalogue
              gameSystemId: gs-1
              selectionEntries:
                - id: se-squad
                  name: Squad
                  type: unit
                  costs:
                    - name: pts
                      typeId: ct-pts
                      value: 10

        steps:
          - data:
              - action: setFields
                entryId: se-squad
                costs:
                  ct-pts: "15"

          - action: saveData

          - action: newRoster

          - roster:
              - action: addForce
                id: add-detachment
                forceEntryId: fe-1

              - action: selectEntry
                forceId: ${{ steps.add-detachment.forceId }}
                entryId: se-squad

          - action: saveRoster
            file: my-list

          - action: openRoster
            file: my-list

          - roster:
              - expectedState:
                  costs:
                    - typeId: ct-pts
                      value: 15
        """ + "\n";

    /// <summary>One rule broken per row: the text of <see cref="Sample"/> replaced, its replacement, and what the lint must say.</summary>
    private static readonly (string Text, string BrokenBy, string Says)[] BrokenSamples =
    [
        // formatted
        ("\n\n  - action: saveData", "\n  - action: saveData", "file is not correctly formatted"),

        // required fields; id and category match the file
        ("description: Every rule accepts this spec", "description: \"\"", "missing 'description'"),
        ("id: sample", "id: other", "expected id 'sample' but got 'other'"),
        ("category: sample", "category: other", "expected category 'sample' but got 'other'"),
        ("description: Every rule accepts this spec\n", "description: Every rule accepts this spec\nengines:\n  battlescribe: flaky\n",
            "engine 'battlescribe' has invalid expectation 'flaky'"),

        // a UI driver's entry never restates its family's, on the spec or in a block
        ("description: Every rule accepts this spec\n",
            "description: Every rule accepts this spec\nengines:\n  newrecruit: fail\n  newrecruit-ui: fail\n",
            "engines: 'newrecruit-ui: fail' restates 'newrecruit: fail' — newrecruit-ui inherits its family's entry"),
        ("              value: 15\n",
            "              value: 15\n          engines:\n            battlescribe:\n              costs: []\n"
            + "            battlescribe-ui:\n              costs: []\n",
            "item 7 (roster): step 1 expectedState: engines 'battlescribe-ui' restates 'battlescribe'"),

        // setup.edit names a loaded file
        ("edit: cat-1", "edit: cat-9", "setup.edit 'cat-9' must be the game system id or one of the catalogue ids"),

        // each item is exactly one kind
        ("  - action: newRoster\n", "  - action: newRoster\n    data: []\n", "item 3 has more than one of 'data', 'roster' and 'action'"),
        ("  - action: saveData\n", "  - file: my-list\n", "item 2 has none of 'data', 'roster' or 'action'"),

        // a block takes neither a hand-off's file nor its expectFailure
        ("          ct-pts: \"15\"\n", "          ct-pts: \"15\"\n    expectFailure: true\n",
            "item 1: 'file' and 'expectFailure' belong to a hand-off"),

        // the hand-off is known
        ("action: saveData", "action: saveDta", "item 2 (step 2): unknown hand-off 'saveDta'"),

        // file exactly where required
        ("  - action: saveRoster\n    file: my-list\n", "  - action: saveRoster\n", "item 5 (step 6): saveRoster requires 'file'"),
        ("  - action: newRoster\n", "  - action: newRoster\n    file: my-list\n", "item 3 (step 3): newRoster takes no 'file'"),

        // no roster block and no saveRoster before a roster exists
        ("  - action: newRoster\n\n", "", "item 3: a roster block needs a roster"),
        ("  - action: newRoster\n\n", "", "saveRoster needs a roster"),

        // openRoster opens a roster an earlier saveRoster saved
        ("action: openRoster\n    file: my-list", "action: openRoster\n    file: your-list",
            "item 6 (step 7): openRoster names 'your-list', which no earlier saveRoster saved (saved: [my-list])"),

        // reloadRoster: an open roster, a saveData since the roster editor read the data, no unsaved roster changes
        ("  - action: saveData\n\n  - action: newRoster\n", "  - action: saveData\n\n  - action: reloadRoster\n\n  - action: newRoster\n",
            "reloadRoster needs an open roster"),
        ("action: openRoster\n    file: my-list", "action: reloadRoster",
            "reloadRoster without a saveData since the roster editor last read the data directory"),
        ("  - action: openRoster\n    file: my-list\n",
            "  - roster:\n      - action: addForce\n        forceEntryId: fe-1\n\n  - action: saveData\n\n  - action: reloadRoster\n",
            "reloadRoster reads the roster back from its file 'my-list', which would drop the roster changes made since"),

        // step ids are one namespace
        ("        entryId: se-squad\n        costs:", "        id: add-detachment\n        entryId: se-squad\n        costs:",
            "step id 'add-detachment' is used by 2 steps"),

        // nested steps: known actions and required parameters, each domain's own rules
        ("action: setFields", "action: setField", "item 1 (data): unknown action 'setField'"),
        ("        entryId: se-squad\n        costs:", "        costs:", "item 1 (data): step 1: setFields requires 'entryId'"),
        ("action: selectEntry", "action: selectEntri", "item 4 (roster): unknown action 'selectEntri'"),
        ("action: selectEntry", "action: setSelectionCount", "item 4 (roster): step 5: setSelectionCount requires 'selectionId'"),
        ("              value: 15\n", "              value: 15\n          errors:\n            - on: roster\n",
            "item 7 (roster): errors: error assertion on='roster' is missing 'from:' field"),

        // the spec ends asserting
        ("              value: 15\n", "              value: 15\n\n  - action: saveData\n", "the last item must be a 'data' or 'roster' block"),
    ];

    /// <summary>
    /// <b>Every rule can fail.</b> <see cref="AllLintChecks"/> passing every spec says nothing about a rule that
    /// no longer matches what it looks for, so each row breaks <see cref="Sample"/> in one place and the lint
    /// must name the break. The sample itself must pass, so what a row sees is its own break.
    /// </summary>
    [Fact]
    public void EveryBrokenSample_IsRejected() => LintSamples.AssertEachBreakIsRejected(Sample, BrokenSamples, LintSample);

    private static List<string> LintSample(string yaml)
    {
        ScenarioSpecFile? spec = null;
        string? loadError = null;
        try
        {
            spec = SpecLoader.LoadScenarioFromYaml(yaml);
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }

        return Lint(yaml, "sample", "sample", spec, loadError);
    }
}
