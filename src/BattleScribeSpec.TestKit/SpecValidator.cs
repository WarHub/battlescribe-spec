using BattleScribeSpec.Protocol;

namespace BattleScribeSpec;

/// <summary>
/// Validates semantic correctness of a loaded spec.
/// Called after YAML deserialization to catch action-parameter mismatches,
/// mutually-exclusive fields, and step field applicability issues.
/// </summary>
public static class SpecValidator
{
    /// <summary>
    /// All step-level YAML field names that are action-only (never valid on assertion steps).
    /// </summary>
    private static readonly HashSet<string> ActionOnlyFields =
    [
        "action",
        "forceEntryId",
        "entryId",
        "catalogueId",
        "forceId",
        "selectionId",
        "costTypeId",
        "count",
        "value",
        "customName",
        "customNotes",
        "categoryEntryId",
        "path",
    ];

    /// <summary>
    /// Per-action definitions of which step parameters are required and which are optional.
    /// </summary>
    private static readonly Dictionary<string, ActionParamDef> ActionParams = new()
    {
        ["addForce"] = new(Required: ["forceEntryId"], Optional: ["catalogueId"]),
        ["addChildForce"] = new(Required: ["forceId", "forceEntryId"], Optional: ["catalogueId"]),
        ["removeForce"] = new(Required: ["forceId"], Optional: []),
        ["selectEntry"] = new(Required: ["forceId", "entryId"], Optional: []),
        ["selectChildEntry"] = new(Required: ["forceId", "selectionId", "entryId"], Optional: []),
        ["deselectSelection"] = new(Required: ["forceId", "selectionId"], Optional: []),
        ["setSelectionCount"] = new(Required: ["forceId", "selectionId", "count"], Optional: []),
        ["duplicateSelection"] = new(Required: ["forceId", "selectionId"], Optional: []),
        ["duplicateForce"] = new(Required: ["forceId"], Optional: []),
        ["setCostLimit"] = new(Required: ["costTypeId", "value"], Optional: []),
        ["setCustomization"] = new(Required: ["forceId"], Optional: ["selectionId", "categoryEntryId", "customName", "customNotes"]),
        ["dump"] = new(Required: [], Optional: []),
    };

    /// <summary>
    /// Tag that opts a spec out of setup cross-reference validation.
    /// Used by specs that intentionally test engine behavior with invalid/broken data.
    /// </summary>
    public const string InvalidDataTag = "invalid-data";

    /// <summary>
    /// Validate the spec. Throws <see cref="SpecValidationException"/> on errors.
    /// </summary>
    public static void Validate(SpecFile spec)
    {
        var errors = new List<string>();

        if (spec.Steps is not null)
        {
            for (var i = 0; i < spec.Steps.Count; i++)
            {
                var step = spec.Steps[i];
                var stepLabel = step.Id is not null ? $"Step {i} ('{step.Id}')" : $"Step {i}";

                var hasAction = step.Action is not null;
                var hasExpectedState = step.ExpectedState is not null;

                if (hasAction && hasExpectedState)
                    errors.Add($"{stepLabel}: step has both 'action' and 'expectedState' — must have exactly one");
                else if (!hasAction && !hasExpectedState)
                    errors.Add($"{stepLabel}: step has neither 'action' nor 'expectedState' — must have exactly one");
                else if (hasAction)
                    ValidateActionStep(step, stepLabel, errors);
                else
                    ValidateAssertionStep(step, stepLabel, errors);
            }
        }

        if (spec.Tags?.Contains(InvalidDataTag) != true)
            ValidateSetupCrossReferences(spec, errors);

        if (errors.Count > 0)
            throw new SpecValidationException(spec.Id, errors);
    }

    private static void ValidateActionStep(StepDef step, string stepLabel, List<string> errors)
    {
        var action = step.Action!;

        if (!ActionParams.TryGetValue(action, out var paramDef))
        {
            errors.Add($"{stepLabel}: unknown action '{action}'");
            return;
        }

        // Check required parameters are present.
        foreach (var required in paramDef.Required)
        {
            if (GetFieldValue(step, required) is null)
                errors.Add($"{stepLabel}: action '{action}' requires '{required}'");
        }

        // Check for unexpected parameters.
        var allowedFields = new HashSet<string>(paramDef.Required);
        allowedFields.UnionWith(paramDef.Optional);
        allowedFields.Add("id"); // id is always allowed (step output reference)

        foreach (var (fieldName, value) in GetSetFields(step))
        {
            if (fieldName is "action" or "expectedState")
                continue;
            if (!allowedFields.Contains(fieldName))
                errors.Add($"{stepLabel}: action '{action}' does not accept '{fieldName}'");
        }
    }

