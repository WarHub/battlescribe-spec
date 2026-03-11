using BattleScribeSpec.Protocol;
using YamlDotNet.Serialization;

namespace BattleScribeSpec;

/// <summary>
/// YAML-serializable models for declarative spec test files.
/// Each spec file defines a complete test scenario: setup, actions, and expected outcomes.
/// </summary>

/// <summary>
/// Root model for a spec YAML file.
/// </summary>
public sealed class SpecFile
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = "";

    [YamlMember(Alias = "category")]
    public string Category { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Per-engine expectations. Null means all engines expected to pass.
    /// Map of engine name to expectation: "pass" (default), "fail", or "skip".
    /// Canonical engine names: "battlescribe", "newrecruit", "phalanx".
    /// Unlisted engines are expected to pass.
    /// </summary>
    [YamlMember(Alias = "engines")]
    public Dictionary<string, string>? Engines { get; set; }

    [YamlMember(Alias = "setup")]
    public SetupDef Setup { get; set; } = new();

    [YamlMember(Alias = "steps")]
    public List<StepDef> Steps { get; set; } = [];

    /// <summary>
    /// Check if this spec should run on the given engine (not "skip").
    /// Null/empty engines means applicable to all engines.
    /// </summary>
    public bool IsApplicableTo(string engineName)
        => !ShouldSkip(engineName);

    /// <summary>
    /// Check if this spec should be skipped entirely for the given engine.
    /// </summary>
    public bool ShouldSkip(string engineName)
    {
        if (Engines is null || Engines.Count == 0)
            return false;
        return Engines.TryGetValue(engineName, out var expectation)
            && string.Equals(expectation, "skip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if this spec is expected to fail on the given engine.
    /// </summary>
    public bool IsExpectedToFail(string engineName)
    {
        if (Engines is null || Engines.Count == 0)
            return false;
        return Engines.TryGetValue(engineName, out var expectation)
            && string.Equals(expectation, "fail", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the expectation for a given engine: "pass", "fail", or "skip".
    /// Defaults to "pass" if engine is not listed or engines is null.
    /// </summary>
    public string GetExpectation(string engineName)
    {
        if (Engines is null || Engines.Count == 0)
            return "pass";
        return Engines.TryGetValue(engineName, out var expectation) ? expectation : "pass";
    }
}

/// <summary>
/// Setup section defining game system and catalogue data.
/// </summary>
public sealed class SetupDef
{
    [YamlMember(Alias = "gameSystem")]
    public ProtocolGameSystem GameSystem { get; set; } = new();

    [YamlMember(Alias = "dataSource")]
    public string? DataSource { get; init; }

    /// <summary>
    /// Multiple catalogues for multi-catalogue scenarios.
    /// </summary>
    [YamlMember(Alias = "catalogues")]
    public List<ProtocolCatalogue>? Catalogues { get; set; }
}

/// <summary>Backward-compatible alias for ProtocolGameSystem in test setup.</summary>
public class GameSystemDef : ProtocolGameSystem { }

/// <summary>Backward-compatible alias for ProtocolCatalogue in test setup.</summary>
public class CatalogueDef : ProtocolCatalogue { }

// ===== Step definitions =====

/// <summary>
/// A single step in a spec test — either an action or an assertion.
/// </summary>
public sealed class StepDef
{
    [YamlMember(Alias = "action")]
    public string? Action { get; set; }

    [YamlMember(Alias = "forceEntryIndex")]
    public int? ForceEntryIndex { get; set; }

    [YamlMember(Alias = "entryName")]
    public string? EntryName { get; set; }

    [YamlMember(Alias = "forceEntryName")]
    public string? ForceEntryName { get; set; }

    [YamlMember(Alias = "childEntryName")]
    public string? ChildEntryName { get; set; }

    [YamlMember(Alias = "forceIndex")]
    public int? ForceIndex { get; set; }

    [YamlMember(Alias = "entryIndex")]
    public int? EntryIndex { get; set; }

    [YamlMember(Alias = "selectionIndex")]
    public int? SelectionIndex { get; set; }

    [YamlMember(Alias = "childEntryIndex")]
    public int? ChildEntryIndex { get; set; }

    [YamlMember(Alias = "catalogueIndex")]
    public int? CatalogueIndex { get; set; }

    [YamlMember(Alias = "catalogueName")]
    public string? CatalogueName { get; set; }

    [YamlMember(Alias = "costTypeId")]
    public string? CostTypeId { get; set; }

    [YamlMember(Alias = "count")]
    public int? Count { get; set; }

    [YamlMember(Alias = "value")]
    public double? Value { get; set; }

    // ===== Assertion fields =====

    [YamlMember(Alias = "assert")]
    public string? Assert { get; set; }

    [YamlMember(Alias = "expected")]
    public object? Expected { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "expectedState")]
    public ExpectedStateDef? ExpectedState { get; set; }
}

/// <summary>
/// Expected roster state for assertion.
/// All fields are optional — only specified fields are checked.
/// </summary>
public sealed class ExpectedStateDef
{
    [YamlMember(Alias = "forceCount")]
    public int? ForceCount { get; set; }

    [YamlMember(Alias = "selectionCount")]
    public int? SelectionCount { get; set; }

    [YamlMember(Alias = "validationErrorCount")]
    public int? ValidationErrorCount { get; set; }

    [YamlMember(Alias = "forces")]
    public List<ExpectedForceDef>? Forces { get; set; }

    [YamlMember(Alias = "costCount")]
    public int? CostCount { get; set; }

    [YamlMember(Alias = "costs")]
    public List<ExpectedCostDef>? Costs { get; set; }

    [YamlMember(Alias = "validationErrors")]
    public List<ExpectedValidationErrorDef>? ValidationErrors { get; set; }

    /// <summary>
    /// New structured error assertions using "on"/"from"/"message" format.
    /// Replaces validationErrors with a more readable syntax.
    /// </summary>
    [YamlMember(Alias = "errors")]
    public List<ErrorAssertionDef>? Errors { get; set; }
}

/// <summary>
/// Expected structured validation error for assertion (legacy format).
/// All fields are optional — only specified fields are checked.
/// </summary>
public sealed class ExpectedValidationErrorDef
{
    [YamlMember(Alias = "message")]
    public string? Message { get; set; }

    [YamlMember(Alias = "ownerType")]
    public string? OwnerType { get; set; }

    [YamlMember(Alias = "ownerEntryId")]
    public string? OwnerEntryId { get; set; }

    [YamlMember(Alias = "entryId")]
    public string? EntryId { get; set; }

    [YamlMember(Alias = "constraintId")]
    public string? ConstraintId { get; set; }
}

/// <summary>
/// Structured error assertion using compact "on"/"from" format.
/// <para>"on" identifies the roster element: "roster", "force", "category cat-troops", "selection se-unit-a"</para>
/// <para>"from" identifies the source as "{entryId}/{constraintId}" with reserved pseudo-values:</para>
/// <para>  - "costLimits/{costTypeId}" for cost limit errors (pseudo-entry)</para>
/// <para>  - "{entryId}/hidden" for hidden entry errors (pseudo-constraint)</para>
/// <para>"message" is an optional substring check on the error message.</para>
/// </summary>
public sealed class ErrorAssertionDef
{
    /// <summary>
    /// The roster element that owns the error.
    /// Format: "{ownerType}" or "{ownerType} {ownerEntryId}".
    /// Examples: "roster", "force", "category cat-troops", "selection se-unit-a".
    /// </summary>
    [YamlMember(Alias = "on")]
    public string On { get; set; } = "";

    /// <summary>
    /// The source entry and constraint that caused the error.
    /// Format: "{entryId}/{constraintId}" with reserved pseudo-values:
    ///   "costLimits/{costTypeId}" for cost limit errors,
    ///   "{entryId}/hidden" for hidden entry errors.
    /// Examples: "se-unit-a/con-min-1", "costLimits/ct-pts", "se-unit-a/hidden".
    /// </summary>
    [YamlMember(Alias = "from")]
    public string? From { get; set; }

    /// <summary>
    /// Optional substring match on the error message.
    /// </summary>
    [YamlMember(Alias = "message")]
    public string? Message { get; set; }
}

public sealed class ExpectedForceDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "selectionCount")]
    public int? SelectionCount { get; set; }

    [YamlMember(Alias = "availableEntryCount")]
    public int? AvailableEntryCount { get; set; }

    [YamlMember(Alias = "selections")]
    public List<ExpectedSelectionDef>? Selections { get; set; }
}

