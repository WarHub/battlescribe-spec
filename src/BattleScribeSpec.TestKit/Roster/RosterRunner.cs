using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Roster;

/// <summary>
/// Executes spec test steps against an IRosterEngine and validates assertions.
/// </summary>
public sealed class RosterRunner
{
    private readonly IRosterEngine _engine;
    private readonly DataSourceResolver? _dataSourceResolver;
    private readonly string? _engineName;
    private readonly List<string> _errors = [];
    private readonly ExpressionResolver _exprResolver = new();
    private bool _isDataSourceMode;
    private IReadOnlyList<string> _catalogueIds = [];

    /// <summary>
    /// Called after each step (action, assertion, or dump) completes.
    /// Parameters: step index, step definition, roster state, validation errors.
    /// Set this to enable state dumping in debugger mode.
    /// </summary>
    public Action<int, StepDef, RosterState, IReadOnlyList<ValidationErrorState>>? OnStepCompleted { get; set; }

    /// <summary>
    /// Called before each step executes. Return false to stop execution before this step.
    /// Parameters: step index, step definition. If null, all steps execute.
    /// </summary>
    public Func<int, StepDef, bool>? OnBeforeStep { get; set; }

    public RosterRunner(IRosterEngine engine, DataSourceResolver? dataSourceResolver = null, string? engineName = null)
    {
        _engine = engine;
        _dataSourceResolver = dataSourceResolver;
        _engineName = engineName;
    }

