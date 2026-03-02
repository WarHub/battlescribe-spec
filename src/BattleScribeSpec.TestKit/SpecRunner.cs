namespace BattleScribeSpec;

/// <summary>
/// Executes spec test steps against an IRosterEngine and validates assertions.
/// </summary>
public sealed class SpecRunner
{
    private readonly IRosterEngine _engine;
    private readonly List<string> _errors = [];

    public SpecRunner(IRosterEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Run a complete spec test. Returns list of assertion failures (empty = pass).
    /// </summary>
    public SpecResult Run(SpecFile spec)
    {
        _errors.Clear();
        try
        {
            // Setup
            var scenario = SpecLoader.ToSpecModels(spec.Setup);
            var setupErrors = _engine.Setup(scenario.GameSystem, scenario.Catalogues);
            if (setupErrors.Count > 0)
            {
                foreach (var setupError in setupErrors)
                    _errors.Add($"Setup error: {setupError}");
                return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors]);
            }

            // Execute steps
            for (var i = 0; i < spec.Steps.Count; i++)
            {
                var step = spec.Steps[i];
                try
                {
                    if (step.Action is not null)
                        ExecuteAction(step, i);
                    else if (step.Assert is not null || step.ExpectedState is not null)
                        ExecuteAssertion(step, i);
                    else
                        _errors.Add($"Step {i}: neither 'action' nor 'assert'/'expectedState' defined");
                }
                catch (Exception ex)
                {
                    _errors.Add($"Step {i}: {ex.GetType().Name}: {ex.Message}");
                    if (step.Action is not null)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _errors.Add($"Setup failed: {ex.GetType().Name}: {ex.Message}");
        }

        return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors]);
    }

    private void ExecuteAction(StepDef step, int stepIndex)
    {
        switch (step.Action)
        {
            case "addForce":
                _engine.AddForce(step.ForceEntryIndex ?? 0, step.CatalogueIndex ?? 0);
                break;

            case "removeForce":
                _engine.RemoveForce(step.ForceIndex ?? 0);
                break;

            case "selectEntry":
                _engine.SelectEntry(step.ForceIndex ?? 0, step.EntryIndex ?? 0);
                break;

            case "selectChildEntry":
                _engine.SelectChildEntry(
                    step.ForceIndex ?? 0,
                    step.SelectionIndex ?? 0,
                    step.ChildEntryIndex ?? 0);
                break;

            case "deselectSelection":
                _engine.DeselectSelection(step.ForceIndex ?? 0, step.SelectionIndex ?? 0);
                break;

            case "setSelectionCount":
                _engine.SetSelectionCount(
                    step.ForceIndex ?? 0,
                    step.EntryIndex ?? 0,
                    step.Count ?? 1);
                break;

            case "duplicateSelection":
                _engine.DuplicateSelection(step.ForceIndex ?? 0, step.SelectionIndex ?? 0);
                break;

            case "setCostLimit":
                _engine.SetCostLimit(step.CostTypeId ?? "", step.Value ?? 0);
                break;

            default:
                _errors.Add($"Step {stepIndex}: unknown action '{step.Action}'");
                break;
        }
    }

    private void ExecuteAssertion(StepDef step, int stepIndex)
    {
        if (step.Assert is not null)
        {
            switch (step.Assert)
            {
                case "rosterState":
                    AssertExpectedState(step.ExpectedState, stepIndex);
                    break;

                case "forceCount":
                    AssertEqual(stepIndex, "forceCount",
                        Convert.ToInt32(step.Expected),
                        _engine.GetRosterState().Forces.Count);
                    break;

                case "hasValidationErrors":
                    AssertEqual(stepIndex, "hasValidationErrors",
                        Convert.ToBoolean(step.Expected),
                        _engine.GetValidationErrors().Count > 0);
                    break;

                case "validationErrorCount":
                    AssertEqual(stepIndex, "validationErrorCount",
                        Convert.ToInt32(step.Expected),
                        _engine.GetValidationErrors().Count);
                    break;

                case "noValidationErrors":
                    var errors = _engine.GetValidationErrors();
                    if (errors.Count > 0)
                    {
                        _errors.Add($"Step {stepIndex}: expected no validation errors but got {errors.Count}: " +
                            string.Join("; ", errors.Select(e => e.Message)));
                    }
                    break;

                case "selectionCount":
                {
                    var state = _engine.GetRosterState();
                    var forceIdx = step.ForceIndex ?? 0;
                    if (forceIdx < state.Forces.Count)
                    {
                        AssertEqual(stepIndex, "selectionCount",
                            Convert.ToInt32(step.Expected),
                            state.Forces[forceIdx].Selections.Count);
                    }
                    else
                    {
                        _errors.Add($"Step {stepIndex}: force index {forceIdx} out of range (have {state.Forces.Count})");
                    }
                    break;
                }

                case "selectionName":
                {
                    var state = _engine.GetRosterState();
                    var forceIdx = step.ForceIndex ?? 0;
                    var selIdx = step.SelectionIndex ?? 0;
                    var expectedName = step.Expected?.ToString();
                    if (forceIdx < state.Forces.Count && selIdx < state.Forces[forceIdx].Selections.Count)
                    {
                        AssertEqual(stepIndex, "selectionName",
                            expectedName ?? "",
                            state.Forces[forceIdx].Selections[selIdx].Name);
                    }
                    else
                    {
                        _errors.Add($"Step {stepIndex}: force[{forceIdx}].selection[{selIdx}] out of range");
                    }
                    break;
                }

                case "totalCost":
                {
                    var state = _engine.GetRosterState();
                    var typeId = step.CostTypeId;
                    var expectedVal = Convert.ToDouble(step.Expected);
                    var cost = state.Costs.FirstOrDefault(c => c.TypeId == typeId);
                    if (cost is null)
                    {
                        _errors.Add($"Step {stepIndex}: cost type '{typeId}' not found");
                    }
                    else
                    {
                        AssertEqual(stepIndex, $"totalCost[{typeId}]", expectedVal, cost.Value);
                    }
                    break;
                }

                default:
                    _errors.Add($"Step {stepIndex}: unknown assert type '{step.Assert}'");
                    break;
            }
        }
        else if (step.ExpectedState is not null)
        {
            AssertExpectedState(step.ExpectedState, stepIndex);
        }
    }

    private void AssertExpectedState(ExpectedStateDef? expected, int stepIndex)
    {
        if (expected is null) return;

        var state = _engine.GetRosterState();

        if (expected.ForceCount is { } fc)
            AssertEqual(stepIndex, "forceCount", fc, state.Forces.Count);

        if (expected.HasValidationErrors is { } hve)
            AssertEqual(stepIndex, "hasValidationErrors", hve, _engine.GetValidationErrors().Count > 0);

        if (expected.ValidationErrorCount is { } vec)
            AssertEqual(stepIndex, "validationErrorCount", vec, _engine.GetValidationErrors().Count);

        if (expected.Costs is { } expectedCosts)
        {
            foreach (var ec in expectedCosts)
            {
                var actual = state.Costs.FirstOrDefault(c => c.TypeId == ec.TypeId);
                if (actual is null)
                    _errors.Add($"Step {stepIndex}: cost type '{ec.TypeId}' not found in roster");
                else if (ec.Value is { } v)
                    AssertEqual(stepIndex, $"cost[{ec.TypeId}].value", v, actual.Value);
            }
        }

        if (expected.Forces is { } expectedForces)
        {
            for (var fi = 0; fi < expectedForces.Count; fi++)
            {
                var ef = expectedForces[fi];
                if (fi >= state.Forces.Count)
                {
                    _errors.Add($"Step {stepIndex}: expected force[{fi}] but only {state.Forces.Count} forces");
                    continue;
                }
                var af = state.Forces[fi];

                if (ef.Name is not null)
                    AssertEqual(stepIndex, $"force[{fi}].name", ef.Name, af.Name);

                if (ef.SelectionCount is { } sc)
                    AssertEqual(stepIndex, $"force[{fi}].selectionCount", sc, af.Selections.Count);

                if (ef.AvailableEntryCount is { } aec && af.AvailableEntryCount is { } actualAec)
                    AssertEqual(stepIndex, $"force[{fi}].availableEntryCount", aec, actualAec);

                if (ef.Selections is { } expectedSels)
                    AssertSelections(stepIndex, $"force[{fi}]", expectedSels, af.Selections);
            }
        }

        if (expected.SelectionCount is { } totalSelCount)
        {
            var actualTotal = state.Forces.Sum(f => f.Selections.Count);
            AssertEqual(stepIndex, "totalSelectionCount", totalSelCount, actualTotal);
        }

        if (expected.ValidationErrors is { } expectedErrors)
        {
            var actualErrors = _engine.GetValidationErrors();
            foreach (var ee in expectedErrors)
            {
                var match = actualErrors.FirstOrDefault(ae =>
                    (ee.Message is null || ae.Message.Contains(ee.Message)) &&
                    (ee.OwnerType is null || ae.OwnerType == ee.OwnerType) &&
                    (ee.OwnerEntryId is null || ae.OwnerEntryId == ee.OwnerEntryId) &&
                    (ee.EntryId is null || ae.EntryId == ee.EntryId) &&
                    (ee.ConstraintId is null || ae.ConstraintId == ee.ConstraintId));
                if (match is null)
                {
                    var desc = string.Join(", ",
                        new[] {
                            ee.Message is not null ? $"message~'{ee.Message}'" : null,
                            ee.OwnerType is not null ? $"ownerType='{ee.OwnerType}'" : null,
                            ee.OwnerEntryId is not null ? $"ownerEntryId='{ee.OwnerEntryId}'" : null,
                            ee.EntryId is not null ? $"entryId='{ee.EntryId}'" : null,
                            ee.ConstraintId is not null ? $"constraintId='{ee.ConstraintId}'" : null,
                        }.Where(s => s is not null));
                    _errors.Add($"Step {stepIndex}: expected validation error matching [{desc}] not found in: [{string.Join("; ", actualErrors.Select(e => e.Message))}]");
                }
            }
        }

        if (expected.Errors is { } errorsAssertions)
        {
            var actualErrors = _engine.GetValidationErrors();
            if (errorsAssertions.Count == 0)
            {
                // errors: [] means expect no errors
                if (actualErrors.Count > 0)
                    _errors.Add($"Step {stepIndex}: expected no errors but got {actualErrors.Count}: " +
                        string.Join("; ", actualErrors.Select(FormatError)));
            }
            else
            {
                foreach (var ea in errorsAssertions)
                {
                    var (expectedOwnerType, expectedOwnerEntryId) = ParseOn(ea.On);
                    var (expectedEntryId, expectedConstraintId) = ParseFrom(ea.From);

                    var match = actualErrors.FirstOrDefault(ae =>
                        ae.OwnerType == expectedOwnerType &&
                        (expectedOwnerEntryId is null || ae.OwnerEntryId == expectedOwnerEntryId) &&
                        (expectedEntryId is null || ae.EntryId == expectedEntryId) &&
                        (expectedConstraintId is null || ae.ConstraintId == expectedConstraintId) &&
                        (ea.Message is null || ae.Message.Contains(ea.Message)));
                    if (match is null)
                    {
                        var desc = $"on='{ea.On}'";
                        if (ea.From is not null) desc += $", from='{ea.From}'";
                        if (ea.Message is not null) desc += $", message~'{ea.Message}'";
                        _errors.Add($"Step {stepIndex}: expected error [{desc}] not found in: [{string.Join("; ", actualErrors.Select(FormatError))}]");
                    }
                }
            }
        }
    }

    private static string FormatError(ValidationErrorState e)
    {
        var on = e.OwnerType ?? "?";
        if (e.OwnerEntryId is not null) on += $" {e.OwnerEntryId}";
        var from = e.EntryId is not null && e.ConstraintId is not null ? $"{e.EntryId}/{e.ConstraintId}" : null;
        return from is not null ? $"{on} <- {from}: {e.Message}" : $"{on}: {e.Message}";
    }

    private static (string ownerType, string? ownerEntryId) ParseOn(string on)
    {
        var spaceIdx = on.IndexOf(' ');
        if (spaceIdx < 0) return (on, null);
        return (on[..spaceIdx], on[(spaceIdx + 1)..]);
    }

    private static (string? entryId, string? constraintId) ParseFrom(string? from)
    {
        if (from is null) return (null, null);
        var slashIdx = from.IndexOf('/');
        if (slashIdx < 0) return (from, null);
        return (from[..slashIdx], from[(slashIdx + 1)..]);
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
                AssertEqual(stepIndex, $"{selPrefix}.name", es.Name, a.Name);

            if (es.Type is not null)
                AssertEqual(stepIndex, $"{selPrefix}.type", es.Type, a.Type);

            if (es.Number is { } num)
                AssertEqual(stepIndex, $"{selPrefix}.number", num, a.Number);

            if (es.Hidden is { } hidden)
                AssertEqual(stepIndex, $"{selPrefix}.hidden", hidden, a.Hidden);

            if (es.Page is not null)
                AssertEqual(stepIndex, $"{selPrefix}.page", es.Page, a.Page);

            if (es.Costs is { } eCosts)
            {
                foreach (var ec in eCosts)
                {
                    var ac = a.Costs.FirstOrDefault(c => c.TypeId == ec.TypeId);
                    if (ac is null)
                        _errors.Add($"Step {stepIndex}: {selPrefix} cost type '{ec.TypeId}' not found");
                    else if (ec.Value is { } v)
                        AssertEqual(stepIndex, $"{selPrefix}.cost[{ec.TypeId}]", v, ac.Value);
                }
            }

            if (es.Profiles is { } eProfiles)
                AssertProfiles(stepIndex, selPrefix, eProfiles, a.Profiles);

            if (es.Rules is { } eRules)
                AssertRules(stepIndex, selPrefix, eRules, a.Rules);

            if (es.Categories is { } eCategories)
                AssertCategories(stepIndex, selPrefix, eCategories, a.Categories);

            if (es.Children is { } expectedChildren)
                AssertSelections(stepIndex, selPrefix, expectedChildren, a.Children);
        }
    }

    private void AssertProfiles(int stepIndex, string prefix,
        List<ExpectedProfileDef> expected, IReadOnlyList<ProfileState> actual)
    {
        for (var pi = 0; pi < expected.Count; pi++)
        {
            var ep = expected[pi];
            // Match by name if specified, otherwise by index
            var ap = ep.Name is not null
                ? actual.FirstOrDefault(p => p.Name == ep.Name)
                : pi < actual.Count ? actual[pi] : null;
            if (ap is null)
            {
                _errors.Add($"Step {stepIndex}: {prefix}.profile[{ep.Name ?? pi.ToString()}] not found (have {actual.Count} profiles)");
                continue;
            }
            var profPrefix = $"{prefix}.profile[{ep.Name ?? pi.ToString()}]";

            if (ep.TypeName is not null)
                AssertEqual(stepIndex, $"{profPrefix}.typeName", ep.TypeName, ap.TypeName);

            if (ep.Hidden is { } h)
                AssertEqual(stepIndex, $"{profPrefix}.hidden", h, ap.Hidden);

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
                        AssertEqual(stepIndex, $"{profPrefix}.characteristic[{ec.Name}].value", ec.Value, ac.Value);
                }
            }
        }
    }

    private void AssertRules(int stepIndex, string prefix,
        List<ExpectedRuleDef> expected, IReadOnlyList<RuleState> actual)
    {
        for (var ri = 0; ri < expected.Count; ri++)
        {
            var er = expected[ri];
            var ar = er.Name is not null
                ? actual.FirstOrDefault(r => r.Name == er.Name)
                : ri < actual.Count ? actual[ri] : null;
            if (ar is null)
            {
                _errors.Add($"Step {stepIndex}: {prefix}.rule[{er.Name ?? ri.ToString()}] not found (have {actual.Count} rules)");
                continue;
            }
            var rulePrefix = $"{prefix}.rule[{er.Name ?? ri.ToString()}]";

            if (er.Description is not null)
                AssertEqual(stepIndex, $"{rulePrefix}.description", er.Description, ar.Description);

            if (er.Hidden is { } h)
                AssertEqual(stepIndex, $"{rulePrefix}.hidden", h, ar.Hidden);
        }
    }

    private void AssertCategories(int stepIndex, string prefix,
        List<ExpectedCategoryDef> expected, IReadOnlyList<CategoryState> actual)
    {
        for (var ci = 0; ci < expected.Count; ci++)
        {
            var ec = expected[ci];
            var ac = ec.Name is not null
                ? actual.FirstOrDefault(c => c.Name == ec.Name)
                : ci < actual.Count ? actual[ci] : null;
            if (ac is null)
            {
                _errors.Add($"Step {stepIndex}: {prefix}.category[{ec.Name ?? ci.ToString()}] not found (have {actual.Count} categories)");
                continue;
            }
            var catPrefix = $"{prefix}.category[{ec.Name ?? ci.ToString()}]";

            if (ec.Primary is { } p)
                AssertEqual(stepIndex, $"{catPrefix}.primary", p, ac.Primary);
        }
    }

    private void AssertEqual<T>(int stepIndex, string field, T expected, T actual)
    {
        if (expected is double ed && actual is double ad)
        {
            if (Math.Abs(ed - ad) > 1e-9)
                _errors.Add($"Step {stepIndex}: {field}: expected {expected} but got {actual}");
            return;
        }

        if (expected is float ef && actual is float af)
        {
            if (Math.Abs(ef - af) > 1e-6f)
                _errors.Add($"Step {stepIndex}: {field}: expected {expected} but got {actual}");
            return;
        }

        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            _errors.Add($"Step {stepIndex}: {field}: expected {expected} but got {actual}");
    }
}

/// <summary>
/// Result of running a single spec test.
/// </summary>
public sealed record SpecResult(
    string SpecId,
    string Category,
    string Description,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}