    private static void ValidateAssertionStep(StepDef step, string stepLabel, List<string> errors)
    {
        // Assertion steps must not have action-only fields.
        foreach (var (fieldName, _) in GetSetFields(step))
        {
            if (fieldName is "id" or "expectedState")
                continue;
            if (ActionOnlyFields.Contains(fieldName))
                errors.Add($"{stepLabel}: assertion step has action-only field '{fieldName}'");
        }

        // Validate mutually-exclusive expectedState fields.
        var es = step.ExpectedState!;
        ValidateExpectedStateMutualExclusion(es, stepLabel, errors);

        // Validate engine overrides too.
        if (es.Engines is not null)
        {
            foreach (var (engine, engineEs) in es.Engines)
            {
                ValidateExpectedStateMutualExclusion(engineEs, $"{stepLabel} [engine '{engine}']", errors);
            }
        }
    }

    private static void ValidateExpectedStateMutualExclusion(
        ExpectedStateDef es, string stepLabel, List<string> errors)
    {
        var hasErrors = es.Errors is not null;
        var hasErrorsContain = es.ErrorsContain is not null;
        var hasErrorCount = es.ErrorCount is not null;

        if (hasErrors && hasErrorsContain)
            errors.Add($"{stepLabel}: 'errors' and 'errorsContain' are mutually exclusive");
        if (hasErrors && hasErrorCount)
            errors.Add($"{stepLabel}: 'errors' and 'errorCount' are mutually exclusive");
        if (hasErrorsContain && hasErrorCount)
            errors.Add($"{stepLabel}: 'errorsContain' and 'errorCount' are mutually exclusive");
    }

    /// <summary>
    /// Returns all non-null field name-value pairs on a StepDef (excluding action and expectedState).
    /// </summary>
    private static IEnumerable<(string FieldName, object Value)> GetSetFields(StepDef step)
    {
        if (step.ForceEntryId is not null) yield return ("forceEntryId", step.ForceEntryId);
        if (step.EntryId is not null) yield return ("entryId", step.EntryId);
        if (step.CatalogueId is not null) yield return ("catalogueId", step.CatalogueId);
        if (step.ForceId is not null) yield return ("forceId", step.ForceId);
        if (step.SelectionId is not null) yield return ("selectionId", step.SelectionId);
        if (step.CostTypeId is not null) yield return ("costTypeId", step.CostTypeId);
        if (step.Count is not null) yield return ("count", step.Count);
        if (step.Value is not null) yield return ("value", step.Value);
        if (step.CustomName is not null) yield return ("customName", step.CustomName);
        if (step.CustomNotes is not null) yield return ("customNotes", step.CustomNotes);
        if (step.CategoryEntryId is not null) yield return ("categoryEntryId", step.CategoryEntryId);
        if (step.Path is not null) yield return ("path", step.Path);
    }

    private static object? GetFieldValue(StepDef step, string fieldName) => fieldName switch
    {
        "forceEntryId" => step.ForceEntryId,
        "entryId" => step.EntryId,
        "catalogueId" => step.CatalogueId,
        "forceId" => step.ForceId,
        "selectionId" => step.SelectionId,
        "costTypeId" => step.CostTypeId,
        "count" => step.Count,
        "value" => step.Value,
        "customName" => step.CustomName,
        "customNotes" => step.CustomNotes,
        "categoryEntryId" => step.CategoryEntryId,
        "path" => step.Path,
        _ => null,
    };

    private sealed record ActionParamDef(string[] Required, string[] Optional);

    // ===== Setup cross-reference validation =====

