# `shared` Flag Semantics

The `shared` boolean flag exists on three protocol types: **constraints**, **conditions**,
and **repeats**. It controls how the engine identifies selections when counting them —
specifically, whether to count by the **shared entry's original ID** or by the
**entry-link-specific composite ID**.

## Context: Entry Links and Shared Entries

In BattleScribe, a `sharedSelectionEntry` (or `sharedSelectionEntryGroup`) is a reusable
definition that lives in the catalogue's shared section. It becomes selectable through
**entry links** — references that expand the shared entry into a roster option.

During catalogue expansion, each entry link creates a composite ID:
`{linkId}::{sharedEntryId}` (e.g., `link-alpha::shared-unit`). Selections made through
different entry links have **different composite IDs** even though they reference the same
underlying shared entry.

The `shared` flag determines which ID is used when counting selections:

| `shared` value | Counted by |
|---------------|-----------|
| `true` | Base shared entry ID — all links aggregate |
| `false` | Composite entry-link ID — each link independent |

## `shared` on Constraints

### `shared=true`

The engine counts selections by the shared entry's **original ID**. All selections from
any entry link referencing the same shared entry aggregate toward a single limit.

```yaml
sharedSelectionEntries:
  - id: shared-unit
    constraints:
      - type: max
        value: 3
        scope: roster
        shared: true    # counts ALL selections of shared-unit across all links
entryLinks:
  - id: link-alpha
    targetId: shared-unit
  - id: link-beta
    targetId: shared-unit
```

Selecting 2 via link-alpha and 1 via link-beta = **3 total** (at limit).

**Spec evidence:** `constraint-entry-link-shared-counting`, `constraint-shared-flag`

### `shared=false` (default)

Each entry link's constraint counts only its own selections independently. The limit
applies per-link, not globally.

**Spec evidence:** `constraint-shared-flag`

### Error Attribution

When a shared constraint fires, the error references the **original shared entry ID**:
- `on: selection shared-unit`, `from: shared-unit/constraint-id`

Both `shared=true` and `shared=false` constraints on the same shared entry produce their
own independent errors with their own constraint IDs and values.

### Scope Interaction

The `scope` field (`parent`, `force`, `roster`) determines **where** to look for
selections. The `shared` flag determines **how** to identify them when counting.

## `shared` on Conditions

### `shared=true`

The engine counts selections by the shared entry's original ID. A condition with
`childId: shared-trigger` and `shared=true` counts selections from ALL entry links
referencing `shared-trigger`.

```yaml
conditions:
  - type: atLeast
    value: 2
    field: selections
    scope: force
    childId: shared-trigger
    shared: true    # 1 via link-alpha + 1 via link-beta = 2 → fires
```

**Spec evidence:** `condition-shared-flag`

### `shared=false` (default)

**BattleScribe:** The engine matches by composite entry-link ID. Since selections via
entry links have composite IDs (`link-alpha::shared-trigger`) and `childId` is the raw
shared ID (`shared-trigger`), the condition can never match any selections. The condition
effectively never fires.

