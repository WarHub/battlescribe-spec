using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Roster;

/// <summary>
/// YAML-serializable models for declarative roster spec test files.
/// Each spec file defines a complete test scenario: setup, actions, and expected outcomes.
/// </summary>

/// <summary>
/// Root model for a roster spec YAML file.
/// </summary>
public sealed class SpecFile : SpecFileBase
{
    public required SetupDef Setup { get; set; }

    public required List<StepDef> Steps { get; set; }
}

/// <summary>
/// Setup section defining game system and catalogue data.
/// </summary>
public sealed class SetupDef
{
    public ProtocolGameSystem? GameSystem { get; set; }

    public string? DataSource { get; set; }

    /// <summary>
    /// Multiple catalogues for multi-catalogue scenarios.
    /// </summary>
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
    public string? Id { get; set; }

    public string? Action { get; set; }

    /// <summary>
    /// Force entry definition ID (for addForce, addChildForce).
    /// References a forceEntry in the setup data by its BattleScribe ID.
    /// </summary>
    public string? ForceEntryId { get; set; }

    /// <summary>
    /// Entry definition ID (for selectEntry, selectChildEntry).
    /// References a selectionEntry/entryLink in the setup data by its BattleScribe ID.
    /// </summary>
    public string? EntryId { get; set; }

    /// <summary>
    /// Catalogue definition ID (for addForce when multiple catalogues exist).
    /// </summary>
    public string? CatalogueId { get; set; }

    /// <summary>
    /// Force instance ID (references a force created by a prior step).
    /// May contain a ${{ steps.xxx.forceId }} expression.
    /// </summary>
    public string? ForceId { get; set; }

    /// <summary>
    /// Selection instance ID (references a selection created by a prior step).
    /// May contain a ${{ steps.xxx.selectionId }} expression.
    /// </summary>
    public string? SelectionId { get; set; }

    public string? CostTypeId { get; set; }

    public int? Count { get; set; }

    public decimal? Value { get; set; }

    /// <summary>
    /// Custom name to set (for setCustomization).
    /// </summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// Custom notes to set (for setCustomization).
    /// </summary>
    public string? CustomNotes { get; set; }

    /// <summary>
    /// Category entry ID for setCustomization. Targets the named category in the force.
    /// NR does not support category customization (silently ignored).
    /// </summary>
    public string? CategoryEntryId { get; set; }

    public string? Path { get; set; }

    /// <summary>
    /// Inline BattleScribe <c>.ros</c> XML payload for <c>loadRoster</c>. Mirrors the gamedata
    /// <c>openFile</c> <c>content</c> field: the roster is authored in the spec itself, so a load
    /// spec needs no external fixture and the payload is reviewable next to what it asserts.
    /// Supports <c>${{ steps.* }}</c> expressions.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// Engine names to skip this action step for (e.g., ["battlescribe"]).
    /// When skipped, an empty <see cref="ActionOutputs"/> is stored for the step's ID (if set),
    /// which prevents "step not found" errors in downstream expressions. However, any expression
    /// that references a specific output field (e.g., <c>${{ steps.id.forceId }}</c>) will still
    /// throw because those fields are null on the empty outputs. Do not use skipped-step expressions
    /// in downstream steps for engines where the step is skipped.
    /// Use for actions not supported by certain engines.
    /// </summary>
    public List<string>? SkipEngines { get; set; }

    public ExpectedStateDef? ExpectedState { get; set; }

    /// <summary>
    /// Byte-compare the exported roster XML against a per-engine snapshot (or inline content),
    /// mirroring gamedata <c>expectedFile</c>. The step must carry an <c>id</c> (the snapshot key).
    /// </summary>
    public GameData.ExpectedFileDef? ExpectedFile { get; set; }

    /// <summary>
    /// Per-engine overrides for this step's <em>action inputs</em>. Each key is an engine name; its
    /// value's non-null action-parameter fields (e.g. <c>value</c>, <c>count</c>, <c>entryId</c>)
    /// replace the base for that engine. Lets one step feed a different input per engine — the
    /// action-side counterpart to <see cref="ExpectedStateDef.Engines"/> / <see cref="SkipEngines"/>.
    /// The step's <c>action</c>, <c>id</c>, and assertions are never overridden.
    /// </summary>
    public Dictionary<string, StepDef>? Engines { get; set; }

