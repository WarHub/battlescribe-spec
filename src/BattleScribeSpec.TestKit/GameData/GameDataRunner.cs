using BattleScribeSpec.Roster;

namespace BattleScribeSpec.GameData;

/// <summary>
/// Runs GameData conformance specs against an <see cref="IGameDataEngine"/> implementation.
/// Parallel to <see cref="RosterRunner"/> for roster specs.
/// </summary>
public sealed class GameDataRunner
{
    private readonly IGameDataEngine _engine;
    private readonly string? _engineName;
    private readonly List<string> _errors = [];
    private readonly GameDataExpressionResolver _exprResolver = new();

    /// <summary>
    /// Called after each step completes (for debug dumping).
    /// Parameters: step index, step definition, current state.
    /// </summary>
    public Action<int, GameDataStepDef, GameDataState>? OnStepCompleted { get; set; }

    public GameDataRunner(IGameDataEngine engine, string? engineName = null)
    {
        _engine = engine;
        _engineName = engineName;
    }

    /// <summary>
    /// Run a complete GameData spec. Returns list of assertion failures (empty = pass).
    /// </summary>
    public SpecResult Run(GameDataSpecFile spec)
    {
        _errors.Clear();
        try
        {
            _engine.SetTestContext(spec.Id);

            // Setup
            var gameSystem = spec.Setup.GameSystem
                ?? throw new InvalidOperationException("GameData spec requires setup.gameSystem");
            var catalogues = spec.Setup.Catalogues?.ToArray() ?? [];
            var setupErrors = _engine.Setup(gameSystem, catalogues);
            if (setupErrors.Count > 0)
            {
                foreach (var err in setupErrors)
                {
                    _errors.Add($"Setup error: {err}");
                }
                return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors]);
            }

            // Execute steps
            for (var i = 0; i < spec.Steps.Count; i++)
            {
                var step = spec.Steps[i];
                try
                {
                    if (step.Action == "dump")
                    {
                        // dump is a no-op in the runner; the callback does the work
                    }
                    else if (step.Action is not null)
                    {
                        ExecuteAction(step, i);
                    }
                    else if (step.ExpectedState is not null)
                    {
                        ExecuteAssertion(step, i);
                    }
                    else
                    {
                        _errors.Add($"Step {i}: neither 'action' nor 'expectedState' defined");
                    }

                    NotifyStepCompleted(i, step);
                }
                catch (Exception ex)
                {
                    _errors.Add($"Step {i}: {ex.GetType().Name}: {ex.Message}");
                    NotifyStepCompleted(i, step);
                    if (step.Action is not null && step.Action != "dump")
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _errors.Add($"Setup failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                _engine.Cleanup();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[GameDataRunner] Cleanup error after spec '{spec.Id}': {ex}");
            }
        }

