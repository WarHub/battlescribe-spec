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
    /// Engine names are open-ended strings (e.g. "battlescribe", "newrecruit").
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
    public string? DataSource { get; set; }

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
    /// <summary>
    /// Optional step ID for referencing this step's outputs in later steps.
    /// Convention: verb-noun kebab-case (add-hq, select-captain).
    /// Required only when outputs are referenced via ${{ steps.xxx.yyy }}.
    /// </summary>
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    [YamlMember(Alias = "action")]
    public string? Action { get; set; }

    /// <summary>
    /// Force entry definition ID (for addForce, addChildForce).
    /// References a forceEntry in the setup data by its BattleScribe ID.
    /// </summary>
    [YamlMember(Alias = "forceEntryId")]
    public string? ForceEntryId { get; set; }

    /// <summary>
    /// Entry definition ID (for selectEntry, selectChildEntry).
    /// References a selectionEntry/entryLink in the setup data by its BattleScribe ID.
    /// </summary>
    [YamlMember(Alias = "entryId")]
    public string? EntryId { get; set; }

    /// <summary>
    /// Catalogue definition ID (for addForce when multiple catalogues exist).
    /// </summary>
    [YamlMember(Alias = "catalogueId")]
    public string? CatalogueId { get; set; }

    /// <summary>
    /// Force instance ID (references a force created by a prior step).
    /// May contain a ${{ steps.xxx.forceId }} expression.
    /// </summary>
    [YamlMember(Alias = "forceId")]
    public string? ForceId { get; set; }

    /// <summary>
    /// Selection instance ID (references a selection created by a prior step).
    /// May contain a ${{ steps.xxx.selectionId }} expression.
    /// </summary>
    [YamlMember(Alias = "selectionId")]
    public string? SelectionId { get; set; }

    [YamlMember(Alias = "costTypeId")]
    public string? CostTypeId { get; set; }

    [YamlMember(Alias = "count")]
    public int? Count { get; set; }

    [YamlMember(Alias = "value")]
    public double? Value { get; set; }

    /// <summary>
    /// Custom name to set (for setCustomization).
    /// </summary>
    [YamlMember(Alias = "customName")]
    public string? CustomName { get; set; }

    /// <summary>
    /// Custom notes to set (for setCustomization).
    /// </summary>
    [YamlMember(Alias = "customNotes")]
    public string? CustomNotes { get; set; }

    /// <summary>
    /// Category entry ID for targeting a specific category (for setCustomization).
    /// </summary>
    [YamlMember(Alias = "categoryEntryId")]
    public string? CategoryEntryId { get; set; }

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

    [YamlMember(Alias = "forces")]
    public List<ExpectedForceDef>? Forces { get; set; }

    [YamlMember(Alias = "costCount")]
    public int? CostCount { get; set; }

    [YamlMember(Alias = "costs")]
    public List<ExpectedCostDef>? Costs { get; set; }

    [YamlMember(Alias = "costLimits")]
    public List<ExpectedCostDef>? CostLimits { get; set; }

    [YamlMember(Alias = "costLimitCount")]
    public int? CostLimitCount { get; set; }

    [YamlMember(Alias = "gameSystemName")]
    public string? GameSystemName { get; set; }

    /// <summary>
    /// Structured error assertions using "on"/"from" format.
    /// Requires an exact-set match: every assertion must match an actual error,
    /// and no extra actual errors are allowed.
    /// Mutually exclusive with <see cref="ErrorsContain"/> and <see cref="ErrorCount"/>.
    /// </summary>
    [YamlMember(Alias = "errors")]
    public List<ErrorAssertionDef>? Errors { get; set; }

    /// <summary>
    /// Subset/superset error assertions: each listed error must match at least one
    /// actual error, but additional actual errors are allowed (not flagged).
    /// Mutually exclusive with <see cref="Errors"/>.
    /// </summary>
    [YamlMember(Alias = "errorsContain")]
    public List<ErrorAssertionDef>? ErrorsContain { get; set; }

    /// <summary>
    /// Assert only the total count of validation errors without matching specifics.
    /// Mutually exclusive with <see cref="Errors"/>.
    /// </summary>
    [YamlMember(Alias = "errorCount")]
    public int? ErrorCount { get; set; }

    /// <summary>
    /// Per-engine overrides. Each key is an engine name (e.g. "newrecruit"),
    /// each value is a partial ExpectedStateDef whose non-null fields replace
    /// the corresponding base fields for that engine.
    /// </summary>
    [YamlMember(Alias = "engines")]
    public Dictionary<string, ExpectedStateDef>? Engines { get; set; }

    /// <summary>
    /// Returns an effective ExpectedStateDef for the given engine: if the engine
    /// has an override, non-null fields replace the base. Unset fields fall through.
    /// </summary>
    public ExpectedStateDef ForEngine(string? engineName)
    {
        if (engineName is null || Engines is null || !Engines.TryGetValue(engineName, out var over))
            return this;
        return new ExpectedStateDef
        {
            ForceCount = over.ForceCount ?? ForceCount,
            SelectionCount = over.SelectionCount ?? SelectionCount,
            Forces = over.Forces ?? Forces,
            CostCount = over.CostCount ?? CostCount,
            Costs = over.Costs ?? Costs,
            CostLimits = over.CostLimits ?? CostLimits,
            CostLimitCount = over.CostLimitCount ?? CostLimitCount,
            GameSystemName = over.GameSystemName ?? GameSystemName,
            Errors = over.Errors ?? Errors,
            ErrorsContain = over.ErrorsContain ?? ErrorsContain,
            ErrorCount = over.ErrorCount ?? ErrorCount,
            // Don't propagate Engines into the merged result
        };
    }
}

