# Constraint `field=forces` Behavior

Research on how the `field` attribute works in BattleScribe constraints, specifically `field="forces"`.

## Background

A BattleScribe `<constraint>` has these key attributes:
- `type`: `min` or `max`
- `value`: numeric threshold (0 = not required, -1 = unlimited)
- `field`: what to count — `"selections"` (default), `"forces"`, or a cost type ID
- `scope`: where to count — `roster`, `force`, `parent`, `self`, etc.
- `includeChildSelections`, `includeChildForces`: recursive flags
- `shared`: deduplication flag

## Field Enum Values

Source: `BaseQuery.java:114-147`

```java
public static enum Field implements IQueryField {
    SELECTIONS("selections", "Selections"),  // default
    FORCES("forces", "Forces");
}
```

Only two built-in field values. The `field` attribute can also reference a CostType ID
(e.g. `"pts"`), which routes to cost-based counting instead.

## Scope Restriction

Source: `engine/a/a.java:3510-3514`

When `field="forces"`, only **ROSTER** and **FORCE** scopes are valid:

```java
if (field == BaseQuery.Field.FORCES) {
    scopes.add(Scope.ROSTER);
    scopes.add(Scope.FORCE);
} else {
    // field="selections": all scopes allowed
    // SELF, PARENT, ANCESTOR, PRIMARY_CATEGORY, PRIMARY_CATALOGUE, FORCE, ROSTER
    scopes.addAll(Arrays.asList(Scope.values()));
}
```

## Query Value Dispatch

Source: `engine/a/c.java:1428-1432`

The engine dispatches to different counting methods based on field:

```java
if (field == Field.SELECTIONS) {
    count = countSelections(rosterElement, childFilter, shared, includeChildForces, includeChildSelections);
} else if (field == Field.FORCES) {
    count = countForces(rosterElement, childFilter, shared, includeChildForces);
    // NOTE: no includeChildSelections parameter — not applicable for force counting
}
```

## Force Counting Logic

Source: `engine/a/c.java:1063-1107`

```java
// Step 1: Count forces by list size
private int countForces(BaseRosterElement element, IFilteredQueryChild child,
                        boolean shared, boolean includeChildForces) {
    return getForces(element, child, shared, includeChildForces).size();
}

// Step 2: Get force list from scope element
private List<Force> getForces(BaseRosterElement element, ...) {
    if (element instanceof Roster) list = roster.getForces();
    else if (element instanceof Force) list = force.getForces(); // sub-forces
    else throw IllegalArgumentException;
    return filterForces(list, child, shared, includeChildForces);
}

// Step 3: Filter by child type
private List<Force> filterForces(List<Force> list, IFilteredQueryChild child,
                                 boolean shared, boolean includeChildForces) {
    if (child == Child.ANY) {
        if (!includeChildForces) return list;  // all direct forces
        else return flattenAllForces(list);     // recursive
    } else if (child instanceof ForceEntry) {
        matchByEntryId(list, forceEntry, shared, includeChildForces, result);
    }
    // IMPLICIT: any other child type (SelectionEntry, etc.) → EMPTY list
    return result;
}
```

Key observations:
- `child == ANY`: counts all forces (optionally recursive)
- `child instanceof ForceEntry`: counts forces matching that ForceEntry ID
- Any other child type: returns empty list (0 forces)

## ChildId Resolution for Constraints

Source: `c.java:1394-1397`

For a `Constraint` (not a Condition), the child filter is the **owning entry**:

```java
if (gettingLimitValue) {
    childFilter = Child.ANY;
} else if (query instanceof Constraint) {
    childFilter = (BaseEntry)owningEntry;  // the entry that has this constraint
}
```

### Implication: Where You Place the Constraint Matters

**On a ForceEntry** (intended use):
- Child filter = ForceEntry → `instanceof ForceEntry` = true
- Counts forces matching that entry ID ✓

**On a SelectionEntry** (questionable use):
- Child filter = SelectionEntry → `instanceof ForceEntry` = false
- Falls through → returns empty list → count = **0 always**
- `atLeast` constraints are always violated; `atMost` constraints are always satisfied

## Auto-Selection Exclusion

Source: `f.java:1451,1473`

The auto-select logic explicitly skips `field=forces`:

```java
if (constraint.isPercentValue() || type != MIN || field != SELECTIONS) continue;
```

So `min 1 field=forces` does NOT auto-select entries (unlike `min 1 field=selections`).

## Validation Error Messages

Source: `f.java:586-591`

```
"{element} has {N} too many forces of/from {entry} (maximum {limit})"
"{element} must have {N} more forces of/from {entry} (minimum {limit})"
```

## Condition vs Constraint

Conditions (`field=forces`) use a different child resolution path:
- Conditions have an explicit `childId` attribute
- `childId` can be set to a ForceEntry ID or left empty
- This is a separate code path from Constraint evaluation

## Summary Table

| Aspect | `field="selections"` | `field="forces"` |
|---|---|---|
| **Counts** | Selection objects (by `number` property) | Force objects (by list size) |
| **Valid scopes** | All (self, parent, ancestor, force, roster, etc.) | Only `roster` and `force` |
| **includeChildForces** | Supported | Supported |
| **includeChildSelections** | Supported | Not applicable |
| **Auto-selects on min≥1** | Yes | No |
| **Intended placement** | SelectionEntry, SelectionEntryGroup | ForceEntry |
| **Error messages** | "too many/few selections" | "too many/few forces" |

## Conformance Specs

All confirmed against BattleScribe Oracle engine:

| Spec | Tests |
|---|---|
| `constraint-forces-field` | `field=forces` on SelectionEntry → always counts 0 → min violation always |
| `constraint-forces-field-on-forceentry` | `field=forces max` on ForceEntry → correctly limits force count |
| `constraint-forces-field-min-error` | `field=forces min` on ForceEntry → error when too few, no auto-select |
| `constraint-forces-field-per-type` | Multiple ForceEntry types counted independently |
