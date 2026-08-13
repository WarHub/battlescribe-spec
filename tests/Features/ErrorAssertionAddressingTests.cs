using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The contract #423 flipped: <c>on:</c> names the roster NODE the engine raised the error on, not
/// the catalogue entry the error was attributed to.
/// <para>
/// Driven through <see cref="RosterRunner"/> against a scripted engine rather than against
/// <see cref="ErrorAddress"/> directly, because the claims are about what a SPEC can say: the
/// <c>${{ steps.… }}</c> resolution, the per-assertion consumption, and the failure text all live in
/// the runner. A test of the parser alone would pass while a spec could not be written.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ErrorAssertionAddressingTests(ITestOutputHelper output)
{
    // ── Node-addressed matching ──────────────────────────────────────

    [Fact]
    public void On_NamesTheRaisingNodeById_AndMatchesIt()
    {
        var result = Run(
            errors: [Error("category", "cat-node-1")],
            on: "category ${{ steps.add-force.categories.cat-troops }}");

        AssertPassed(result);
    }

    /// <summary>
    /// The whole point of the epic, in one assertion: two selections of ONE entry, and the address
    /// separates them. The old form named <c>se-unit-a</c> and could not.
    /// </summary>
    [Fact]
    public void On_PointedAtTheWrongNodeOfTheSameEntry_Fails()
    {
        var matching = Run(
            errors: [Error("selection", "sel-node-1")],
            on: "selection ${{ steps.select-first.selectionId }}");
        AssertPassed(matching);

        var wrongNode = Run(
            errors: [Error("selection", "sel-node-1")],
            on: "selection ${{ steps.select-second.selectionId }}");

        var failure = Assert.Single(wrongNode.Failures);
        output.WriteLine(failure);
        Assert.Contains("sel-node-2", failure, StringComparison.Ordinal);

        // And the legacy form cannot tell the two apart — which is why the flip exists. Both
        // selections are `se-unit-a`, so the entry-addressed assertion matches either way.
        AssertPassed(Run(errors: [Error("selection", "sel-node-1")], on: "selection se-unit-a"));
        AssertPassed(Run(errors: [Error("selection", "sel-node-2")], on: "selection se-unit-a"));
    }

    [Fact]
    public void On_NamesANodeTheErrorWasNotRaisedOn_FailsEvenWhenTheOwnerAttributionAgrees()
    {
        // Raised on the category, attributed to the selection — the shape placement produces for
        // every collective over-limit violation on BattleScribe.
        var error = Error("category", "cat-node-1") with { OwnerType = "selection", OwnerEntryId = "se-unit-a" };

        var result = Run(errors: [error], on: "selection ${{ steps.select-first.selectionId }}");

        var failure = Assert.Single(result.Failures);
        output.WriteLine(failure);
    }

    // ── The two bare, kind-only forms ────────────────────────────────

    [Fact]
    public void OnRoster_IsBare_AndMatchesTheRosterRaisingNode()
    {
        // RosterState exposes no id on any lane, so there is nothing for a spec to name — but the
        // engine does report one, and the bare form must not require it.
        AssertPassed(Run(errors: [Error("roster", "roster-guid-nothing-names")], on: "roster"));
    }

    [Fact]
    public void OnGroup_IsBare_AndMatchesTheEntryGroupRaisingNode()
    {
        // NewRecruit raises entry-group constraints on a real group node that no engine's state
        // model carries. `group` is not an owner type on any lane — only a raising-node kind — so
        // this form can only work against the raising node.
        var error = Error("group", "nr-group-uid") with { OwnerType = "selection", OwnerEntryId = "se-parent" };

        AssertPassed(Run(errors: [error], on: "group"));
    }

    [Fact]
    public void BareForm_DoesNotMatchAnotherKind()
    {
        var result = Run(errors: [Error("selection", "sel-node-1")], on: "roster");
        Assert.Single(result.Failures);
    }

    // ── Expression resolution in assertion fields ────────────────────

    [Fact]
    public void On_ResolvesStepReferences_AndReportsAnUnresolvableOneAsAFailure()
    {
        var result = Run(
            errors: [Error("force", "force-node-1")],
            on: "force ${{ steps.no-such-step.forceId }}");

        var failure = Assert.Single(result.Failures);
        output.WriteLine(failure);
        Assert.Contains("no-such-step", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void On_ResolvesEachOfTheFourStepOutputShapes()
    {
        AssertPassed(Run([Error("force", "force-node-1")], "force ${{ steps.add-force.forceId }}"));
        AssertPassed(Run([Error("category", "cat-node-1")], "category ${{ steps.add-force.categories.cat-troops }}"));
        AssertPassed(Run([Error("selection", "sel-node-1")], "selection ${{ steps.select-first.selectionId }}"));
        AssertPassed(Run([Error("selection", "auto-node-1")], "selection ${{ steps.add-force.selections.se-auto }}"));
    }

    // ── One-to-one, consume-once ─────────────────────────────────────

    /// <summary>
    /// Measured, not stylistic: <c>constraint-forces-field-on-forceentry</c> step 5 produces three
    /// byte-identical errors sharing raising node AND <c>from:</c>, and NewRecruit reports nothing
    /// there, so nothing on either lane can tell them apart. A matcher that answered "is there an
    /// error on this node?" would let two assertions be satisfied by one error.
    /// </summary>
    [Fact]
    public void TwoAssertionsCannotBeSatisfiedByOneError()
    {
        var result = Run(
            errors: [Error("roster", "roster-guid-nothing-names")],
            on: "roster",
            secondOn: "roster");

        Assert.NotEmpty(result.Failures);
        foreach (var failure in result.Failures)
        {
            output.WriteLine(failure);
        }

        Assert.Contains(result.Failures, e => e.Contains("not found in", StringComparison.Ordinal));
    }

    [Fact]
    public void IdenticalAssertionsMatchIdenticalErrorsOneForOne()
    {
        var identical = Error("roster", "roster-guid-nothing-names");

        AssertPassed(Run(errors: [identical, identical], on: "roster", secondOn: "roster"));
    }

    // ── TRANSIENT: the legacy entry-addressed branch (#424) ──────────

    [Fact]
    public void LegacyForm_StillMatchesTheOwnerAttribution_WhileTheCorpusMigrates()
    {
        // Placement moved this off its raising node, so the two attributions disagree — exactly the
        // 27 corpus assertions #424 has yet to migrate. The legacy form reads the owner pair.
        var error = Error("category", "cat-node-1") with { OwnerType = "selection", OwnerEntryId = "se-unit-a" };

        AssertPassed(Run(errors: [error], on: "selection se-unit-a"));
        Assert.Single(Run(errors: [error], on: "selection se-other").Failures);
    }

    [Fact]
    public void TheDiscriminatorIsTheExpression_NotTheKind()
    {
        var address = ErrorAddress.Parse("selection se-unit-a");
        Assert.True(address.IsLegacyEntryAddressed);

        var resolved = ErrorAddress.Parse("selection ${{ steps.select-first.selectionId }}", _ => "sel-node-1");
        Assert.False(resolved.IsLegacyEntryAddressed);
        Assert.Equal("sel-node-1", resolved.NodeId);

        Assert.False(ErrorAddress.Parse("roster").IsLegacyEntryAddressed);
        Assert.False(ErrorAddress.Parse("group").IsLegacyEntryAddressed);
    }

    // ── Siblings of one entry (#428) ─────────────────────────────────

    /// <summary>
    /// #428's whole point, at the level a spec writes: ONE step mints TWO selections of ONE entry,
    /// and each is separately nameable. Before the shape change the outputs map held one node per
    /// entry id, so one of these two existed in the roster with nothing able to name it.
    /// </summary>
    [Fact]
    public void TwoSelectionsOfOneEntryFromOneStep_AreIndividuallyNameable()
    {
        AssertPassed(Run(
            errors: [Error("selection", "auto-node-1")],
            on: "selection ${{ steps.add-force.selections.se-auto }}"));

        AssertPassed(Run(
            errors: [Error("selection", "auto-node-2")],
            on: "selection ${{ steps.add-force.selections.se-auto[1] }}"));
    }

    /// <summary>
    /// And the addresses are not interchangeable — otherwise the index would be decoration. Each
    /// sibling rejects the error raised on the other.
    /// </summary>
    [Fact]
    public void PointingAtTheWrongSiblingOfOneEntry_Fails()
    {
        var first = Assert.Single(Run(
            errors: [Error("selection", "auto-node-2")],
            on: "selection ${{ steps.add-force.selections.se-auto }}").Failures);
        output.WriteLine(first);

        var second = Assert.Single(Run(
            errors: [Error("selection", "auto-node-1")],
            on: "selection ${{ steps.add-force.selections.se-auto[1] }}").Failures);
        output.WriteLine(second);
    }

    // ── Harness ──────────────────────────────────────────────────────

    private static ValidationErrorState Error(string raisedOnType, string raisedOnId) => new(
        Message: "over the limit",
        OwnerType: raisedOnType,
        OwnerEntryId: "se-unit-a",
        EntryId: "se-unit-a",
        ConstraintId: "con-max",
        RaisedOnType: raisedOnType,
        RaisedOnId: raisedOnId);

    /// <summary>
    /// Runs a two-action, one-assertion spec whose <c>on:</c> is <paramref name="on"/> against an
    /// engine that reports exactly <paramref name="errors"/>. The actions exist so the step outputs
    /// a node-addressed <c>on:</c> resolves against are real runner state, not a fixture.
    /// </summary>
    private static SpecResult Run(
        IReadOnlyList<ValidationErrorState> errors, string on, string? secondOn = null)
    {
        var assertions = string.Join("\n", (secondOn is null ? new[] { on } : [on, secondOn])
            .Select(o => $"        - on: {o}\n          from: se-unit-a/con-max"));

        var yaml = SpecTemplate.Replace("ASSERTIONS", assertions, StringComparison.Ordinal);

        using var engine = new ScriptedEngine(errors);
        return new RosterRunner(engine, engineName: "battlescribe").Run(SpecLoader.LoadFromYaml(yaml));
    }

    private const string SpecTemplate = """
        id: error-addressing
        category: constraint
        description: harness

        setup:
          gameSystem:
            forceEntries:
              - id: fe-1
                name: Force
          catalogues:
            - id: cat-1
              selectionEntries:
                - id: se-unit-a
                  name: Unit A
                  type: unit

        steps:
          - action: addForce
            id: add-force
            forceEntryId: fe-1

          - action: selectEntry
            id: select-first
            forceId: ${{ steps.add-force.forceId }}
            entryId: se-unit-a

          - action: selectEntry
            id: select-second
            forceId: ${{ steps.add-force.forceId }}
            entryId: se-unit-a

          - expectedState:
              errors:
        ASSERTIONS

        """;

    private static void AssertPassed(SpecResult result)
        => Assert.True(result.Failures.Count == 0, string.Join("\n", result.Failures));

    /// <summary>
    /// An engine with fixed, nameable node ids and a fixed error list. Deliberately not a real
    /// engine: the claims here are about the matcher, and a real engine's ids are minted per run,
    /// which is the very thing that makes them unwritable as literals.
    /// </summary>
    private sealed class ScriptedEngine(IReadOnlyList<ValidationErrorState> errors) : IRosterEngine
    {
        private int _selections;

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

        public ActionOutputs AddForce(string forceEntryId, string catalogueId) => new()
        {
            ForceId = "force-node-1",
            Categories = new Dictionary<string, List<string>> { ["cat-troops"] = ["cat-node-1"] },
            Selections = new Dictionary<string, List<string>> { ["se-auto"] = ["auto-node-1", "auto-node-2"] },
        };

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
            => new() { ForceId = "child-force-node-1" };

        public void RemoveForce(string forceId)
        {
        }

        public ActionOutputs SelectEntry(string forceId, string entryId)
            => new() { SelectionId = $"sel-node-{++_selections}" };

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
            => new() { SelectionId = $"sel-node-{++_selections}" };

        public void DeselectSelection(string forceId, string selectionId)
        {
        }

        public void SetSelectionCount(string forceId, string selectionId, int count)
        {
        }

        public ActionOutputs DuplicateSelection(string forceId, string selectionId)
            => new() { SelectionId = $"sel-node-{++_selections}" };

        public ActionOutputs DuplicateForce(string forceId) => new() { ForceId = "force-node-2" };

        public void SetCostLimit(string costTypeId, decimal value)
        {
        }

        public RosterState GetRosterState() => new("roster", "gs", [], [], errors);

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => errors;

        public void Cleanup()
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The same claim as <see cref="ErrorAssertionAddressingTests.On_PointedAtTheWrongNodeOfTheSameEntry_Fails"/>,
/// made against the real in-process BattleScribe engine and real minted node ids.
/// <para>
/// The scripted version proves the matcher compares the field. This proves the field is worth
/// comparing: two forces built from ONE force entry each own a Troops category node built from ONE
/// catalogue category entry, and the engine raises the violation on exactly one of them. Every field
/// an assertion could match before #423 — owner type, owner entry id, <c>from:</c>, message — is
/// identical for both candidates. Only the node id is not, and pointing at the wrong one has to fail
/// or the flip bought nothing.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ErrorAssertionWrongNodeTests(ITestOutputHelper output)
{
    private const string SpecTemplate = """
        id: wrong-node-proof
        category: constraint
        description: harness

        setup:
          gameSystem:
            categoryEntries:
              - id: cat-troops
                name: Troops
            forceEntries:
              - id: fe-patrol
                name: Patrol
                categoryLinks:
                  - id: cl-fe-troops
                    targetId: cat-troops
                    name: Troops
          catalogues:
            - id: cat-1
              selectionEntries:
                - id: se-unit-a
                  name: Unit A
                  type: unit
                  categoryLinks:
                    - id: cl-sea-troops
                      targetId: cat-troops
                      name: Troops
                      primary: true
                  constraints:
                    - id: con-max
                      type: max
                      value: 1
                      field: selections
                      scope: parent

        steps:
          - action: addForce
            id: add-first
            forceEntryId: fe-patrol

          - action: addForce
            id: add-second
            forceEntryId: fe-patrol

          - action: selectEntry
            forceId: ${{ steps.add-first.forceId }}
            entryId: se-unit-a

          - action: selectEntry
            forceId: ${{ steps.add-first.forceId }}
            entryId: se-unit-a

          - expectedState:
              errors:
                - on: category ${{ steps.ADDRESSED.categories.cat-troops }}
                  from: se-unit-a/con-max

        """;

    [Fact]
    public void ANodeAddressedAssertion_FailsWhenPointedAtTheSiblingNodeOfTheSameCategoryEntry()
    {
        var right = RunAddressing("add-first");
        Assert.True(right.Failures.Count == 0, string.Join("\n", right.Failures));

        var wrong = RunAddressing("add-second");
        var failure = Assert.Single(wrong.Failures);
        output.WriteLine(failure);

        // Not "no error at all" — the error is there, on the sibling node, and the assertion
        // rejects it. That is the distinction the entry-addressed form could not draw.
        Assert.Contains("not found in", failure, StringComparison.Ordinal);
        Assert.Contains("category ", failure, StringComparison.Ordinal);
    }

    private static SpecResult RunAddressing(string stepId)
    {
        var yaml = SpecTemplate.Replace("ADDRESSED", stepId, StringComparison.Ordinal);
        using var engine = new BattleScribeRosterEngine();
        return new RosterRunner(engine, engineName: "battlescribe").Run(SpecLoader.LoadFromYaml(yaml));
    }
}

/// <summary>
/// #428, against the real in-process BattleScribe engine: ONE step mints TWO selections of ONE
/// catalogue entry, and a spec can name each of them.
/// <para>
/// <c>min: 2, scope: force</c> makes <c>addForce</c> auto-add two Unit A. Each carries a Gear child
/// whose <c>min: 2</c>/<c>max: 1</c> contradiction fires permanently, so the engine raises one error
/// per Unit A — two errors identical in owner type, owner entry id, <c>from:</c> and message, and
/// distinguishable only by the node they were raised on. Before this the outputs map held one node
/// per entry id, so the second Unit A sat in the roster with nothing able to name it, and the two
/// errors could not be told apart at all.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class SiblingSelectionAddressingTests(ITestOutputHelper output)
{
    private const string SpecTemplate = """
        id: sibling-node-proof
        category: constraint
        description: harness

        setup:
          gameSystem:
            categoryEntries:
              - id: cat-troops
                name: Troops
            forceEntries:
              - id: fe-patrol
                name: Patrol
                categoryLinks:
                  - id: cl-fe-troops
                    targetId: cat-troops
                    name: Troops
          catalogues:
            - id: cat-1
              selectionEntries:
                - id: se-unit-a
                  name: Unit A
                  type: unit
                  categoryLinks:
                    - id: cl-sea-troops
                      targetId: cat-troops
                      name: Troops
                      primary: true
                  constraints:
                    - id: con-min-force
                      type: min
                      value: 2
                      field: selections
                      scope: force
                  selectionEntries:
                    - id: se-gear
                      name: Gear
                      type: upgrade
                      constraints:
                        - id: con-gear-min
                          type: min
                          value: 2
                          field: selections
                          scope: parent
                        - id: con-gear-max
                          type: max
                          value: 1
                          field: selections
                          scope: parent

        steps:
          - action: addForce
            id: add-patrol
            forceEntryId: fe-patrol

          - expectedState:
              errors:
                - on: selection ${{ steps.add-patrol.selections.se-unit-a FIRST }}
                  from: se-gear/con-gear-max
                - on: selection ${{ steps.add-patrol.selections.se-unit-a SECOND }}
                  from: se-gear/con-gear-max

        """;

    /// <summary>
    /// Both siblings are nameable, and the bare form is the first — so the address the corpus has
    /// always written keeps meaning the node it has always meant.
    /// </summary>
    [Fact]
    public void EachOfTwoSelectionsOfOneEntry_IsNameableSeparately()
    {
        AssertPassed(Run(first: "", second: "[1]"));

        // …and the bare form is exactly [0], not merely "one of them".
        AssertPassed(Run(first: "[0]", second: "[1]"));
    }

    /// <summary>
    /// Pointed at the wrong sibling, the assertion fails. Consume-once means naming ONE node twice
    /// leaves the other error unclaimed, which is only true if the two addresses really are
    /// different nodes — the claim the entry-addressed form could not make.
    /// </summary>
    [Fact]
    public void PointingBothAssertionsAtOneSibling_Fails()
    {
        var result = Run(first: "", second: "");

        Assert.NotEmpty(result.Failures);
        foreach (var failure in result.Failures)
        {
            output.WriteLine(failure);
        }

        Assert.Contains(result.Failures, f => f.Contains("not found in", StringComparison.Ordinal));
    }

    /// <summary>
    /// And an index past the end says so, naming the count, rather than resolving to nothing and
    /// reporting a missing error.
    /// </summary>
    [Fact]
    public void NamingAThirdSiblingOfTwo_ReportsTheCount()
    {
        var failure = Assert.Single(Run(first: "", second: "[2]").Failures);
        output.WriteLine(failure);

        Assert.Contains("2 node(s)", failure, StringComparison.Ordinal);
    }

    private static SpecResult Run(string first, string second)
    {
        var yaml = SpecTemplate
            .Replace(" FIRST ", $"{first} ", StringComparison.Ordinal)
            .Replace(" SECOND ", $"{second} ", StringComparison.Ordinal);
        using var engine = new BattleScribeRosterEngine();
        return new RosterRunner(engine, engineName: "battlescribe").Run(SpecLoader.LoadFromYaml(yaml));
    }

    private static void AssertPassed(SpecResult result)
        => Assert.True(result.Failures.Count == 0, string.Join("\n", result.Failures));
}