    /// <summary>
    /// Effective step for the given engine: an engine override's non-null action-input fields replace
    /// the base. Action, id, skip list, assertions, and the override map itself are kept from the base.
    /// </summary>
    public StepDef ForEngine(string? engineName)
    {
        if (engineName is null || Engines is null || !Engines.TryGetValue(engineName, out var o))
        {
            return this;
        }

        return new StepDef
        {
            // Identity / dispatch / assertions — never overridden.
            Id = Id,
            Action = Action,
            SkipEngines = SkipEngines,
            ExpectedState = ExpectedState,
            ExpectedFile = ExpectedFile,
            Engines = Engines,
            // Action inputs — overridable per engine.
            ForceEntryId = o.ForceEntryId ?? ForceEntryId,
            EntryId = o.EntryId ?? EntryId,
            CatalogueId = o.CatalogueId ?? CatalogueId,
            ForceId = o.ForceId ?? ForceId,
            SelectionId = o.SelectionId ?? SelectionId,
            CostTypeId = o.CostTypeId ?? CostTypeId,
            Count = o.Count ?? Count,
            Value = o.Value ?? Value,
            CustomName = o.CustomName ?? CustomName,
            CustomNotes = o.CustomNotes ?? CustomNotes,
            CategoryEntryId = o.CategoryEntryId ?? CategoryEntryId,
            Path = o.Path ?? Path,
            Content = o.Content ?? Content,
        };
    }
}

/// <summary>
/// Expected roster state for assertion.
/// All fields are optional — only specified fields are checked.
/// </summary>
public sealed class ExpectedStateDef
{
    public string? Name { get; set; }

    public int? ForceCount { get; set; }

    public int? SelectionCount { get; set; }

    public List<ExpectedForceDef>? Forces { get; set; }

    public int? CostCount { get; set; }

    public List<ExpectedCostDef>? Costs { get; set; }

    public List<ExpectedCostDef>? CostLimits { get; set; }

    public int? CostLimitCount { get; set; }

    public string? GameSystemName { get; set; }

    public string? GameSystemId { get; set; }

    /// <summary>
    /// Structured error assertions using "on"/"from" format.
    /// Requires an exact-set match: every assertion must match an actual error,
    /// and no extra actual errors are allowed.
    /// Mutually exclusive with <see cref="ErrorsContain"/> and <see cref="ErrorCount"/>.
    /// </summary>
    public List<ErrorAssertionDef>? Errors { get; set; }

    /// <summary>
    /// Subset/superset error assertions: each listed error must match at least one
    /// actual error, but additional actual errors are allowed (not flagged).
    /// Mutually exclusive with <see cref="Errors"/>.
    /// </summary>
    public List<ErrorAssertionDef>? ErrorsContain { get; set; }

    /// <summary>
    /// Assert only the total count of validation errors without matching specifics.
    /// Mutually exclusive with <see cref="Errors"/>.
    /// </summary>
    public int? ErrorCount { get; set; }

    /// <summary>
    /// Per-engine overrides. Each key is an engine name (e.g. "newrecruit"),
    /// each value is a partial ExpectedStateDef whose non-null fields replace
    /// the corresponding base fields for that engine.
    /// </summary>
    public Dictionary<string, ExpectedStateDef>? Engines { get; set; }

