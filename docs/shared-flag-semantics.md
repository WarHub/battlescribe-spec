# `shared` Flag Semantics

The `shared` boolean flag exists on three protocol types: **constraints**, **conditions**,
and **repeats**. It controls how the engine identifies selections when counting them for
validation or evaluation — specifically, whether to count by the **shared entry's original
ID** or by the **entry-link-specific composite ID**.

## Context: Entry Links and Shared Entries

In BattleScribe, a `sharedSelectionEntry` (or `sharedSelectionEntryGroup`) is a reusable
definition that lives in the catalogue's shared section. It becomes available in a roster
through **entry links** — references that expand the shared entry into a selectable option.

During catalogue expansion, each entry link creates a composite ID:
`{linkId}::{sharedEntryId}` (e.g., `link-alpha::shared-unit`). This means selections made
through different entry links have **different composite IDs** even though they reference the
same underlying shared entry.

## `shared=true` on Constraints

**Behavior:** When a constraint has `shared=true`, the engine counts selections by the
shared entry's **original ID** rather than the composite entry link ID. This means all
selections from any entry link referencing the same shared entry are counted together toward
a single limit.

**Without `shared=true`:** Each entry link's constraint counts only its own selections
independently. The max/min limit applies per-link, not globally.

### Example

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

With `shared=true`: selecting 2 via link-alpha and 1 via link-beta = **3 total** (at limit).
Without `shared=true`: each link would independently track its own count.

### Error Attribution

When a shared constraint fires, the error references the **original shared entry ID** (not
the composite link ID):
- Error `on: selection shared-unit` with `from: shared-unit/constraint-id`

### Scope Interaction

The `scope` field (parent, force, roster) determines **where** to look for selections.
The `shared` flag determines **how** to identify/group them when counting.

## `shared=true` on Conditions

**Behavior:** When a condition has `shared=true` and references a `childId`, the engine
counts selections by the shared entry's original ID rather than the composite link ID.

This matters when a condition's `childId` points to a shared entry referenced by multiple
entry links. With `shared=true`, the condition counts selections from ALL entry links that
reference that shared entry, not just one specific link.

### Example

```yaml
sharedSelectionEntries:
  - id: shared-trigger
    name: Trigger Unit
entryLinks:
  - id: link-1
    targetId: shared-trigger
  - id: link-2
    targetId: shared-trigger

selectionEntries:
  - id: se-target
    modifiers:
      - type: set
        field: name
        value: Activated
        conditions:
          - type: atLeast
            value: 2
            field: selections
            scope: force
            childId: shared-trigger
            shared: true    # counts selections from BOTH link-1 and link-2
```

With `shared=true`: 1 selection via link-1 + 1 via link-2 = 2 total → condition met.
Without `shared=true`: each link counted separately → condition would need 2 from a single link.

## `shared=true` on Repeats

**Behavior:** Analogous to conditions. When a repeat has `shared=true`, it counts selections
by the shared entry's original ID when calculating the repeat multiplier.

## Same-ID Constraint Deduplication

A separate but related behavior: when multiple non-shared entries have constraints with the
**same constraint ID** and `shared=true`, the constraint IDs enable error deduplication in
some engines. This is a secondary effect — the primary purpose of `shared` is cross-link
counting on shared entries.

## Existing Spec Coverage

| Spec ID | What it Tests |
|---------|---------------|
| `constraint-shared` | Two entries with shared constraints (basic, no limit exceeded) |
| `constraint-shared-deduplication` | Single shared entry with min constraint auto-satisfied |
| `constraint-shared-linked` | Same constraint ID on two entries, error per entry |
| `constraint-entry-link-shared-counting` | **Key spec:** Two links → same shared entry, shared max counted across both |
| `constraint-entry-link-shared-target` | Single link → shared entry with shared constraint |
| `constraint-entry-link-merged` | Shared entry constraint + link constraint merge |
| `constraint-entry-link-own` | Link-only constraint (no shared entry constraint) |
| `condition-shared-counting` | Condition with shared=true counts across entry links |
| `constraint-not-shared-per-link` | shared=false means per-link independent counting |

## Gaps Addressed

1. **Condition `shared=true`** — previously untested; new spec `condition-shared-counting`
2. **Constraint `shared=false` contrast** — new spec `constraint-not-shared-per-link` proves
   that without shared=true, each link counts independently
