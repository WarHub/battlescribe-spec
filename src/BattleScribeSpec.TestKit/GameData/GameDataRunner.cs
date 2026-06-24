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
    private string _specId = "";
    private string? _specDir;

    /// <summary>
    /// When true, <c>expectedFile</c> assertions (re)write the expected side-file from the actual
    /// export instead of comparing. Defaults to the <c>BSSPEC_UPDATE_SNAPSHOTS</c> env var so the
    /// xUnit conformance harness honors it; the CLI also sets it via <c>--update-snapshots</c>.
    /// </summary>
    public bool UpdateSnapshots { get; set; }
        = Environment.GetEnvironmentVariable("BSSPEC_UPDATE_SNAPSHOTS") is "1" or "true";

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
        _specId = spec.Id;
        _specDir = spec.SourceDirectory;
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
                    else if (step.ExpectedState is not null || step.ExpectedFile is not null)
                    {
                        if (step.ExpectedState is not null)
                        {
                            ExecuteAssertion(step, i);
                        }
                        if (step.ExpectedFile is not null)
                        {
                            ExecuteFileAssertion(step, i);
                        }
                    }
                    else
                    {
                        _errors.Add($"Step {i}: neither 'action', 'expectedState' nor 'expectedFile' defined");
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

    /// <summary>
    /// Export the active file and byte-compare it to the expected (inline or side-file). In
    /// update-snapshots mode, (re)write the expected side-file from the actual export instead.
    /// </summary>
    private void ExecuteFileAssertion(GameDataStepDef step, int stepIndex)
    {
        var def = step.ExpectedFile!.ForEngine(_engineName);
        var actual = NormalizeNewlines(_engine.ExportActiveFile());
        var ext = FileExtFromRoot(actual);

        // Inline expected content (author-maintained; never rewritten by --update-snapshots).
        if (def.Content is { } inline)
        {
            var expectedInline = NormalizeNewlines(_exprResolver.Resolve(inline) ?? inline);
            if (expectedInline != actual)
            {
                ReportFileMismatch(stepIndex, "(inline)", expectedInline, actual);
            }
            return;
        }

        // Side-file resolved by the step's id.
        if (step.Id is not { Length: > 0 } key)
        {
            _errors.Add($"Step {stepIndex}: expectedFile side-file requires the step to have an 'id'");
            return;
        }
        if (_specDir is null)
        {
            _errors.Add($"Step {stepIndex}: expectedFile side-file needs a spec loaded from disk (no SourceDirectory)");
            return;
        }

        var engine = _engineName ?? GameDataSnapshotResolver.BaseEngineName;

        if (UpdateSnapshots)
        {
            WriteSnapshot(engine, key, ext, actual);
            return;
        }

        var path = GameDataSnapshotResolver.Resolve(_specDir, _specId, key, engine, ext);
        if (path is null)
        {
            _errors.Add($"Step {stepIndex}: no expected file for snapshot '{key}' (engine '{engine}', .{ext}); " +
                "run with --update-snapshots (or BSSPEC_UPDATE_SNAPSHOTS=1) to create it");
            return;
        }

        var expected = NormalizeNewlines(File.ReadAllText(path));
        expected = NormalizeNewlines(_exprResolver.Resolve(expected) ?? expected);
        if (expected != actual)
        {
            ReportFileMismatch(stepIndex, Path.GetFileName(path), expected, actual);
        }
    }

    /// <summary>(Re)write an expected side-file: base from the base engine, override (only on divergence) otherwise.</summary>
    private void WriteSnapshot(string engine, string key, string ext, string actual)
    {
        var basePath = GameDataSnapshotResolver.BasePath(_specDir!, _specId, key, ext);
        if (GameDataSnapshotResolver.IsBaseEngine(engine))
        {
            SafeWriteSnapshot(basePath, actual);
            return;
        }

        var overridePath = GameDataSnapshotResolver.OverridePath(_specDir!, _specId, key, engine, ext);
        var baseContent = File.Exists(basePath) ? NormalizeNewlines(File.ReadAllText(basePath)) : null;
        if (baseContent == actual)
        {
            if (File.Exists(overridePath))
            {
                File.Delete(overridePath);
            }
            return;
        }

        if (baseContent is null)
        {
            Console.Error.WriteLine($"[snapshot] base missing for '{key}'; wrote '{engine}' override. " +
                $"Generate the base ('{GameDataSnapshotResolver.BaseEngineName}') first.");
        }

        SafeWriteSnapshot(overridePath, actual);
    }

    private static void SafeWriteSnapshot(string path, string content)
    {
        // Don't clobber an author-maintained templated expected.
        if (File.Exists(path) && File.ReadAllText(path).Contains("${{", StringComparison.Ordinal))
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, content);
    }

    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n");

    private static string FileExtFromRoot(string xml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, @"<\s*(gameSystem|catalogue|roster)\b");
        return m.Success && m.Groups[1].Value == "gameSystem" ? "gst" : "cat";
    }

    private void ReportFileMismatch(int stepIndex, string source, string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var detail = $"expected {e.Length} line(s), actual {a.Length} line(s)";
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "(missing)";
            var al = i < a.Length ? a[i] : "(missing)";
            if (el != al)
            {
                detail = $"first diff at line {i + 1}:\n      expected: {el}\n      actual:   {al}";
                break;
            }
        }

        _errors.Add($"Step {stepIndex}: exported file does not match expected ({source}). {detail}");
    }

    /// <summary>
    /// openFile: open an already-loaded file by <paramref name="entryId"/>, or load a file from
    /// inline <c>content</c> / a side-file keyed by the step id and open it. Returns the opened id.
    /// </summary>
    private GameDataActionOutputs ExecuteOpenFile(GameDataStepDef step, int stepIndex, string? entryId)
    {
        // Open an already-loaded file by id.
        if (entryId is { Length: > 0 })
        {
            _engine.OpenFile(entryId);
            return new GameDataActionOutputs { EntryId = entryId };
        }

        // Load from inline content and open.
        if (step.Content is { } inline)
        {
            var xml = _exprResolver.Resolve(inline) ?? inline;
            return new GameDataActionOutputs { EntryId = _engine.LoadFile(xml) };
        }

        // Load from a side-file keyed by the step id and open.
        if (step.Id is { Length: > 0 } key && _specDir is not null)
        {
            var engine = _engineName ?? GameDataSnapshotResolver.BaseEngineName;
            var path = GameDataSnapshotResolver.Resolve(_specDir, _specId, key, engine, "cat")
                ?? GameDataSnapshotResolver.Resolve(_specDir, _specId, key, engine, "gst")
                ?? throw new InvalidOperationException(
                    $"Step {stepIndex}: openFile found no side-file for key '{key}' (engine '{engine}', .cat/.gst) next to the spec");
            var xml = File.ReadAllText(path);
            xml = _exprResolver.Resolve(xml) ?? xml;
            return new GameDataActionOutputs { EntryId = _engine.LoadFile(xml) };
        }

        throw new InvalidOperationException(
            $"Step {stepIndex}: openFile requires 'entryId' (open a loaded file), 'content' (inline XML), or a step 'id' matching a side-file");
    }

    private void ExecuteAction(GameDataStepDef step, int stepIndex)
    {
        var entryId = _exprResolver.Resolve(step.EntryId);
        var parentId = _exprResolver.Resolve(step.ParentId);

        GameDataActionOutputs? outputs = null;
        switch (step.Action)
        {
            case "addEntry":
                // On addEntry, entryId (if given) is the declared id for the created entry.
                outputs = _engine.AddEntry(
                    parentId ?? throw new InvalidOperationException($"Step {stepIndex}: addEntry requires parentId"),
                    step.EntryType ?? throw new InvalidOperationException($"Step {stepIndex}: addEntry requires entryType"),
                    step.Name,
                    entryId);
                break;

            case "removeEntry":
                _engine.RemoveEntry(
                    entryId ?? throw new InvalidOperationException($"Step {stepIndex}: removeEntry requires entryId"));
                break;

            case "openFile":
                outputs = ExecuteOpenFile(step, stepIndex, entryId);
                break;

            case "setFields":
                {
                    var target = entryId ?? throw new InvalidOperationException($"Step {stepIndex}: setFields requires entryId");
                    if (step.Fields is null && step.Characteristics is null && step.Costs is null)
                    {
                        throw new InvalidOperationException(
                            $"Step {stepIndex}: setFields requires at least one of 'fields', 'characteristics' or 'costs'");
                    }

                    // Apply scalar fields first (e.g. a profile's typeId before its characteristics).
                    if (step.Fields is { } fields)
                    {
                        foreach (var (field, value) in fields)
                        {
                            _engine.SetField(target, field, _exprResolver.Resolve(value));
                        }
                    }

                    if (step.Costs is { } costs)
                    {
                        foreach (var (costTypeId, value) in costs)
                        {
                            _engine.SetCost(target, _exprResolver.Resolve(costTypeId)!, _exprResolver.Resolve(value));
                        }
                    }

                    if (step.Characteristics is { } characteristics)
                    {
                        foreach (var (nameOrTypeId, value) in characteristics)
                        {
                            _engine.SetCharacteristic(target, _exprResolver.Resolve(nameOrTypeId)!, _exprResolver.Resolve(value));
                        }
                    }

                    break;
                }

            case "addLink":
                // On addLink, entryId (if given) is the declared id for the created link.
                outputs = _engine.AddLink(
                    parentId ?? throw new InvalidOperationException($"Step {stepIndex}: addLink requires parentId"),
                    step.LinkType ?? throw new InvalidOperationException($"Step {stepIndex}: addLink requires linkType"),
                    _exprResolver.Resolve(step.TargetId) ?? throw new InvalidOperationException($"Step {stepIndex}: addLink requires targetId"),
                    entryId);
                break;

            case "reload":
                _engine.Reload();
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
        if (expected.Errors is { } expectedErrors)
        {
            AssertErrors(stepIndex, expectedErrors);
        }

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

    private void AssertErrors(int stepIndex, List<ExpectedErrorDef> expected)
    {
        var actual = _engine.GetValidationErrors();

        // Empty expected list = assert no validation errors.
        if (expected.Count == 0)
        {
            if (actual.Count > 0)
            {
                _errors.Add($"Step {stepIndex}: expected no validation errors but got {actual.Count}: " +
                    $"[{string.Join("; ", actual.Select(e => e.Message))}]");
            }

            return;
        }

        foreach (var ee in expected)
        {
            var match = actual.FirstOrDefault(a =>
                (ee.Message is null || (a.Message ?? "").Contains(ee.Message, StringComparison.OrdinalIgnoreCase))
                && (ee.EntryId is null || a.EntryId == ee.EntryId)
                && (ee.ConstraintId is null || a.ConstraintId == ee.ConstraintId));

            if (match is null)
            {
                _errors.Add($"Step {stepIndex}: expected validation error (message~'{ee.Message}', " +
                    $"entryId={ee.EntryId}, constraintId={ee.ConstraintId}) not found. " +
                    $"Actual: [{string.Join("; ", actual.Select(e => e.Message))}]");
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

        AssertRootFields(stepIndex, prefix, expected.Fields, actual.Fields);

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

        if (expected.SharedSelectionEntryGroups is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedSelectionEntryGroups", expected.SharedSelectionEntryGroups, actual.SharedSelectionEntryGroups);
        }

        if (expected.SharedInfoGroups is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedInfoGroups", expected.SharedInfoGroups, actual.SharedInfoGroups);
        }
    }

    private void AssertCatalogue(int stepIndex, ExpectedCatalogueDataDef expected, CatalogueDataState actual)
    {
        var prefix = $"catalogue[{actual.Id}]";
        if (expected.Name is not null)
        {
            AssertEqual(stepIndex, $"{prefix}.name", expected.Name, actual.Name);
        }

        AssertRootFields(stepIndex, prefix, expected.Fields, actual.Fields);

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

        if (expected.SharedInfoGroups is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedInfoGroups", expected.SharedInfoGroups, actual.SharedInfoGroups);
        }

        if (expected.CatalogueLinks is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.catalogueLinks", expected.CatalogueLinks, actual.CatalogueLinks);
        }

        if (expected.SharedForceEntries is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedForceEntries", expected.SharedForceEntries, actual.SharedForceEntries);
        }

        if (expected.SharedAssociations is not null)
        {
            AssertEntryList(stepIndex, $"{prefix}.sharedAssociations", expected.SharedAssociations, actual.SharedAssociations);
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

    private void AssertRootFields(int stepIndex, string prefix,
        Dictionary<string, string?>? expected, IReadOnlyDictionary<string, string?>? actual)
    {
        if (expected is not { Count: > 0 })
        {
            return;
        }

        foreach (var (key, expectedValue) in expected)
        {
            string? actualValue = null;
            actual?.TryGetValue(key, out actualValue);
            AssertEqual(stepIndex, $"{prefix}.fields[{key}]", expectedValue ?? "", actualValue ?? "");
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
