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
            // NR flattens ALL descendant forces under getForces() at every level,
            // so we must filter to only direct children at each step.
            window.getForceAtPath = function(army, path) {
                let forces = getForces(army).filter(f => !f.getParent?.()?.isForce?.());
                for (let i = 0; i < path.length; i++) {
                    if (path[i] >= forces.length) return null;
                    const current = forces[path[i]];
                    if (i < path.length - 1) {
                        // Filter to direct children of current force
                        const all = current.getForces?.() || [];
                        forces = all.filter(cf => cf.getParentForce?.() === current
                            || cf.getParentForce?.() === current);
                    } else {
                        return current;
                    }
                }
                return null;
            };

            // Find a force anywhere in the army tree by its uid.
            window.getForceByUid = function(army, uid) {
                for (const f of (army.getForces?.() || [])) {
                    const raw = f;
                    if (raw?.uid === uid) return f;
                }
                return null;
            };

            // Find a selection anywhere under a parent by its uid (recursive).
            window.getSelectionByUid = function(parent, uid) {
                function search(nodes) {
                    for (const s of nodes) {
                        const raw = s;
                        if (raw?.uid === uid) return s;
                        const children = s.getSelections?.() || s.getChildren?.() || [];
                        const found = search(children);
                        if (found) return found;
                    }
                    return null;
                }
                return search(getSelections(parent));
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
                    // NR flattens all forces (including children) under army.getForces().
                    // Filter to only root forces whose parent is not a force.
                    return forces.filter(f => !f.getParent?.()?.isForce?.()).map(f => extractForce(f));
                }

                function extractForce(f) {
                    const allChildren = f.getForces?.() || [];
                    // NR flattens all descendants — filter to direct children only.
                    const directChildren = Array.isArray(allChildren)
                        ? allChildren.filter(cf => cf.getParentForce?.() === f
                            || cf.getParentForce?.() === f)
                        : [];
                    return {
                        id: f?.uid || null,
                        name: f.getName?.() || '',
                        catalogueId: f.catalogueId || f.getId?.() || null,
                        hidden: f.isHidden?.() === true,
                        selections: extractSelections(f),
                        childForces: directChildren.map(cf => extractForce(cf)),
                        profiles: extractProfiles(f),
                        rules: extractRules(f),
                        publicationId: f.publication?.id || f.source?.publication?.id || null,
                        page: (f.page ?? f.source?.page) != null ? String(f.page ?? f.source?.page) : null
                    };
                }

                function extractSelections(parent) {
                    let selections = parent.getSelections?.();
                    if (!selections?.length) selections = parent.getChildren?.();
                    if (!selections?.length) selections = [];
                    // Use NR's native order (alphabetical via initializeChilds sort).
                    return [...selections].map(s => extractSelection(s));
                }

                function extractSelection(sel) {
                    const costs = sel.getCosts?.() || [];
                    const profiles = sel.getModifiedProfiles?.() || [];
                    const rules = sel.getModifiedRules?.() || [];
                    const cats = sel.getSelectionCategories?.() || [];
                    const src = sel.source;
                    const selPage = src?.page != null ? String(src.page) : null;
                    const selPubId = src?.publication?.id || null;
                    const selPubName = src?.publication?.name || null;

                    return {
                        id: sel?.uid || null,
                        name: sel.getName?.() || '',
                        entryId: sel.getId?.() || null,
                        type: sel.getType?.() || null,
                        number: sel.getAmount?.() || 1,
                        hidden: sel.isHidden?.() || false,
                        costs: costs.map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: (c.value || 0) * (sel.getAmount?.() || 1)
                        })),
                        children: extractSelections(sel),
                        profiles: profiles.map(p => ({
                            name: p.name || p.getName?.() || '',
                            typeId: p.typeId || null,
                            typeName: p.typeName || null,
                            hidden: p.hidden || false,
                            page: p.page != null ? String(p.page) : null,
                            publicationId: p.publication?.id || null,                            characteristics: (p.characteristics || []).map(ch => ({
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
                            publicationId: r.publication?.id || null
                        })),
                        categories: cats.map(cat => ({
                            name: cat.name || cat.getName?.() || '',
                            entryId: cat.entryId || cat.getId?.() || null,
                            primary: cat.primary || false,
                            profiles: extractProfiles(cat),
                            rules: extractRules(cat),
                            publicationId: cat.publication?.id || null,
                            page: cat.page != null ? String(cat.page) : null
                        })),
                        page: selPage != null ? String(selPage) : null,
                        publicationId: selPubId,
                        publicationName: selPubName
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
                        publicationId: p.publication?.id || null,
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
                        publicationId: r.publication?.id || null
                    }));
                }

                function extractTotalCosts(army) {
                    // Manual summation for ALL cost types (visible and hidden).
                    // NR's calcTotalCosts() omits hidden types, so we use uniform
                    // summation from individual selections for correctness.
                    const result = {};
                    for (const force of (army.getForces?.() || [])) {
                        sumNode(force);
                    }
                    function sumNode(node) {
                        for (const sel of (node.getSelections?.() || node.getChildren?.() || [])) {
                            const amount = sel.getAmount?.() ?? 0;
                            if (amount <= 0) continue;
                            for (const c of (sel.getCosts?.() || [])) {
                                const tid = c.typeId || '';
                                if (!tid) continue;
                                if (!result[tid]) result[tid] = { name: c.name || '', typeId: tid, value: 0 };
                                result[tid].value += (c.value || 0) * amount;
                            }
                            sumNode(sel);
                        }
                    }

                    const vals = Object.values(result);
                    if (vals.length > 0) return vals;

                    // Fallback for empty roster: zero-valued from game system
                    const gs = window.__bsspec?.book?.catalogue?.gameSystem;
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
                        const raw = ownerNode;
                        if (raw) {
                            const srcId = raw.source?.id;
                            const targetId = raw.source?.targetId;
                            ownerEntryId = targetId || srcId || raw.getId?.() || null;
                        }

                        let constraintId = null;
                        if (e.constraint?.id) constraintId = e.constraint.id;

                        let entryId = null;
                        if (constraintId && ownerNode) {
                            const rawOwner = ownerNode;
                            const selectors = rawOwner.selectors || [];
                            for (const sel of selectors) {
                                const rawSel = sel;
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
                                    const rawEntry = entry;
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

                        // Roster-level errors: only emit cost limit violations
                        if (ownerType === 'roster') {
                            if (e.constraint?.type === 'max' && e.constraint?.field) {
                                result.push({
                                    message: cleanMsg,
                                    ownerType: 'roster',
                                    ownerEntryId: null,
                                    entryId: 'costLimits',
                                    constraintId: e.constraint.field
                                });
                            }
                            return;
                        }

                        if (ownerType === 'selection' && !constraintId) {
                            const raw = ownerNode;
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
