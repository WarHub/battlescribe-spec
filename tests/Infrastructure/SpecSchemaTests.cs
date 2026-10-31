using System.Text.Json;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Validates every spec against its domain's JSON Schema in <c>docs/</c> (<see cref="SpecSchemas"/>): roster
/// specs against <c>roster-spec-schema.json</c>, GameData specs against <c>gamedata-spec-schema.json</c>,
/// scenario specs against <c>scenario-spec-schema.json</c>. Then pins the restrictive direction of each schema
/// with broken samples — which the corpus cannot do on its own, since no spec is written wrong.
/// </summary>
/// <remarks>
/// The schemas are what an editor validates a spec against while it is written (<c>.vscode/settings.json</c>),
/// so they are held to what the loaders and lints accept: a schema that blesses a shape the linter then refuses
/// sends the author to the wrong place, and one that refuses a shape the suite runs flags a correct spec. That
/// the schemas name every field the models have, and every action the runners know, is
/// <see cref="SpecSchemaDriftTests"/>.
/// </remarks>
[Trait("Category", "Lint")]
public sealed class SpecSchemaTests
{
    /// <summary>
    /// One row per roster spec, carrying and labelled with its name (<c>category/id</c>), never its
    /// path — see <see cref="TheoryRowIdentityTests"/>.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string>> RosterSpecs() =>
        Rows(SpecLoader.FindRosterSpecsDirectory(), SpecLoader.DiscoverSpecs);

    /// <summary>One row per GameData spec, labelled with its name.</summary>
    public static IEnumerable<TheoryDataRow<string>> GameDataSpecs() =>
        Rows(SpecLoader.FindGameDataSpecsDirectory(), SpecLoader.DiscoverGameDataSpecs);

    /// <summary>One row per scenario spec, labelled with its name.</summary>
    public static IEnumerable<TheoryDataRow<string>> ScenarioSpecs() =>
        Rows(SpecLoader.FindScenarioSpecsDirectory(), SpecLoader.DiscoverScenarioSpecs);

    private static IEnumerable<TheoryDataRow<string>> Rows(
        string? specsDir, Func<string, IEnumerable<(string Path, string Id, string Category)>> discover)
    {
        // No corpus, no rows: xunit fails a theory without data, so a checkout that lost a spec folder is a
        // red lint run, not a skipped one.
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            yield break;
        }

