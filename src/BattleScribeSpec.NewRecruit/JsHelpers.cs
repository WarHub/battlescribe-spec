namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Consolidated JS helper functions and the state reader injection script.
/// Registered as a page init script via <see cref="NewRecruitBrowser.RegisterHelpersOnPageAsync"/>
/// so helpers are automatically available after every page navigation.
/// </summary>
internal static class JsHelpers
{
    /// <summary>
    /// Single JS blob that defines all helper functions and the state reader
    /// as window globals. Registered as an init script, it runs automatically
    /// on every full page navigation. For client-side navigation (Vue Router),
    /// window globals persist naturally.
    /// </summary>
    public const string InjectionScript = """
        (() => {
            // --- Action helpers (used by NewRecruitActions.cs) ---

            window.getForces = function(army) {
                const f = army.getForces?.();
                if (f?.length) return f;
                const c = army.getChildren?.();
                if (c?.length) return c;
                return [];
            };

            window.getSelections = function(force) {
                const s = force.getSelections?.();
                if (s?.length) return s;
                const c = force.getChildren?.();
                if (c?.length) return c;
                return [];
            };

            // Navigate to a force at a given path (array of indices).
            // path=[0] → first root force; path=[0,1] → second child of first root force.
            window.getForceAtPath = function(army, path) {
                let forces = getForces(army);
                for (let i = 0; i < path.length; i++) {
                    if (path[i] >= forces.length) return null;
                    if (i < path.length - 1) {
                        forces = forces[path[i]].getForces?.() || [];
                    } else {
                        return forces[path[i]];
                    }
                }
                return null;
            };

            // Navigate to a selection at a given path within a force.
            // path=[0] → first selection; path=[0,2] → third child of first selection.
            window.getSelectionAtPath = function(force, path) {
                let parent = force;
                for (let i = 0; i < path.length; i++) {
                    const sels = getSortedSelections(parent);
                    if (path[i] >= sels.length) return null;
                    parent = sels[path[i]];
                }
                return parent === force ? null : parent;
            };

            window.getSortedSelections = function(force) {
                const sels = getSelections(force);
                const entryOrder = window.__bsspec?.entryOrder || [];
                const orderMap = {};
                entryOrder.forEach((id, i) => { orderMap[id] = i; });
                return [...sels].sort((a, b) => {
                    const ra = a?.__v_raw || a;
                    const rb = b?.__v_raw || b;
                    const seqA = ra?.__bsspec_seq ?? -1;
                    const seqB = rb?.__bsspec_seq ?? -1;
                    if (seqA !== seqB) return seqA - seqB;
                    const idA = ra?.selector?.source?.id || ra?.source?.id;
                    const idB = rb?.selector?.source?.id || rb?.source?.id;
                    return (orderMap[idA] ?? 999) - (orderMap[idB] ?? 999);
                });
            };

            window.findSelectorById = function(node, targetId) {
                if (!node) return null;
                if (node.ids?.includes(targetId)) return node;
                const sels = node.selectors || [];
                for (const s of sels) {
                    if (s.ids?.includes(targetId)) return s;
                    if (typeof s.first === 'function') {
                        const inst = s.first();
                        if (inst?.selectors) {
                            const found = findSelectorById(inst, targetId);
                            if (found) return found;
                        }
                    }
                    if (s.selectors) {
                        const found = findSelectorById(s, targetId);
                        if (found) return found;
                    }
                }
                return null;
            };

            window.findSelectorByName = function(node, targetName) {
                if (!node) return null;
                if (node.getName?.() === targetName || node.name === targetName) return node;
                const sels = node.selectors || [];
                for (const s of sels) {
                    if (s.getName?.() === targetName || s.name === targetName) return s;
                    if (typeof s.first === 'function') {
                        const inst = s.first();
                        if (inst?.selectors) {
                            const found = findSelectorByName(inst, targetName);
                            if (found) return found;
                        }
                    }
                    if (s.selectors) {
                        const found = findSelectorByName(s, targetName);
                        if (found) return found;
                    }
                }
                return null;
            };

            // --- State reader (used by NewRecruitStateReader.cs) ---

            window.__bsspec_readState = function() {
                const emptyErr = msg => ({ message: msg, ownerType: null, ownerEntryId: null, entryId: null, constraintId: null });
                const empty = { name: '', gameSystemId: '', forces: [], costs: [], validationErrors: [] };

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
                    let forces = army.getForces?.();
                    if (!forces?.length) forces = army.getChildren?.();
                    if (!forces?.length) forces = [];
                    if (!Array.isArray(forces)) return [];
                    return forces.map(f => extractForce(f));
                }

                function extractForce(f) {
                    const childForces = f.getForces?.() || [];
                    return {
                        name: f.getName?.() || '',
                        catalogueId: f.catalogueId || f.getId?.() || null,
                        selections: extractSelections(f, false),
                        childForces: Array.isArray(childForces) ? childForces.map(cf => extractForce(cf)) : [],
                        profiles: extractProfiles(f),
                        rules: extractRules(f),
                        publicationId: f.publicationId || null,
                        page: f.page != null ? String(f.page) : null
                    };
                }

                function extractSelections(parent, sortByEntryOrder) {
                    let selections = parent.getSelections?.();
                    if (!selections?.length) selections = parent.getChildren?.();
                    if (!selections?.length) selections = [];

                    const entryOrder = window.__bsspec?.entryOrder || [];
                    const orderMap = {};
                    entryOrder.forEach((id, i) => { orderMap[id] = i; });

                    if (!sortByEntryOrder) {
                        const sorted = [...selections].sort((a, b) => {
                            const ra = a?.__v_raw || a;
                            const rb = b?.__v_raw || b;
                            const seqA = ra?.__bsspec_seq ?? -1;
                            const seqB = rb?.__bsspec_seq ?? -1;
                            if (seqA !== seqB) return seqA - seqB;
                            const idA = ra?.selector?.source?.id || ra?.source?.id;
                            const idB = rb?.selector?.source?.id || rb?.source?.id;
                            const oA = orderMap[idA] ?? 999;
                            const oB = orderMap[idB] ?? 999;
                            return oA - oB;
                        });
                        return sorted.map(s => extractSelection(s));
                    }

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
                            page: p.page != null ? String(p.page) : null,
                            publicationId: p.publicationId || null,
                            characteristics: (p.characteristics || []).map(ch => ({
                                name: ch.name || '',
                                typeId: ch.typeId || '',
                                value: (ch.value ?? '').toString()
                            }))
                        })),
                        rules: rules.map(r => ({
                            name: r.name || r.getName?.() || '',
                            description: r.description || '',
                            hidden: r.hidden || false,
                            page: r.page != null ? String(r.page) : null,
                            publicationId: r.publicationId || null
                        })),
                        categories: cats.map(cat => ({
                            name: cat.name || cat.getName?.() || '',
                            entryId: cat.entryId || cat.getId?.() || null,
                            primary: cat.primary || false,
                            profiles: extractProfiles(cat),
                            rules: extractRules(cat),
                            publicationId: cat.publicationId || null,
                            page: cat.page != null ? String(cat.page) : null
                        })),
                        page: null
                    };
                }

                function extractProfiles(node) {
                    const profiles = node.getModifiedProfiles?.() || node.getProfiles?.() || [];
                    return profiles.map(p => ({
                        name: p.name || p.getName?.() || '',
                        typeId: p.typeId || null,
                        typeName: p.typeName || null,
                        hidden: p.hidden || false,
                        page: p.page != null ? String(p.page) : null,
                        publicationId: p.publicationId || null,
                        characteristics: (p.characteristics || []).map(ch => ({
                            name: ch.name || '',
                            typeId: ch.typeId || '',
                            value: (ch.value ?? '').toString()
                        }))
                    }));
                }

                function extractRules(node) {
                    const rules = node.getModifiedRules?.() || node.getRules?.() || [];
                    return rules.map(r => ({
                        name: r.name || r.getName?.() || '',
                        description: r.description || '',
                        hidden: r.hidden || false,
                        page: r.page != null ? String(r.page) : null,
                        publicationId: r.publicationId || null
                    }));
                }

                function extractTotalCosts(army) {
                    const apiCosts = army.calcTotalCosts?.() || [];
                    if (Array.isArray(apiCosts) && apiCosts.length > 0) {
                        return apiCosts.map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: c.value || 0
                        }));
                    }
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
                    try { army.checkConstraints(); } catch(e) {}
                    const forces = army.getForces?.() || [];
                    for (const f of forces) {
                        try { f.checkConstraints?.(); } catch(e) {}
                        for (const cat of (f.getCategories?.() || []))
                            try { cat.checkConstraints?.(); } catch(e) {}
                        for (const sel of (f.getSelections?.() || [])) {
                            try { sel.checkConstraints?.(); } catch(e) {}
                            (function checkChildSels(parent) {
                                for (const child of (parent.getSelections?.() || [])) {
                                    try { child.checkConstraints?.(); } catch(e) {}
                                    checkChildSels(child);
                                }
                            })(sel);
                        }
                    }

                    const costTypeLookup = [];
                    const costTypes = spec?.book?.catalogue?.gameSystem?.costTypes || [];
                    const costLimitConfig = spec?.costLimitConfig || {};
                    for (const ct of costTypes) {
                        costTypeLookup.push({ name: ct.name, typeId: ct.id });
                    }

                    const seen = new Set();
                    const result = [];

                    function addError(e, ownerType, ownerNode) {
                        const hash = e.hash || '';
                        if (seen.has(hash) && hash) return;
                        if (hash) seen.add(hash);

                        const msg = typeof e === 'string' ? e
                            : (e.msg || e.message || e.text || '');
                        const cleanMsg = msg.replace(/<[^>]*>/g, '');

                        let ownerEntryId = null;
                        const raw = ownerNode?.__v_raw || ownerNode;
                        if (raw) {
                            const srcId = raw.source?.id;
                            const targetId = raw.source?.targetId;
                            ownerEntryId = targetId || srcId || raw.getId?.() || null;
                        }

                        let constraintId = null;
                        if (e.constraint?.id) constraintId = e.constraint.id;

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
                            if (!entryId) {
                                const ownerSrc = rawOwner.selector?.source || rawOwner.source;
                                if (ownerSrc?.constraints) {
                                    for (const c of ownerSrc.constraints) {
                                        if (c.id === constraintId) {
                                            entryId = ownerSrc.id || null;
                                            break;
                                        }
                                    }
                                }
                            }
                            if (!entryId) {
                                const ownerSrc = rawOwner.selector?.source || rawOwner.source;
                                entryId = ownerSrc?.targetId || null;
                            }
                        }

                        if (ownerType === 'roster') {
                            return;
                        }

                        if (ownerType === 'selection' && !constraintId) {
                            const raw = ownerNode?.__v_raw || ownerNode;
                            if (raw?.isHidden?.()) {
                                entryId = ownerEntryId || null;
                                constraintId = 'hidden';
                            }
                        }

                        result.push({
                            message: cleanMsg,
                            ownerType, ownerEntryId,
                            entryId, constraintId
                        });
                    }

                    for (const e of (army.errors || []))
                        addError(e, 'roster', army);

                    for (const f of forces) {
                        for (const e of (f.errors || []))
                            addError(e, 'force', f);
                        for (const cat of (f.getCategories?.() || []))
                            for (const e of (cat.errors || []))
                                addError(e, 'category', cat);
                        function walkSel(sel) {
                            for (const e of (sel.errors || []))
                                addError(e, 'selection', sel);
                            for (const child of (sel.getSelections?.() || []))
                                walkSel(child);
                        }
                        for (const sel of (f.getSelections?.() || []))
                            walkSel(sel);
                    }

                    const totalCosts = extractTotalCosts(army);
                    for (const ct of costTypeLookup) {
                        const configuredLimit = costLimitConfig[ct.name];
                        if (configuredLimit === undefined || configuredLimit === null || configuredLimit < 0) continue;
                        const actual = totalCosts.find(c => c.typeId === ct.typeId);
                        const totalValue = actual?.value || 0;
                        if (totalValue > configuredLimit) {
                            result.push({
                                message: '',
                                ownerType: 'roster',
                                ownerEntryId: null,
                                entryId: 'costLimits',
                                constraintId: ct.typeId
                            });
                        }
                    }

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
            };
        })()
        """;
}