        return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors]);
    }

    private void NotifyStepCompleted(int stepIndex, GameDataStepDef step)
    {
        if (OnStepCompleted is not { } callback)
        {
            return;
        }
        try
        {
            var state = _engine.GetState();
            callback(stepIndex, step, state);
        }
        catch
        {
            // Don't let dump failures break spec execution
        }
    }

    private void ExecuteAction(GameDataStepDef step, int stepIndex)
    {
        var entryId = _exprResolver.Resolve(step.EntryId);
        var parentId = _exprResolver.Resolve(step.ParentId);

        GameDataActionOutputs? outputs = null;
        switch (step.Action)
        {
            case "addEntry":
                outputs = _engine.AddEntry(
                    parentId ?? throw new InvalidOperationException($"Step {stepIndex}: addEntry requires parentId"),
                    step.EntryType ?? throw new InvalidOperationException($"Step {stepIndex}: addEntry requires entryType"),
                    step.Name);
                break;

            case "removeEntry":
                _engine.RemoveEntry(
                    entryId ?? throw new InvalidOperationException($"Step {stepIndex}: removeEntry requires entryId"));
                break;

            case "moveEntry":
                _engine.MoveEntry(
                    entryId ?? throw new InvalidOperationException($"Step {stepIndex}: moveEntry requires entryId"),
                    _exprResolver.Resolve(step.NewParentId) ?? throw new InvalidOperationException($"Step {stepIndex}: moveEntry requires newParentId"),
                    step.Index);
                break;

            case "setField":
                _engine.SetField(
                    entryId ?? throw new InvalidOperationException($"Step {stepIndex}: setField requires entryId"),
                    step.Field ?? throw new InvalidOperationException($"Step {stepIndex}: setField requires field"),
                    step.Value);
                break;

            case "addLink":
                outputs = _engine.AddLink(
                    parentId ?? throw new InvalidOperationException($"Step {stepIndex}: addLink requires parentId"),
                    step.LinkType ?? throw new InvalidOperationException($"Step {stepIndex}: addLink requires linkType"),
                    _exprResolver.Resolve(step.TargetId) ?? throw new InvalidOperationException($"Step {stepIndex}: addLink requires targetId"));
                break;

            default:
                _errors.Add($"Step {stepIndex}: unknown action '{step.Action}'");
                break;
        }

        // Store outputs for expression resolution in later steps
        if (step.Id is { Length: > 0 } stepId && outputs is not null)
        {
            _exprResolver.StoreOutputs(stepId, outputs);
        }
    }

    private void ExecuteAssertion(GameDataStepDef step, int stepIndex)
    {
        if (step.ExpectedState is not null)
        {
            var effective = step.ExpectedState.ForEngine(_engineName);
            AssertExpectedState(effective, stepIndex);
        }
    }

    private void AssertExpectedState(GameDataExpectedStateDef expected, int stepIndex)
    {
        var state = _engine.GetState();

        if (expected.GameSystem is { } expectedGs)
        {
            if (state.GameSystem is null)
            {
                _errors.Add($"Step {stepIndex}: expected gameSystem but state has none");
            }
            else
            {
                AssertGameSystem(stepIndex, expectedGs, state.GameSystem);
            }
        }

        if (expected.Catalogues is { } expectedCats)
        {
            foreach (var ec in expectedCats)
            {
                var actual = ec.Id is { Length: > 0 } id
                    ? state.Catalogues.FirstOrDefault(c => c.Id == id)
                    : ec.Name is { Length: > 0 } name
                        ? state.Catalogues.FirstOrDefault(c => c.Name == name)
                        : state.Catalogues.Count > 0 ? state.Catalogues[0] : null;

                if (actual is null)
                {
                    _errors.Add($"Step {stepIndex}: expected catalogue '{ec.Id ?? ec.Name ?? "?"}' not found");
                    continue;
                }

                AssertCatalogue(stepIndex, ec, actual);
            }
        }
    }

    private void AssertGameSystem(int stepIndex, ExpectedGameSystemDataDef expected, GameSystemDataState actual)
    {
        var prefix = "gameSystem";
        if (expected.Id is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.id", expected.Id, actual.Id);
        }

        if (expected.Name is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.name", expected.Name, actual.Name);
        }

        if (expected.ForceEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.forceEntries", expected.ForceEntries, actual.ForceEntries);
        }

        if (expected.CategoryEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.categoryEntries", expected.CategoryEntries, actual.CategoryEntries);
        }

        if (expected.CostTypes is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.costTypes", expected.CostTypes, actual.CostTypes);
        }

        if (expected.ProfileTypes is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.profileTypes", expected.ProfileTypes, actual.ProfileTypes);
        }

        if (expected.SelectionEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.selectionEntries", expected.SelectionEntries, actual.SelectionEntries);
        }

        if (expected.SharedSelectionEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedSelectionEntries", expected.SharedSelectionEntries, actual.SharedSelectionEntries);
        }
    }

    private void AssertCatalogue(int stepIndex, ExpectedCatalogueDataDef expected, CatalogueDataState actual)
    {
        var prefix = $"catalogue[{actual.Id}]";
        if (expected.Name is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.name", expected.Name, actual.Name);
        }

        if (expected.EntryCount is { } ec)
        {
            var totalCount = actual.SelectionEntries.Count + actual.EntryLinks.Count + actual.Rules.Count
                + actual.SharedSelectionEntries.Count + actual.SharedSelectionEntryGroups.Count
                + actual.SharedRules.Count + actual.SharedProfiles.Count
                + actual.ForceEntries.Count + actual.CategoryEntries.Count
                + actual.Publications.Count + actual.CostTypes.Count + actual.ProfileTypes.Count;
            AssertEqual(stepIndex, $"{prefix}.entryCount", ec, totalCount);
        }
        if (expected.SelectionEntryCount is { } sec)
        {
            AssertEqual(stepIndex, $"{prefix}.selectionEntryCount", sec, actual.SelectionEntries.Count);
        }

        if (expected.SharedSelectionEntryCount is { } ssec)
        {
            AssertEqual(stepIndex, $"{prefix}.sharedSelectionEntryCount", ssec, actual.SharedSelectionEntries.Count);
        }

        if (expected.SelectionEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.selectionEntries", expected.SelectionEntries, actual.SelectionEntries);
        }

        if (expected.SharedSelectionEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedSelectionEntries", expected.SharedSelectionEntries, actual.SharedSelectionEntries);
        }

        if (expected.SharedSelectionEntryGroups is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedSelectionEntryGroups", expected.SharedSelectionEntryGroups, actual.SharedSelectionEntryGroups);
        }

        if (expected.EntryLinks is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.entryLinks", expected.EntryLinks, actual.EntryLinks);
        }

        if (expected.Rules is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.rules", expected.Rules, actual.Rules);
        }

        if (expected.SharedRules is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedRules", expected.SharedRules, actual.SharedRules);
        }

        if (expected.SharedProfiles is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedProfiles", expected.SharedProfiles, actual.SharedProfiles);
        }

        if (expected.ForceEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.forceEntries", expected.ForceEntries, actual.ForceEntries);
        }

        if (expected.CategoryEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.categoryEntries", expected.CategoryEntries, actual.CategoryEntries);
        }

        if (expected.Publications is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.publications", expected.Publications, actual.Publications);
        }
    }

    private void AssertEntryList(int stepIndex, string prefix,
        List<ExpectedDataEntryDef> expected, IReadOnlyList<DataEntryState> actual)
    {
        for (var i = 0; i < expected.Count; i++)
        {
            var ee = expected[i];

            // Match by ID if specified, otherwise by index
            DataEntryState? ae;
            string matchKey;
            if (ee.Id is { Length: > 0 } id)
            {
                ae = actual.FirstOrDefault(e => e.Id == id);
                matchKey = id;
            }
            else if (ee.Name is { Length: > 0 } name)
            {
                ae = actual.FirstOrDefault(e => e.Name == name);
                matchKey = name;
            }
            else
            {
                ae = i < actual.Count ? actual[i] : null;
                matchKey = $"[{i}]";
            }

            if (ae is null)
            {
                _errors.Add($"Step {stepIndex}: {prefix}[{matchKey}] not found (actual count: {actual.Count})");
                continue;
            }

            AssertEntry(stepIndex, $"{prefix}[{matchKey}]", ee, ae);
        }

        // If expected list is same length as actual, assert no extras
        if (expected.Count > 0 && actual.Count > expected.Count)
        {
            // Only flag if expected looks like it's asserting the complete set (no ID-based matching)
            var allPositional = expected.All(e => e.Id is null && e.Name is null);
            if (allPositional)
            {
                _errors.Add($"Step {stepIndex}: {prefix} expected {expected.Count} entries but got {actual.Count}");
            }
        }
    }

    private void AssertEntry(int stepIndex, string prefix, ExpectedDataEntryDef expected, DataEntryState actual)
    {
        if (expected.Name is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.name", expected.Name, actual.Name);
        }

        if (expected.EntryType is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.entryType", expected.EntryType, actual.EntryType);
        }

        if (expected.Hidden is { } h)
        {
            AssertEqual(stepIndex, $"{prefix}.hidden", h, actual.Hidden);
        }

        if (expected.ChildCount is { } cc)
        {
            AssertEqual(stepIndex, $"{prefix}.childCount", cc, actual.Children.Count);
        }

        if (expected.Children is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.children", expected.Children, actual.Children);
        }

        if (expected.Fields is not null && expected.Fields.Count > 0)
        {
            foreach (var (key, expectedValue) in expected.Fields)
            {
                string? actualValue = null;
                actual.Fields?.TryGetValue(key, out actualValue);
                AssertEqual(stepIndex, $"{prefix}.fields[{key}]", expectedValue ?? "", actualValue ?? "");
            }
        }
    }

    private void AssertEqual<T>(int stepIndex, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            _errors.Add($"Step {stepIndex}: {field} expected '{expected}' but got '{actual}'");
        }
    }
}

