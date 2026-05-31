# Cost-Field Repeat Evaluation Algorithm

How BattleScribe and NewRecruit evaluate modifier repeats that reference cost
fields. This is the core algorithm that determines how entry costs are computed
after every roster mutation.

## BattleScribe: Single-Pass Live-Value Algorithm

### The Refresh Cycle

After every roster mutation (`selectEntry`, `deselectEntry`, non-isDuplicate
`setNumSelections`), the engine executes `t()` (f.java:150):

```
t() {
    u();             // mark dependent entries as changed via dependency graph
    a(false, true);  // cost refresh — single-pass over CHANGED selections
    v();             // validate constraints
    d();             // clear query cache
    w();             // clear all 'changed' flags
}
```

### Cost Refresh Pass — `a(false, true)` (f.java:618)

The cost refresh iterates over selections in **insertion order** (the order
entries were added to the roster). For each selection marked as changed:

1. Subtract old costs from roster total
2. Apply modifiers to a copy of the entry data
3. Set new costs on the selection (effective cost × number)
4. Add new costs to roster total

**Processing order**: Children-first DFS within each selection tree, then list
order across top-level force selections. This means child entries have their
costs set *before* their parent's modifiers evaluate.

### Live-Value Reads

When a repeat modifier evaluates, it queries the **live `getCosts()` values**
of matching selections in scope. These are the in-place values on selection
objects — not snapshots from before the pass started.

Consequence: selections processed earlier in the pass have already-updated
costs visible to later-processed selections. Selections not yet processed
still have their values from the *previous* refresh cycle.

### New Entries Have Empty Costs

Newly created selections start with an empty cost list (ArrayList size 0).
They contribute **0** to any cost query until they are processed in the current
refresh pass. This is the mechanism behind what appears to be a "cross-type
exemption" — there is no special rule excluding certain entries; new entries
simply have no costs to count yet.

### Repeat Multiplier Formula (c.java:1396)

```
multiplier = floor(queryResult / repeat.value) × repeat.repeats
```

Where `queryResult` sums `getCosts()` of all matching selections in scope for
the specified cost type.

### Insertion-Order Asymmetry

Because the algorithm is single-pass with live values, the **order in which
entries were added to the roster** affects their final costs when self- or
mutual-referencing modifiers are involved.

Example: 3 units (base=100pts, modifier: +10 per 50pts of same entry in force):

```
Unit[0]: query = Unit[0](120,self-old) + Unit[1](120,old) + Unit[2](0,new) = 240
         floor(240/50)=4 reps → 100+40 = 140
Unit[1]: query = Unit[0](140,updated) + Unit[1](120,self-old) + Unit[2](0,new) = 260
         floor(260/50)=5 reps → 100+50 = 150
Unit[2]: query = Unit[0](140) + Unit[1](150,updated) + Unit[2](0,self-new) = 290
         floor(290/50)=5 reps → 100+50 = 150
```

Result: [140, 150, 150] — the first-inserted entry gets the lowest cost because
it is processed first and sees other entries at their old (lower) values.

### Self-Reference Behavior

A single entry's modifier counting its own cost type sees its **own old value**
(from the previous refresh) in the query sum. For a freshly created entry, this
is 0 (empty costs). For an existing entry being re-evaluated, it is the value
from the last refresh pass.

There is no "exclude self" rule — the observed behavior of self-referencing
modifiers producing base cost for a single instance is simply because a new
entry has empty costs (contributing 0 to its own query).

### `isDuplicate` and `setNumSelections`

Entries with `isDuplicate=true` always create separate selection nodes (one per
add). The engine's `setNumSelections` checks `isDuplicate` first:

```java
calculateDelta(parent, entry, count) {
    if (isDuplicate(entry)) return 0;  // always no-op
    // ... normal delta calculation with min/max constraints
}
```

For isDuplicate entries: `setNumSelections` returns immediately without calling
`t()`. Count cannot be changed via this API — each instance is a separate node
managed by individual `selectEntry`/`deselectEntry` calls.

For non-isDuplicate entries: delta is computed (respecting constraints), the
number field is adjusted, and `t()` is called once.

### Count Spinner: Loop-Based Execution

The BattleScribe desktop UI does **not** use `setNumSelections` for count
changes. Instead, it computes `getNumChanges(parent, entry, targetCount)` to
get the delta, then loops individual `selectEntry` or `deselectEntry` calls.

Each individual call triggers a full `t()` refresh. For N changes, there are
N refresh cycles with intermediate cost states visible to subsequent iterations.
This produces different results than the atomic `setNumSelections` API when
self-referencing cost-field modifiers are present.

## NewRecruit: Reactive Fixed-Point Algorithm

NR uses a fundamentally different evaluation model based on Vue.js reactivity.

### Reactive Dependency Tracking

NR's cost evaluation is driven by Vue's reactive system. When a cost value is
read during modifier evaluation, Vue records the dependency. When any upstream
value changes, dependent computations are automatically re-triggered.

### Fixed-Point Iteration for Self-References

When a modifier references costs that are themselves affected by that modifier
(directly or transitively), NR's reactive system detects the circular dependency
and iterates until values converge (fixed-point). This produces:

- **Symmetric results** — all instances of the same entry type converge to the
  same cost (no insertion-order asymmetry)
- **Higher costs than BS** — the fixed-point includes self-contributions that
  BS's single-pass misses on earlier-processed entries
- **Potential divergence** — mutual references (A counts B, B counts A) can
  escalate without bound within a single operation

### Comparison: BS vs NR for Self-Reference

Entry: base=100pts, +10 per 50pts of same type in force.

| Instances | BattleScribe | NewRecruit |
|-----------|-------------|------------|
| 1 | 100 (new entry = 0pts in query) | 120 (fixed-point: floor(120/50)=2, 100+20=120) |
| 2 | 120, 120 (symmetric by coincidence) | 160, 160 (fixed-point: floor(320/50)=6, 100+60=160) |
| 3 | 140, 150, 150 (asymmetric) | 230, 230, 230 (fixed-point: floor(690/50)=13, 100+130=230) |

### Mutual References

For entries A and B that count each other's costs:

- **BS**: Escalates across mutations (each add triggers re-evaluation seeing
  previous inflated values), but within a single `t()` only one direction fires
  fully (insertion-order dependent)
- **NR**: Diverges to infinity within a single operation (reactive loop has no
  convergence bound). These specs are skipped for NR (`newrecruit: skip`).

### Non-Self-Referencing Cases

For modifiers that count a *different* entry type's cost (no circular
dependency), both engines produce identical results. The cost is simply read
from the target entry's current value, which is stable.

## Implications for Spec Design

1. **Self-referencing cost-field specs** need per-engine `expectedState`
   overrides — the algorithms produce fundamentally different results.

2. **Insertion-order matters in BS** — adding entries in different sequences
   produces different costs for self-referencing modifiers. Specs should add
   entries in a defined order and document the expected asymmetry.

3. **Mutual-reference specs** should skip NR (diverges) and use
   `engines.battlescribe` overrides for the escalation behavior.

4. **Non-self-referencing specs** work identically across both engines — no
   per-engine overrides needed.

5. **`setSelectionCount` specs** should use non-isDuplicate entries (child
   models under a unit). isDuplicate entries (root-level or `type: model` at
   force level) make `setSelectionCount` a no-op.

## Related Documentation

- [BattleScribe UI Flow](battlescribe-ui-flow.md) — UI→engine call mapping
- [NR Behavioral Differences](nr-behavioral-differences.md) — Full NR divergence catalog
- [Shared Flag Semantics](shared-flag-semantics.md) — How `shared` affects repeat counting scope