**NewRecruit:** Ignores `shared=false` — conditions fire as if `shared=true`. See
[NewRecruit Engine Behavior](#newrecruit-engine-behavior) for root cause.

| Scenario | BattleScribe | NewRecruit |
|----------|-------------|-----------|
| Condition at force level, shared=false | Never fires (ID mismatch) | Fires (cross-link count, same as shared=true) |
| Condition nested in unit, shared=false | Never fires (ID mismatch) | Never fires (scope = container, no base-ID counter at container level) |

**Spec evidence:**
- `condition-shared-flag` — top-level case; engines diverge
- `condition-shared-flag-nested` — nested case; engines agree (different reasons)

## `shared` on Repeats

### `shared=true`

Analogous to conditions. The repeat multiplier is calculated by counting selections
using the shared entry's original ID, aggregating across all entry links.

**Spec evidence:** `modifier-repeat-shared-flag`

### `shared=false` (default)

**BattleScribe:** Analogous to conditions — composite ID mismatch means no selections
match the raw `childId`. Repeat multiplier stays at zero.

**NewRecruit:** Ignores `shared=false` — repeats fire as if `shared=true`. Same root
cause as conditions.

**Spec evidence:** `modifier-repeat-shared-flag`

## Same-Constraint-ID Side Effect (Historical)

Using the same constraint ID on multiple separate (non-shared) entries causes NR to
deduplicate errors (reporting only one), while BattleScribe reports each independently.
This was a data bug in the spec — constraint IDs should be unique per entry.

**Fix:** The `constraint-shared-linked` spec now uses unique IDs (`con-min-a`, `con-min-b`)
and both engines agree: each entry produces its own independent error.

**Recommendation:** Always use unique constraint IDs. Duplicate IDs produce undefined
deduplication behavior that differs across engines.

## Spec Coverage Summary

| Spec ID | What it Tests |
|---------|---------------|
| `constraint-entry-link-shared-counting` | Two links → shared max counted across both; error fires at aggregate limit |
| `constraint-entry-link-shared-target` | Single link → shared entry with shared constraint; error fires at limit |
| `constraint-shared-flag` | shared=true vs shared=false on same entry; both constraints error-proven |
| `constraint-shared-linked` | Two entries with independent min constraints → independent errors |
| `constraint-entry-link-merged` | Shared entry constraint + link constraint merge |
| `constraint-entry-link-own` | Link-only constraint (no shared entry constraint) |
| `condition-shared-flag` | shared=true counts across links; shared=false never fires in BS; NR diverges |
| `condition-shared-flag-nested` | Nested shared=false; both engines agree (different mechanisms) |
| `modifier-repeat-shared-flag` | shared=true counts across links; shared=false multiplier stays zero in BS; NR diverges |

## NewRecruit Engine Behavior

NewRecruit correctly implements `shared=false` for **constraints** but ignores it for
**conditions** and **repeats**.

### Summary Table

| Element | shared=true | shared=false |
|---------|------------|--------------|
| Constraint | ✅ Counts across all links | ✅ Counts per-link only |
| Condition | ✅ Counts across all links | ❌ Ignored — fires as shared=true (at force level) |
| Repeat | ✅ Counts across all links | ❌ Ignored — fires as shared=true (at force level) |

Additionally, for entries **nested inside a parent selection** (not a direct force child):

| Scenario | BattleScribe | NewRecruit |
|----------|-------------|-----------|
| Force-level + shared=false condition | ❌ never fires | ❌ fires (hash collision with shared=true) |
| Nested + shared=false condition | ❌ never fires | ✅ never fires (no base-ID counter at container scope) |

NR gets the right answer for the nested case, but for the wrong reason.

### Root Cause: `hash()` and Missing `childId` Adaptation

NR's reactive counting system keys listeners via a `hash()` function
(from bundle `BA2pibXD.js`):

```js
hash(e) {
  return `${this.prefix(e.includeChildSelections, e.includeChildForces)}::${e.field}::${e.childId || ...}`
}
```

The hash does **NOT** include `shared`. Conditions/repeats with `shared=true` and
`shared=false` but the same `field`/`childId` share the identical listener bucket —
they receive the same cross-link count.

For **constraints**, the setup code explicitly adapts `childId` when `shared===false`:

```js
this.listen(t, {
  ...t.source,
  childId: t.source.shared===!1 && !this.source.isCategory()
    ? this.source.id        // per-link composite ID → unique hash key
    : this.source.getId()   // shared base ID → cross-link hash key
})
```

When `shared=false`, the constraint's `childId` becomes the per-link composite ID
(e.g., `link-alpha::shared-unit`), creating a **distinct hash key** for each link.
This is correct ✅

For **conditions and repeats**, no equivalent `childId` adaptation exists. The `childId`
stays as the raw shared entry ID. Both `shared=true` and `shared=false` hash to the same
key and always behave as `shared=true`.

### The `find()` Scope Method

The `find(scope, shared)` method also handles `shared=false`:

```js
find(e, t, n) {
  if (t === !1) switch(e) {
    case "force": return this.source.isGroup() ? this : this.parent;
  }
  switch(e) {
    case "force": return this.findParentOrSelf(i => i.source.isForce());
  }
}
```

When `shared=false`, `find("force")` returns `this.parent` (immediate parent) instead of
traversing to the force node. Since `isGroup()` returns `false` for all entry types in NR,
this always returns `this.parent`.

- **Force-level entries:** `this.parent` is the Force → same result as `shared=true`
- **Nested entries:** `this.parent` is the container selection → scope restricted to container

For nested entries, even though scope is restricted to the Container, the condition still
doesn't fire because NR's reactive counters for the base shared-entry ID are only maintained
at force scope. Container-level counters track composite IDs only, so the base-ID count seen
by the condition is 0. (This is inferred from observable behavior and the JS source structure;
it cannot be directly asserted in a YAML spec.)

This distinction is what `condition-shared-flag-nested.yaml` observes: the condition doesn't
fire for nested entries in NR, while `condition-shared-flag.yaml` shows it incorrectly fires
for force-level entries. The contrast between the two specs provides behavioral evidence for
the scope restriction mechanism.

