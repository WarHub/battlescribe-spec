using System.Xml.Linq;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Scenario;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <see cref="ScenarioRunner"/> against recording fakes: what crosses between the data editor and the
/// roster editor, and when. The engines here do nothing but remember what they were asked, so each test
/// reads as the hand-off rule it pins — the roster editor sees data only as of the last <c>saveData</c>,
/// and only when it starts or opens a roster.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ScenarioRunnerTests(ITestOutputHelper output)
{
    private const string Setup = """
        id: scenario-under-test
        category: handoff
        description: A scenario run against recording fakes.
        setup:
          edit: cat-1
          gameSystem:
            id: gs-1
            name: GS
          catalogues:
            - id: cat-1
              name: Cat
              gameSystemId: gs-1
        {MORE_CATALOGUES}
        steps:
        """;

    [Fact]
    public void Seed_ExportsEveryFile_ThenReopensEdit()
    {
        var (data, roster, result) = Run(
            """
              - action: newRoster
            """,
            moreCatalogues: """
                - id: cat-2
                  name: Cat 2
                  gameSystemId: gs-1
            """);

        AssertPassed(result);
        Assert.Equal(
            ["open gs-1", "export gs-1", "open cat-1", "export cat-1", "open cat-2", "export cat-2", "open cat-1"],
            data.Log);
        Assert.Equal(["gs-1.gst", "cat-1.cat", "cat-2.cat"], Assert.Single(roster.Setups).Select(f => f.FileName));
    }

    [Fact]
    public void UnsavedEdit_DoesNotReachNewRoster()
    {
        var (_, roster, result) = Run(
            """
              - data:
                  - action: setFields
                    entryId: cat-1
                    fields:
                      name: Edited
              - action: newRoster
            """);

        AssertPassed(result);
        Assert.DoesNotContain("Edited", CatalogueOf(Assert.Single(roster.Setups)), StringComparison.Ordinal);
    }

    [Fact]
    public void SavedEdit_ReachesNewRoster_ThroughSaveAndReopen()
    {
        var (data, roster, result) = Run(
            """
              - data:
                  - action: setFields
                    entryId: cat-1
                    fields:
                      name: Edited
              - action: saveData
              - action: newRoster
            """);

        AssertPassed(result);
        Assert.Contains("name=\"Edited\"", CatalogueOf(Assert.Single(roster.Setups)), StringComparison.Ordinal);
        Assert.Equal(["reload cat-1", "export cat-1"], data.Log.TakeLast(2));
    }

    [Fact]
    public void OpenRoster_LoadsSavedXml_AfterSetupFromCurrentData()
    {
        var (_, roster, result) = Run(
            """
              - action: newRoster
              - action: saveRoster
                file: my-list
              - data:
                  - action: setFields
                    entryId: cat-1
                    fields:
                      name: Edited
              - action: saveData
              - action: openRoster
                file: my-list
            """);

        AssertPassed(result);

        // The last cleanup is the end of the run's.
        string[] expected = ["cleanup", "setup", "export", "cleanup", "setup", "load <roster id=\"saved-1\"/>", "cleanup"];
        Assert.Equal(expected, roster.Log);
        Assert.Contains("name=\"Edited\"", CatalogueOf(roster.Setups[1]), StringComparison.Ordinal);
    }

    /// <summary>
    /// A misspelled roster name is the spec's mistake. It must never satisfy <c>expectFailure</c> — the
    /// rule #25 established for actions — or a spec could assert its own typo on every engine at once.
    /// </summary>
    [Fact]
    public void OpenRoster_UnknownFile_IsSpecError_EvenUnderExpectFailure()
    {
        var (_, roster, result) = Run(
            """
              - action: newRoster
              - action: saveRoster
                file: my-list
              - action: openRoster
                file: my-lsit
                expectFailure: true
            """);

        var failure = Assert.Single(result.Failures);
        output.WriteLine(failure);
        Assert.StartsWith("Step 2:", failure, StringComparison.Ordinal);
        Assert.Contains("no roster was saved as 'my-lsit'", failure, StringComparison.Ordinal);
        Assert.Contains("my-list", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(roster.Log, l => l.StartsWith("load", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveRoster_BeforeAnyRoster_IsSpecError()
    {
        var (_, roster, result) = Run(
            """
              - action: saveRoster
                file: my-list
            """);

        Assert.Contains("needs a roster", Assert.Single(result.Failures), StringComparison.Ordinal);
        Assert.DoesNotContain("export", roster.Log);
    }

    [Fact]
    public void RosterBlock_BeforeAnyRoster_IsSpecError()
    {
        var (_, roster, result) = Run(
            """
              - roster:
                  - action: addForce
                    forceEntryId: fe-1
            """);

        Assert.Contains("start one with 'newRoster' or 'openRoster'", Assert.Single(result.Failures), StringComparison.Ordinal);
        Assert.Empty(roster.Forces);
    }

    /// <summary>
    /// The refusal #280 needs: data edited so the saved roster can no longer be opened. The run continues
    /// so the next block can assert what the refusal left behind.
    /// </summary>
    [Fact]
    public void ExpectFailure_OnOpenRoster_Satisfied_RunContinues()
    {
        var (_, roster, result) = Run(
            """
              - action: newRoster
              - action: saveRoster
                file: my-list
              - action: openRoster
                file: my-list
                expectFailure:
                  messageContains: catalogue missing
              - roster:
                  - expectedState:
                      forceCount: 0
            """,
            roster: new RecordingRosterEditor { LoadThrows = new InvalidOperationException("catalogue missing") });

        AssertPassed(result);
        Assert.True(roster.StateReads > 0);
    }

    /// <summary>
    /// A roster editor that cannot read the data directory is a fault, never a refusal. Over the protocol a
    /// setup failure arrives as bare text whatever caused it — this one is what a host's null reference
    /// becomes on the wire — so letting it satisfy expectFailure would pass a spec on a broken harness.
    /// </summary>
    [Fact]
    public void SetupErrors_NeverSatisfyExpectFailure()
    {
        var (_, _, result) = Run(
            """
              - action: newRoster
                expectFailure: true
            """,
            roster: new RecordingRosterEditor { SetupErrors = ["Object reference not set to an instance of an object."] });

        var failure = Assert.Single(result.Failures);
        Assert.Contains("harness fault", failure, StringComparison.Ordinal);
        Assert.Contains("Object reference not set", failure, StringComparison.Ordinal);
        Assert.NotNull(result.HarnessError);
    }

    [Fact]
    public void ExpectFailure_OnABlock_IsSpecError()
    {
        var (_, _, result) = Run(
            """
              - action: newRoster
              - roster:
                  - action: addForce
                    forceEntryId: fe-1
                expectFailure: true
            """);

        Assert.Contains("belong to a hand-off", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Data Editor writes back to the file it opened, even when an edit changed that file's own id: the
    /// roster editor then reads one catalogue under the new id, not the stale one beside it.
    /// </summary>
    [Fact]
    public void SaveData_AfterAnIdEdit_ReplacesTheOpenedFile()
    {
        var (_, roster, result) = Run(
            """
              - data:
                  - action: setFields
                    entryId: cat-1
                    fields:
                      id: cat-9
              - action: saveData
              - action: newRoster
            """);

        AssertPassed(result);
        Assert.Equal(["gs-1.gst", "cat-9.cat"], Assert.Single(roster.Setups).Select(f => f.FileName));
    }

    [Fact]
    public void ExpectFailure_OnOpenRoster_ThatSucceeds_Fails()
    {
        var (_, _, result) = Run(
            """
              - action: newRoster
              - action: saveRoster
                file: my-list
              - action: openRoster
                file: my-list
                expectFailure: true
            """);

        Assert.Contains("but it succeeded", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void StepIndex_IsGlobalAcrossBlocks()
    {
        var (_, _, result) = Run(
            """
              - action: newRoster
              - roster:
                  - action: addForce
                    forceEntryId: fe-1
                  - expectedState:
                      forceCount: 5
            """);

        Assert.StartsWith("Step 2:", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The before-step hook fires once for every step of every block and every hand-off, in order, with the
    /// global index.</b> Once is the point: the roster block's steps run through the roster runner, which has a
    /// hook of its own, and a scenario that announced them both there and here would open a <c>--break</c> REPL
    /// twice at one index. The index is the one the run's failures name — the failing assertion is "Step 5".
    /// </summary>
    [Fact]
    public void OnBeforeStep_FiresOncePerStepAndHandoff_WithTheIndexFailuresName()
    {
        var seen = new List<(int Index, ScenarioStepKind Kind, bool RosterEditorStarted, string What)>();
        var (_, _, result) = Run(
            """
              - data:
                  - action: setFields
                    entryId: cat-1
                    fields:
                      name: Edited
              - action: saveData
              - action: newRoster
              - roster:
                  - action: addForce
                    forceEntryId: fe-1
                  - action: dump
                  - expectedState:
                      forceCount: 5
            """,
            configure: runner => runner.OnBeforeStep = (index, step) =>
            {
                seen.Add((index, step.Kind, step.RosterEditorStarted, step.Kind switch
                {
                    ScenarioStepKind.Data => step.DataStep!.Action!,
                    ScenarioStepKind.Roster => step.RosterStep!.Action ?? "expectedState",
                    _ => step.Handoff!.Action!,
                }));
                return true;
            });

        Assert.Equal(
            [
                (0, ScenarioStepKind.Data, false, "setFields"),
                (1, ScenarioStepKind.Handoff, false, "saveData"),
                (2, ScenarioStepKind.Handoff, false, "newRoster"),
                (3, ScenarioStepKind.Roster, true, "addForce"),
                (4, ScenarioStepKind.Roster, true, "dump"),
                (5, ScenarioStepKind.Roster, true, "expectedState"),
            ],
            seen);
        Assert.StartsWith("Step 5:", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A hook that returns false stops the run before the step</b>: a stop at a hand-off means the roster
    /// editor never reads the data directory, and nothing after it is announced or run. The result carries no
    /// failure for the stop — the contract of <c>RosterRunner.OnBeforeStep</c>; the caller reports it.
    /// </summary>
    [Fact]
    public void OnBeforeStep_ReturningFalse_AtAHandoff_StopsTheRunBeforeIt()
    {
        var seen = new List<int>();
        var (data, roster, result) = Run(
            """
              - data:
                  - action: setFields
                    entryId: cat-1
                    fields:
                      name: Edited
              - action: saveData
              - action: newRoster
              - roster:
                  - action: addForce
                    forceEntryId: fe-1
            """,
            configure: runner => runner.OnBeforeStep = (index, _) =>
            {
                seen.Add(index);
                return index != 2;
            });

        Assert.Empty(result.Failures);
        Assert.Equal([0, 1, 2], seen);
        Assert.Equal(["reload cat-1", "export cat-1"], data.Log.TakeLast(2));
        Assert.DoesNotContain("setup", roster.Log);
        Assert.Empty(roster.Forces);
    }

    /// <summary>
    /// A stop inside a block: the step does not reach the engine, and no completion callback fires for it —
    /// a <c>--break</c> that quits must not dump a state for a step that never ran.
    /// </summary>
    [Fact]
    public void OnBeforeStep_ReturningFalse_InABlock_StopsBeforeTheStep_WithNoCompletionCallback()
    {
        var completed = new List<int>();
        var (_, roster, result) = Run(
            """
              - action: newRoster
              - roster:
                  - action: addForce
                    forceEntryId: fe-1
                  - action: addForce
                    forceEntryId: fe-1
            """,
            configure: runner =>
            {
                runner.OnBeforeStep = (index, _) => index != 2;
                runner.OnRosterStepCompleted = (index, _, _, _) => completed.Add(index);
            });

        Assert.Empty(result.Failures);
        Assert.Equal([1], completed);
        Assert.Equal(["fe-1@cat-1"], roster.Forces);
    }

    /// <summary>
    /// A hook that throws is a failure at the step it was announcing — not the "Setup failed" the run's own
    /// catch-all would call it — and stops the run.
    /// </summary>
    [Fact]
    public void OnBeforeStep_Throwing_FailsThatStep_AndStops()
    {
        var (_, roster, result) = Run(
            """
              - action: newRoster
              - action: saveRoster
                file: my-list
            """,
            configure: runner => runner.OnBeforeStep = (index, _) =>
                index == 1 ? throw new IOException("stdin went away") : true);

        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Step 1: before-step hook failed: IOException: stdin went away", failure, StringComparison.Ordinal);
        Assert.NotNull(result.HarnessError);
        Assert.DoesNotContain("export", roster.Log);
    }

    [Fact]
    public void AddForce_DefaultsCatalogue_FromDataDirectory()
    {
        var (_, roster, result) = Run(
            """
              - action: newRoster
              - roster:
                  - action: addForce
                    forceEntryId: fe-1
                  - expectedState:
                      forceCount: 1
            """);

        AssertPassed(result);
        Assert.Equal(["fe-1@cat-1"], roster.Forces);
    }

    [Fact]
    public void SaveData_OfAnUnknownRoot_FailsNamingIt()
    {
        var data = new RecordingDataEditor { ExportAfterReload = """<roster id="r-1"/>""" };
        var (_, _, result) = Run(
            """
              - action: saveData
            """,
            data: data);

        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Step 0: saveData:", failure, StringComparison.Ordinal);
        Assert.Contains("<roster>", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemWithTwoKinds_IsRejected()
    {
        var (_, _, result) = Run(
            """
              - action: newRoster
                roster:
                  - expectedState:
                      forceCount: 0
            """);

        Assert.Contains("exactly one of 'data', 'roster' or 'action'", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void FileOnSaveData_IsSpecError()
    {
        var (_, _, result) = Run(
            """
              - action: saveData
                file: cat-1.cat
            """);

        Assert.Contains("takes no 'file'", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Model_LoadsEveryItemKind()
    {
        var spec = SpecLoader.LoadScenarioFromYaml(Spec(
            """
              - data:
                  - action: reload
              - action: newRoster
              - roster:
                  - expectedState:
                      forceCount: 0
              - action: saveRoster
                file: my-list
            """));

        Assert.Equal("cat-1", spec.Setup.Edit);
        Assert.Collection(
            spec.Steps,
            s => Assert.Equal("reload", Assert.Single(s.Data!).Action),
            s => Assert.Equal(ScenarioHandoffs.NewRoster, s.Action),
            s => Assert.Equal(0, Assert.Single(s.Roster!).ExpectedState!.ForceCount),
            s => Assert.Equal(("saveRoster", "my-list"), (s.Action, s.File)));
    }

    // ── Harness ──────────────────────────────────────────────────────

    private static string Spec(string steps, string moreCatalogues = "")
        => Setup.Replace("{MORE_CATALOGUES}", moreCatalogues, StringComparison.Ordinal) + "\n" + steps;

    private (RecordingDataEditor Data, RecordingRosterEditor Roster, SpecResult Result) Run(
        string steps,
        string moreCatalogues = "",
        RecordingDataEditor? data = null,
        RecordingRosterEditor? roster = null,
        Action<ScenarioRunner>? configure = null)
    {
        data ??= new RecordingDataEditor();
        roster ??= new RecordingRosterEditor();
        var runner = new ScenarioRunner(data, roster, EngineIdentity.Of("battlescribe"));
        configure?.Invoke(runner);
        var result = runner.Run(SpecLoader.LoadScenarioFromYaml(Spec(steps, moreCatalogues)));
        foreach (var failure in result.Failures)
        {
            output.WriteLine(failure);
        }

        return (data, roster, result);
    }

    private static void AssertPassed(SpecResult result)
        => Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures));

    private static string CatalogueOf(IReadOnlyList<(string FileName, string Content)> files)
        => files.Single(f => f.FileName == "cat-1.cat").Content;

    /// <summary>
    /// A data editor holding each file as XML. <c>setFields</c> sets an attribute on the open file's root,
    /// so an edit is visible in exactly the exports that follow it — which is all a hand-off test needs.
    /// </summary>
    private sealed class RecordingDataEditor : IGameDataEngine
    {
        private readonly Dictionary<string, XElement> _files = new(StringComparer.Ordinal);
        private string _open = "";
        private bool _reloaded;

        public List<string> Log { get; } = [];

        /// <summary>When set, the export after a reload returns this instead of the open file.</summary>
        public string? ExportAfterReload { get; init; }

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        {
            _files[gameSystem.Id] = new XElement("gameSystem", new XAttribute("id", gameSystem.Id), new XAttribute("name", gameSystem.Name));
            foreach (var catalogue in catalogues)
            {
                _files[catalogue.Id] = new XElement("catalogue", new XAttribute("id", catalogue.Id), new XAttribute("name", catalogue.Name));
            }

            return [];
        }

        public void OpenFile(string id)
        {
            Log.Add($"open {id}");
            _open = id;
        }

        public void SetField(string entryId, string field, string? value) => _files[_open].SetAttributeValue(field, value);

        public void Reload()
        {
            Log.Add($"reload {_open}");
            _reloaded = true;
        }

        public string ExportActiveFile()
        {
            Log.Add($"export {_open}");
            return _reloaded && ExportAfterReload is { } replaced ? replaced : _files[_open].ToString(SaveOptions.DisableFormatting);
        }

        public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null) => new();

        public void RemoveEntry(string entryId) { }

        public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null) => new();

        public GameDataState GetState() => new();

        public void Dispose() { }
    }

    /// <summary>A roster editor that records what it read, saved and loaded.</summary>
    private sealed class RecordingRosterEditor : IRosterEngine
    {
        private int _saves;

        public List<string> Log { get; } = [];

        public List<IReadOnlyList<(string FileName, string Content)>> Setups { get; } = [];

        public List<string> Forces { get; } = [];

        public int StateReads { get; private set; }

        public Exception? LoadThrows { get; init; }

        public IReadOnlyList<string> SetupErrors { get; init; } = [];

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
            => throw new InvalidOperationException("A scenario's roster editor reads files, never the Protocol model.");

        public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
        {
            Log.Add("setup");
            Setups.Add(files);
            Forces.Clear();
            return SetupErrors;
        }

        public void Cleanup() => Log.Add("cleanup");

        public string ExportRosterXml()
        {
            Log.Add("export");
            return $"<roster id=\"saved-{++_saves}\"/>";
        }

        public void LoadRoster(string xml)
        {
            if (LoadThrows is not null)
            {
                throw LoadThrows;
            }

            Log.Add($"load {xml}");
        }

        public ActionOutputs AddForce(string forceEntryId, string catalogueId)
        {
            Forces.Add($"{forceEntryId}@{catalogueId}");
            return new() { ForceId = $"force-{Forces.Count}" };
        }

        public RosterState GetRosterState()
        {
            StateReads++;
            return new RosterState(
                "scenario-under-test",
                "gs-1",
                [.. Forces.Select((f, i) => new ForceState($"force-{i + 1}", "Force", "cat-1", []))],
                [],
                []);
        }

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) => new();

        public void RemoveForce(string forceId) { }

        public ActionOutputs SelectEntry(string forceId, string entryId) => new();

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) => new();

        public void DeselectSelection(string forceId, string selectionId) { }

        public void SetSelectionCount(string forceId, string selectionId, int count) { }

        public ActionOutputs DuplicateSelection(string forceId, string selectionId) => new();

        public ActionOutputs DuplicateForce(string forceId) => new();

        public void SetCostLimit(string costTypeId, decimal value) { }

        public void Dispose() { }
    }
}