/// <summary>
/// GameData-specific expression resolver for ${{ steps.xxx.entryId }} expressions.
/// </summary>
internal sealed class GameDataExpressionResolver
{
    private const string ExprStart = "${{";
    private const string ExprEnd = "}}";
    private const string StepsPrefix = "steps.";

    private readonly Dictionary<string, GameDataActionOutputs> _stepOutputs = [];

    public void StoreOutputs(string stepId, GameDataActionOutputs outputs)
    {
        _stepOutputs[stepId] = outputs;
    }

    public string? Resolve(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(ExprStart, StringComparison.Ordinal) || !trimmed.EndsWith(ExprEnd, StringComparison.Ordinal))
        {
            return value;
        }

        var expr = trimmed[ExprStart.Length..^ExprEnd.Length].Trim();
        return ResolveExpression(expr, value);
    }

    private string ResolveExpression(string expr, string rawExpr)
    {
        if (!expr.StartsWith(StepsPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invalid expression '{rawExpr}': only 'steps.' expressions are supported.");
        }

        var path = expr[StepsPrefix.Length..];
        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            throw new InvalidOperationException(
                $"Invalid expression '{rawExpr}': expected 'steps.<stepId>.<field>'.");
        }

        var stepId = path[..dotIndex];
        var field = path[(dotIndex + 1)..];

        if (!_stepOutputs.TryGetValue(stepId, out var outputs))
        {
            throw new InvalidOperationException(
                $"Expression '{rawExpr}': step '{stepId}' not found. " +
                $"Available steps: [{string.Join(", ", _stepOutputs.Keys)}].");
        }

        return field switch
        {
            "entryId" => outputs.EntryId
                ?? throw new InvalidOperationException($"Expression '{rawExpr}': step has no entryId output."),
            _ => throw new InvalidOperationException(
                $"Expression '{rawExpr}': unknown field '{field}'. Supported: entryId.")
        };
    }
}
