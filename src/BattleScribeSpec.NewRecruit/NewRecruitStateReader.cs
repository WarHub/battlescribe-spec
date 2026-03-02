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
        // Return JSON string to avoid Playwright's type coercion issues with nested records
        var json = await page.EvaluateAsync<string>("""
            (() => {
                const empty = { name: '', gameSystemId: '', forces: [], costs: [], validationErrors: [] };

                // Read from global reference saved during Setup
                const spec = window.__bsspec;
                if (!spec) return JSON.stringify({...empty, validationErrors: ['window.__bsspec not set — was Setup called?']});

                const army = spec.army;
                if (army === null || army === undefined) return JSON.stringify({...empty, validationErrors: ['army is null']});

                try {
                    const result = {
                        name: army.getCustomName?.() || army.getName?.() || spec.row?.name || '',
                        gameSystemId: spec.row?.bsid_system || '',
                        forces: extractForces(army),
                        costs: extractTotalCosts(army),
                        validationErrors: extractErrors(army)
                    };
                    return JSON.stringify(result);
                } catch(e) {
                    return JSON.stringify({ ...empty, validationErrors: ['State read error: ' + e.message] });
                }

                function extractForces(army) {
                    const forces = army.getForces?.() || [];
                    if (!Array.isArray(forces)) return [];
                    return forces.map(f => ({
                        name: f.getName?.() || '',
                        catalogueId: f.catalogueId || f.getId?.() || null,
                        selections: extractSelections(f)
                    }));
                }

                function extractSelections(parent) {
                    const selections = parent.getSelections?.() || [];
                    return selections.map(s => extractSelection(s));
                }

                function extractSelection(sel) {
                    const costs = sel.getCosts?.() || [];
                    const profiles = sel.getModifiedProfiles?.() || [];
                    const rules = sel.getModifiedRules?.() || [];
                    const cats = sel.getSelectionCategories?.() || [];

                    return {
                        name: sel.getName?.() || '',
                        entryId: sel.getId?.() || null,
                        type: sel.getType?.() || null,
                        number: sel.getAmount?.() || 1,
                        hidden: sel.isHidden?.() || false,
                        costs: costs.map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: c.value || 0
                        })),
                        children: extractSelections(sel),
                        profiles: profiles.map(p => ({
                            name: p.name || p.getName?.() || '',
                            typeId: p.typeId || null,
                            typeName: p.typeName || null,
                            hidden: p.hidden || false,
                            characteristics: (p.characteristics || []).map(ch => ({
                                name: ch.name || '',
                                typeId: ch.typeId || '',
                                value: (ch.value ?? '').toString()
                            }))
                        })),
                        rules: rules.map(r => ({
                            name: r.name || r.getName?.() || '',
                            description: r.description || '',
                            hidden: r.hidden || false
                        })),
                        categories: cats.map(cat => ({
                            name: cat.name || cat.getName?.() || '',
                            entryId: cat.entryId || cat.getId?.() || null,
                            primary: cat.primary || false
                        })),
                        page: null
                    };
                }

                function extractTotalCosts(army) {
                    const costs = army.getTotalCosts?.() || army.getCosts?.() || [];
                    if (Array.isArray(costs)) {
                        return costs.map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: c.value || 0
                        }));
                    }
                    if (typeof costs === 'object') {
                        return Object.entries(costs).map(([key, val]) => ({
                            name: key,
                            typeId: key,
                            value: typeof val === 'number' ? val : 0
                        }));
                    }
                    return [];
                }

                function extractErrors(army) {
                    // NR uses errors/allErrors properties on nodes
                    const errors = army.allErrors || army.errors || [];
                    if (Array.isArray(errors)) {
                        return errors.map(e => typeof e === 'string' ? e : (e.message || e.text || JSON.stringify(e)));
                    }
                    return [];
                }
            })()
            """);

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<NrRosterSnapshot>(json, _jsonOptions)
            ?? new NrRosterSnapshot();
        return MapToRosterState(snapshot);
    }

    /// <summary>
    /// Read validation errors from NR's store.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadValidationErrorsAsync(IPage page)
    {
        var state = await ReadRosterStateAsync(page);
        return state.ValidationErrors;
    }

    private static RosterState MapToRosterState(NrRosterSnapshot snapshot)
    {
        var forces = snapshot.Forces.Select(f => new ForceState(
            f.Name,
            f.CatalogueId,
            f.Selections.Select(MapSelection).ToList()
        )).ToList();

        var costs = snapshot.Costs.Select(c =>
            new CostState(c.Name, c.TypeId, c.Value)).ToList();

        return new RosterState(
            snapshot.Name,
            snapshot.GameSystemId,
            forces,
            costs,
            snapshot.ValidationErrors);
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
                p.Characteristics.Select(ch => new CharacteristicState(ch.Name, ch.TypeId, ch.Value)).ToList()
            )).ToList(),
            Rules: sel.Rules.Select(r => new RuleState(r.Name, r.Description, r.Hidden)).ToList(),
            Categories: sel.Categories.Select(c => new CategoryState(c.Name, c.EntryId, c.Primary)).ToList(),
            Page: sel.Page);
    }

    // JSON-serializable snapshot types for page.EvaluateAsync deserialization
    internal record NrRosterSnapshot
    {
        public string Name { get; init; } = "";
        public string GameSystemId { get; init; } = "";
        public List<NrForceSnapshot> Forces { get; init; } = [];
        public List<NrCostSnapshot> Costs { get; init; } = [];
        public List<string> ValidationErrors { get; init; } = [];
    }

    internal record NrForceSnapshot
    {
        public string Name { get; init; } = "";
        public string? CatalogueId { get; init; }
        public List<NrSelectionSnapshot> Selections { get; init; } = [];
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
    }

    internal record NrCategorySnapshot
    {
        public string Name { get; init; } = "";
        public string? EntryId { get; init; }
        public bool Primary { get; init; }
    }
}
