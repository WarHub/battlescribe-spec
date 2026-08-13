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
                    if (f.uid === uid) return f;
                }
                return null;
            };

            // Find a selection anywhere under a parent by its uid (recursive).
            window.getSelectionByUid = function(parent, uid) {
                function search(nodes) {
                    for (const s of nodes) {
                        if (s.uid === uid) return s;
                        const children = s.getSelections?.() || [];
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
                // For composite IDs (containing ::), match via getBattleScribePath
                if (targetId.includes('::') && typeof node.getBattleScribePath === 'function'
                    && node.getBattleScribePath() === targetId) return node;
                const sels = node.selectors || [];
                for (const s of sels) {
                    if (s.ids?.includes(targetId)) return s;
                    if (targetId.includes('::') && typeof s.getBattleScribePath === 'function'
                        && s.getBattleScribePath() === targetId) return s;
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

                // Prefer NR's LIVE list over the reference captured at creation, when they are the
                // same list.
                //
                // `spec.army` is a snapshot taken once, and NR re-hydrates `currentList.army` —
                // replacing the object, not mutating it — after a roster is created. Every read then
                // reported the stale object, which has no selections, so specs failed with
                // "force[0].selection[0] expected but only 0 selections" while the roster on screen
                // was perfectly correct. In the NR-UI lane that re-hydration was being outrun by a
                // 1500ms sleep during roster creation; the staleness itself was always there.
                //
                // Gated on the list key matching so this cannot silently retarget: the store-direct
                // engine can hold lists that are not NR's current one, and "whatever is current" is
                // not the same claim as "the list under test".
                let live = null;
                try {
                    const pinia = document.querySelector('#__nuxt')
                        ?.__vue_app__?.config?.globalProperties?.$pinia;
                    const cl = pinia?._s?.get('lists')?.currentList;
                    const sameList = cl && cl.army && spec.row && cl.row
                        && cl.row.list_key === spec.row.list_key;
                    if (sameList) { live = cl; }
                } catch(e) { /* fall back to the captured reference */ }

                const army = live?.army ?? spec.army;
                if (army === null || army === undefined) return JSON.stringify({...empty, validationErrors: [emptyErr('army is null')]});

                const gs = (live?.book ?? spec.book)?.catalogue?.gameSystem;
                const costTypeHiddenMap = {};
                if (gs?.costTypes) {
                    for (const ct of gs.costTypes) {
                        if (ct.id) costTypeHiddenMap[ct.id] = ct.hidden || false;
                    }
                }

                try {
                    const result = {
                        name: army.getCustomName?.() || army.getName?.() || spec.row?.name || '',
                        gameSystemId: spec.row?.bsid_system || '',
                        gameSystemName: spec.row?.system_name || gs?.name || null,
                        forces: extractForces(army),
                        costs: extractTotalCosts(army),
                        costLimits: extractCostLimits(army),
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
                        ? allChildren.filter(cf => cf.getParentForce?.() === f)
                        : [];
                    return {
                        id: f.uid,
                        name: f.getName(),
                        catalogueId: f.catalogueId || f.getId?.() || null,
                        hidden: f.isHidden?.() === true,
                        selections: extractSelections(f),
                        childForces: directChildren.map(cf => extractForce(cf)),
                        profiles: extractProfiles(f),
                        rules: extractRules(f),
                        publicationId: f.publication?.id || f.source?.publication?.id || null,
                        page: (f.page ?? f.source?.page) != null ? String(f.page ?? f.source?.page) : null,
                        entryId: f.source?.id || null,
                        categories: extractForceCategories(f),
                        publications: extractForcePublications(f),
                        catalogueName: f.catalogueName || f.source?.catalogueName || null,
                        customName: f.customName || null,
                        customNotes: f.note || null
                    };
                }

                function extractSelections(parent) {
                    const selections = parent.getSelections?.() || [];
                    // NR pre-creates template instances (amount=0) for all possible child entries.
                    // Only include selections that are actually activated (amount > 0).
                    // Instance nodes always have getAmount — error if missing (wrong node type).
                    return [...selections]
                        .filter(s => {
                            if (typeof s.getAmount !== 'function')
                                throw new Error(`Node ${s.getId?.() || s.uid || '?'} has no getAmount — not an instance node`);
                            return s.getAmount() > 0;
                        })
                        .map(s => extractSelection(s));
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

                    // sel is an instance node (filtered by getAmount > 0 in extractSelections).
                    // Instance nodes always have getName, getId, getAmount, getType, isHidden.
                    // Use getSelectionCount("root") for number — it multiplies through
                    // parent chain, matching BattleScribe's exported number attribute.
                    // This correctly handles collective entries (amount stays at 1 internally
                    // but getSelectionCount("root") returns parent-multiplied total).
                    const count = sel.getSelectionCount?.("root") || sel.getAmount();
                    return {
                        id: sel.uid,
                        name: sel.getName(),
                        entryId: sel.getBattleScribePath(),
                        type: sel.getType?.() || null,
                        number: count,
                        hidden: sel.isHidden?.() || false,
                        costs: costs.map(c => ({
                            name: c.name || '',
                            typeId: c.typeId || '',
                            value: (c.value || 0) * count,
                            hidden: costTypeHiddenMap[c.typeId] || false
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
                            // No node id, deliberately. getSelectionCategories() returns plain
                            // object literals — no prototype, no methods, no uid — because a
                            // selection's categories are the TAGS it counts against, not nodes in
                            // the tree. Their `id` key is a catalogue id and is NOT a node
                            // identity; reading it here would invent one. See CategoryState.
                            id: cat.uid || null,
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
                        publicationName: selPubName,
                        entryGroupId: sel.getBattleScribePath(true) || null,
                        customName: sel.customName || null,
                        customNotes: sel.note || null
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
                        for (const sel of (node.getSelections?.() || [])) {
                            if (typeof sel.getAmount !== 'function') continue;
                            const amount = sel.getAmount();
                            if (amount <= 0) continue;
                            // Use getSelectionCount("root") for cost multiplication —
                            // correctly accounts for parent model count on collective entries.
                            const count = sel.getSelectionCount?.("root") || amount;
                            for (const c of (sel.getCosts?.() || [])) {
                                const tid = c.typeId || '';
                                if (!tid) continue;
                                if (!result[tid]) result[tid] = { name: c.name || '', typeId: tid, value: 0, hidden: costTypeHiddenMap[tid] || false };
                                result[tid].value += (c.value || 0) * count;
                            }
                            sumNode(sel);
                        }
                    }

                    const vals = Object.values(result);
                    if (vals.length > 0) return vals;

                    // Fallback for empty roster: zero-valued from game system
                    const gs2 = window.__bsspec?.book?.catalogue?.gameSystem;
                    if (gs2?.costTypes) {
                        return gs2.costTypes.map(ct => ({
                            name: ct.name || '',
                            typeId: ct.id || '',
                            value: 0,
                            hidden: ct.hidden || false
                        }));
                    }
                    return [];
                }

                function extractCostLimits(army) {
                    const maxCosts = army.getMaxCosts?.();
                    if (!maxCosts || !Array.isArray(maxCosts)) return null;
                    const limits = [];
                    for (const c of maxCosts) {
                        const value = c.value ?? c.defaultCostLimit ?? -1;
                        if (value < 0) continue;
                        limits.push({ name: c.name || '', typeId: c.typeId || '', value: value, hidden: costTypeHiddenMap[c.typeId] || false });
                    }
                    return limits.length > 0 ? limits : null;
                }

                function extractForceCategories(f) {
                    const cats = f.getCategories?.() || [];
                    return cats.map(c => ({
                        // `uid` is the NODE identity, and the only one: `id`/`getId()` on a force
                        // category return the CATALOGUE entry id (cat-troops), `source.id` the
                        // categoryLink's. NR keys its own validation-error hashes on this uid and
                        // writes it as the `id` attribute of exported roster XML.
                        id: c.uid || null,
                        name: c.getName?.() || c.name || '',
                        entryId: c.source?.targetId || c.source?.id || c.getId?.() || null,
                        primary: c.isPrimary?.() === true,
                        publicationId: c.publication?.id || c.source?.publication?.id || null,
                        page: (c.page ?? c.source?.page) != null ? String(c.page ?? c.source?.page) : null
                    }));
                }

                function extractForcePublications(f) {
                    const pubs = f.getPublications?.() || f.source?.publications || [];
                    if (!pubs || pubs.length === 0) return null;
                    return pubs.map(p => ({
                        id: p.id || p.getId?.() || '',
                        name: p.name || p.getName?.() || ''
                    }));
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

                    // Always merge army.getErrors() — some errors (e.g. entry-group
                    // constraints) only appear here, not on individual node .errors arrays.
                    try {
                        const errs = army.getErrors?.() || [];
                        for (const e of errs) {
                            const hash = e.hash || '';
                            if (seen.has(hash) && hash) continue;
                            if (hash) seen.add(hash);
                            const msg = (e.msg || e.message || '').replace(/<[^>]*>/g, '');
                            const constraintId = e.constraint?.id || null;

                            let ownerType = e.scope || null;
                            let ownerEntryId = null;
                            let entryId = null;

                            // Extract entryId from the error's parent node (the entry owning the constraint)
                            // and walk up to find the owning selection
                            if (e.parent) {
                                const parentSrc = e.parent.source;
                                entryId = parentSrc?.targetId || parentSrc?.id || null;

                                // Walk up from the constraint's parent to find the owning selection
                                let walker = e.parent.parent;
                                while (walker) {
                                    const wSrc = walker.source;
                                    const wId = wSrc?.targetId || wSrc?.id;
                                    if (wId) {
                                        ownerType = 'selection';
                                        ownerEntryId = wId;
                                        break;
                                    }
                                    walker = walker.parent;
                                }
                            }

                            result.push({
                                message: msg,
                                ownerType, ownerEntryId,
                                entryId, constraintId
                            });
                        }
                    } catch(ex) {}

                    return result;
                }
            };
        })()
        """;
}
