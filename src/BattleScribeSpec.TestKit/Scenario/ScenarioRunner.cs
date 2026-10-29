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
    private readonly string _engineName;
    private readonly string? _baseEngineName;
    private readonly GameDataRunner _dataRunner;
    private readonly RosterRunner _rosterRunner;
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, string> _savedRosters = new(StringComparer.Ordinal);
    private ScenarioDataDirectory _dataDirectory = new();
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
    /// <param name="engineName">The engine actually running — <c>battlescribe-ui</c>, not <c>battlescribe</c>.</param>
    /// <param name="baseEngineName">
    /// The engine whose per-engine declarations apply when the spec has none for
    /// <paramref name="engineName"/>. One rule for both kinds of block: see
    /// <see cref="GameDataRunner(IGameDataEngine, string?, string?)"/>.
    /// </param>
    public ScenarioRunner(IGameDataEngine dataEngine, IRosterEngine rosterEngine, string engineName, string? baseEngineName = null)
    {
        _data = dataEngine;
        _roster = rosterEngine;
        _engineName = engineName;
        _baseEngineName = baseEngineName;
        _dataRunner = new GameDataRunner(dataEngine, engineName, baseEngineName);
        _rosterRunner = new RosterRunner(rosterEngine, dataSourceResolver: null, baseEngineName ?? engineName, engineName);
    }

    /// <summary>Mirrors <see cref="RosterRunner.UpdateSnapshots"/>, for <c>expectedFile</c> steps in either kind of block.</summary>
    public bool UpdateSnapshots { get; set; }

    /// <summary>Mirrors <see cref="RosterRunner.OnDiverge"/>.</summary>
    public Func<ExportSnapshotAssertion.SnapshotDivergence, ExportSnapshotAssertion.SnapshotWriteTarget>? OnDiverge { get; set; }

    /// <summary>Called after each step of a <c>data:</c> block: global step index, step, data state.</summary>
    public Action<int, GameDataStepDef, GameDataState>? OnDataStepCompleted { get; set; }

    /// <summary>Called after each step of a <c>roster:</c> block: global step index, step, roster state, errors.</summary>
    public Action<int, StepDef, RosterState, IReadOnlyList<ValidationErrorState>>? OnRosterStepCompleted { get; set; }

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

        _dataRunner.UpdateSnapshots = UpdateSnapshots;
        _dataRunner.OnDiverge = OnDiverge;
        _dataRunner.OnStepCompleted = OnDataStepCompleted;
        _rosterRunner.UpdateSnapshots = UpdateSnapshots;
        _rosterRunner.OnDiverge = OnDiverge;
        _rosterRunner.OnStepCompleted = OnRosterStepCompleted;
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
                $"engine '{_engineName}' reports none ({ex.Message}). Implement ExportActiveFile, or opt this " +
                $"engine out with 'engines: {{{_engineName}: skip}}' on the spec.");
            return false;
        }

        _data.OpenFile(spec.Setup.Edit);
        return true;
    }

    /// <summary>
    /// Execute one item of <c>steps</c>: every step of a block, or one hand-off. Each step and each hand-off
    /// takes the next global index, so "Step 7" is the seventh a reader counts down the file. Returns false
    /// when the run must stop.
    /// </summary>
    private bool ExecuteItem(ScenarioStepDef item, ref int stepIndex)
    {
        var kinds = (item.Data is null ? 0 : 1) + (item.Roster is null ? 0 : 1) + (item.Action is null ? 0 : 1);
        if (kinds != 1)
        {
            _errors.Add($"Step {stepIndex}: a scenario item is exactly one of 'data', 'roster' or 'action' (this one has {kinds})");
            return false;
        }

        if (item.Data is { } dataSteps)
        {
            foreach (var step in dataSteps)
            {
                var proceed = _dataRunner.ExecuteStep(step, stepIndex++);
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
                var proceed = _rosterRunner.ExecuteStep(step, stepIndex++);
                CollectFailures();
                if (!proceed)
                {
                    return false;
                }
            }

            return true;
        }

        return ExecuteHandoff(item, item.Action!, stepIndex++);
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
            _errors.Add(ExpectFailure.Explain(ex, expected, stepIndex, action, _engineName));
            if (ExpectFailure.IsHarnessFault(ex))
            {
                _harnessError ??= $"{ex.GetType().Name}: {ex.Message}";
            }

            return false;
        }

        _errors.Add(ExpectFailure.ExplainUnexpectedSuccess(stepIndex, action, _engineName));
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
                    _dataDirectory.Write(_data.ExportActiveFile());
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
            throw new InvalidOperationException(
                $"the roster editor could not read the data directory: {string.Join("; ", setupErrors)}");
        }

        _rosterRunner.SetCatalogueIds(_dataDirectory.CatalogueIds);
        if (savedRoster is not null)
        {
            _roster.LoadRoster(savedRoster);
        }
    }

    /// <summary>The per-engine key that applies: the running engine's own entry if declared, else the base's.</summary>
    private string? OverrideKeyFor<T>(Dictionary<string, T>? engines)
        => engines is not null && engines.ContainsKey(_engineName) ? _engineName : _baseEngineName ?? _engineName;

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
