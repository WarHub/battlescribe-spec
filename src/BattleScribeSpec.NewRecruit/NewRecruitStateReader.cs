using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Reads roster state from New Recruit's Pinia store via page.EvaluateAsync().
/// Maps NR's internal JavaScript state to BattleScribeSpec RosterState records.
///
/// Based on NR store discovery (docs/nr-store-mapping.md):
/// - `lists` store: treeData, listData, getCurrentList()
/// - `listsPage` store: editedForce, editedUnit, addingUnit
/// - `gameStore`: gameUnit
///
/// The roster data model is accessed via lists.getCurrentList() which returns
/// the active list object containing the roster, forces, and selections.
/// </summary>
public static class NewRecruitStateReader
{
    /// <summary>
    /// Read the current roster state from NR's reactive store.
    /// </summary>
    public static async Task<RosterState> ReadRosterStateAsync(IPage page)
    {
        var state = await page.EvaluateAsync<NrRosterSnapshot>("""
            (() => {
                const empty = { name: '', gameSystemId: '', forces: [], costs: [], validationErrors: [] };

                const app = document.querySelector('#__nuxt')?.__vue_app__;
                if (!app) return empty;

                const pinia = app.config.globalProperties.$pinia;
                if (!pinia) return empty;

                const lists = pinia._s.get('lists');
                if (!lists) return empty;

                const currentList = lists.getCurrentList();
                if (!currentList) return empty;

                // The roster object is either currentList.roster or currentList itself
                const roster = currentList.roster || currentList;

                try {
                    return {
                        name: roster.name || currentList.name || '',
                        gameSystemId: roster.gameSystemId || currentList.gameSystemId || '',
                        forces: extractForces(roster),
                        costs: extractCosts(roster),
                        validationErrors: extractErrors(roster)
                    };
                } catch(e) {
                    return { ...empty, validationErrors: ['State read error: ' + e.message] };
                }

                function extractForces(roster) {
                    const forces = roster.forces || [];
                    return forces.map(f => ({
                        name: f.name || f.forceName || '',
                        catalogueId: f.catalogueId || f.catalogue?.id || null,
                        selections: extractSelections(f)
                    }));
                }

                function extractSelections(parent) {
                    // Selections may be in different properties depending on NR's model
                    const selections = parent.selections || parent.units || parent.children || [];
                    return selections.map(s => extractSelection(s));
                }

                function extractSelection(sel) {
                    return {
                        name: sel.name || '',
                        entryId: sel.entryId || sel.id || null,
                        type: sel.type || null,
                        number: sel.number || sel.count || 1,
                        hidden: sel.hidden || false,
                        costs: extractCosts(sel),
                        children: extractSelections(sel),
                        profiles: (sel.profiles || []).map(p => ({
                            name: p.name || '',
                            typeId: p.typeId || null,
                            typeName: p.typeName || null,
                            hidden: p.hidden || false,
                            characteristics: (p.characteristics || []).map(ch => ({
                                name: ch.name || '',
                                typeId: ch.typeId || '',
                                value: ch.value?.toString() || ''
                            }))
                        })),
                        rules: (sel.rules || []).map(r => ({
                            name: r.name || '',
                            description: r.description || '',
                            hidden: r.hidden || false
                        })),
                        categories: (sel.categories || []).map(cat => ({
                            name: cat.name || '',
                            entryId: cat.entryId || cat.id || null,
                            primary: cat.primary || false
                        })),
                        page: sel.page || null
                    };
                }

                function extractCosts(obj) {
                    const costs = obj.costs || [];
                    if (Array.isArray(costs)) {
                        return costs.map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || c.id || '',
                            value: c.value || 0
                        }));
                    }
                    // Costs might be an object { pts: 100, pl: 5 }
                    if (typeof costs === 'object') {
                        return Object.entries(costs).map(([key, val]) => ({
                            name: key,
                            typeId: key,
                            value: typeof val === 'number' ? val : 0
                        }));
                    }
                    return [];
                }

                function extractErrors(roster) {
                    // Try multiple paths for validation errors
                    const errors = roster.validationErrors
                        || roster.errors
                        || roster.validationMessages
                        || [];
                    if (Array.isArray(errors)) {
                        return errors.map(e => typeof e === 'string' ? e : (e.message || e.text || JSON.stringify(e)));
                    }
                    return [];
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
