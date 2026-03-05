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
                const emptyErr = msg => ({ message: msg, ownerType: null, ownerEntryId: null, entryId: null, constraintId: null });
                const empty = { name: '', gameSystemId: '', forces: [], costs: [], validationErrors: [] };

                // Read from global reference saved during Setup
                const spec = window.__bsspec;
                if (!spec) return JSON.stringify({...empty, validationErrors: [emptyErr('window.__bsspec not set — was Setup called?')]});

                const army = spec.army;
                if (army === null || army === undefined) return JSON.stringify({...empty, validationErrors: [emptyErr('army is null')]});

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
                    return JSON.stringify({ ...empty, validationErrors: [emptyErr('State read error: ' + e.message)] });
                }

                function extractForces(army) {
                    // getForces() works for locally-loaded systems; getChildren() may return []
                    let forces = army.getForces?.();
                    if (!forces?.length) forces = army.getChildren?.();
                    if (!forces?.length) forces = [];
                    if (!Array.isArray(forces)) return [];
                    return forces.map(f => ({
                        name: f.getName?.() || '',
                        catalogueId: f.catalogueId || f.getId?.() || null,
                        selections: extractSelections(f, false)
                    }));
                }

                function extractSelections(parent, sortByEntryOrder) {
                    // getSelections() returns selected entries; getChildren() is fallback
                    let selections = parent.getSelections?.();
                    if (!selections?.length) selections = parent.getChildren?.();
                    if (!selections?.length) selections = [];

                    const entryOrder = window.__bsspec?.entryOrder || [];
                    const orderMap = {};
                    entryOrder.forEach((id, i) => { orderMap[id] = i; });

                    if (!sortByEntryOrder) {
                        // Top-level: sort by insertion sequence (tagged during
                        // SelectEntry/DuplicateSelection). Untagged entries
                        // (auto-selected by NR) sort first in catalogue order.
                        const sorted = [...selections].sort((a, b) => {
                            const ra = a?.__v_raw || a;
                            const rb = b?.__v_raw || b;
                            const seqA = ra?.__bsspec_seq ?? -1;
                            const seqB = rb?.__bsspec_seq ?? -1;
                            if (seqA !== seqB) return seqA - seqB;
                            // Tiebreak: catalogue order for auto-selected entries
                            const idA = ra?.selector?.source?.id || ra?.source?.id;
                            const idB = rb?.selector?.source?.id || rb?.source?.id;
                            const oA = orderMap[idA] ?? 999;
                            const oB = orderMap[idB] ?? 999;
                            return oA - oB;
                        });
                        return sorted.map(s => extractSelection(s));
                    }

                    // Child selections: sort by catalogue-defined entry order.
                    // NR sorts children alphabetically, but BS uses catalogue
                    // definition order. Children are part of the entry definition,
                    // not user-ordered, so catalogue order is correct.
                    const sorted = [...selections].sort((a, b) => {
                        const ra = a?.__v_raw || a;
                        const rb = b?.__v_raw || b;
                        const idA = ra?.selector?.source?.id || ra?.source?.id;
                        const idB = rb?.selector?.source?.id || rb?.source?.id;
                        const orderA = orderMap[idA] ?? 999;
                        const orderB = orderMap[idB] ?? 999;
                        if (orderA !== orderB) return orderA - orderB;
                        const nameA = ra?.getName?.() || ra?.source?.name || '';
                        const nameB = rb?.getName?.() || rb?.source?.name || '';
                        return nameA.localeCompare(nameB);
                    });

                    return sorted.map(s => extractSelection(s));
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
                        children: extractSelections(sel, true),
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
                    // With proper child selection (incrementAmount), NR's
                    // calcTotalCosts() returns correct aggregated totals.
                    const apiCosts = army.calcTotalCosts?.() || [];
                    if (Array.isArray(apiCosts) && apiCosts.length > 0) {
                        return apiCosts.map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: c.value || 0
                        }));
                    }
                    // Fallback: recursive walk for cases where calcTotalCosts
                    // doesn't work (sums costs of nodes with amount > 0).
                    const forces = army.getForces?.() || [];
                    const totals = {};
                    function sumNodeCosts(node) {
                        const children = node.getSelections?.() || node.getChildren?.() || [];
                        for (const sel of children) {
                            const amount = sel.getAmount?.() ?? 0;
                            if (amount <= 0) continue;
                            const costs = sel.getCosts?.() || [];
                            for (const c of costs) {
                                const tid = c.typeId || '';
                                if (!totals[tid]) totals[tid] = { name: c.name || '', typeId: tid, value: 0 };
                                totals[tid].value += (c.value || 0) * amount;
                            }
                            sumNodeCosts(sel);
                        }
                    }
                    for (const force of forces) {
                        sumNodeCosts(force);
                    }
                    const result = Object.values(totals);
                    if (result.length > 0) return result;

                    // Last resort: return cost types from game system with value 0
                    const spec = window.__bsspec;
                    const gs = spec?.book?.catalogue?.gameSystem;
                    if (gs?.costTypes) {
                        return gs.costTypes.map(ct => ({
                            name: ct.name || '',
                            typeId: ct.id || '',
                            value: 0
                        }));
                    }
                    return [];
                }

                function extractErrors(army) {
                    // NR doesn't validate lazily — must explicitly call
                    // checkConstraints() to populate error arrays on each node.
                    try { army.checkConstraints(); } catch(e) {}
                    const forces = army.getForces?.() || [];
                    for (const f of forces) {
                        try { f.checkConstraints?.(); } catch(e) {}
                        for (const cat of (f.getCategories?.() || []))
                            try { cat.checkConstraints?.(); } catch(e) {}
                        for (const sel of (f.getSelections?.() || [])) {
                            try { sel.checkConstraints?.(); } catch(e) {}
                            // Also check children recursively
                            (function checkChildSels(parent) {
                                for (const child of (parent.getSelections?.() || [])) {
                                    try { child.checkConstraints?.(); } catch(e) {}
                                    checkChildSels(child);
                                }
                            })(sel);
                        }
                    }

                    // Walk the tree, collecting errors per-node with
                    // position-based ownerType (roster/force/category/selection).
                    const seen = new Set();
                    const result = [];

                    function addError(e, ownerType, ownerNode) {
                        const hash = e.hash || '';
                        if (seen.has(hash) && hash) return;
                        if (hash) seen.add(hash);

                        const msg = typeof e === 'string' ? e
                            : (e.msg || e.message || e.text || '');
                        const cleanMsg = msg.replace(/<[^>]*>/g, '');

                        // ownerEntryId: try the node's source.id (contains the
                        // catalogue XML ID of the category link / entry),
                        // then look up the targetId if it's a category link.
                        let ownerEntryId = null;
                        const raw = ownerNode?.__v_raw || ownerNode;
                        if (raw) {
                            // For categories: source references the categoryLink,
                            // we need the categoryEntry targetId
                            const srcId = raw.source?.id;
                            const targetId = raw.source?.targetId;
                            ownerEntryId = targetId || srcId || raw.getId?.() || null;
                        }

                        // constraintId from the constraint object (preserves XML ID)
                        let constraintId = null;
                        if (e.constraint?.id) constraintId = e.constraint.id;

                        // entryId: find which child entry defined this constraint
                        // by searching selectors under the owner node — each selector
                        // has source.constraints[] that preserves XML constraint IDs.
                        let entryId = null;
                        if (constraintId && ownerNode) {
                            const rawOwner = ownerNode.__v_raw || ownerNode;
                            const selectors = rawOwner.selectors || [];
                            for (const sel of selectors) {
                                const rawSel = sel?.__v_raw || sel;
                                const srcCons = rawSel?.source?.constraints || [];
                                for (const c of srcCons) {
                                    if (c.id === constraintId) {
                                        entryId = rawSel.source?.id || null;
                                        break;
                                    }
                                }
                                if (entryId) break;
                            }
                            // Also check entries if selectors didn't find it
                            if (!entryId) {
                                const entries = rawOwner.getEntries?.() || [];
                                for (const entry of entries) {
                                    const rawEntry = entry?.__v_raw || entry;
                                    const selSrc = rawEntry?.selector?.source;
                                    if (selSrc?.constraints) {
                                        for (const c of selSrc.constraints) {
                                            if (c.id === constraintId) {
                                                entryId = selSrc.id || rawEntry.source?.id || null;
                                                break;
                                            }
                                        }
                                    }
                                    if (entryId) break;
                                }
                            }
                        }

                        result.push({
                            message: cleanMsg,
                            ownerType, ownerEntryId,
                            entryId, constraintId
                        });
                    }

                    // Roster-level errors
                    for (const e of (army.errors || []))
                        addError(e, 'roster', army);

                    for (const f of forces) {
                        // Force-level errors
                        for (const e of (f.errors || []))
                            addError(e, 'force', f);
                        // Category-level errors
                        for (const cat of (f.getCategories?.() || []))
                            for (const e of (cat.errors || []))
                                addError(e, 'category', cat);
                        // Selection-level errors (recursive)
                        function walkSel(sel) {
                            for (const e of (sel.errors || []))
                                addError(e, 'selection', sel);
                            for (const child of (sel.getSelections?.() || []))
                                walkSel(child);
                        }
                        for (const sel of (f.getSelections?.() || []))
                            walkSel(sel);
                    }

                    // If tree walk found nothing, try getErrors() as fallback
                    // (walks the tree internally, returns all errors)
                    if (result.length === 0) {
                        try {
                            const errs = army.getErrors?.() || [];
                            for (const e of errs) {
                                const hash = e.hash || '';
                                if (seen.has(hash) && hash) continue;
                                if (hash) seen.add(hash);
                                const msg = (e.msg || e.message || '').replace(/<[^>]*>/g, '');
                                const constraintId = e.constraint?.id || null;
                                result.push({
                                    message: msg,
                                    ownerType: e.scope || null,
                                    ownerEntryId: null,
                                    entryId: null,
                                    constraintId
                                });
                            }
                        } catch(ex) {}
                    }

                    return result;
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
            f.Selections.Select(MapSelection).ToList()
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