public sealed class ExpectedSelectionDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "number")]
    public int? Number { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool? Hidden { get; set; }

    [YamlMember(Alias = "costs")]
    public List<ExpectedCostDef>? Costs { get; set; }

    [YamlMember(Alias = "children")]
    public List<ExpectedSelectionDef>? Children { get; set; }

    [YamlMember(Alias = "profiles")]
    public List<ExpectedProfileDef>? Profiles { get; set; }

    [YamlMember(Alias = "rules")]
    public List<ExpectedRuleDef>? Rules { get; set; }

    [YamlMember(Alias = "categories")]
    public List<ExpectedCategoryDef>? Categories { get; set; }

    [YamlMember(Alias = "page")]
    public string? Page { get; set; }

    [YamlMember(Alias = "publicationId")]
    public string? PublicationId { get; set; }
}

public sealed class ExpectedCostDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "typeId")]
    public string? TypeId { get; set; }

    [YamlMember(Alias = "value")]
    public double? Value { get; set; }
}

public sealed class ExpectedProfileDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "typeId")]
    public string? TypeId { get; set; }

    [YamlMember(Alias = "typeName")]
    public string? TypeName { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool? Hidden { get; set; }

    [YamlMember(Alias = "page")]
    public string? Page { get; set; }

    [YamlMember(Alias = "publicationId")]
    public string? PublicationId { get; set; }

    [YamlMember(Alias = "characteristics")]
    public List<ExpectedCharacteristicDef>? Characteristics { get; set; }
}

public sealed class ExpectedCharacteristicDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "typeId")]
    public string? TypeId { get; set; }

    [YamlMember(Alias = "value")]
    public string? Value { get; set; }
}

public sealed class ExpectedRuleDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool? Hidden { get; set; }

    [YamlMember(Alias = "page")]
    public string? Page { get; set; }

    [YamlMember(Alias = "publicationId")]
    public string? PublicationId { get; set; }
}

public sealed class ExpectedCategoryDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "entryId")]
    public string? EntryId { get; set; }

    [YamlMember(Alias = "primary")]
    public bool? Primary { get; set; }
}


