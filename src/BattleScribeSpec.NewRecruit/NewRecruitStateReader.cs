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
    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
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

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<NrRosterSnapshot>(json, _jsonOptions)
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
        var forces = snapshot.Forces.Select(f => new ForceState(
            f.Name,
            f.CatalogueId,
            f.Selections.Select(MapSelection).ToList(),
            Profiles: f.Profiles.Select(p => new ProfileState(
                p.Name, p.TypeId, p.TypeName, p.Hidden,
                p.Characteristics.Select(ch => new CharacteristicState(ch.Name, ch.TypeId, ch.Value)).ToList(),
                p.Page, p.PublicationId)).ToList(),
            Rules: f.Rules.Select(r => new RuleState(r.Name, r.Description, r.Hidden, r.Page, r.PublicationId)).ToList(),
            PublicationId: f.PublicationId,
            Page: f.Page
        )).ToList();

        var costs = snapshot.Costs.Select(c =>
            new CostState(c.Name, c.TypeId, c.Value)).ToList();

        return new RosterState(
            snapshot.Name,
            snapshot.GameSystemId,
            forces,
            costs,
            snapshot.ValidationErrors.Select(e => new ValidationErrorState(
                e.Message,
                OwnerType: e.OwnerType,
                OwnerEntryId: e.OwnerEntryId,
                EntryId: e.EntryId,
                ConstraintId: e.ConstraintId)).ToList());
    }

    private static SelectionState MapSelection(NrSelectionSnapshot sel)
    {
        return new SelectionState(
            sel.Name,
            sel.EntryId,
            sel.Type,
            sel.Number,
            sel.Hidden,
            sel.Costs.Select(c => new CostState(c.Name, c.TypeId, c.Value)).ToList(),
            sel.Children.Select(MapSelection).ToList(),
            Profiles: sel.Profiles.Select(p => new ProfileState(
                p.Name,
                p.TypeId,
                p.TypeName,
                p.Hidden,
                p.Characteristics.Select(ch => new CharacteristicState(ch.Name, ch.TypeId, ch.Value)).ToList(),
                p.Page,
                p.PublicationId
            )).ToList(),
            Rules: sel.Rules.Select(r => new RuleState(r.Name, r.Description, r.Hidden, r.Page, r.PublicationId)).ToList(),
            Categories: sel.Categories.Select(c => new CategoryState(
                c.Name, c.EntryId, c.Primary,
                Profiles: c.Profiles.Select(p => new ProfileState(
                    p.Name, p.TypeId, p.TypeName, p.Hidden,
                    p.Characteristics.Select(ch => new CharacteristicState(ch.Name, ch.TypeId, ch.Value)).ToList(),
                    p.Page, p.PublicationId)).ToList(),
                Rules: c.Rules.Select(r => new RuleState(r.Name, r.Description, r.Hidden, r.Page, r.PublicationId)).ToList(),
                PublicationId: c.PublicationId,
                Page: c.Page)).ToList(),
            Page: sel.Page);
    }

    // JSON-serializable snapshot types for page.EvaluateAsync deserialization
    internal record NrRosterSnapshot
    {
        public string Name { get; init; } = "";
        public string GameSystemId { get; init; } = "";
        public List<NrForceSnapshot> Forces { get; init; } = [];
        public List<NrCostSnapshot> Costs { get; init; } = [];
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
        public string Name { get; init; } = "";
        public string? CatalogueId { get; init; }
        public List<NrSelectionSnapshot> Selections { get; init; } = [];
        public List<NrProfileSnapshot> Profiles { get; init; } = [];
        public List<NrRuleSnapshot> Rules { get; init; } = [];
        public string? PublicationId { get; init; }
        public string? Page { get; init; }
    }

    internal record NrSelectionSnapshot
    {
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
    }

    internal record NrCostSnapshot
    {
        public string Name { get; init; } = "";
        public string TypeId { get; init; } = "";
        public double Value { get; init; }
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
}
