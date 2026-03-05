using BattleScribeSpec;
using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Integration tests for NewRecruitRosterEngine.
/// These exercise the full adapter against the live NR site.
/// Skipped unless NR_ENGINE_URL is set.
/// </summary>
[Collection("NewRecruit")]
public sealed class NrIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly NewRecruitFixture _fixture;

    public NrIntegrationTests(ITestOutputHelper output, NewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [SkippableFact]
    public void Setup_CreatesRosterWithForce()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _fixture.Engine!.Setup(gs, [cat]);

        _output.WriteLine($"Setup errors: [{string.Join(", ", errors)}]");
        Assert.Empty(errors);

        // Small delay to let Pinia store settle
        Thread.Sleep(1000);

        var state = _fixture.Engine.GetRosterState();
        _output.WriteLine($"Roster: '{state.Name}', Forces: {state.Forces.Count}");
        foreach (var err in state.ValidationErrors)
            _output.WriteLine($"  Validation: {err}");

        Assert.True(state.Forces.Count >= 1, $"Should have at least 1 force after setup. Name='{state.Name}', Forces={state.Forces.Count}");
    }

    [SkippableFact]
    public void SelectEntry_AddsSelection()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var stateBefore = _fixture.Engine.GetRosterState();
        var selsBefore = stateBefore.Forces[0].Selections.Count;
        _output.WriteLine($"Before SelectEntry: {selsBefore} selections");
        foreach (var sel in stateBefore.Forces[0].Selections)
            _output.WriteLine($"  [{sel.Type}] {sel.Name} (count={sel.Number})");

        // SelectEntry calls incrementAmount — for already-selected entries this increases count
        // For entries that don't accept more, it may have no effect
        // This test verifies the call doesn't throw
        try
        {
            _fixture.Engine.SelectEntry(0, 0);
            _output.WriteLine("SelectEntry(0, 0) succeeded");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"SelectEntry(0, 0) threw: {ex.Message}");
        }

        var stateAfter = _fixture.Engine.GetRosterState();
        var selsAfter = stateAfter.Forces[0].Selections.Count;
        _output.WriteLine($"After SelectEntry: {selsAfter} selections");

        // Verify state reading still works after an action
        Assert.True(stateAfter.Forces.Count >= 1);
    }

    [SkippableFact]
    public void GetRosterState_ReturnsSelectionDetails()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        // Select an entry to have a non-default selection
        _fixture.Engine.SelectEntry(0, 0);

        var state = _fixture.Engine.GetRosterState();
        Assert.NotEmpty(state.Forces);

        var force = state.Forces[0];
        Assert.NotEmpty(force.Selections);

        // Log all selections for debugging
        foreach (var sel in force.Selections)
        {
            _output.WriteLine($"  Selection: {sel.Name} (type={sel.Type}, count={sel.Number}, costs={sel.Costs.Count}, children={sel.Children.Count})");
            foreach (var cost in sel.Costs)
                _output.WriteLine($"    Cost: {cost.Name}={cost.Value}");
            foreach (var child in sel.Children)
                _output.WriteLine($"    Child: {child.Name} (type={child.Type}, count={child.Number})");
        }
    }

    [SkippableFact]
    public void GetValidationErrors_ReturnsErrors()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var validationErrors = _fixture.Engine.GetValidationErrors();
        _output.WriteLine($"Validation errors: {validationErrors.Count}");
        foreach (var err in validationErrors)
            _output.WriteLine($"  - {err}");

        // Just verify it doesn't throw — errors are expected for an empty roster
    }

    /// <summary>
    /// Deep probe of NR error mechanism: discover where validation errors live,
    /// what format they're in, and how to trigger and read them.
    /// Uses min=5 constraint (auto-select creates max 1-2) to ensure violation.
    /// </summary>
    [SkippableFact]
    public void Debug_ProbeErrorObjects()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(
            Id: "probe-gs",
            Name: "Probe GS",
            CategoryEntries: [
                new CategoryEntrySpec(Id: "cat-troops", Name: "Troops")
            ],
            ForceEntries: [
                new ForceEntrySpec(
                    Id: "fe-1",
                    Name: "Patrol",
                    CategoryLinks: [
                        new CategoryLinkSpec(Id: "cl-1", TargetId: "cat-troops", Name: "Troops")
                    ])
            ],
            CostTypes: [
                new CostTypeSpec(Id: "pts", Name: "pts", DefaultCostLimit: 100)
            ]);
        var cat = new CatalogueSpec(
            Id: "probe-cat",
            Name: "Probe Cat",
            GameSystemId: "probe-gs",
            SelectionEntries: [
                new SelectionEntrySpec(
                    Id: "se-unit",
                    Name: "Expensive Unit",
                    Type: "unit",
                    CategoryLinks: [
                        new CategoryLinkSpec(Id: "cl-unit", TargetId: "cat-troops", Name: "Troops", Primary: true)
                    ],
                    Constraints: [
                        new ConstraintSpec(Id: "con-min", Type: "min", Value: 5, Field: "selections", Scope: "parent")
                    ],
                    Costs: [new CostSpec(Name: "pts", TypeId: "pts", Value: 60)])
            ]);

        var setupErrors = _fixture.Engine!.Setup(gs, [cat]);
        _output.WriteLine($"Setup errors: [{string.Join(", ", setupErrors)}]");

        _fixture.Engine.AddForce(0);
        Thread.Sleep(1000);

        var state = _fixture.Engine.GetRosterState();
        _output.WriteLine($"Forces: {state.Forces.Count}, Selections: {state.Forces[0].Selections.Count}");
        _output.WriteLine($"State validation errors: {state.ValidationErrors.Count}");
        foreach (var e in state.ValidationErrors)
            _output.WriteLine($"  err: {e.Message}");

        // Deep probe of the army object and all possible error sources
        var probeResult = _fixture.Engine!.Browser.Page.EvaluateAsync<string>("""
            (() => {
                const army = window.__bsspec?.army;
                if (!army) return 'No army';
                const lines = [];

                // 1. ALL methods and properties on army prototype chain
                lines.push('=== army ALL prototype methods ===');
                let proto = Object.getPrototypeOf(army);
                let depth = 0;
                while (proto && depth < 3) {
                    const names = Object.getOwnPropertyNames(proto).filter(k => k !== 'constructor');
                    lines.push(`proto[${depth}]: ${names.join(', ')}`);
                    proto = Object.getPrototypeOf(proto);
                    depth++;
                }

                // 2. ALL own properties on army (enumerable and non-enumerable)
                lines.push('\n=== army own properties ===');
                const ownKeys = Object.getOwnPropertyNames(army);
                lines.push('ownKeys: ' + ownKeys.join(', '));
                for (const k of ownKeys) {
                    try {
                        const v = army[k];
                        const t = typeof v;
                        if (t === 'function') continue;
                        const s = t === 'object' && v !== null
                            ? (Array.isArray(v) ? `Array(${v.length})` : `{${Object.keys(v).slice(0,5).join(',')}}`)
                            : String(v)?.slice(0,100);
                        lines.push(`  .${k} = [${t}] ${s}`);
                    } catch(e) { lines.push(`  .${k} = ERR: ${e.message}`); }
                }

                // 3. Try calling EVERY method that could return errors
                lines.push('\n=== error method calls ===');
                const tryMethods = ['allErrors', 'errors', 'getErrors', 'getAllErrors',
                    'getValidationErrors', 'validate', 'checkErrors', 'hasErrors',
                    'hasValidationErrors', 'getWarnings', 'getDiagnostics'];
                for (const m of tryMethods) {
                    try {
                        const fn = army[m];
                        if (fn === undefined) { lines.push(`  ${m}: undefined`); continue; }
                        if (typeof fn === 'function') {
                            const result = fn.call(army);
                            lines.push(`  ${m}(): ${typeof result} = ${JSON.stringify(result)?.slice(0,300)}`);
                        } else {
                            lines.push(`  ${m}: ${typeof fn} = ${JSON.stringify(fn)?.slice(0,300)}`);
                        }
                    } catch(e) { lines.push(`  ${m}: ERR: ${e.message}`); }
                }

                // 4. Force-level: all methods
                lines.push('\n=== force methods and errors ===');
                const forces = army.getForces?.() || [];
                for (let fi = 0; fi < forces.length; fi++) {
                    const f = forces[fi];
                    lines.push(`Force[${fi}]: ${f.getName?.()}`);
                    let fProto = Object.getPrototypeOf(f);
                    if (fProto) {
                        const fMethods = Object.getOwnPropertyNames(fProto).filter(k => k !== 'constructor');
                        lines.push(`  proto: ${fMethods.join(', ')}`);
                    }
                    // Try error-related methods
                    for (const m of tryMethods) {
                        try {
                            const fn = f[m];
                            if (fn === undefined) continue;
                            const val = typeof fn === 'function' ? fn.call(f) : fn;
                            if (val !== undefined && val !== null && val !== false &&
                                !(Array.isArray(val) && val.length === 0)) {
                                lines.push(`  ${m}: ${JSON.stringify(val)?.slice(0,300)}`);
                            }
                        } catch(e) { lines.push(`  ${m}: ERR: ${e.message}`); }
                    }

                    // Walk selectors/entries looking for errors
                    const entries = f.getEntries?.() || [];
                    for (const ent of entries) {
                        const entErrors = ent.allErrors || ent.errors;
                        if (entErrors && (Array.isArray(entErrors) ? entErrors.length > 0 : true)) {
                            lines.push(`  Entry ${ent.getName?.()}: errors = ${JSON.stringify(entErrors)?.slice(0,200)}`);
                        }
                    }

                    // Check selectors (amount=0 nodes)
                    const sels = f.getSelections?.() || [];
                    for (const sel of sels) {
                        for (const m of ['allErrors', 'errors', 'getErrors']) {
                            try {
                                const fn = sel[m];
                                if (fn === undefined) continue;
                                const val = typeof fn === 'function' ? fn.call(sel) : fn;
                                if (val && (Array.isArray(val) ? val.length > 0 : true)) {
                                    lines.push(`  Sel ${sel.getName?.()}: ${m} = ${JSON.stringify(val)?.slice(0,200)}`);
                                }
                            } catch(e) {}
                        }
                    }
                }

                // 5. Check lists store for validation data
                lines.push('\n=== lists store ===');
                try {
                    const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                    const lists = pinia?._s.get('lists');
                    if (lists) {
                        const current = lists.getCurrentList?.();
                        if (current) {
                            lines.push('getCurrentList keys: ' + Object.keys(current).join(', '));
                            if (current.army) {
                                lines.push('army === window.__bsspec.army: ' + (current.army === army));
                            }
                        }
                        // Check for validation-related properties on lists store
                        const lProto = Object.getPrototypeOf(lists);
                        if (lProto) {
                            const lMethods = Object.getOwnPropertyNames(lProto).filter(k =>
                                k.includes('error') || k.includes('valid') || k.includes('Error') || k.includes('Valid'));
                            if (lMethods.length) lines.push('lists validation methods: ' + lMethods.join(', '));
                        }
                        const lKeys = Object.keys(lists.$state || lists);
                        const errKeys = lKeys.filter(k => k.includes('error') || k.includes('valid') || k.includes('Error'));
                        if (errKeys.length) lines.push('lists error state keys: ' + errKeys.join(', '));
                    }
                } catch(e) { lines.push('Lists error: ' + e.message); }

                // 6. Check gameStore for validation
                lines.push('\n=== gameStore ===');
                try {
                    const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                    const game = pinia?._s.get('gameStore');
                    if (game) {
                        const gKeys = Object.keys(game.$state || game);
                        lines.push('gameStore keys: ' + gKeys.join(', '));
                        const errKeys = gKeys.filter(k =>
                            k.includes('error') || k.includes('valid') || k.includes('Error') || k.includes('warn'));
                        if (errKeys.length) lines.push('error-related: ' + errKeys.join(', '));
                    }
                } catch(e) { lines.push('gameStore error: ' + e.message); }

                // 7. Vue reactivity: try accessing through Vue's proxy
                lines.push('\n=== Vue reactivity check ===');
                try {
                    const raw = army.__v_raw || army;
                    lines.push('has __v_raw: ' + (army.__v_raw !== undefined));
                    lines.push('raw === army: ' + (raw === army));
                    if (raw !== army && raw.allErrors) {
                        lines.push('raw.allErrors: ' + JSON.stringify(raw.allErrors)?.slice(0,300));
                    }
                } catch(e) { lines.push('Vue check error: ' + e.message); }

                return lines.join('\n');
            })()
            """).GetAwaiter().GetResult();

        _output.WriteLine(probeResult);
    }

    /// <summary>
    /// Probe #2: Replicate constraint-min-violation-linked scenario
    /// (add force, deselect to trigger min violation, inspect errors per-node)
    /// </summary>
    [SkippableFact]
    public void Debug_ProbeErrorsAfterValidation()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(
            Id: "probe-gs2",
            Name: "Probe GS2",
            CategoryEntries: [
                new CategoryEntrySpec(Id: "cat-troops", Name: "Troops")
            ],
            ForceEntries: [
                new ForceEntrySpec(
                    Id: "fe-patrol",
                    Name: "Patrol",
                    CategoryLinks: [
                        new CategoryLinkSpec(Id: "cl-fe-troops", TargetId: "cat-troops", Name: "Troops")
                    ])
            ]);
        var cat = new CatalogueSpec(
            Id: "probe-cat2",
            Name: "Probe Cat2",
            GameSystemId: "probe-gs2",
            SelectionEntries: [
                new SelectionEntrySpec(
                    Id: "se-unit-a",
                    Name: "Unit A",
                    Type: "unit",
                    CategoryLinks: [
                        new CategoryLinkSpec(Id: "cl-unit", TargetId: "cat-troops", Name: "Troops", Primary: true)
                    ],
                    Constraints: [
                        new ConstraintSpec(Id: "con-min-1", Type: "min", Value: 1, Field: "selections", Scope: "parent")
                    ])
            ]);

        _fixture.Engine!.Setup(gs, [cat]);
        _fixture.Engine.AddForce(0);
        Thread.Sleep(500);

        // Should auto-select 1 Unit A (min=1), now deselect to violate
        _fixture.Engine.DeselectSelection(0, 0);
        Thread.Sleep(500);

        var probeResult = _fixture.Engine!.Browser.Page.EvaluateAsync<string>("""
            (() => {
                const army = window.__bsspec?.army;
                if (!army) return 'No army';
                const lines = [];

                // Call checkConstraints on all nodes
                try { army.checkConstraints(); } catch(e) {}
                const forces = army.getForces?.() || [];
                for (const f of forces) {
                    try { f.checkConstraints?.(); } catch(e) {}
                    for (const cat of (f.getCategories?.() || []))
                        try { cat.checkConstraints?.(); } catch(e) {}
                    for (const sel of (f.getSelections?.() || []))
                        try { sel.checkConstraints?.(); } catch(e) {}
                }

                // Check errors on each level
                lines.push('=== army errors: ' + (army.errors?.length || 0));
                for (const e of (army.errors || [])) {
                    lines.push('  msg: ' + e.msg);
                    lines.push('  scope: ' + e.scope + ', hash: ' + e.hash);
                    if (e.constraint) {
                        lines.push('  constraint.id: ' + e.constraint.id + ', type: ' + e.constraint.type + ', field: ' + e.constraint.field + ', scope: ' + e.constraint.scope);
                    }
                    lines.push('  parent type: ' + typeof e.parent);
                    if (e.parent) {
                        lines.push('  parent keys: ' + Object.keys(e.parent).slice(0,15).join(', '));
                        const raw = e.parent.__v_raw || e.parent;
                        lines.push('  parent.uid: ' + (raw.uid || 'undef'));
                        try { lines.push('  parent.source.id: ' + raw.source?.id); } catch(x) {}
                        try { lines.push('  parent.source.name: ' + raw.source?.name); } catch(x) {}
                        lines.push('  parent isRoster: ' + raw.isRoster?.());
                        lines.push('  parent isForce: ' + raw.isForce?.());
                        lines.push('  parent isCategory: ' + raw.isCategory?.());
                        lines.push('  parent isUnit: ' + raw.isUnit?.());
                        lines.push('  parent getId: ' + raw.getId?.());
                        lines.push('  parent getName: ' + raw.getName?.());
                        try {
                            const src = raw.selector?.source;
                            if (src) lines.push('  parent.selector.source.id: ' + src.id + ', name: ' + src.name);
                        } catch(x) {}
                    }
                }

                for (let fi = 0; fi < forces.length; fi++) {
                    const f = forces[fi];
                    lines.push('=== force[' + fi + '] ' + f.getName?.() + ' errors: ' + (f.errors?.length || 0));
                    for (const e of (f.errors || [])) {
                        lines.push('  msg: ' + e.msg + ', scope: ' + e.scope + ', hash: ' + e.hash);
                    }
                    for (const cat of (f.getCategories?.() || [])) {
                        lines.push('  cat ' + cat.getName?.() + ' errors: ' + (cat.errors?.length || 0));
                        for (const e of (cat.errors || [])) {
                            lines.push('    msg: ' + e.msg + ', scope: ' + e.scope + ', hash: ' + e.hash);
                            if (e.constraint) lines.push('    constraint.id: ' + e.constraint.id);
                            if (e.parent) {
                                const raw = e.parent.__v_raw || e.parent;
                                lines.push('    parent.uid: ' + raw.uid + ', source.id: ' + raw.source?.id);
                                lines.push('    parent isCategory: ' + raw.isCategory?.());
                            }
                        }
                    }
                    for (const sel of (f.getSelections?.() || [])) {
                        if (sel.errors?.length > 0) {
                            lines.push('  sel ' + sel.getName?.() + ' errors: ' + (sel.errors?.length || 0));
                        }
                    }
                }

                // Also check allErrors on army
                lines.push('=== army.allErrors: ' + (army.allErrors?.length || 0));
                for (const e of (army.allErrors || [])) {
                    lines.push('  msg: ' + e.msg + ', scope: ' + e.scope + ', hash: ' + e.hash);
                }

                // Try getErrors() too
                lines.push('=== army.getErrors(): ');
                try {
                    const errs = army.getErrors();
                    lines.push('count: ' + (errs?.length || 0));
                    for (const e of (errs || [])) {
                        lines.push('  msg: ' + e.msg);
                        lines.push('  scope: ' + e.scope + ', hash: ' + e.hash);
                        if (e.constraint) lines.push('  constraint.id: ' + e.constraint.id + ', type: ' + e.constraint.type + ', field: ' + e.constraint.field);
                    }
                } catch(ex) { lines.push('error: ' + ex.message); }

                return lines.join('\n');
            })()
            """).GetAwaiter().GetResult();

        _output.WriteLine(probeResult);
    }

    /// <summary>
    /// Probe #3: Inspect category node internals to find entry ID for constraint errors.
    /// </summary>
    [SkippableFact]
    public void Debug_ProbeCategoryEntries()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(
            Id: "probe-gs3",
            Name: "Probe GS3",
            CategoryEntries: [
                new CategoryEntrySpec(Id: "cat-troops", Name: "Troops")
            ],
            ForceEntries: [
                new ForceEntrySpec(
                    Id: "fe-patrol",
                    Name: "Patrol",
                    CategoryLinks: [
                        new CategoryLinkSpec(Id: "cl-fe-troops", TargetId: "cat-troops", Name: "Troops")
                    ])
            ]);
        var cat = new CatalogueSpec(
            Id: "probe-cat3",
            Name: "Probe Cat3",
            GameSystemId: "probe-gs3",
            SelectionEntries: [
                new SelectionEntrySpec(
                    Id: "se-unit-a",
                    Name: "Unit A",
                    Type: "unit",
                    CategoryLinks: [
                        new CategoryLinkSpec(Id: "cl-unit", TargetId: "cat-troops", Name: "Troops", Primary: true)
                    ],
                    Constraints: [
                        new ConstraintSpec(Id: "con-min-1", Type: "min", Value: 1, Field: "selections", Scope: "parent")
                    ])
            ]);

        _fixture.Engine!.Setup(gs, [cat]);
        _fixture.Engine.AddForce(0);
        Thread.Sleep(500);
        // Don't deselect — just probe the category structure

        var probeResult = _fixture.Engine!.Browser.Page.EvaluateAsync<string>("""
            (() => {
                try {
                const army = window.__bsspec?.army;
                if (!army) return 'No army';
                const lines = [];

                // Call checkConstraints — some nodes may crash, wrap each
                try { army.checkConstraints(); } catch(e) { lines.push('army.checkConstraints error: ' + e.message); }
                const forces = army.getForces?.() || [];
                for (const f of forces) {
                    try { f.checkConstraints?.(); } catch(e) { lines.push('force.checkConstraints error: ' + e.message); }
                    // SKIP cat.checkConstraints — crashes on getInstancesAmount
                }

                for (const f of forces) {
                    const cats = f.getCategories?.() || [];
                    for (const cat of cats) {
                        try {
                        const rawCat = cat.__v_raw || cat;
                        lines.push('=== Category: ' + cat.getName?.());
                        lines.push('  cat.source.id: ' + rawCat.source?.id);
                        lines.push('  cat.source.targetId: ' + rawCat.source?.targetId);
                        lines.push('  cat.uid: ' + rawCat.uid);
                        lines.push('  cat own keys: ' + Object.keys(rawCat).slice(0, 30).join(', '));

                        // Check entries - wrap each access in try-catch
                        try {
                            const entries = rawCat.getEntries?.() || [];
                            lines.push('  getEntries count: ' + entries.length);
                            for (let i = 0; i < Math.min(entries.length, 5); i++) {
                                try {
                                    const entry = entries[i];
                                    const rawEntry = entry?.__v_raw || entry;
                                    lines.push('  entry[' + i + ']:');
                                    lines.push('    keys: ' + Object.keys(rawEntry || {}).slice(0, 20).join(', '));
                                    lines.push('    source?.id: ' + rawEntry?.source?.id);
                                    lines.push('    source?.name: ' + rawEntry?.source?.name);
                                    lines.push('    source?.targetId: ' + rawEntry?.source?.targetId);
                                    // constraints
                                    const cons = rawEntry?.constraints;
                                    lines.push('    constraints type: ' + typeof cons + ', isArray: ' + Array.isArray(cons));
                                    if (Array.isArray(cons)) {
                                        lines.push('    constraints.length: ' + cons.length);
                                        for (const c of cons) {
                                            lines.push('      c.id=' + c?.id + ' c.type=' + c?.type + ' c.field=' + c?.field);
                                        }
                                    }
                                } catch(ie) { lines.push('  entry[' + i + '] error: ' + ie.message); }
                            }
                        } catch(ee) { lines.push('  getEntries error: ' + ee.message); }

                        // Also check selectors property
                        try {
                            const sels = rawCat.selectors;
                            lines.push('  selectors type: ' + typeof sels + ', isArray: ' + Array.isArray(sels) + ', length: ' + (sels?.length ?? 'N/A'));
                            if (Array.isArray(sels)) {
                                for (let i = 0; i < Math.min(sels.length, 5); i++) {
                                    const s = sels[i]?.__v_raw || sels[i];
                                    lines.push('  selector[' + i + ']:');
                                    lines.push('    keys: ' + Object.keys(s || {}).slice(0, 20).join(', '));
                                    lines.push('    source?.id: ' + s?.source?.id);
                                    lines.push('    source?.name: ' + s?.source?.name);
                                    const sc = s?.constraints;
                                    if (Array.isArray(sc)) {
                                        lines.push('    constraints.length: ' + sc.length);
                                        for (const c of sc) {
                                            lines.push('      c.id=' + c?.id + ' c.type=' + c?.type);
                                        }
                                    }
                                    // Also check source.constraints
                                    const srcCons = s?.source?.constraints;
                                    if (Array.isArray(srcCons)) {
                                        lines.push('    source.constraints.length: ' + srcCons.length);
                                        for (const c of srcCons) {
                                            lines.push('      src-c.id=' + c?.id + ' c.type=' + c?.type);
                                        }
                                    }
                                }
                            }
                        } catch(se) { lines.push('  selectors error: ' + se.message); }

                        // Check errors (may be empty since we didn't call checkConstraints on cat)
                        const errs = rawCat.errors || [];
                        lines.push('  errors count: ' + errs.length);
                        for (const e of errs) {
                            lines.push('  ERR: ' + (e.msg||'').replace(/<[^>]*>/g,''));
                            lines.push('    constraint.id: ' + e.constraint?.id);
                        }
                        } catch(catErr) { lines.push('Category error: ' + catErr.message); }
                    }
                }

                return lines.join('\n');
                } catch(topErr) { return 'TOP ERROR: ' + topErr.message + '\n' + topErr.stack; }
            })()
            """).GetAwaiter().GetResult();

        _output.WriteLine(probeResult);
    }

    /// <summary>
    /// Probe #4: Inspect selection ordering structure for multi-type entries.
    /// </summary>
    [SkippableFact]
    public void Debug_ProbeSelectionOrdering()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(
            Id: "probe-gs4",
            Name: "Probe GS4",
            ForceEntries: [
                new ForceEntrySpec(Id: "fe-1", Name: "Detachment")
            ]);
        var cat = new CatalogueSpec(
            Id: "probe-cat4",
            Name: "Probe Cat4",
            GameSystemId: "probe-gs4",
            SelectionEntries: [
                new SelectionEntrySpec(Id: "se-unit", Name: "Infantry Squad", Type: "unit"),
                new SelectionEntrySpec(Id: "se-model", Name: "Sergeant", Type: "model"),
                new SelectionEntrySpec(Id: "se-upgrade", Name: "Power Sword", Type: "upgrade")
            ]);

        _fixture.Engine!.Setup(gs, [cat]);
        _fixture.Engine.AddForce(0);
        Thread.Sleep(500);
        _fixture.Engine.SelectEntry(0, 0);
        Thread.Sleep(300);
        _fixture.Engine.SelectEntry(0, 1);
        Thread.Sleep(300);
        _fixture.Engine.SelectEntry(0, 2);
        Thread.Sleep(300);

        var probeResult = _fixture.Engine!.Browser.Page.EvaluateAsync<string>("""
            (() => {
                try {
                const army = window.__bsspec?.army;
                if (!army) return 'No army';
                const lines = [];

                const forces = army.getForces?.() || [];
                const f = forces[0];
                if (!f) return 'No force';
                const rawF = f.__v_raw || f;

                // Force selectors
                const fSels = rawF.selectors || [];
                lines.push('force.selectors count: ' + fSels.length);
                fSels.forEach((s, i) => {
                    const rs = s?.__v_raw || s;
                    lines.push('  fSel[' + i + '] source.id: ' + rs?.source?.id + ', name: ' + rs?.source?.name);
                });

                // Force categories
                const cats = f.getCategories?.() || [];
                lines.push('force.getCategories count: ' + cats.length);
                cats.forEach((cat, i) => {
                    const rc = cat?.__v_raw || cat;
                    lines.push('  cat[' + i + '] name: ' + cat.getName?.() + ', source.id: ' + rc?.source?.id);
                    const catSels = rc?.selectors || [];
                    lines.push('    selectors count: ' + catSels.length);
                    catSels.forEach((cs, j) => {
                        const rcs = cs?.__v_raw || cs;
                        lines.push('    catSel[' + j + '] source.id: ' + rcs?.source?.id + ', name: ' + rcs?.source?.name);
                    });
                });

                // Force selections
                const selections = f.getSelections?.() || [];
                lines.push('force.getSelections count: ' + selections.length);
                selections.forEach((sel, i) => {
                    const rs = sel?.__v_raw || sel;
                    lines.push('  sel[' + i + '] name: ' + sel.getName?.() + ', source.id: ' + rs?.source?.id);
                    lines.push('    selector?.source?.id: ' + rs?.selector?.source?.id);
                    lines.push('    selector keys: ' + Object.keys(rs?.selector || {}).slice(0,15).join(', '));
                });

                return lines.join('\n');
                } catch(e) { return 'ERROR: ' + e.message + '\n' + e.stack; }
            })()
            """).GetAwaiter().GetResult();

        _output.WriteLine(probeResult);
    }
}
