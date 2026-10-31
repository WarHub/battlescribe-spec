using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Scenario;

/// <summary>
/// Runs a scenario spec against one engine family's data editor and roster editor. See
/// <c>docs/adr/002-scenario-specs.md</c>.
/// <para>
/// <b>Composition, not a third runner.</b> A step in a <c>data:</c> block is executed by
/// <see cref="GameDataRunner"/> and a step in a <c>roster:</c> block by <see cref="RosterRunner"/>, through
/// their step-at-a-time entry points — the same code that runs it in a gamedata or roster spec, assertions,
/// <c>expectFailure</c>, <c>skipEngines</c> and snapshots included. What this class adds is what neither
/// domain has: the data directory, the saved rosters, the four hand-offs between the editors, one failure
/// list in step order, and one step index counted across blocks.
/// </para>
/// <para>
/// <b>The hand-off rule.</b> The roster editor sees data only as of the last <c>saveData</c>, and only when
/// it starts or opens a roster. Everything else the data editor holds is unsaved, and stays there.
/// </para>
/// </summary>
public sealed class ScenarioRunner
{
    private readonly IGameDataEngine _data;
    private readonly IRosterEngine _roster;
    private readonly EngineIdentity? _identity;
    private readonly GameDataRunner _dataRunner;
    private readonly RosterRunner _rosterRunner;
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, string> _savedRosters = new(StringComparer.Ordinal);
    private ScenarioDataDirectory _dataDirectory = new();
    private string? _setupEdit;
    private int _dataErrorsSeen;
    private int _rosterErrorsSeen;
    private string? _harnessError;

    /// <summary>
    /// Set once a <c>newRoster</c> or <c>openRoster</c> has been attempted. Before that the roster engine
    /// has never read any data, so a <c>roster:</c> block or a <c>saveRoster</c> would ask an engine with
    /// no setup — which fails somewhere inside the engine, with a message about the engine rather than
    /// about the spec. Set on the attempt, not the success: after an <c>openRoster</c> the spec declared
    /// refused, the next block asserting what the refusal left behind is the point.
    /// </summary>
    private bool _rosterEditorStarted;

    /// <param name="dataEngine">The family's data editor.</param>
    /// <param name="rosterEngine">The family's roster editor.</param>
    /// <param name="identity">
    /// The engine the spec is read for — <c>battlescribe-ui</c>, in the <c>battlescribe</c> family. Both kinds of
    /// block and the hand-offs resolve per-engine declarations through it, by the one rule
    /// (<see cref="EngineIdentity"/>): the driver's own entry, else its family's. Null — an anonymous adapter —
    /// applies no engine's declarations.
    /// </param>
    public ScenarioRunner(IGameDataEngine dataEngine, IRosterEngine rosterEngine, EngineIdentity? identity)
    {
        _data = dataEngine;
        _roster = rosterEngine;
        _identity = identity;
        _dataRunner = new GameDataRunner(dataEngine, identity);
        _rosterRunner = new RosterRunner(rosterEngine, dataSourceResolver: null, identity);
    }

    /// <summary>What a message calls the engine running this scenario.</summary>
    private string EngineLabel => _identity?.Name ?? EngineIdentity.Unnamed;

    /// <summary>Mirrors <see cref="RosterRunner.UpdateSnapshots"/>, for <c>expectedFile</c> steps in either kind of block.</summary>
    public bool UpdateSnapshots { get; set; }

    /// <summary>Mirrors <see cref="RosterRunner.OnDiverge"/>.</summary>
    public Func<ExportSnapshotAssertion.SnapshotDivergence, ExportSnapshotAssertion.SnapshotWriteTarget>? OnDiverge { get; set; }

    /// <summary>Called after each step of a <c>data:</c> block: global step index, step, data state.</summary>
    public Action<int, GameDataStepDef, GameDataState>? OnDataStepCompleted { get; set; }

    /// <summary>Called after each step of a <c>roster:</c> block: global step index, step, roster state, errors.</summary>
    public Action<int, StepDef, RosterState, IReadOnlyList<ValidationErrorState>>? OnRosterStepCompleted { get; set; }