        foreach (var (_, id, category) in discover(specsDir))
        {
            var specName = SpecLoader.SpecName(category, id);
            yield return new TheoryDataRow<string>(specName) { Label = specName };
        }
    }

    [Theory]
    [MemberData(nameof(RosterSpecs))]
    public void RosterSpecValidatesAgainstSchema(string specName) =>
        AssertValid(SpecSchemas.RosterFile, SpecLoader.ResolveRosterSpec(specName), specName);

    [Theory]
    [MemberData(nameof(GameDataSpecs))]
    public void GameDataSpecValidatesAgainstSchema(string specName) =>
        AssertValid(SpecSchemas.GameDataFile, SpecLoader.ResolveGameDataSpec(specName), specName);

    [Theory]
    [MemberData(nameof(ScenarioSpecs))]
    public void ScenarioSpecValidatesAgainstSchema(string specName) =>
        AssertValid(SpecSchemas.ScenarioFile, SpecLoader.ResolveScenarioSpec(specName), specName);

    private static void AssertValid(string schemaFile, string specPath, string specName)
    {
        var errors = Validate(schemaFile, File.ReadAllText(specPath));
        Assert.True(errors.Count == 0,
            $"{specName} failed validation against docs/{schemaFile}:\n  {string.Join("\n  ", errors)}");
    }

    private static IReadOnlyList<string> Validate(string schemaFile, string yaml) => schemaFile switch
    {
        SpecSchemas.RosterFile => SpecSchemas.Validate(SpecSchemas.Roster, yaml),
        SpecSchemas.GameDataFile => SpecSchemas.Validate(SpecSchemas.GameData, yaml),
        SpecSchemas.ScenarioFile => SpecSchemas.Validate(SpecSchemas.Scenario, yaml),
        _ => throw new ArgumentOutOfRangeException(nameof(schemaFile), schemaFile, "not a spec schema"),
    };

    // ── The schemas can refuse ───────────────────────────────────────
    // Each sample is valid; each row breaks it in one place and names where the schema must say so — the
    // instance location and the keyword that refuses it.

    /// <summary>A scenario the schema accepts: every kind of item, every hand-off, a step of each vocabulary.</summary>
    private const string ScenarioSample = """
        id: sample
        category: sample
        description: The schema accepts this spec

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
            expectFailure:
              engines:
                battlescribe: false

          - roster:
              - expectedState:
                  costs:
                    - typeId: ct-pts
                      value: 15
        """ + "\n";

    private static readonly (string Text, string BrokenBy, string Says)[] ScenarioBreaks =
    [
        // An item is exactly one kind: a hand-off and a block in one item, or a hand-off's fields on a block.
        ("  - action: newRoster\n", "  - action: newRoster\n    data:\n      - action: reload\n", "/steps/2: oneOf"),
        ("          ct-pts: \"15\"\n", "          ct-pts: \"15\"\n    expectFailure: true\n", "/steps/0: oneOf"),
        ("  - action: newRoster\n", "  - file: my-list\n", "/steps/2: oneOf"),

        // The hand-off is one of the four, with its fields and no others.
        ("action: saveData", "action: saveDta", "/steps/1/action: enum"),
        ("  - action: newRoster\n", "  - action: newRoster\n    name: fresh\n", "/steps/2/name: "),

        // file exactly where the hand-off takes one.
        ("  - action: saveData\n", "  - action: saveData\n    file: data\n", "/steps/1: not"),
        ("  - action: newRoster\n", "  - action: newRoster\n    file: my-list\n", "/steps/2: not"),
        ("  - action: openRoster\n    file: my-list\n", "  - action: openRoster\n", "/steps/5: required"),
        ("  - action: saveRoster\n    file: my-list\n", "  - action: saveRoster\n    file: \"\"\n", "/steps/4/file: minLength"),

        // A block holds at least one step, each in its own domain's vocabulary.
        ("  - data:\n      - action: setFields\n        entryId: se-squad\n        costs:\n          ct-pts: \"15\"\n",
            "  - data: []\n", "/steps/0/data: minItems"),
        ("action: setFields", "action: setField", "/steps/0/data/0/action: enum"),
        ("        entryId: se-squad\n        costs:", "        entryID: se-squad\n        costs:", "/steps/0/data/0/entryID: "),
        ("action: selectEntry", "action: selectEntri", "/steps/3/roster/1/action: enum"),
        ("        forceEntryId: fe-1\n", "        forceEntry: fe-1\n", "/steps/3/roster/0/forceEntry: "),

        // The setup is a GameData setup: it names the file the data editor has open.
        ("  edit: cat-1\n", "", "/setup: required"),
    ];

    [Fact]
    public void ScenarioSchema_RefusesEachBrokenSample() =>
        LintSamples.AssertEachBreakIsRejected(ScenarioSample, ScenarioBreaks, yaml => Validate(SpecSchemas.ScenarioFile, yaml));

    /// <summary>A GameData spec the schema accepts.</summary>
    private const string GameDataSample = """
        id: sample
        category: sample
        description: The schema accepts this spec

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
                  constraints:
                    - id: con-1
                      type: max
                      value: 1
                      negative: true

        steps:
          - action: addLink
            id: add-link
            parentId: cat-1
            linkType: entryLink
            targetId: sse-bolter

          - action: openFile
            content: <catalogue/>
            expectFailure:
              messageContains: unexpected

          - expectedState:
              catalogues:
                - id: cat-1
                  entryLinks:
                    - entryType: entryLink
                      fields:
                        targetId: sse-bolter
            expectedFile:
              content: <catalogue/>
        """ + "\n";

    private static readonly (string Text, string BrokenBy, string Says)[] GameDataBreaks =
    [
        ("action: addLink", "action: addLinks", "/steps/0/action: enum"),
        ("linkType: entryLink", "linkType: entryLinks", "/steps/0/linkType: enum"),
        ("    targetId: sse-bolter\n\n", "    targetID: sse-bolter\n\n", "/steps/0/targetID: "),

        // A step is an action or an assertion, and expectFailure goes on an action.
        ("    targetId: sse-bolter\n\n", "    targetId: sse-bolter\n    expectedState: {}\n\n", "/steps/0: not"),
        ("  - expectedState:\n", "  - expectFailure: true\n    expectedState:\n", "/steps/2: required"),
        ("    expectedFile:\n      content: <catalogue/>\n", "    expectedFile: {}\n", "/steps/2: required"),
        ("      messageContains: unexpected\n", "      messageContain: unexpected\n", "/steps/1/expectFailure/messageContain: "),
        ("      messageContains: unexpected\n", "      messageContains: unexpected\n      engines:\n        newrecruit:\n          engines: {}\n",
            "/steps/1/expectFailure/engines/newrecruit/engines: "),

        // Assertions name only what the models assert.
        ("            - entryType: entryLink\n", "            - entryKind: entryLink\n", "/steps/2/expectedState/catalogues/0/entryLinks/0/entryKind: "),
        ("              fields:\n                targetId: sse-bolter\n", "              fields:\n                targetId: 4\n",
            "/steps/2/expectedState/catalogues/0/entryLinks/0/fields/targetId: type"),

        // The setup names the file it edits, and its data is the protocol's.
        ("  edit: cat-1\n", "", "/setup: required"),
        ("              negative: true\n", "              negativ: true\n", "/setup/catalogues/0/sharedSelectionEntries/0/constraints/0/negativ: "),
    ];

    [Fact]
    public void GameDataSchema_RefusesEachBrokenSample() =>
        LintSamples.AssertEachBreakIsRejected(GameDataSample, GameDataBreaks, yaml => Validate(SpecSchemas.GameDataFile, yaml));

    /// <summary>A roster spec the schema accepts.</summary>
    private const string RosterSample = """
        id: sample
        category: sample
        description: The schema accepts this spec

        setup:
          gameSystem:
            id: gs-1
            name: Test System
            forceEntries:
              - id: fe-1
                name: Detachment

        steps:
          - action: addForce
            id: add-detachment
            forceEntryId: fe-1
            engines:
              newrecruit:
                forceEntryId: fe-1

          - expectedState:
              forceCount: 1
        """ + "\n";

    private static readonly (string Text, string BrokenBy, string Says)[] RosterBreaks =
    [
        ("action: addForce", "action: addForces", "/steps/0/action: enum"),
        ("    forceEntryId: fe-1\n    engines:", "    forceEntryID: fe-1\n    engines:", "/steps/0/forceEntryID: "),
        ("        forceEntryId: fe-1\n\n", "        expectedState: {}\n\n", "/steps/0/engines/newrecruit/expectedState: "),
        ("  - expectedState:\n", "  - expectFailure: true\n    expectedState:\n", "/steps/1: required"),
    ];

    [Fact]
    public void RosterSchema_RefusesEachBrokenSample() =>
        LintSamples.AssertEachBreakIsRejected(RosterSample, RosterBreaks, yaml => Validate(SpecSchemas.RosterFile, yaml));

    /// <summary>
    /// The corpus is the regression test for the permissive direction — every spec is evaluated
    /// against this schema above. Nothing guards the restrictive direction: no spec is written in
    /// the entry-addressed <c>on:</c> dialect #419 retired, so loosening the pattern back to
    /// <c>( \S.*)?</c> would reintroduce the legacy form without failing a single test.
    ///
    /// That mattered once already: the schema accepted the legacy form long after the code stopped
    /// honouring it (<see cref="Roster.ErrorAddress.Matches"/> never matches a literal id, and
    /// <c>SpecLintTests</c> rejects the spec outright), so a spec could pass schema validation and
    /// then be refused by the linter — which names a catalogue entry, not the schema — for a shape the
    /// schema had just blessed.
    ///
    /// The pattern tolerates surrounding whitespace because the runtime does: <c>ErrorAddress.Parse</c>
    /// and <c>ExpressionResolver.Resolve</c> both trim before inspecting. It still rejects a value
    /// whose expression is not the whole token — <c>Resolve</c> returns those unchanged, so they
    /// resolve to a literal that matches nothing, silently.
    /// </summary>
    [Theory]
    // Retired entry-addressed form: a literal second token names a catalogue ENTRY, which is a set
    // of nodes rather than one. Rejected at every layer.
    [InlineData("selection se-unit-a", false)]
    [InlineData("force fe-detachment", false)]
    [InlineData("category cat-troops", false)]
    // Node-addressed: the only way to name a per-run node id.
    [InlineData("selection ${{ steps.select-first.selectionId }}", true)]
    [InlineData("force ${{ steps.add-force.forceId }}", true)]
    [InlineData("category ${{ steps.select-first.categoryId }}", true)]
    // Bare kinds that have no id a spec can name.
    [InlineData("roster", true)]
    [InlineData("group", true)]
    // Bare addressable kinds match on kind alone — unused by the corpus, but the runner supports it,
    // so the schema must not forbid it.
    [InlineData("selection", true)]
    [InlineData("force", true)]
    [InlineData("category", true)]
    // Whitespace the runtime trims away.
    [InlineData("selection  ${{ steps.a.selectionId }}", true)]
    [InlineData("selection ${{ steps.a.selectionId }} ", true)]
    // The expression must be the whole token, or it resolves to a literal that never matches.
    [InlineData("selection ${{ steps.a.selectionId }} junk", false)]
    [InlineData("selection sel-${{ steps.a.selectionId }}", false)]
    [InlineData("selection ${{", false)]
    // Giving an id-less kind an id is a lint error, and the pattern rejects it too.
    [InlineData("roster ros-1", false)]
    public void ErrorAddressPattern_AcceptsOnlyNodeAddressedForms(string on, bool expectedValid)
    {
        // Evaluate through the real validator, not a .NET Regex re-run of the pattern: the pattern
        // is only meaningful as JSON Schema, and a proxy assertion can agree with the regex engine
        // while the validator disagrees. JSON is YAML, so the probe goes through the same path as a spec.
        var specJson = $$"""
            {
              "id": "error-address-probe",
              "category": "probe",
              "description": "Minimal spec whose only variable is the error address under test.",
              "setup": { "gameSystem": {} },
              "steps": [
                {
                  "expectedState": {
                    "errors": [ { "on": {{JsonSerializer.Serialize(on)}}, "from": "entry/constraint" } ]
                  }
                }
              ]
            }
            """;

        var valid = Validate(SpecSchemas.RosterFile, specJson).Count == 0;

        Assert.True(
            valid == expectedValid,
            $"on: '{on}' should be {(expectedValid ? "accepted" : "rejected")} by docs/{SpecSchemas.RosterFile}, " +
            $"but the validator {(valid ? "accepted" : "rejected")} it.");
    }
}