/// <summary>
/// Structured error assertion using compact "on"/"from" format.
/// <para>"on" identifies the roster element: "roster", "force", "category cat-troops", "selection se-unit-a"</para>
/// <para>"from" identifies the source as "{entryId}/{constraintId}" with reserved pseudo-values:</para>
/// <para>  - "costLimits/{costTypeId}" for cost limit errors (pseudo-entry)</para>
/// <para>  - "{entryId}/hidden" for hidden entry errors (pseudo-constraint)</para>
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
    /// Required. Format: "{entryId}/{constraintId}" with reserved pseudo-values:
    ///   "costLimits/{costTypeId}" for cost limit errors,
    ///   "{entryId}/hidden" for hidden entry errors.
    /// Examples: "se-unit-a/con-min-1", "costLimits/ct-pts", "se-unit-a/hidden".
    /// </summary>
    [YamlMember(Alias = "from")]
    public string From { get; set; } = "";

    /// <summary>
    /// Optional substring to match against the error message text.
    /// When set, the actual error's message must contain this value (case-insensitive).
    /// </summary>
    [YamlMember(Alias = "messageContains")]
    public string? MessageContains { get; set; }
}

public sealed class ExpectedForceDef
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "selectionCount")]
    public int? SelectionCount { get; set; }

    [YamlMember(Alias = "availableEntryCount")]
    public int? AvailableEntryCount { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool? Hidden { get; set; }

    [YamlMember(Alias = "childForceCount")]
    public int? ChildForceCount { get; set; }

    [YamlMember(Alias = "childForces")]
    public List<ExpectedForceDef>? ChildForces { get; set; }

    [YamlMember(Alias = "selections")]
    public List<ExpectedSelectionDef>? Selections { get; set; }

    [YamlMember(Alias = "profiles")]
    public List<ExpectedProfileDef>? Profiles { get; set; }

    [YamlMember(Alias = "rules")]
    public List<ExpectedRuleDef>? Rules { get; set; }

    [YamlMember(Alias = "publicationId")]
    public string? PublicationId { get; set; }

    [YamlMember(Alias = "page")]
    public string? Page { get; set; }

    [YamlMember(Alias = "entryId")]
    public string? EntryId { get; set; }

    [YamlMember(Alias = "categoryCount")]
    public int? CategoryCount { get; set; }

    [YamlMember(Alias = "categories")]
    public List<ExpectedCategoryDef>? Categories { get; set; }

    [YamlMember(Alias = "publications")]
    public List<ExpectedPublicationDef>? Publications { get; set; }

    [YamlMember(Alias = "catalogueName")]
    public string? CatalogueName { get; set; }

    [YamlMember(Alias = "catalogueId")]
    public string? CatalogueId { get; set; }

    [YamlMember(Alias = "customName")]
    public string? CustomName { get; set; }

    [YamlMember(Alias = "customNotes")]
    public string? CustomNotes { get; set; }
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

    [YamlMember(Alias = "publicationName")]
    public string? PublicationName { get; set; }

    [YamlMember(Alias = "entryGroupId")]
    public string? EntryGroupId { get; set; }

    [YamlMember(Alias = "entryId")]
    public string? EntryId { get; set; }

    [YamlMember(Alias = "customName")]
    public string? CustomName { get; set; }

    [YamlMember(Alias = "customNotes")]
    public string? CustomNotes { get; set; }

    [YamlMember(Alias = "childCount")]
    public int? ChildCount { get; set; }
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

    [YamlMember(Alias = "profiles")]
    public List<ExpectedProfileDef>? Profiles { get; set; }

    [YamlMember(Alias = "rules")]
    public List<ExpectedRuleDef>? Rules { get; set; }

    [YamlMember(Alias = "publicationId")]
    public string? PublicationId { get; set; }

    [YamlMember(Alias = "page")]
    public string? Page { get; set; }

    [YamlMember(Alias = "customNotes")]
    public string? CustomNotes { get; set; }
}


public sealed class ExpectedPublicationDef
{
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    [YamlMember(Alias = "name")]
    public string? Name { get; set; }
}