    /// <summary>
    /// Called before each step of a block and before each hand-off, with the global step index the run's
    /// failures name (<c>Step N:</c>) and what is about to run. Return false to stop the run there: the step
    /// does not run, no completion callback fires for it, and the result holds what ran before it — the
    /// contract of <see cref="RosterRunner.OnBeforeStep"/>. Like that one, the result does not say the run was
    /// stopped; a caller that stops it says so itself (<c>bs-spec run --break</c> exits 130).
    /// <para>
    /// This is the one place a scenario step is announced. The inner roster runner's own hook stays unset,
    /// so a step in a <c>roster:</c> block fires once, not once here and again inside the block.
    /// </para>
    /// </summary>
    public Func<int, ScenarioStepView, bool>? OnBeforeStep { get; set; }

    /// <summary>Run a complete scenario spec. The result's failures are empty when it passed.</summary>
    public SpecResult Run(ScenarioSpecFile spec)
    {
        _errors.Clear();
        _savedRosters.Clear();
        _dataDirectory = new ScenarioDataDirectory();
        _dataErrorsSeen = 0;
        _rosterErrorsSeen = 0;
        _harnessError = null;
        _rosterEditorStarted = false;
        _setupEdit = spec.Setup.Edit;

        _dataRunner.UpdateSnapshots = UpdateSnapshots;
        _dataRunner.OnDiverge = OnDiverge;
        _dataRunner.OnStepCompleted = OnDataStepCompleted;
        _rosterRunner.UpdateSnapshots = UpdateSnapshots;
        _rosterRunner.OnDiverge = OnDiverge;
        _rosterRunner.OnStepCompleted = OnRosterStepCompleted;
        _rosterRunner.OnBeforeStep = null; // announced here instead: see OnBeforeStep
        _dataRunner.BeginSteps(spec.Id, spec.SourceDirectory);
        _rosterRunner.BeginSteps(spec.Id, spec.SourceDirectory);

        try
        {
            _data.SetTestContext(spec.Id);
            _roster.SetTestContext(spec.Id);

            if (SetUpDataEditor(spec))
            {
                var stepIndex = 0;
                foreach (var item in spec.Steps)
                {
                    if (!ExecuteItem(item, ref stepIndex))
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _errors.Add($"Setup failed: {ex.GetType().Name}: {ex.Message}");
            _harnessError ??= $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            CleanUp(_data.Cleanup, "data editor", spec.Id);
            CleanUp(_roster.Cleanup, "roster editor", spec.Id);
        }

        return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors])
        {
            HarnessError = _harnessError,
            SkippedSteps = [.. _rosterRunner.SkippedSteps],
        };
    }

