using BattleScribeSpec.Roster;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Reads roster state from NR's Pinia store via page.EvaluateAsync().
/// Maps NR's internal JS tree to BattleScribeSpec RosterState records.
///
/// Access: lists.getCurrentList() → {row, army, book}
/// The 'army' is the roster object with prototype methods:
/// - getName(), getForces(), getCosts(), getTotalCosts()
/// - getSelections(), getEntries(), getChildren()
/// - getModifiedProfiles(), getModifiedRules()
/// - getPrimaryCategory(), getSelectionCategories()
/// - errors/allErrors properties for validation
/// </summary>
public static class NewRecruitStateReader
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    /// <summary>
    /// Read the current roster state from NR's reactive store.
    /// </summary>
    public static async Task<RosterState> ReadRosterStateAsync(IPage page)
    {
        // Call the pre-injected state reader function (defined in JsHelpers.InjectionScript)
        var json = await page.EvaluateAsync<string>("() => window.__bsspec_readState()");

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<NrRosterSnapshot>(json, JsonOptions)
            ?? new NrRosterSnapshot();
        return MapToRosterState(snapshot);
    }

    /// <summary>
    /// Read validation errors from NR's store.
    /// </summary>
    public static async Task<IReadOnlyList<ValidationErrorState>> ReadValidationErrorsAsync(IPage page)
    {
        var state = await ReadRosterStateAsync(page);
        return state.ValidationErrors;
    }

    private static RosterState MapToRosterState(NrRosterSnapshot snapshot)
    {
        var forces = snapshot.Forces.Select(MapForce).ToList();

        var costs = snapshot.Costs.Select(c =>
            new CostState(c.Name, c.TypeId, c.Value, c.Hidden)).ToList();

        var costLimits = snapshot.CostLimits?.Select(c =>
            new CostState(c.Name, c.TypeId, c.Value, c.Hidden)).ToList();

        return new RosterState(
            snapshot.Name,
            snapshot.GameSystemId,
            forces,
            costs,
            [.. snapshot.ValidationErrors.Select(e => new ValidationErrorState(
                e.Message,
                OwnerType: e.OwnerType,
                OwnerEntryId: e.OwnerEntryId,
                EntryId: e.EntryId,
                ConstraintId: e.ConstraintId))],
            CostLimits: costLimits,
            GameSystemName: snapshot.GameSystemName);
    }

    private static ForceState MapForce(NrForceSnapshot f)
    {
        return new ForceState(
            Id: f.Id,
            f.Name,
            f.CatalogueId,
            [.. f.Selections.Select(MapSelection)],
            ChildForces: [.. f.ChildForces.Select(MapForce)],
            Profiles: [.. f.Profiles.Select(p => new ProfileState(
                p.Name, p.TypeId, p.TypeName, p.Hidden,
                [.. p.Characteristics.Select(ch => new CharacteristicState(ch.Name, ch.TypeId, ch.Value))],
                p.Page, p.PublicationId))],
            Rules: [.. f.Rules.Select(r => new RuleState(r.Name, r.Description, r.Hidden, r.Page, r.PublicationId))],
            Hidden: f.Hidden,
            PublicationId: f.PublicationId,
            Page: f.Page,
            EntryId: f.EntryId,
            Categories: [.. f.Categories.Select(c => new CategoryState(
                Name: c.Name,
                EntryId: c.EntryId,
                Primary: c.Primary,
                PublicationId: c.PublicationId,
                Page: c.Page))],
            Publications: f.Publications?.Select(p => new PublicationState(p.Id, p.Name)).ToList(),
            CatalogueName: f.CatalogueName,
            CustomName: f.CustomName,
            CustomNotes: f.CustomNotes
        );
    }

    private static SelectionState MapSelection(NrSelectionSnapshot sel)
    {
        return new SelectionState(
            Id: sel.Id,
            sel.Name,
            sel.EntryId,
            sel.Type,
            sel.Number,
            sel.Hidden,
            [.. sel.Costs.Select(c => new CostState(c.Name, c.TypeId, c.Value, c.Hidden))],
            [.. sel.Children.Select(MapSelection)],
            Profiles: [.. sel.Profiles.Select(p => new ProfileState(
                p.Name,
                p.TypeId,
                p.TypeName,
                p.Hidden,
                [.. p.Characteristics.Select(ch => new CharacteristicState(ch.Name, ch.TypeId, ch.Value))],
                p.Page,
                p.PublicationId
            ))],
            Rules: [.. sel.Rules.Select(r => new RuleState(r.Name, r.Description, r.Hidden, r.Page, r.PublicationId))],
            Categories: [.. sel.Categories.Select(c => new CategoryState(
                Name: c.Name,
                EntryId: c.EntryId,
                Primary: c.Primary,
                Profiles: [.. c.Profiles.Select(p => new ProfileState(
                    p.Name, p.TypeId, p.TypeName, p.Hidden,
                    [.. p.Characteristics.Select(ch => new CharacteristicState(ch.Name, ch.TypeId, ch.Value))],
                    p.Page, p.PublicationId))],
                Rules: [.. c.Rules.Select(r => new RuleState(r.Name, r.Description, r.Hidden, r.Page, r.PublicationId))],
                PublicationId: c.PublicationId,
                Page: c.Page))],
            Page: sel.Page,
            PublicationId: sel.PublicationId,
            PublicationName: sel.PublicationName,
            EntryGroupId: sel.EntryGroupId,
            CustomName: sel.CustomName,
            CustomNotes: sel.CustomNotes);
    }

    // JSON-serializable snapshot types for page.EvaluateAsync deserialization
    internal record NrRosterSnapshot
    {
        public string Name { get; init; } = "";
        public string GameSystemId { get; init; } = "";
        public string? GameSystemName { get; init; }
        public List<NrForceSnapshot> Forces { get; init; } = [];
        public List<NrCostSnapshot> Costs { get; init; } = [];
        public List<NrCostSnapshot>? CostLimits { get; init; }
        public List<NrErrorSnapshot> ValidationErrors { get; init; } = [];
    }

    internal record NrErrorSnapshot
    {
        public string Message { get; init; } = "";
        public string? OwnerType { get; init; }
        public string? OwnerEntryId { get; init; }
        public string? EntryId { get; init; }
        public string? ConstraintId { get; init; }
    }

    internal record NrForceSnapshot
    {
        public string? Id { get; init; }
        public string Name { get; init; } = "";
        public string? CatalogueId { get; init; }
        public bool Hidden { get; init; }
        public List<NrSelectionSnapshot> Selections { get; init; } = [];
        public List<NrForceSnapshot> ChildForces { get; init; } = [];
        public List<NrProfileSnapshot> Profiles { get; init; } = [];
        public List<NrRuleSnapshot> Rules { get; init; } = [];
        public string? PublicationId { get; init; }
        public string? Page { get; init; }
        public string? EntryId { get; init; }
        public List<NrCategorySnapshot> Categories { get; init; } = [];
        public List<NrPublicationSnapshot>? Publications { get; init; }
        public string? CatalogueName { get; init; }
        public string? CustomName { get; init; }
        public string? CustomNotes { get; init; }
    }

    internal record NrSelectionSnapshot
    {
        public string? Id { get; init; }
        public string Name { get; init; } = "";
        public string? EntryId { get; init; }
        public string? Type { get; init; }
        public int Number { get; init; } = 1;
        public bool Hidden { get; init; }
        public List<NrCostSnapshot> Costs { get; init; } = [];
        public List<NrSelectionSnapshot> Children { get; init; } = [];
        public List<NrProfileSnapshot> Profiles { get; init; } = [];
        public List<NrRuleSnapshot> Rules { get; init; } = [];
        public List<NrCategorySnapshot> Categories { get; init; } = [];
        public string? Page { get; init; }
        public string? PublicationId { get; init; }
        public string? PublicationName { get; init; }
        public string? EntryGroupId { get; init; }
        public string? CustomName { get; init; }
        public string? CustomNotes { get; init; }
    }

    internal record NrCostSnapshot
    {
        public string Name { get; init; } = "";
        public string TypeId { get; init; } = "";
        public decimal Value { get; init; }
        public bool Hidden { get; init; }
    }

    internal record NrProfileSnapshot
    {
        public string Name { get; init; } = "";
        public string? TypeId { get; init; }
        public string? TypeName { get; init; }
        public bool Hidden { get; init; }
        public string? Page { get; init; }
        public string? PublicationId { get; init; }
        public List<NrCharacteristicSnapshot> Characteristics { get; init; } = [];
    }

    internal record NrCharacteristicSnapshot
    {
        public string Name { get; init; } = "";
        public string? TypeId { get; init; }
        public string Value { get; init; } = "";
    }

    internal record NrRuleSnapshot
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public bool Hidden { get; init; }
        public string? Page { get; init; }
        public string? PublicationId { get; init; }
    }

    internal record NrCategorySnapshot
    {
        public string Name { get; init; } = "";
        public string? EntryId { get; init; }
        public bool Primary { get; init; }
        public List<NrProfileSnapshot> Profiles { get; init; } = [];
        public List<NrRuleSnapshot> Rules { get; init; } = [];
        public string? PublicationId { get; init; }
        public string? Page { get; init; }
    }

    internal record NrPublicationSnapshot
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
    }
}