    private static void ValidateSetupCrossReferences(SpecFile spec, List<string> errors)
    {
        var setup = spec.Setup;
        var gs = setup.GameSystem;
        if (gs is null)
        {
            if (setup.DataSource is null)
                errors.Add("Setup requires either 'gameSystem' or 'dataSource' to be defined.");
            return;
        }

        // Collect all declared cost type IDs (from game system and catalogues).
        var costTypeIds = new HashSet<string>();
        if (gs.CostTypes is not null)
            foreach (var ct in gs.CostTypes)
                costTypeIds.Add(ct.Id);

        if (setup.Catalogues is not null)
        {
            foreach (var cat in setup.Catalogues)
            {
                // Catalogue gameSystemId must match the game system.
                if (!string.IsNullOrEmpty(cat.GameSystemId) && cat.GameSystemId != gs.Id)
                    errors.Add($"Catalogue '{cat.Id}' has gameSystemId '{cat.GameSystemId}' but gameSystem id is '{gs.Id}'");

                if (cat.CostTypes is not null)
                    foreach (var ct in cat.CostTypes)
                        costTypeIds.Add(ct.Id);
            }
        }

        // Validate cost value typeId references (walk the entire setup tree).
        ValidateCostTypeRefs(gs.SelectionEntries, costTypeIds, "gameSystem", errors);
        ValidateCostTypeRefs(gs.SharedSelectionEntries, costTypeIds, "gameSystem/shared", errors);

        if (setup.Catalogues is not null)
        {
            foreach (var cat in setup.Catalogues)
            {
                var catPath = $"catalogue '{cat.Id}'";
                ValidateCostTypeRefs(cat.SelectionEntries, costTypeIds, catPath, errors);
                ValidateCostTypeRefs(cat.SharedSelectionEntries, costTypeIds, $"{catPath}/shared", errors);
                ValidateEntryGroupDefaultRefs(cat.SharedSelectionEntryGroups, catPath, errors);
            }
        }

        // Validate selection entry groups (defaultSelectionEntryId).
        ValidateEntryGroupDefaultRefs(gs.SharedSelectionEntryGroups, "gameSystem", errors);
    }

    private static void ValidateCostTypeRefs(
        List<ProtocolSelectionEntry>? entries,
        HashSet<string> costTypeIds,
        string parentPath,
        List<string> errors)
    {
        if (entries is null) return;
        foreach (var entry in entries)
        {
            var entryPath = $"{parentPath}/entry '{entry.Id}'";
            if (entry.Costs is not null)
            {
                foreach (var cost in entry.Costs)
                {
                    if (!string.IsNullOrEmpty(cost.TypeId) && !costTypeIds.Contains(cost.TypeId))
                        errors.Add($"{entryPath}: cost typeId '{cost.TypeId}' not found in declared costTypes");
                }
            }
            // Recurse into children.
            ValidateCostTypeRefs(entry.SelectionEntries, costTypeIds, entryPath, errors);
            ValidateEntryGroupCostRefs(entry.SelectionEntryGroups, costTypeIds, entryPath, errors);
        }
    }

    private static void ValidateEntryGroupCostRefs(
        List<ProtocolSelectionEntryGroup>? groups,
        HashSet<string> costTypeIds,
        string parentPath,
        List<string> errors)
    {
        if (groups is null) return;
        foreach (var group in groups)
        {
            var groupPath = $"{parentPath}/group '{group.Id}'";
            if (group.Costs is not null)
            {
                foreach (var cost in group.Costs)
                {
                    if (!string.IsNullOrEmpty(cost.TypeId) && !costTypeIds.Contains(cost.TypeId))
                        errors.Add($"{groupPath}: cost typeId '{cost.TypeId}' not found in declared costTypes");
                }
            }
            ValidateCostTypeRefs(group.SelectionEntries, costTypeIds, groupPath, errors);
            ValidateEntryGroupCostRefs(group.SelectionEntryGroups, costTypeIds, groupPath, errors);
        }
    }

    private static void ValidateEntryGroupDefaultRefs(
        List<ProtocolSelectionEntryGroup>? groups,
        string parentPath,
        List<string> errors)
    {
        if (groups is null) return;
        foreach (var group in groups)
        {
            var groupPath = $"{parentPath}/group '{group.Id}'";
            if (group.DefaultSelectionEntryId is { } defaultId)
            {
                // defaultSelectionEntryId must reference a direct child entry or entry link.
                var childIds = new HashSet<string>();
                if (group.SelectionEntries is not null)
                    foreach (var se in group.SelectionEntries)
                        childIds.Add(se.Id);
                if (group.EntryLinks is not null)
                    foreach (var el in group.EntryLinks)
                        childIds.Add(el.Id);
                if (!childIds.Contains(defaultId))
                    errors.Add($"{groupPath}: defaultSelectionEntryId '{defaultId}' not found in group's children");
            }
            // Recurse into nested groups.
            ValidateEntryGroupDefaultRefs(group.SelectionEntryGroups, groupPath, errors);
        }
    }
}

/// <summary>
/// Thrown when spec validation fails.
/// </summary>
public sealed class SpecValidationException(string? specId, IReadOnlyList<string> errors)
    : Exception($"Spec '{specId}' validation failed:\n" + string.Join("\n", errors.Select(e => $"  - {e}")))
{
    public string? SpecId => specId;
    public IReadOnlyList<string> Errors => errors;
}
