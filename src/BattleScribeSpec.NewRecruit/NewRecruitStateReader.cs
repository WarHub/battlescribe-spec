using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Reads roster state from New Recruit's Pinia store via page.EvaluateAsync().
/// Maps NR's internal JavaScript state to BattleScribeSpec RosterState records.
/// </summary>
public static class NewRecruitStateReader
{
    /// <summary>
    /// Read the current roster state from NR's reactive store.
    /// </summary>
    public static async Task<RosterState> ReadRosterStateAsync(IPage page)
    {
        // Evaluate JS in the NR page context to extract roster state from the Pinia store.
        // NR uses Pinia for state management — the store is accessible via Vue's app context.
        // The exact store shape needs to be discovered via browser DevTools during initial development.
        var state = await page.EvaluateAsync<NrRosterSnapshot>("""
            (() => {
                // Access the NR roster store — exact path TBD during integration
                // This is a placeholder that will be refined when testing against the live site
                const app = document.querySelector('#__nuxt')?.__vue_app__;
                if (!app) return { name: '', gameSystemId: '', forces: [], costs: [], validationErrors: [] };

                // Try to find the Pinia store containing roster state
                const pinia = app.config.globalProperties.$pinia;
                if (!pinia) return { name: '', gameSystemId: '', forces: [], costs: [], validationErrors: [] };

                // Iterate stores to find the roster store
                // Placeholder — exact store ID will be discovered during integration
                const stores = pinia._s;
                for (const [id, store] of stores) {
                    if (store.roster || store.forces) {
                        return extractRosterState(store);
                    }
                }

                return { name: '', gameSystemId: '', forces: [], costs: [], validationErrors: [] };

                function extractRosterState(store) {
                    // Placeholder extraction — will be refined based on actual NR store shape
                    return {
                        name: store.roster?.name || store.name || '',
                        gameSystemId: store.roster?.gameSystemId || store.gameSystemId || '',
                        forces: (store.roster?.forces || store.forces || []).map(f => ({
                            name: f.name || '',
                            catalogueId: f.catalogueId || null,
                            selections: (f.selections || []).map(s => extractSelection(s))
                        })),
                        costs: (store.roster?.costs || store.costs || []).map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: c.value || 0
                        })),
                        validationErrors: store.roster?.validationErrors || store.validationErrors || []
                    };
                }

                function extractSelection(sel) {
                    return {
                        name: sel.name || '',
                        entryId: sel.entryId || null,
                        type: sel.type || null,
                        number: sel.number || 1,
                        hidden: sel.hidden || false,
                        costs: (sel.costs || []).map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: c.value || 0
                        })),
                        children: (sel.selections || sel.children || []).map(c => extractSelection(c)),
                        profiles: (sel.profiles || []).map(p => ({
                            name: p.name || '',
                            typeId: p.typeId || null,
                            typeName: p.typeName || null,
                            hidden: p.hidden || false,
                            characteristics: (p.characteristics || []).map(ch => ({
                                name: ch.name || '',
                                typeId: ch.typeId || '',
                                value: ch.value || ''
                            }))
                        })),
                        rules: (sel.rules || []).map(r => ({
                            name: r.name || '',
                            description: r.description || '',
                            hidden: r.hidden || false
                        })),
                        categories: (sel.categories || []).map(cat => ({
                            name: cat.name || '',
                            entryId: cat.entryId || null,
                            primary: cat.primary || false
                        })),
                        page: sel.page || null
                    };
                }
            })()
            """);

        return MapToRosterState(state);
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