    /// <summary>
    /// Run a complete spec test. Returns list of assertion failures (empty = pass).
    /// </summary>
    public SpecResult Run(SpecFile spec)
    {
        _errors.Clear();
        _isDataSourceMode = false;
        _catalogueIds = [];
        try
        {
            _engine.SetTestContext(spec.Id);

            // Setup — either DataSource (file-based) or inline (model-based)
            if (spec.Setup.DataSource is { Length: > 0 } dataSourceUri)
            {
                SetupFromDataSource(dataSourceUri);
            }
            else
            {
                var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup);
                _catalogueIds = [.. catalogues.Select(c => c.Id)];
                var setupErrors = _engine.Setup(gameSystem, catalogues);
                if (setupErrors.Count > 0)
                {
                    foreach (var setupError in setupErrors)
                    {
                        _errors.Add($"Setup error: {setupError}");
                    }

                    return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors]);
                }
            }

            // Execute steps
            for (var i = 0; i < spec.Steps.Count; i++)
            {
                var step = spec.Steps[i];
                try
                {
                    if (OnBeforeStep is { } beforeStep && !beforeStep(i, step))
                    {
                        break;
                    }

                    if (step.Action == "dump")
                    {
                        // dump is a no-op in the runner itself; the callback does the work
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
                Console.Error.WriteLine($"[SpecRunner] Cleanup error after spec '{spec.Id}': {ex}");
            }
        }

        return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors]);
    }

    private void NotifyStepCompleted(int stepIndex, StepDef step)
    {
        if (OnStepCompleted is not { } callback)
        {
            return;
        }

        try
        {
            var state = _engine.GetRosterState();
            var errors = _engine.GetValidationErrors();
            callback(stepIndex, step, state, errors);
        }
        catch
        {
            // Don't let dump failures break spec execution
        }
    }

    private void SetupFromDataSource(string dataSourceUri)
    {
        if (_dataSourceResolver is null)
        {
            throw new InvalidOperationException(
                "DataSource specs require a DataSourceResolver. Pass one to the SpecRunner constructor.");
        }

        _isDataSourceMode = true;
        var resolvedDir = _dataSourceResolver.Resolve(dataSourceUri);

        // Read all .gst and .cat files from the resolved directory
        var files = new List<(string FileName, string Content)>();
        foreach (var file in Directory.EnumerateFiles(resolvedDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".gst", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)))
        {
            files.Add((System.IO.Path.GetFileName(file), File.ReadAllText(file)));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"No .gst or .cat files found in resolved data source directory: {resolvedDir}");
        }

        var setupErrors = _engine.SetupFromFiles(files);
        foreach (var err in setupErrors)
        {
            _errors.Add($"Setup: {err}");
        }
    }

    private void ExecuteAction(StepDef step, int stepIndex)
    {
        // Skip this step for engines listed in SkipEngines
        if (step.SkipEngines?.Contains(_engineName, StringComparer.OrdinalIgnoreCase) == true)
        {
            if (step.Id is { Length: > 0 } skippedId)
            {
                _exprResolver.StoreOutputs(skippedId, new ActionOutputs());
            }
            return;
        }

        // Resolve expressions in instance ID fields
        var forceId = _exprResolver.Resolve(step.ForceId);
        var selectionId = _exprResolver.Resolve(step.SelectionId);

        ActionOutputs? outputs = null;
        switch (step.Action)
        {
            case "addForce":
                outputs = _engine.AddForce(
                    step.ForceEntryId ?? throw new InvalidOperationException($"Step {stepIndex}: addForce requires forceEntryId"),
                    ProtocolValidator.ResolveCatalogueId(step.CatalogueId, _catalogueIds));
                break;

            case "addChildForce":
                outputs = _engine.AddChildForce(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: addChildForce requires forceId"),
                    step.ForceEntryId ?? throw new InvalidOperationException($"Step {stepIndex}: addChildForce requires forceEntryId"),
                    ProtocolValidator.ResolveCatalogueId(step.CatalogueId, _catalogueIds));
                break;

            case "removeForce":
                _engine.RemoveForce(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: removeForce requires forceId"));
                break;

            case "selectEntry":
                outputs = _engine.SelectEntry(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: selectEntry requires forceId"),
                    step.EntryId ?? throw new InvalidOperationException($"Step {stepIndex}: selectEntry requires entryId"));
                break;

            case "selectChildEntry":
                outputs = _engine.SelectChildEntry(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: selectChildEntry requires forceId"),
                    selectionId ?? throw new InvalidOperationException($"Step {stepIndex}: selectChildEntry requires selectionId"),
                    step.EntryId ?? throw new InvalidOperationException($"Step {stepIndex}: selectChildEntry requires entryId"));
                break;

            case "deselectSelection":
                _engine.DeselectSelection(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: deselectSelection requires forceId"),
                    selectionId ?? throw new InvalidOperationException($"Step {stepIndex}: deselectSelection requires selectionId"));
                break;

            case "setSelectionCount":
                _engine.SetSelectionCount(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: setSelectionCount requires forceId"),
                    selectionId ?? throw new InvalidOperationException($"Step {stepIndex}: setSelectionCount requires selectionId"),
                    step.Count ?? throw new InvalidOperationException($"Step {stepIndex}: setSelectionCount requires count"));
                break;

            case "duplicateSelection":
                outputs = _engine.DuplicateSelection(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: duplicateSelection requires forceId"),
                    selectionId ?? throw new InvalidOperationException($"Step {stepIndex}: duplicateSelection requires selectionId"));
                break;

            case "duplicateForce":
                outputs = _engine.DuplicateForce(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: duplicateForce requires forceId"));
                break;

            case "setCostLimit":
                _engine.SetCostLimit(
                    step.CostTypeId ?? throw new InvalidOperationException($"Step {stepIndex}: setCostLimit requires costTypeId"),
                    step.Value ?? throw new InvalidOperationException($"Step {stepIndex}: setCostLimit requires value"));
                break;

            case "setCustomization":
                _engine.SetCustomization(
                    forceId ?? throw new InvalidOperationException($"Step {stepIndex}: setCustomization requires forceId"),
                    selectionId,
                    step.CategoryEntryId,
                    step.CustomName,
                    step.CustomNotes);
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

    private void ExecuteAssertion(StepDef step, int stepIndex)
    {
        if (step.ExpectedState is not null)
        {
            var effective = step.ExpectedState.ForEngine(_engineName);
            AssertExpectedState(effective, stepIndex);
        }
    }

    private void AssertExpectedState(ExpectedStateDef? expected, int stepIndex)
    {
        if (expected is null)
        {
            return;
        }

        var state = _engine.GetRosterState();

        if (expected.ForceCount is { } fc)
        {
            AssertEqual(stepIndex, "forceCount", fc, state.Forces.Count);
        }

        if (expected.CostCount is { } cc)
        {
            AssertEqual(stepIndex, "costCount", cc, state.Costs.Count);
        }

        if (expected.Costs is { } expectedCosts)
        {
            foreach (var ec in expectedCosts)
            {
                CostState? actual;
                string matchKey;
                if (ec.TypeId is { Length: > 0 } typeId)
                {
                    matchKey = typeId;
                    actual = state.Costs.FirstOrDefault(c => c.TypeId == typeId);
                }
                else if (ec.Name is { Length: > 0 } name)
                {
                    matchKey = name;
                    actual = state.Costs.FirstOrDefault(c => c.Name == name);
                }
                else
                {
                    _errors.Add($"Step {stepIndex}: cost assertion has neither typeId nor name");
                    continue;
                }

                if (actual is null)
                {
                    _errors.Add($"Step {stepIndex}: cost type '{matchKey}' not found in roster");
                }
                else if (ec.Value is { } v)
                {
                    AssertEqual(stepIndex, $"cost[{matchKey}].value", v, actual.Value);
                }
            }

            if (state.Costs.Count > expectedCosts.Count)
            {
                _errors.Add($"Step {stepIndex}: expected {expectedCosts.Count} cost(s) but got {state.Costs.Count}: " +
                    $"[{string.Join(", ", state.Costs.Select(c => $"{c.Name}={c.Value}"))}]");
            }
        }

        if (expected.CostLimitCount is { } clc)
        {
            AssertEqual(stepIndex, "costLimitCount", clc, state.CostLimits?.Count ?? 0);
        }

        if (expected.CostLimits is { } expectedCostLimits)
        {
            var actualCostLimits = state.CostLimits ?? [];
            foreach (var ecl in expectedCostLimits)
            {
                CostState? actual;
                string matchKey;
                if (ecl.TypeId is { Length: > 0 } typeId)
                {
                    matchKey = typeId;
                    actual = actualCostLimits.FirstOrDefault(c => c.TypeId == typeId);
                }
                else if (ecl.Name is { Length: > 0 } name)
                {
                    matchKey = name;
                    actual = actualCostLimits.FirstOrDefault(c => c.Name == name);
                }
                else
                {
                    _errors.Add($"Step {stepIndex}: costLimit assertion has neither typeId nor name");
                    continue;
                }

                if (actual is null)
                {
                    _errors.Add($"Step {stepIndex}: costLimit type '{matchKey}' not found in roster");
                }
                else if (ecl.Value is { } v)
                {
                    AssertEqual(stepIndex, $"costLimit[{matchKey}].value", v, actual.Value);
                }
            }

            if (actualCostLimits.Count > expectedCostLimits.Count)
            {
                _errors.Add($"Step {stepIndex}: expected {expectedCostLimits.Count} costLimit(s) but got {actualCostLimits.Count}: " +
                    $"[{string.Join(", ", actualCostLimits.Select(c => $"{c.Name}={c.Value}"))}]");
            }
        }

        if (expected.GameSystemName is { } gsName)
        {
            AssertEqual(stepIndex, "gameSystemName", gsName, state.GameSystemName ?? "");
        }

        if (expected.GameSystemId is { } gsId)
        {
            AssertEqual(stepIndex, "gameSystemId", gsId, state.GameSystemId);
        }

        if (expected.Name is { } rosterName)
        {
            AssertEqual(stepIndex, "name", rosterName, state.Name);
        }

        if (expected.Forces is { } expectedForces)
        {
            for (var fi = 0; fi < expectedForces.Count; fi++)
            {
                if (fi >= state.Forces.Count)
                {
                    _errors.Add($"Step {stepIndex}: expected force[{fi}] but only {state.Forces.Count} forces");
                    continue;
                }
                AssertForce(stepIndex, $"force[{fi}]", expectedForces[fi], state.Forces[fi]);
            }
            if (state.Forces.Count > expectedForces.Count)
            {
                _errors.Add($"Step {stepIndex}: expected {expectedForces.Count} forces but got {state.Forces.Count}");
            }
        }

        if (expected.SelectionCount is { } totalSelCount)
        {
            var actualTotal = state.Forces.Sum(f => f.Selections.Count);
            AssertEqual(stepIndex, "totalSelectionCount", totalSelCount, actualTotal);
        }

        if (expected.Errors is not null && expected.ErrorsContain is not null)
        {
            _errors.Add($"Step {stepIndex}: 'errors' and 'errorsContain' are mutually exclusive");
            return;
        }

        if (expected.Errors is { } errorsAssertions)
        {
            var actualErrors = _engine.GetValidationErrors();
            if (errorsAssertions.Count == 0)
            {
                // errors: [] means expect no errors
                if (actualErrors.Count > 0)
                {
                    _errors.Add($"Step {stepIndex}: expected no errors but got {actualErrors.Count}: " +
                        string.Join("; ", actualErrors.Select(FormatError)));
                }
            }
            else
            {
                MatchErrors(stepIndex, errorsAssertions, actualErrors, exactSet: true);
            }
        }

        if (expected.ErrorsContain is { } errorsContainAssertions)
        {
            var actualErrors = _engine.GetValidationErrors();
            MatchErrors(stepIndex, errorsContainAssertions, actualErrors, exactSet: false);
        }

        if (expected.ErrorCount is { } expectedErrorCount)
        {
            var actualErrors = _engine.GetValidationErrors();
            if (actualErrors.Count != expectedErrorCount)
            {
                _errors.Add($"Step {stepIndex}: expected {expectedErrorCount} error(s) but got {actualErrors.Count}: " +
                    $"[{string.Join("; ", actualErrors.Select(FormatError))}]");
            }
        }

        // Default: if no error assertion was specified, assert zero errors
        // (skip for dataSource specs which inherently have many constraint violations)
        if (!_isDataSourceMode && expected.Errors is null && expected.ErrorsContain is null && expected.ErrorCount is null)
        {
            var actualErrors = _engine.GetValidationErrors();
            if (actualErrors.Count > 0)
            {
                _errors.Add($"Step {stepIndex}: expected no errors (default) but got {actualErrors.Count}: " +
                    string.Join("; ", actualErrors.Select(FormatError)));
            }
        }
    }

    /// <summary>
    /// Matches expected error assertions against actual errors.
    /// With 'from' required on every assertion, patterns are non-overlapping
    /// and matching is fully order-independent.
    /// When <paramref name="exactSet"/> is true, also asserts that no extra unmatched errors remain.
    /// </summary>
    private void MatchErrors(int stepIndex, List<ErrorAssertionDef> assertions,
        IReadOnlyList<ValidationErrorState> actualErrors, bool exactSet)
    {
        var consumed = new HashSet<int>();
        foreach (var ea in assertions)
        {
            var (expectedOwnerType, expectedOwnerEntryId) = ParseOn(ea.On);
            var (expectedEntryId, expectedConstraintId) = ParseFrom(ea.From);

            var matchIndex = -1;
            for (var i = 0; i < actualErrors.Count; i++)
            {
                if (consumed.Contains(i))
                {
                    continue;
                }

                var ae = actualErrors[i];
                if (ae.OwnerType == expectedOwnerType &&
                    (expectedOwnerEntryId is null || ae.OwnerEntryId == expectedOwnerEntryId) &&
                    ae.EntryId == expectedEntryId &&
                    ae.ConstraintId == expectedConstraintId &&
                    (ea.MessageContains is null || (ae.Message?.Contains(ea.MessageContains, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    matchIndex = i;
                    break;
                }
            }
            if (matchIndex >= 0)
            {
                consumed.Add(matchIndex);
            }
            else
            {
                var desc = $"on='{ea.On}', from='{ea.From}'";
                if (ea.MessageContains is not null)
                {
                    desc += $", messageContains='{ea.MessageContains}'";
                }

                _errors.Add($"Step {stepIndex}: expected error [{desc}] not found in: [{string.Join("; ", actualErrors.Select(FormatError))}]");
            }
        }

        // Exact-set mode: no extra unmatched errors allowed
        if (exactSet && actualErrors.Count != assertions.Count)
        {
            _errors.Add($"Step {stepIndex}: expected {assertions.Count} error(s) but got {actualErrors.Count}: " +
                $"[{string.Join("; ", actualErrors.Select(FormatError))}]");
        }
    }

    private static string FormatError(ValidationErrorState e)
    {
        var on = e.OwnerType ?? "?";
        if (e.OwnerEntryId is not null)
        {
            on += $" {e.OwnerEntryId}";
        }

        var from = e.EntryId is not null && e.ConstraintId is not null ? $"{e.EntryId}/{e.ConstraintId}" : null;
        return from is not null ? $"{on} <- {from}: {e.Message}" : $"{on}: {e.Message}";
    }

    private static (string ownerType, string? ownerEntryId) ParseOn(string on)
    {
        var spaceIdx = on.IndexOf(' ');
        if (spaceIdx < 0)
        {
            return (on, null);
        }

        return (on[..spaceIdx], on[(spaceIdx + 1)..]);
    }

    private static (string entryId, string constraintId) ParseFrom(string from)
    {
        var slashIdx = from.IndexOf('/');
        if (slashIdx < 0)
        {
            return (from, "");
        }

        return (from[..slashIdx], from[(slashIdx + 1)..]);
    }

    private void AssertForce(int stepIndex, string prefix, ExpectedForceDef ef, ForceState af)
    {
        if (ef.Name is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.name", ef.Name, af.Name);
        }

        if (ef.EntryId is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.entryId", ef.EntryId, af.EntryId ?? "");
        }

        if (ef.SelectionCount is { } sc)
        {
            AssertEqual(stepIndex, $"{prefix}.selectionCount", sc, af.Selections.Count);
        }

        if (ef.AvailableEntryCount is { } aec && af.AvailableEntryCount is { } actualAec)
        {
            AssertEqual(stepIndex, $"{prefix}.availableEntryCount", aec, actualAec);
        }

        AssertEqual(stepIndex, $"{prefix}.hidden", ef.Hidden ?? false, af.Hidden);

        if (ef.ChildForceCount is { } cfc)
        {
            AssertEqual(stepIndex, $"{prefix}.childForceCount", cfc, af.ChildForces?.Count ?? 0);
        }

        if (ef.ChildForces is { } expectedChildForces)
        {
            AssertChildForces(stepIndex, prefix, expectedChildForces, af.ChildForces);
        }

        if (ef.Selections is { } expectedSels)
        {
            AssertSelections(stepIndex, prefix, expectedSels, af.Selections);
        }

        if (ef.Profiles is { } forceProfs)
        {
            AssertProfiles(stepIndex, prefix, forceProfs, af.Profiles);
        }

        if (ef.Rules is { } forceRules)
        {
            AssertRules(stepIndex, prefix, forceRules, af.Rules);
        }

        if (ef.CategoryCount is { } catCount)
        {
            AssertEqual(stepIndex, $"{prefix}.categoryCount", catCount, af.Categories?.Count ?? 0);
        }

        if (ef.Categories is { } forceCats)
        {
            AssertCategories(stepIndex, prefix, forceCats, af.Categories);
        }

        if (ef.Publications is { } forcePubs)
        {
            AssertPublications(stepIndex, prefix, forcePubs, af.Publications);
        }

        if (ef.PublicationId is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.publicationId", ef.PublicationId, af.PublicationId ?? "");
        }

        if (ef.Page is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.page", ef.Page, af.Page ?? "");
        }

        if (ef.CatalogueName is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.catalogueName", ef.CatalogueName, af.CatalogueName ?? "");
        }

        if (ef.CatalogueId is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.catalogueId", ef.CatalogueId, af.CatalogueId ?? "");
        }

        if (ef.CustomName is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.customName", ef.CustomName, af.CustomName ?? "");
        }

        if (ef.CustomNotes is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.customNotes", ef.CustomNotes, af.CustomNotes ?? "");
        }
    }

    private void AssertChildForces(int stepIndex, string prefix,
        List<ExpectedForceDef> expected, IReadOnlyList<ForceState>? actual)
    {
        var actualCount = actual?.Count ?? 0;
        for (var ci = 0; ci < expected.Count; ci++)
        {
            if (ci >= actualCount)
            {
                _errors.Add($"Step {stepIndex}: {prefix}.childForce[{ci}] expected but only {actualCount} child forces");
                continue;
            }
            AssertForce(stepIndex, $"{prefix}.childForce[{ci}]", expected[ci], actual![ci]);
        }
        if (actualCount > expected.Count)
        {
            _errors.Add($"Step {stepIndex}: {prefix} expected {expected.Count} child forces but got {actualCount}");
        }
    }

    private void AssertSelections(int stepIndex, string prefix,
        List<ExpectedSelectionDef> expected, IReadOnlyList<SelectionState> actual)
    {
        for (var si = 0; si < expected.Count; si++)
        {
            var es = expected[si];
            if (si >= actual.Count)
            {
                _errors.Add($"Step {stepIndex}: {prefix}.selection[{si}] expected but only {actual.Count} selections");
                continue;
            }
            var a = actual[si];
            var selPrefix = $"{prefix}.selection[{si}]";

            if (es.Name is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.name", es.Name, a.Name);
            }

            if (es.Type is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.type", es.Type, a.Type);
            }

            if (es.Number is { } num)
            {
                AssertEqual(stepIndex, $"{selPrefix}.number", num, a.Number);
            }

            AssertEqual(stepIndex, $"{selPrefix}.hidden", es.Hidden ?? false, a.Hidden);

            if (es.EntryId is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.entryId", es.EntryId, a.EntryId ?? "");
            }

            if (es.Page is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.page", es.Page, a.Page);
            }

            if (es.PublicationId is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.publicationId", es.PublicationId, a.PublicationId ?? "");
            }

            if (es.PublicationName is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.publicationName", es.PublicationName, a.PublicationName ?? "");
            }

            if (es.EntryGroupId is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.entryGroupId", es.EntryGroupId, a.EntryGroupId ?? "");
            }

            if (es.CustomName is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.customName", es.CustomName, a.CustomName ?? "");
            }

            if (es.CustomNotes is not null)
            {
                AssertEqual(stepIndex, $"{selPrefix}.customNotes", es.CustomNotes, a.CustomNotes ?? "");
            }

            if (es.Costs is { } eCosts)
            {
                foreach (var ec in eCosts)
                {
                    var matchKey = ec.TypeId ?? ec.Name;
                    var ac = ec.TypeId is { Length: > 0 }
                        ? a.Costs.FirstOrDefault(c => c.TypeId == ec.TypeId)
                        : ec.Name is { Length: > 0 }
                            ? a.Costs.FirstOrDefault(c => c.Name == ec.Name)
                            : null;
                    if (ac is null)
                    {
                        _errors.Add($"Step {stepIndex}: {selPrefix} cost type '{matchKey}' not found");
                    }
                    else if (ec.Value is { } v)
                    {
                        AssertEqual(stepIndex, $"{selPrefix}.cost[{matchKey}]", v, ac.Value);
                    }
                }

                if (a.Costs.Count > eCosts.Count)
                {
                    _errors.Add($"Step {stepIndex}: {selPrefix} expected {eCosts.Count} cost(s) but got {a.Costs.Count}: " +
                        $"[{string.Join(", ", a.Costs.Select(c => $"{c.Name}={c.Value}"))}]");
                }
            }

            if (es.Profiles is { } eProfiles)
            {
                AssertProfiles(stepIndex, selPrefix, eProfiles, a.Profiles);
            }

            if (es.Rules is { } eRules)
            {
                AssertRules(stepIndex, selPrefix, eRules, a.Rules);
            }

            if (es.Categories is { } eCategories)
            {
                AssertCategories(stepIndex, selPrefix, eCategories, a.Categories);
            }

            if (es.Children is { } expectedChildren)
            {
                AssertSelections(stepIndex, selPrefix, expectedChildren, a.Children);
            }

            if (es.ChildCount is { } childCount)
            {
                AssertEqual(stepIndex, $"{selPrefix}.childCount", childCount, a.Children.Count);
            }
        }
        if (actual.Count > expected.Count)
        {
            _errors.Add($"Step {stepIndex}: {prefix} expected {expected.Count} selections but got {actual.Count}");
        }
    }

    private void AssertProfiles(int stepIndex, string prefix,
        List<ExpectedProfileDef> expected, IReadOnlyList<ProfileState>? actual)
    {
        var actualCount = actual?.Count ?? 0;
        for (var pi = 0; pi < expected.Count; pi++)
        {
            var ep = expected[pi];
            // Match by name if specified, otherwise by index
            var ap = ep.Name is not null
                ? actual?.FirstOrDefault(p => p.Name == ep.Name)
                : pi < actualCount ? actual![pi] : null;
            if (ap is null)
            {
                _errors.Add($"Step {stepIndex}: {prefix}.profile[{ep.Name ?? pi.ToString()}] not found (have {actualCount} profiles)");
                continue;
            }
            var profPrefix = $"{prefix}.profile[{ep.Name ?? pi.ToString()}]";

            if (ep.TypeName is not null)
            {
                AssertEqual(stepIndex, $"{profPrefix}.typeName", ep.TypeName, ap.TypeName);
            }

            if (ep.TypeId is not null)
            {
                AssertEqual(stepIndex, $"{profPrefix}.typeId", ep.TypeId, ap.TypeId);
            }

            AssertEqual(stepIndex, $"{profPrefix}.hidden", ep.Hidden ?? false, ap.Hidden);

            if (ep.Page is not null)
            {
                AssertEqual(stepIndex, $"{profPrefix}.page", ep.Page, ap.Page ?? "");
            }

            if (ep.PublicationId is not null)
            {
                AssertEqual(stepIndex, $"{profPrefix}.publicationId", ep.PublicationId, ap.PublicationId ?? "");
            }

            if (ep.Characteristics is { } eChars)
            {
                foreach (var ec in eChars)
                {
                    var ac = ec.Name is not null
                        ? ap.Characteristics.FirstOrDefault(c => c.Name == ec.Name)
                        : null;
                    if (ac is null)
                    {
                        _errors.Add($"Step {stepIndex}: {profPrefix}.characteristic[{ec.Name}] not found");
                        continue;
                    }
                    if (ec.Value is not null)
                    {
                        AssertEqual(stepIndex, $"{profPrefix}.characteristic[{ec.Name}].value", ec.Value, ac.Value);
                    }

                    if (ec.TypeId is not null)
                    {
                        AssertEqual(stepIndex, $"{profPrefix}.characteristic[{ec.Name}].typeId", ec.TypeId, ac.TypeId);
                    }
                }
            }
        }
        if (actualCount > expected.Count)
        {
            _errors.Add($"Step {stepIndex}: {prefix} expected {expected.Count} profiles but got {actualCount}");
        }
    }

    private void AssertRules(int stepIndex, string prefix,
        List<ExpectedRuleDef> expected, IReadOnlyList<RuleState>? actual)
    {
        var actualCount = actual?.Count ?? 0;
        for (var ri = 0; ri < expected.Count; ri++)
        {
            var er = expected[ri];
            var ar = er.Name is not null
                ? actual?.FirstOrDefault(r => r.Name == er.Name)
                : ri < actualCount ? actual![ri] : null;
            if (ar is null)
            {
                _errors.Add($"Step {stepIndex}: {prefix}.rule[{er.Name ?? ri.ToString()}] not found (have {actualCount} rules)");
                continue;
            }
            var rulePrefix = $"{prefix}.rule[{er.Name ?? ri.ToString()}]";

            if (er.Description is not null)
            {
                AssertEqual(stepIndex, $"{rulePrefix}.description", er.Description, ar.Description);
            }

            AssertEqual(stepIndex, $"{rulePrefix}.hidden", er.Hidden ?? false, ar.Hidden);

            if (er.Page is not null)
            {
                AssertEqual(stepIndex, $"{rulePrefix}.page", er.Page, ar.Page ?? "");
            }

            if (er.PublicationId is not null)
            {
                AssertEqual(stepIndex, $"{rulePrefix}.publicationId", er.PublicationId, ar.PublicationId ?? "");
            }
        }
        if (actualCount > expected.Count)
        {
            _errors.Add($"Step {stepIndex}: {prefix} expected {expected.Count} rules but got {actualCount}");
        }
    }

    private void AssertCategories(int stepIndex, string prefix,
        List<ExpectedCategoryDef> expected, IReadOnlyList<CategoryState>? actual)
    {
        var actualCount = actual?.Count ?? 0;
        for (var ci = 0; ci < expected.Count; ci++)
        {
            var ec = expected[ci];
            // Match by entryId first, then name, then index
            var ac = ec.EntryId is not null
                ? actual?.FirstOrDefault(c => c.EntryId == ec.EntryId)
                : ec.Name is not null
                    ? actual?.FirstOrDefault(c => c.Name == ec.Name)
                    : ci < actualCount ? actual![ci] : null;
            if (ac is null)
            {
                var matchKey = ec.EntryId ?? ec.Name ?? ci.ToString();
                _errors.Add($"Step {stepIndex}: {prefix}.category[{matchKey}] not found (have {actualCount} categories)");
                continue;
            }
            var catPrefix = $"{prefix}.category[{ec.EntryId ?? ec.Name ?? ci.ToString()}]";

            if (ec.Name is not null && ec.EntryId is not null)
            {
                AssertEqual(stepIndex, $"{catPrefix}.name", ec.Name, ac.Name);
            }

            if (ec.Primary is { } p)
            {
                AssertEqual(stepIndex, $"{catPrefix}.primary", p, ac.Primary);
            }

            if (ec.Profiles is { } catProfs)
            {
                AssertProfiles(stepIndex, catPrefix, catProfs, ac.Profiles);
            }

            if (ec.Rules is { } catRules)
            {
                AssertRules(stepIndex, catPrefix, catRules, ac.Rules);
            }

            if (ec.PublicationId is not null)
            {
                AssertEqual(stepIndex, $"{catPrefix}.publicationId", ec.PublicationId, ac.PublicationId ?? "");
            }

            if (ec.Page is not null)
            {
                AssertEqual(stepIndex, $"{catPrefix}.page", ec.Page, ac.Page ?? "");
            }

            if (ec.CustomName is not null)
            {
                AssertEqual(stepIndex, $"{catPrefix}.customName", ec.CustomName, ac.CustomName ?? "");
            }

            if (ec.CustomNotes is not null)
            {
                AssertEqual(stepIndex, $"{catPrefix}.customNotes", ec.CustomNotes, ac.CustomNotes ?? "");
            }
        }
        if (actualCount > expected.Count)
        {
            _errors.Add($"Step {stepIndex}: {prefix} expected {expected.Count} categories but got {actualCount}");
        }
    }

    private void AssertPublications(int stepIndex, string prefix,
        List<ExpectedPublicationDef> expected, IReadOnlyList<PublicationState>? actual)
    {
        var actualCount = actual?.Count ?? 0;
        for (var pi = 0; pi < expected.Count; pi++)
        {
            var ep = expected[pi];
            var ap = ep.Id is not null
                ? actual?.FirstOrDefault(p => p.Id == ep.Id)
                : ep.Name is not null
                    ? actual?.FirstOrDefault(p => p.Name == ep.Name)
                    : pi < actualCount ? actual![pi] : null;
            if (ap is null)
            {
                var matchKey = ep.Id ?? ep.Name ?? pi.ToString();
                _errors.Add($"Step {stepIndex}: {prefix}.publication[{matchKey}] not found (have {actualCount} publications)");
                continue;
            }
            var pubPrefix = $"{prefix}.publication[{ep.Id ?? ep.Name ?? pi.ToString()}]";

            if (ep.Name is not null && ep.Id is not null)
            {
                AssertEqual(stepIndex, $"{pubPrefix}.name", ep.Name, ap.Name);
            }
        }
        if (actualCount > expected.Count)
        {
            _errors.Add($"Step {stepIndex}: {prefix} expected {expected.Count} publications but got {actualCount}");
        }
    }

    private void AssertEqual<T>(int stepIndex, string field, T expected, T actual)
    {
        if (expected is decimal em && actual is decimal am)
        {
            if (em != am)
            {
                _errors.Add($"Step {stepIndex}: {field}: expected {expected} but got {actual}");
            }

            return;
        }

        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            _errors.Add($"Step {stepIndex}: {field}: expected {expected} but got {actual}");
        }
    }
}