    /// <summary>
    /// Set the data editor up exactly as a gamedata spec's is, then seed the data directory from the data
    /// editor's own export of every setup file. The files the roster editor first reads are therefore the
    /// ones its family's data editor writes — and they are the only XML a runner can come by, since the
    /// TestKit has no Protocol→XML generator of its own (that lives with the engines, in XmlGen).
    /// </summary>
    private bool SetUpDataEditor(ScenarioSpecFile spec)
    {
        var (gameSystem, catalogues) = SpecLoader.GetGameDataSetupData(spec.Setup);
        var setupErrors = _data.Setup(gameSystem, catalogues);
        if (setupErrors.Count > 0)
        {
            _errors.AddRange(setupErrors.Select(e => $"Setup error: {e}"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(spec.Setup.Edit))
        {
            _errors.Add("Setup error: setup.edit is required (the id of the file the data editor has open)");
            return false;
        }

        try
        {
            foreach (var id in new[] { gameSystem.Id }.Concat(catalogues.Select(c => c.Id)))
            {
                _data.OpenFile(id);
                _dataDirectory.Write(_data.ExportActiveFile());
            }
        }
        catch (NotSupportedException ex)
        {
            _errors.Add(
                $"Setup error: a scenario's data directory is the data editor's own export of its files, but " +
                $"engine '{EngineLabel}' reports none ({ex.Message}). Implement ExportActiveFile, or opt this " +
                $"engine out with 'engines: {{{EngineLabel}: skip}}' on the spec.");
            return false;
        }

        _data.OpenFile(spec.Setup.Edit);
        return true;
    }

    /// <summary>
    /// Execute one item of <c>steps</c>: every step of a block, or one hand-off. Each step and each hand-off
    /// takes the next global index, counted from 0 as the other runners count: "Step 7" is the eighth down the
    /// file, whichever block it sits in. Returns false when the run must stop.
    /// </summary>
    private bool ExecuteItem(ScenarioStepDef item, ref int stepIndex)
    {
        var kinds = (item.Data is null ? 0 : 1) + (item.Roster is null ? 0 : 1) + (item.Action is null ? 0 : 1);
        if (kinds != 1)
        {
            _errors.Add($"Step {stepIndex}: a scenario item is exactly one of 'data', 'roster' or 'action' (this one has {kinds})");
            return false;
        }

        if (item.Action is null && (item.File is not null || item.ExpectFailure is not null))
        {
            // Both belong to a hand-off. On a block they would be read by nothing — and an expectFailure
            // indented one level too shallow, meant for a step inside the block, would vanish silently.
            _errors.Add(
                $"Step {stepIndex}: 'file' and 'expectFailure' belong to a hand-off; a data or roster block " +
                "takes neither — put expectFailure on the step inside the block");
            return false;
        }

        if (item.Data is { } dataSteps)
        {
            foreach (var step in dataSteps)
            {
                var index = stepIndex++;
                if (!BeforeStep(index, new ScenarioStepView(ScenarioStepKind.Data, _rosterEditorStarted, dataStep: step)))
                {
                    return false;
                }

                var proceed = _dataRunner.ExecuteStep(step, index);
                CollectFailures();
                if (!proceed)
                {
                    return false;
                }
            }

            return true;
        }

        if (item.Roster is { } rosterSteps)
        {
            if (!_rosterEditorStarted)
            {
                _errors.Add($"Step {stepIndex}: a roster block needs a roster — start one with 'newRoster' or 'openRoster' first");
                return false;
            }

            foreach (var step in rosterSteps)
            {
                var index = stepIndex++;
                if (!BeforeStep(index, new ScenarioStepView(ScenarioStepKind.Roster, _rosterEditorStarted, rosterStep: step)))
                {
                    return false;
                }

                var proceed = _rosterRunner.ExecuteStep(step, index);
                CollectFailures();
                if (!proceed)
                {
                    return false;
                }
            }

            return true;
        }

        var handoffIndex = stepIndex++;
        return BeforeStep(handoffIndex, new ScenarioStepView(ScenarioStepKind.Handoff, _rosterEditorStarted, handoff: item))
            && ExecuteHandoff(item, item.Action!, handoffIndex);
    }

    /// <summary>
    /// <see cref="OnBeforeStep"/> for the step at <paramref name="stepIndex"/>; false when the run must stop.
    /// A hook that throws stops the run with a failure at that step — not with the "Setup failed" the run's
    /// own catch would otherwise report for it.
    /// </summary>
    private bool BeforeStep(int stepIndex, ScenarioStepView step)
    {
        if (OnBeforeStep is not { } hook)
        {
            return true;
        }

        try
        {
            return hook(stepIndex, step);
        }
        catch (Exception ex)
        {
            _errors.Add($"Step {stepIndex}: before-step hook failed: {ex.GetType().Name}: {ex.Message}");
            _harnessError ??= $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Copy the failures each runner added since the last look into the one list, in step order.</summary>
    private void CollectFailures()
    {
        var data = _dataRunner.Errors;
        for (; _dataErrorsSeen < data.Count; _dataErrorsSeen++)
        {
            _errors.Add(data[_dataErrorsSeen]);
        }

        var roster = _rosterRunner.Errors;
        for (; _rosterErrorsSeen < roster.Count; _rosterErrorsSeen++)
        {
            _errors.Add(roster[_rosterErrorsSeen]);
        }

        _harnessError ??= _rosterRunner.HarnessError;
    }

    /// <summary>
    /// Run a hand-off, honouring its <c>expectFailure</c> the way <see cref="RosterRunner"/> honours one on an
    /// action: the spec's half (<see cref="PrepareHandoff"/>) runs first and outside the declaration, so a
    /// misspelled <c>file</c> can never pass for the engine refusing to open a roster (#25).
    /// </summary>
    private bool ExecuteHandoff(ScenarioStepDef item, string action, int stepIndex)
    {
        var declared = item.ExpectFailure;
        var expected = declared?.ForEngine(OverrideKeyFor(declared.Engines));

        Action? dispatch;
        try
        {
            dispatch = PrepareHandoff(item, action, stepIndex);
        }
        catch (InvalidOperationException ex)
        {
            _errors.Add($"Step {stepIndex}: {ex.Message}");
            return false;
        }

        if (dispatch is null)
        {
            return false;
        }

        if (expected is null || !expected.IsExpected)
        {
            try
            {
                dispatch();
                return true;
            }
            catch (Exception ex)
            {
                _errors.Add($"Step {stepIndex}: {action}: {ex.GetType().Name}: {ex.Message}");
                _harnessError ??= $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        try
        {
            dispatch();
        }
        catch (Exception ex) when (ExpectFailure.IsSatisfiedBy(ex, expected))
        {
            // The refusal the spec asked for. The run continues so the next block can assert what the
            // refusal left behind.
            return true;
        }
        catch (Exception ex)
        {
            _errors.Add(ExpectFailure.Explain(ex, expected, stepIndex, action, EngineLabel));
            if (ExpectFailure.IsHarnessFault(ex))
            {
                _harnessError ??= $"{ex.GetType().Name}: {ex.Message}";
            }

            return false;
        }

        _errors.Add(ExpectFailure.ExplainUnexpectedSuccess(stepIndex, action, EngineLabel));
        return false;
    }

    /// <summary>
    /// Everything a hand-off does before an engine is asked anything — read and check its inputs, find the
    /// saved roster — and the engine calls themselves, bound and not yet made. Every exception thrown here is
    /// a statement about the spec. Null for an unknown hand-off, already recorded.
    /// </summary>
    private Action? PrepareHandoff(ScenarioStepDef item, string action, int stepIndex)
    {
        if (!ScenarioHandoffs.All.Contains(action, StringComparer.Ordinal))
        {
            _errors.Add($"Step {stepIndex}: unknown hand-off '{action}'. Known: {string.Join(", ", ScenarioHandoffs.All)}.");
            return null;
        }

        string? file = null;
        if (ScenarioHandoffs.TakesFile(action))
        {
            file = item.File is { Length: > 0 } named
                ? named
                : throw new InvalidOperationException($"{action} requires 'file', the name the roster is saved under");
        }
        else if (item.File is not null)
        {
            throw new InvalidOperationException($"{action} takes no 'file': the data editor saves its open file under its own id");
        }

        switch (action)
        {
            case ScenarioHandoffs.SaveData:
                // Reload is save-and-reopen in every gamedata engine; the Data Editor re-reads a file from disk
                // when the author switches to it, so a save that only exported would lose the edits at the
                // next openFile. The export after it is what the data directory takes.
                return () =>
                {
                    _data.Reload();
                    _dataDirectory.Write(_data.ExportActiveFile(), replaces: _dataRunner.OpenedFileId ?? _setupEdit);
                };

            case ScenarioHandoffs.NewRoster:
                _rosterEditorStarted = true;
                return () => StartRosterEditor(savedRoster: null);

            case ScenarioHandoffs.SaveRoster:
                if (!_rosterEditorStarted)
                {
                    throw new InvalidOperationException(
                        "saveRoster needs a roster — start one with 'newRoster' or 'openRoster' first");
                }

                return () => _savedRosters[file!] = _roster.ExportRosterXml();

            default: // OpenRoster
                var xml = _savedRosters.TryGetValue(file!, out var saved)
                    ? saved
                    : throw new InvalidOperationException(
                        $"openRoster: no roster was saved as '{file}' by an earlier saveRoster " +
                        $"(saved: [{string.Join(", ", _savedRosters.Keys)}])");
                _rosterEditorStarted = true;
                return () => StartRosterEditor(xml);
        }
    }

    /// <summary>
    /// The roster editor reads the data directory as it is now, then starts an empty roster on it or opens
    /// <paramref name="savedRoster"/>. <c>Cleanup</c> first: the contract that makes a cleaned engine safe to
    /// set up again is the one the protocol host's warm reuse already relies on between specs.
    /// </summary>
    private void StartRosterEditor(string? savedRoster)
    {
        _roster.Cleanup();
        var setupErrors = _roster.SetupFromFiles(_dataDirectory.Files);
        if (setupErrors.Count > 0)
        {
            // A fault, never a refusal an expectFailure can assert. Over the protocol a setup failure arrives
            // as bare text whatever caused it — the host failing to start a browser, an engine's own null
            // reference — so nothing here can tell an engine declining the data from the harness breaking.
            // The refusal an openRoster asserts is LoadRoster's, which keeps its classification.
            throw new HarnessFaultException(
                $"the roster editor could not read the data directory: {string.Join("; ", setupErrors)}");
        }

        _rosterRunner.SetCatalogueIds(_dataDirectory.CatalogueIds);
        if (savedRoster is not null)
        {
            _roster.LoadRoster(savedRoster);
        }
    }

    /// <summary>The per-engine override key that applies (<see cref="EngineIdentity.KeyIn{T}"/>); null applies none.</summary>
    private string? OverrideKeyFor<T>(Dictionary<string, T>? engines) => _identity?.KeyIn(engines);

    private static void CleanUp(Action cleanup, string editor, string specId)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ScenarioRunner] {editor} cleanup error after spec '{specId}': {ex}");
        }
    }
}

/// <summary>Which editor a scenario step runs in, or neither: a hand-off runs between them.</summary>
public enum ScenarioStepKind
{
    /// <summary>A step of a <c>data:</c> block, run by the data editor.</summary>
    Data,

    /// <summary>A step of a <c>roster:</c> block, run by the roster editor.</summary>
    Roster,

    /// <summary>One of the <see cref="ScenarioHandoffs"/>, which carries data or a roster between the editors.</summary>
    Handoff,
}

/// <summary>
/// The step a scenario is about to run, as <see cref="ScenarioRunner.OnBeforeStep"/> reports it. Exactly one
/// of <see cref="DataStep"/>, <see cref="RosterStep"/> and <see cref="Handoff"/> is set, the one
/// <see cref="Kind"/> names.
/// </summary>
public sealed class ScenarioStepView
{
    internal ScenarioStepView(
        ScenarioStepKind kind,
        bool rosterEditorStarted,
        GameDataStepDef? dataStep = null,
        StepDef? rosterStep = null,
        ScenarioStepDef? handoff = null)
    {
        Kind = kind;
        RosterEditorStarted = rosterEditorStarted;
        DataStep = dataStep;
        RosterStep = rosterStep;
        Handoff = handoff;
    }

    /// <summary>Which editor runs the step, or <see cref="ScenarioStepKind.Handoff"/>.</summary>
    public ScenarioStepKind Kind { get; }

    /// <summary>The step of a <c>data:</c> block; set when <see cref="Kind"/> is <see cref="ScenarioStepKind.Data"/>.</summary>
    public GameDataStepDef? DataStep { get; }

    /// <summary>The step of a <c>roster:</c> block; set when <see cref="Kind"/> is <see cref="ScenarioStepKind.Roster"/>.</summary>
    public StepDef? RosterStep { get; }

    /// <summary>The hand-off item, its <c>file</c> and <c>expectFailure</c> included; set when <see cref="Kind"/> is <see cref="ScenarioStepKind.Handoff"/>.</summary>
    public ScenarioStepDef? Handoff { get; }

    /// <summary>
    /// Whether a <c>newRoster</c> or <c>openRoster</c> has run before this step — an <c>openRoster</c> the spec
    /// declared refused included, after which the roster editor holds what the refusal left. Until one has,
    /// the roster editor has read no data and there is nothing on the roster side to ask about: the rule that
    /// makes a <c>roster:</c> block there a spec error.
    /// </summary>
    public bool RosterEditorStarted { get; }
}