    /// <summary>
    /// Returns an effective ExpectedStateDef for the given engine: if the engine
    /// has an override, non-null fields replace the base. Unset fields fall through.
    /// </summary>
    public ExpectedStateDef ForEngine(string? engineName)
    {
        if (engineName is null || Engines is null || !Engines.TryGetValue(engineName, out var over))
        {
            return this;
        }

        return new ExpectedStateDef
        {
            Name = over.Name ?? Name,
            ForceCount = over.ForceCount ?? ForceCount,
            SelectionCount = over.SelectionCount ?? SelectionCount,
            Forces = over.Forces ?? Forces,
            CostCount = over.CostCount ?? CostCount,
            Costs = over.Costs ?? Costs,
            CostLimits = over.CostLimits ?? CostLimits,
            CostLimitCount = over.CostLimitCount ?? CostLimitCount,
            GameSystemName = over.GameSystemName ?? GameSystemName,
            GameSystemId = over.GameSystemId ?? GameSystemId,
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
    public required string On { get; set; }

    /// <summary>
    /// The source entry and constraint that caused the error.
    /// Format: "{entryId}/{constraintId}" with reserved pseudo-values:
    ///   "costLimits/{costTypeId}" for cost limit errors,
    ///   "{entryId}/hidden" for hidden entry errors.
    /// Examples: "se-unit-a/con-min-1", "costLimits/ct-pts", "se-unit-a/hidden".
    /// </summary>
    public required string From { get; set; }

    /// <summary>
    /// Optional substring to match against the error message text.
    /// When set, the actual error's message must contain this value (case-insensitive).
    /// </summary>
    public string? MessageContains { get; set; }
}

public sealed class ExpectedForceDef
{
    public string? Name { get; set; }

    public int? SelectionCount { get; set; }

    public int? AvailableEntryCount { get; set; }

    public bool? Hidden { get; set; }

    public int? ChildForceCount { get; set; }

    public List<ExpectedForceDef>? ChildForces { get; set; }

    public List<ExpectedSelectionDef>? Selections { get; set; }

    public List<ExpectedProfileDef>? Profiles { get; set; }

    public List<ExpectedRuleDef>? Rules { get; set; }

    public string? PublicationId { get; set; }

    public string? Page { get; set; }

    public string? EntryId { get; set; }

    public int? CategoryCount { get; set; }

    public List<ExpectedCategoryDef>? Categories { get; set; }

    public List<ExpectedPublicationDef>? Publications { get; set; }

    public string? CatalogueName { get; set; }

    public string? CatalogueId { get; set; }

    public string? CustomName { get; set; }

    public string? CustomNotes { get; set; }
}

public sealed class ExpectedSelectionDef
{
    public string? Name { get; set; }

    public string? Type { get; set; }

    public int? Number { get; set; }

    public bool? Hidden { get; set; }

    public List<ExpectedCostDef>? Costs { get; set; }

    public List<ExpectedSelectionDef>? Children { get; set; }

    public List<ExpectedProfileDef>? Profiles { get; set; }

    public List<ExpectedRuleDef>? Rules { get; set; }

    public List<ExpectedCategoryDef>? Categories { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public string? PublicationName { get; set; }

    public string? EntryGroupId { get; set; }

    public string? EntryId { get; set; }

    public string? CustomName { get; set; }

    public string? CustomNotes { get; set; }

    public int? ChildCount { get; set; }
}

public sealed class ExpectedCostDef
{
    public string? Name { get; set; }

    public string? TypeId { get; set; }

    public decimal? Value { get; set; }

    public bool? Hidden { get; set; }
}

public sealed class ExpectedProfileDef
{
    public string? Name { get; set; }

    public string? TypeId { get; set; }

    public string? TypeName { get; set; }

    public bool? Hidden { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }

    public List<ExpectedCharacteristicDef>? Characteristics { get; set; }
}

public sealed class ExpectedCharacteristicDef
{
    public string? Name { get; set; }

    public string? TypeId { get; set; }

    public string? Value { get; set; }
}

public sealed class ExpectedRuleDef
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public bool? Hidden { get; set; }

    public string? Page { get; set; }

    public string? PublicationId { get; set; }
}

public sealed class ExpectedCategoryDef
{
    public string? Name { get; set; }

    public string? EntryId { get; set; }

    public bool? Primary { get; set; }

    public List<ExpectedProfileDef>? Profiles { get; set; }

    public List<ExpectedRuleDef>? Rules { get; set; }

    public string? PublicationId { get; set; }

    public string? Page { get; set; }

    public string? CustomName { get; set; }

    public string? CustomNotes { get; set; }
}


public sealed class ExpectedPublicationDef
{
    public string? Id { get; set; }

    public string? Name { get; set; }
}
