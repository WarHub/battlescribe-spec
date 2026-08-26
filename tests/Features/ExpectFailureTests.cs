using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The action-level failure primitive: a step can assert that the engine <b>refused</b> its action,
/// as distinct from <c>expectedState.errors</c>, which asserts the validation list of a roster the
/// engine accepted.
/// <para>
/// Most of what is worth testing here is what <c>expectFailure</c> refuses to be satisfied by. A
/// primitive that matched any failure would be satisfied by a typo in the spec, by an engine that
/// does not implement the action, and by a dead adapter — and each of those would read as a passing
/// conformance test. So the negative cases below carry the weight, one per
/// <see cref="ActionFailureKind"/>.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ExpectFailureTests(ITestOutputHelper output)
{
    /// <summary>The declaration in its shortest form: this action must be refused.</summary>
    private const string MustFail = "expectFailure: true";

    /// <summary>…and constrained to a substring of what the engine said.</summary>
    private static string MustFailWith(string message)
        => $"expectFailure:\n  messageContains: \"{message}\"";

    // ── The assertion itself ─────────────────────────────────────────

    [Fact]
    public void AnEngineRefusal_SatisfiesTheDeclaration()
    {
        var result = Run(
            throws: new InvalidOperationException("Content is not allowed in prolog."),
            expectFailure: MustFail);

        AssertPassed(result);
    }

    [Fact]
    public void AnEngineRefusal_MatchesOnItsOwnMessage()
    {
        AssertPassed(Run(
            throws: new InvalidOperationException("Content is not allowed in prolog."),
            expectFailure: MustFailWith("not allowed in prolog")));
    }

    [Fact]
    public void AMessageThatDoesNotMatch_FailsAndShowsWhatTheEngineActuallySaid()
    {
        var failure = Assert.Single(Run(
            throws: new InvalidOperationException("Content is not allowed in prolog."),
            expectFailure: MustFailWith("unexpected end of file")).Failures);

        output.WriteLine(failure);
        Assert.Contains("refused 'loadRoster' as expected", failure, StringComparison.Ordinal);
        Assert.Contains("not allowed in prolog", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inverse guard. Without it the primitive would be one-sided: a spec could declare a
    /// refusal, watch the engine accept the payload, and pass.
    /// </summary>
    [Fact]
    public void AnActionThatSucceeds_FailsTheStep_AndNamesTheOptOut()
    {
        var failure = Assert.Single(Run(throws: null, expectFailure: MustFail).Failures);

        output.WriteLine(failure);
        Assert.Contains("but it succeeded", failure, StringComparison.Ordinal);
        Assert.Contains("battlescribe: false", failure, StringComparison.Ordinal);
    }

    // ── What must NOT satisfy it ─────────────────────────────────────

    /// <summary>
    /// The reason the discriminator exists. <c>SpecAddressingException</c> is what an adapter throws
    /// when the spec named something that is not there — every engine raises it identically, from
    /// its own lookup, before the engine is asked anything. If it satisfied the declaration, a spec
    /// could assert its own typo.
    /// </summary>
    [Fact]
    public void AnAddressingFailure_DoesNotSatisfyIt()
    {
        var failure = Assert.Single(Run(
            throws: new SpecAddressingException("Entry 'se-typo' not found in force 'f1'."),
            expectFailure: MustFail).Failures);

        output.WriteLine(failure);
        Assert.Contains("could not resolve an id this spec named", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// #309, restated at action level. Three of the four engines do not implement roster load
    /// (#450), and the interface default throws <see cref="NotSupportedException"/>. Were that a
    /// refusal, all three would pass every malformed-input spec in #23 without parsing a byte.
    /// </summary>
    [Fact]
    public void AnEngineThatDoesNotImplementTheAction_DoesNotSatisfyIt()
    {
        var failure = Assert.Single(Run(
            throws: new NotSupportedException("This engine does not support roster load."),
            expectFailure: MustFail).Failures);

        output.WriteLine(failure);
        Assert.Contains("does not implement 'loadRoster' at all", failure, StringComparison.Ordinal);
        Assert.Contains("skipEngines: [battlescribe]", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AHarnessFault_DoesNotSatisfyIt()
    {
        var failure = Assert.Single(Run(
            throws: new TimeoutException("No response from adapter after 30s."),
            expectFailure: MustFail).Failures);

        output.WriteLine(failure);
        Assert.Contains("harness fault", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// An adapter that has not adopted the <c>kind</c> field cannot have its refusals asserted. The
    /// safe direction: the spec fails, naming what to implement, rather than passing on a failure
    /// nothing examined.
    /// </summary>
    [Fact]
    public void AnUnclassifiedProtocolFailure_DoesNotSatisfyIt()
    {
        var failure = Assert.Single(Run(
            throws: new ActionFailedException(
                "Action 'loadRoster' failed: something went wrong",
                ActionFailureKind.Unclassified,
                "something went wrong"),
            expectFailure: MustFail).Failures);

        output.WriteLine(failure);
        Assert.Contains("sent no 'kind'", failure, StringComparison.Ordinal);
        Assert.Contains("docs/adapter-protocol.md", failure, StringComparison.Ordinal);
    }

    // ── Classification ───────────────────────────────────────────────

    [Fact]
    public void ClassifyPutsEachFailureInItsOwnBox()
    {
        Assert.Equal(ActionFailureKind.Address, ActionFailure.Classify(new SpecAddressingException("x")));
        Assert.Equal(ActionFailureKind.Unsupported, ActionFailure.Classify(new NotSupportedException("x")));
        Assert.Equal(ActionFailureKind.Harness, ActionFailure.Classify(new TimeoutException("x")));
        Assert.Equal(ActionFailureKind.Harness, ActionFailure.Classify(new HarnessFaultException("x")));
        Assert.Equal(ActionFailureKind.Engine, ActionFailure.Classify(new InvalidOperationException("x")));
    }

    /// <summary>
    /// An adapter's verdict is never re-derived downstream — it rides the wire and is read back as
    /// given, including the deliberately unhelpful <see cref="ActionFailureKind.Unclassified"/>.
    /// </summary>
    [Fact]
    public void AVerdictSurvivesTheWireRoundTrip()
    {
        foreach (var kind in new[]
        {
            ActionFailureKind.Engine,
            ActionFailureKind.Address,
            ActionFailureKind.Harness,
            ActionFailureKind.Unsupported,
        })
        {
            Assert.Equal(kind, ActionFailure.FromWire(ActionFailure.ToWire(kind)));
        }

        Assert.Null(ActionFailure.ToWire(ActionFailureKind.Unclassified));
        Assert.Equal(ActionFailureKind.Unclassified, ActionFailure.FromWire(null));
        Assert.Equal(ActionFailureKind.Unclassified, ActionFailure.FromWire("something-new"));
    }

    /// <summary>
    /// <c>messageContains</c> matches the engine's own words, not the <c>Action 'X' failed:</c>
    /// framing the transport wraps them in — otherwise every spec would be pinned to harness wording.
    /// </summary>
    [Fact]
    public void MessageMatchingUsesTheEnginesOwnWords()
    {
        var framed = new ActionFailedException(
            "Action 'loadRoster' failed: Content is not allowed in prolog.",
            ActionFailureKind.Engine,
            "Content is not allowed in prolog.");

        Assert.Equal("Content is not allowed in prolog.", ActionFailure.MessageOf(framed));
        Assert.True(ExpectFailure.IsSatisfiedBy(framed, new ExpectFailureDef { MessageContains = "PROLOG" }));
        Assert.False(ExpectFailure.IsSatisfiedBy(framed, new ExpectFailureDef { MessageContains = "failed:" }));
    }

    // ── Per-engine divergence ────────────────────────────────────────

    /// <summary>
    /// The <c>false</c> form: an engine that accepts a payload the others reject is a finding, and
    /// the spec records it rather than skipping the engine. A skip says "we did not look".
    /// </summary>
    [Fact]
    public void AnEngineDeclaredToAcceptTheInput_MustSucceed()
    {
        const string PerEngine = "expectFailure:\n  engines:\n    newrecruit: false";

        AssertPassed(Run(throws: null, expectFailure: PerEngine, engineName: "newrecruit"));
        AssertPassed(Run(
            throws: new InvalidOperationException("Content is not allowed in prolog."),
            expectFailure: PerEngine,
            engineName: "battlescribe"));

        // And the engine declared to accept it does not get to refuse quietly either.
        var failure = Assert.Single(Run(
            throws: new InvalidOperationException("Content is not allowed in prolog."),
            expectFailure: PerEngine,
            engineName: "newrecruit").Failures);
        output.WriteLine(failure);
        Assert.Contains("InvalidOperationException", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void APerEngineMessageReplacesTheBaseOne()
    {
        const string PerEngine = """
            expectFailure:
              messageContains: "not allowed in prolog"
              engines:
                newrecruit:
                  messageContains: "Unexpected close tag"
            """;

        AssertPassed(Run(
            throws: new InvalidOperationException("Unexpected close tag </roster>"),
            expectFailure: PerEngine,
            engineName: "newrecruit"));

        Assert.NotEmpty(Run(
            throws: new InvalidOperationException("Unexpected close tag </roster>"),
            expectFailure: PerEngine,
            engineName: "battlescribe").Failures);
    }

    // ── The run continues past a refusal ─────────────────────────────

    /// <summary>
    /// A refused step does not end the run. This is the property #23 needs: whether a rejected load
    /// leaves the previous roster intact is the conformance question, and it is unanswerable if the
    /// harness stops at the refusal.
    /// </summary>
    [Fact]
    public void ARefusedStep_LeavesTheRunAliveForTheNextAssertion()
    {
        using var engine = new ScriptedEngine(new InvalidOperationException("Content is not allowed in prolog."));
        var result = new RosterRunner(engine, engineName: "battlescribe").Run(SpecLoader.LoadFromYaml("""
            id: expect-failure-continues
            category: roundtrip
            description: harness

            setup:
              gameSystem:
                forceEntries:
                  - id: fe-1
                    name: Force
              catalogues:
                - id: cat-1
                  selectionEntries:
                    - id: se-1
                      name: Unit
                      type: unit

            steps:
              - action: addForce
                id: add-force
                forceEntryId: fe-1

              - action: loadRoster
                content: "<roster truncated"
                expectFailure: true

              - expectedState:
                  forceCount: 1
            """));

        AssertPassed(result);
        Assert.True(engine.ReachedStateAssertion, "the run stopped at the refusal instead of continuing");
    }

    // ── Rejected before it runs ──────────────────────────────────────

    /// <summary>
    /// Structural mistakes are lint errors, not mysterious runtime behaviour — the same reason a
    /// literal <c>on:</c> id is rejected before a run rather than reading as a missing error (#419).
    /// </summary>
    [Fact]
    public void ExpectFailureOnAnAssertionStep_IsRejected()
    {
        var ex = Assert.Throws<SpecValidationException>(() => SpecLoader.LoadFromYaml("""
            id: expect-failure-misplaced
            category: roundtrip
            description: harness
            setup:
              gameSystem:
                forceEntries: [{ id: fe-1, name: Force }]
              catalogues: [{ id: cat-1 }]
            steps:
              - expectedState:
                  forceCount: 0
                expectFailure: true
            """));

        output.WriteLine(ex.Message);
        Assert.Contains("belongs on an action step", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused step mints nothing, so an <c>id</c> on it can never be referenced. Caught at load
    /// rather than surfacing later as an unresolvable expression in an unrelated step.
    /// </summary>
    [Fact]
    public void AnIdOnAStepNoEngineAccepts_IsRejected()
    {
        var ex = Assert.Throws<SpecValidationException>(() => SpecLoader.LoadFromYaml("""
            id: expect-failure-dead-id
            category: roundtrip
            description: harness
            setup:
              gameSystem:
                forceEntries: [{ id: fe-1, name: Force }]
              catalogues: [{ id: cat-1 }]
            steps:
              - action: loadRoster
                id: load-bad
                content: "<roster truncated"
                expectFailure: true
            """));

        output.WriteLine(ex.Message);
        Assert.Contains("can never be referenced", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>…but it is legitimate when some engine is declared to accept the input, because on
    /// that engine's run the step really does produce outputs.</summary>
    [Fact]
    public void AnIdIsAllowedWhenSomeEngineAcceptsTheInput()
    {
        var spec = SpecLoader.LoadFromYaml("""
            id: expect-failure-live-id
            category: roundtrip
            description: harness
            setup:
              gameSystem:
                forceEntries: [{ id: fe-1, name: Force }]
              catalogues: [{ id: cat-1 }]
            steps:
              - action: loadRoster
                id: load-bad
                content: "<roster truncated"
                expectFailure:
                  engines:
                    newrecruit: false
            """);

        Assert.Equal("load-bad", spec.Steps[0].Id);
        Assert.False(spec.Steps[0].ExpectFailure!.ForEngine("newrecruit").IsExpected);
        Assert.True(spec.Steps[0].ExpectFailure!.ForEngine("battlescribe").IsExpected);
    }

    [Fact]
    public void AnUnknownKeyIsRejectedByName()
    {
        var ex = Assert.Throws<YamlDotNet.Core.YamlException>(() => SpecLoader.LoadFromYaml("""
            id: expect-failure-typo
            category: roundtrip
            description: harness
            setup:
              gameSystem:
                forceEntries: [{ id: fe-1, name: Force }]
              catalogues: [{ id: cat-1 }]
            steps:
              - action: loadRoster
                content: "<roster truncated"
                expectFailure:
                  messageContain: "typo"
            """));

        output.WriteLine(ex.Message);
        Assert.Contains("messageContain", ex.Message, StringComparison.Ordinal);
    }

    // ── Harness ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs a one-action spec whose <c>loadRoster</c> carries <paramref name="expectFailure"/>
    /// against an engine that throws <paramref name="throws"/> (or succeeds, when null).
    /// <paramref name="expectFailure"/> is the whole declaration starting at the key, written flat
    /// and indented into place here — YAML block indentation is easier to get right once than at
    /// every call site.
    /// </summary>
    private static SpecResult Run(
        Exception? throws,
        string expectFailure,
        string engineName = "battlescribe")
    {
        var yaml = """
            id: expect-failure
            category: roundtrip
            description: harness

            setup:
              gameSystem:
                forceEntries:
                  - id: fe-1
                    name: Force
              catalogues:
                - id: cat-1
                  selectionEntries:
                    - id: se-1
                      name: Unit
                      type: unit

            steps:
              - action: loadRoster
                content: "<roster truncated"
            DECLARATION
            """.Replace("DECLARATION", Indent(expectFailure, 4), StringComparison.Ordinal);

        using var engine = new ScriptedEngine(throws);
        return new RosterRunner(engine, engineName: engineName).Run(SpecLoader.LoadFromYaml(yaml));
    }

    private static string Indent(string yaml, int spaces)
        => string.Join(
            "\n",
            yaml.Split('\n').Select(line => line.Length == 0 ? line : new string(' ', spaces) + line));

    private static void AssertPassed(SpecResult result)
        => Assert.True(result.Failures.Count == 0, string.Join("\n", result.Failures));

    /// <summary>
    /// An engine whose <c>loadRoster</c> throws whatever the test hands it, or succeeds when handed
    /// null. Scripted rather than real because the claims here are about how the runner reads a
    /// failure, and a real engine can only produce the one kind it happens to raise.
    /// </summary>
    private sealed class ScriptedEngine(Exception? loadThrows) : IRosterEngine
    {
        public bool ReachedStateAssertion { get; private set; }

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

        public ActionOutputs AddForce(string forceEntryId, string catalogueId) => new() { ForceId = "force-1" };

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
            => new() { ForceId = "child-force-1" };

        public void RemoveForce(string forceId) { }

        public ActionOutputs SelectEntry(string forceId, string entryId) => new() { SelectionId = "sel-1" };

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
            => new() { SelectionId = "sel-2" };

        public void DeselectSelection(string forceId, string selectionId) { }

        public void SetSelectionCount(string forceId, string selectionId, int count) { }

        public ActionOutputs DuplicateSelection(string forceId, string selectionId) => new() { SelectionId = "sel-3" };

        public ActionOutputs DuplicateForce(string forceId) => new() { ForceId = "force-2" };

        public void SetCostLimit(string costTypeId, decimal value) { }

        public void LoadRoster(string xml)
        {
            if (loadThrows is not null)
            {
                throw loadThrows;
            }
        }

        public RosterState GetRosterState()
        {
            ReachedStateAssertion = true;
            return new RosterState(
                Name: "roster",
                GameSystemId: "gs-1",
                Forces: [new ForceState("force-1", "Force", "cat-1", [], EntryId: "fe-1")],
                Costs: [],
                ValidationErrors: []);
        }

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

        public void Dispose() { }
    }
}
