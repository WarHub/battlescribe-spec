using BattleScribeSpec.Protocol;

namespace BattleScribeSpec;

/// <summary>
/// Executes spec test steps against an IRosterEngine and validates assertions.
/// </summary>
public sealed class SpecRunner
{
    private readonly IRosterEngine _engine;
    private readonly DataSourceResolver? _dataSourceResolver;
    private readonly string? _engineName;
    private readonly List<string> _errors = [];
    private ProtocolGameSystem? _gameSystem;
    private ProtocolCatalogue[]? _catalogues;
    private bool _isDataSourceMode;

    public SpecRunner(IRosterEngine engine, DataSourceResolver? dataSourceResolver = null, string? engineName = null)
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
        _gameSystem = null;
        _catalogues = null;
        _isDataSourceMode = false;
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
                _gameSystem = gameSystem;
                _catalogues = catalogues;
                var setupErrors = _engine.Setup(gameSystem, catalogues);
                if (setupErrors.Count > 0)
                {
                    foreach (var setupError in setupErrors)
                        _errors.Add($"Setup error: {setupError}");
                    return new SpecResult(spec.Id, spec.Category, spec.Description, [.. _errors]);
                }
            }

            // Execute steps
            for (var i = 0; i < spec.Steps.Count; i++)
            {
                var step = spec.Steps[i];
                try
                {
                    if (step.Action is not null)
                        ExecuteAction(step, i);
                    else if (step.ExpectedState is not null)
                        ExecuteAssertion(step, i);
                    else
                        _errors.Add($"Step {i}: neither 'action' nor 'expectedState' defined");
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

    private void SetupFromDataSource(string dataSourceUri)
    {
        if (_dataSourceResolver is null)
            throw new InvalidOperationException(
                "DataSource specs require a DataSourceResolver. Pass one to the SpecRunner constructor.");

        _isDataSourceMode = true;
        var resolvedDir = _dataSourceResolver.Resolve(dataSourceUri);

        // Read all .gst and .cat files from the resolved directory
        var files = new List<(string FileName, string Content)>();
        foreach (var file in Directory.EnumerateFiles(resolvedDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".gst", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)))
        {
            files.Add((Path.GetFileName(file), File.ReadAllText(file)));
        }

        if (files.Count == 0)
            throw new InvalidOperationException(
                $"No .gst or .cat files found in resolved data source directory: {resolvedDir}");

        var setupErrors = _engine.SetupFromFiles(files);
        foreach (var err in setupErrors)
            _errors.Add($"Setup: {err}");
    }

    private void ExecuteAction(StepDef step, int stepIndex)
    {
        switch (step.Action)
        {
            case "addForce":
                var addForceCatalogueIndex = step.CatalogueIndex ?? 0;
                if (_isDataSourceMode && step.ForceEntryName is { Length: > 0 } dsForceEntryName)
                {
                    _engine.AddForceByName(dsForceEntryName, step.CatalogueName, addForceCatalogueIndex);
                }
                else
                {
                    var forceEntryIndex = step.ForceEntryName is { Length: > 0 } forceEntryName
                        ? ResolveForceEntryIndex(forceEntryName, stepIndex)
                        : step.ForceEntryIndex ?? 0;
                    if (forceEntryIndex < 0) return;
                    _engine.AddForce(forceEntryIndex, addForceCatalogueIndex);
                }
                break;

            case "removeForce":
                _engine.RemoveForce(step.ForceIndex ?? 0);
                break;

            case "addChildForce":
                _engine.AddChildForce(step.ForceIndex ?? 0, step.ChildForceEntryIndex ?? 0, step.ChildForceIndex);
                break;

            case "removeChildForce":
                _engine.RemoveChildForce(step.ForceIndex ?? 0, step.ChildForceIndex ?? 0);
                break;

            case "selectEntry":
                if (_isDataSourceMode && step.EntryName is { Length: > 0 } dsEntryName)
                {
                    _engine.SelectEntryByName(step.ForceIndex ?? 0, dsEntryName);
                }
                else
                {
                    var selectEntryCatalogueIndex = step.CatalogueIndex ?? 0;
                    var entryIndex = step.EntryName is { Length: > 0 } entryName
                        ? ResolveEntryIndex(entryName, selectEntryCatalogueIndex, stepIndex)
                        : step.EntryIndex ?? 0;
                    if (entryIndex < 0) return;
                    _engine.SelectEntry(step.ForceIndex ?? 0, entryIndex);
                }
                break;

            case "selectChildEntry":
                if (_isDataSourceMode && step.ChildEntryName is { Length: > 0 } dsChildEntryName)
                {
                    _engine.SelectChildEntryByName(
                        step.ForceIndex ?? 0,
                        step.SelectionIndex ?? 0,
                        dsChildEntryName);
                }
                else
                {
                    var selectChildCatalogueIndex = step.CatalogueIndex ?? 0;
                    var childEntryIndex = step.ChildEntryName is { Length: > 0 } childEntryName
                        ? ResolveChildEntryIndex(childEntryName, step.ForceIndex ?? 0, step.SelectionIndex ?? 0, selectChildCatalogueIndex, stepIndex)
                        : step.ChildEntryIndex ?? 0;
                    if (childEntryIndex < 0) return;
                    _engine.SelectChildEntry(
                        step.ForceIndex ?? 0,
                        step.SelectionIndex ?? 0,
                        childEntryIndex);
                }
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

    private int ResolveForceEntryIndex(string forceEntryName, int stepIndex)
    {
        var allForceEntries = new List<ProtocolForceEntry>();
        if (_gameSystem?.ForceEntries != null)
            allForceEntries.AddRange(_gameSystem.ForceEntries);
        if (_catalogues != null)
            foreach (var cat in _catalogues)
                if (cat.ForceEntries != null)
                    allForceEntries.AddRange(cat.ForceEntries);
        if (allForceEntries.Count == 0)
        {
            _errors.Add($"Step {stepIndex}: no force entries available for force entry '{forceEntryName}'");
            return -1;
        }

        var index = allForceEntries.FindIndex(fe => string.Equals(fe.Name, forceEntryName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            _errors.Add($"Step {stepIndex}: force entry name '{forceEntryName}' not found");
        return index;
    }

    private int ResolveEntryIndex(string entryName, int catalogueIndex, int stepIndex)
    {
        var catalogue = GetCatalogue(catalogueIndex, stepIndex);
        if (catalogue?.SelectionEntries is null)
        {
            _errors.Add($"Step {stepIndex}: catalogue[{catalogueIndex}] selection entries not available for entry '{entryName}'");
            return -1;
        }

        var index = catalogue.SelectionEntries.FindIndex(se => string.Equals(se.Name, entryName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            _errors.Add($"Step {stepIndex}: entry name '{entryName}' not found in catalogue[{catalogueIndex}]");
        return index;
    }

    private int ResolveChildEntryIndex(string childEntryName, int forceIndex, int selectionIndex, int catalogueIndex, int stepIndex)
    {
        var state = _engine.GetRosterState();
        if (forceIndex < 0 || forceIndex >= state.Forces.Count)
        {
            _errors.Add($"Step {stepIndex}: force index {forceIndex} out of range (have {state.Forces.Count})");
            return -1;
        }

        var selections = state.Forces[forceIndex].Selections;
        if (selectionIndex < 0 || selectionIndex >= selections.Count)
        {
            _errors.Add($"Step {stepIndex}: selection index {selectionIndex} out of range (have {selections.Count})");
            return -1;
        }

        var parentSelectionName = selections[selectionIndex].Name;
        var catalogue = GetCatalogue(catalogueIndex, stepIndex);
        var parentEntry = catalogue?.SelectionEntries?
            .FirstOrDefault(se => string.Equals(se.Name, parentSelectionName, StringComparison.OrdinalIgnoreCase));

        if (parentEntry is null)
        {
            _errors.Add($"Step {stepIndex}: parent entry '{parentSelectionName}' not found in catalogue[{catalogueIndex}]");
            return -1;
        }

        if (parentEntry.SelectionEntries is null)
        {
            _errors.Add($"Step {stepIndex}: parent entry '{parentSelectionName}' has no child entries in catalogue[{catalogueIndex}]");
            return -1;
        }

        var childIndex = parentEntry.SelectionEntries.FindIndex(se => string.Equals(se.Name, childEntryName, StringComparison.OrdinalIgnoreCase));
        if (childIndex < 0)
            _errors.Add($"Step {stepIndex}: child entry name '{childEntryName}' not found under parent '{parentSelectionName}'");
        return childIndex;
    }

    private ProtocolCatalogue? GetCatalogue(int catalogueIndex, int stepIndex)
    {
        if (_catalogues is null)
        {
            _errors.Add($"Step {stepIndex}: catalogues not available");
            return null;
        }

        if (catalogueIndex < 0 || catalogueIndex >= _catalogues.Length)
        {
            _errors.Add($"Step {stepIndex}: catalogue index {catalogueIndex} out of range (have {_catalogues.Length})");
            return null;
        }

        return _catalogues[catalogueIndex];
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
        if (expected is null) return;

        var state = _engine.GetRosterState();

        if (expected.ForceCount is { } fc)
            AssertEqual(stepIndex, "forceCount", fc, state.Forces.Count);

        if (expected.CostCount is { } cc)
            AssertEqual(stepIndex, "costCount", cc, state.Costs.Count);

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
                    _errors.Add($"Step {stepIndex}: cost type '{matchKey}' not found in roster");
                else if (ec.Value is { } v)
                    AssertEqual(stepIndex, $"cost[{matchKey}].value", v, actual.Value);
            }
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
                    _errors.Add($"Step {stepIndex}: expected no errors but got {actualErrors.Count}: " +
                        string.Join("; ", actualErrors.Select(FormatError)));
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

            int matchIndex = -1;
            for (int i = 0; i < actualErrors.Count; i++)
            {
                if (consumed.Contains(i))
                    continue;
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
                if (ea.MessageContains is not null) desc += $", messageContains='{ea.MessageContains}'";
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

    private static (string entryId, string constraintId) ParseFrom(string from)
    {
        var slashIdx = from.IndexOf('/');
        if (slashIdx < 0) return (from, "");
        return (from[..slashIdx], from[(slashIdx + 1)..]);
    }

    private void AssertForce(int stepIndex, string prefix, ExpectedForceDef ef, ForceState af)
    {
        if (ef.Name is not null)
            AssertEqual(stepIndex, $"{prefix}.name", ef.Name, af.Name);

        if (ef.SelectionCount is { } sc)
            AssertEqual(stepIndex, $"{prefix}.selectionCount", sc, af.Selections.Count);

        if (ef.AvailableEntryCount is { } aec && af.AvailableEntryCount is { } actualAec)
            AssertEqual(stepIndex, $"{prefix}.availableEntryCount", aec, actualAec);

        if (ef.ChildForceCount is { } cfc)
            AssertEqual(stepIndex, $"{prefix}.childForceCount", cfc, af.ChildForces.Count);

        if (ef.ChildForces is { } expectedChildForces)
        {
            for (var ci = 0; ci < expectedChildForces.Count; ci++)
            {
                if (ci >= af.ChildForces.Count)
                {
                    _errors.Add($"Step {stepIndex}: {prefix}.childForce[{ci}] expected but only {af.ChildForces.Count} child forces");
                    continue;
                }
                AssertForce(stepIndex, $"{prefix}.childForce[{ci}]", expectedChildForces[ci], af.ChildForces[ci]);
            }
        }

        if (ef.Selections is { } expectedSels)
            AssertSelections(stepIndex, prefix, expectedSels, af.Selections);

        if (ef.Profiles is { } forceProfs)
            AssertProfiles(stepIndex, prefix, forceProfs, af.Profiles);

        if (ef.Rules is { } forceRules)
            AssertRules(stepIndex, prefix, forceRules, af.Rules);

        if (ef.PublicationId is not null)
            AssertEqual(stepIndex, $"{prefix}.publicationId", ef.PublicationId, af.PublicationId ?? "");

        if (ef.Page is not null)
            AssertEqual(stepIndex, $"{prefix}.page", ef.Page, af.Page ?? "");
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

            if (es.PublicationId is not null)
                AssertEqual(stepIndex, $"{selPrefix}.publicationId", es.PublicationId, a.PublicationId ?? "");

            if (es.PublicationName is not null)
                AssertEqual(stepIndex, $"{selPrefix}.publicationName", es.PublicationName, a.PublicationName ?? "");

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
                        _errors.Add($"Step {stepIndex}: {selPrefix} cost type '{matchKey}' not found");
                    else if (ec.Value is { } v)
                        AssertEqual(stepIndex, $"{selPrefix}.cost[{matchKey}]", v, ac.Value);
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

            if (ep.Page is not null)
                AssertEqual(stepIndex, $"{profPrefix}.page", ep.Page, ap.Page ?? "");

            if (ep.PublicationId is not null)
                AssertEqual(stepIndex, $"{profPrefix}.publicationId", ep.PublicationId, ap.PublicationId ?? "");

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

            if (er.Page is not null)
                AssertEqual(stepIndex, $"{rulePrefix}.page", er.Page, ar.Page ?? "");

            if (er.PublicationId is not null)
                AssertEqual(stepIndex, $"{rulePrefix}.publicationId", er.PublicationId, ar.PublicationId ?? "");
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

            if (ec.Profiles is { } catProfs)
                AssertProfiles(stepIndex, catPrefix, catProfs, ac.Profiles);

            if (ec.Rules is { } catRules)
                AssertRules(stepIndex, catPrefix, catRules, ac.Rules);

            if (ec.PublicationId is not null)
                AssertEqual(stepIndex, $"{catPrefix}.publicationId", ec.PublicationId, ac.PublicationId ?? "");

            if (ec.Page is not null)
                AssertEqual(stepIndex, $"{catPrefix}.page", ec.Page, ac.Page ?? "");
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
