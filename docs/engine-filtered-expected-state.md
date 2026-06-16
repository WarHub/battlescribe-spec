# Engine-Filtered Expected State

Engine-filtered `expectedState` allows a single spec to document per-engine
behavioral differences inline, replacing opaque `engines: newrecruit: fail`
markers with precise, structured expectations for each engine.

## Problem

Different BattleScribe engine implementations sometimes produce slightly
different — but equally valid — behavior. For example, BattleScribe Desktop
places a constraint error on the **force** node while New Recruit places it on
the **selection** node. Both engines correctly detect the violation; they just
report it at a different level.

Previously, these differences were handled with blanket `engines: newrecruit: fail`
at the spec level, which provided no insight into _what_ was different. The spec
simply didn't run assertions for that engine at all.

## Solution

The `expectedState` block now supports an `engines` map. Each key is an engine
name (e.g. `"battlescribe"`, `"newrecruit"`, `"battlescribe-ui"`), and each value is a
partial `expectedState` whose non-null fields **replace** the corresponding base
fields for that engine. Fields not specified in the override fall through to the
base assertion. Engine names are open-ended strings; unlisted engines default to
the base assertion.

## YAML Syntax

### Basic: override errors only

```yaml
- expectedState:
    forces:
      - selectionCount: 3
    errors:
      - on: force fe-1
        from: shared-unit/con-shared-max
    engines:
      newrecruit:
        errors:
          - on: selection shared-unit
            from: link-1/con-link-max
```

When running on **BattleScribe**: asserts `selectionCount: 3` and expects one
error on `force fe-1`.

When running on **New Recruit**: asserts `selectionCount: 3` (inherited from
base) and expects one error on `selection shared-unit` (overridden).

### Engine adds errors where base has none

```yaml
- expectedState:
    forces:
      - selectionCount: 3
    engines:
      newrecruit:
        errors:
          - on: selection se-unit-a
            from: se-unit-a/con-max-2
```

The base `expectedState` has no `errors` field, so the default behavior applies:
no errors expected. The NR override adds an error expectation just for that
engine.

### Override any expectedState field

Any field in `expectedState` can be overridden per engine — not just `errors`:

```yaml
- expectedState:
    forces:
      - selectionCount: 3
    engines:
      newrecruit:
        forces:
          - selectionCount: 2
```

### Multiple engines

```yaml
- expectedState:
    errors:
      - on: force fe-1
        from: se-unit/con-max
    engines:
      newrecruit:
        errors:
          - on: selection se-unit
            from: se-unit/con-max
      newrecruit-ui:
        errors: []
```

## Merge Semantics

The `ForEngine()` method creates a new `ExpectedStateDef` by merging the
engine-specific override with the base:

| Override field | Result |
|---------------|--------|
| Non-null | Override replaces base |
| Null / absent | Base value used |

The merge is **field-level**, not deep. If an engine override specifies `errors`,
the _entire_ error list replaces the base error list — individual errors are not
merged.

The `engines` map itself is **not** propagated into the merged result.

## Relationship to Spec-Level `engines`

Spec files have _two_ different `engines` fields that serve different purposes:

### Spec-level `engines` (pass/fail/skip)

```yaml
id: my-spec
engines:
  newrecruit: fail    # expected to fail on NR
  battlescribe: skip  # don't run on BS
```

This controls whether the spec is **run at all** and whether failure is expected.
Unlisted engines default to `"pass"`.

### Step-level `engines` (expectedState override)

```yaml
- expectedState:
    errors:
      - on: force fe-1
        from: se-unit/con-max
    engines:
      newrecruit:
        errors:
          - on: selection se-unit
            from: se-unit/con-max
```

This provides **per-engine assertion overrides** within a spec that runs on all
engines. The spec passes on all engines — each just asserts the correct behavior
for that engine.

**Prefer step-level overrides** over spec-level `fail` markers whenever
possible. Step-level overrides document exactly what differs, while `fail`
markers hide the details.

## Error Assertion Format

Error assertions use the compact `on`/`from` format:

```yaml
errors:
  - on: force fe-1              # roster element owning the error
    from: se-unit-a/con-max-2   # entryId/constraintId that caused it
```

### `on` field (required)

Identifies the roster element that owns the error:

| Format | Example | Matches |
|--------|---------|---------|
| `{ownerType}` | `roster` | Any error on a roster node |
| `{ownerType} {entryId}` | `force fe-1` | Error on force with entryId `fe-1` |
| `{ownerType} {entryId}` | `selection se-unit-a` | Error on selection `se-unit-a` |
| `{ownerType} {entryId}` | `category cat-troops` | Error on category `cat-troops` |

### `from` field (optional)

Identifies the source entry and constraint:

| Format | Example | Meaning |
|--------|---------|---------|
| `{entryId}/{constraintId}` | `se-unit-a/con-max-2` | Constraint `con-max-2` on entry `se-unit-a` |
| `costLimits/{costTypeId}` | `costLimits/ct-pts` | Cost limit violation for cost type `ct-pts` |
| `{entryId}/hidden` | `se-unit-a/hidden` | Hidden entry error (pseudo-constraint) |
| `{entryId}` | `se-unit-a` | Any constraint on entry `se-unit-a` |

When `from` is omitted, the assertion matches any error on the specified roster
element regardless of source.

### Default behavior

- `errors: []` — assert that zero validation errors exist.
- `errors` omitted — **also asserts zero errors** (implicit default). Specs do
  not need to write `errors: []` explicitly.
- One or more errors listed — assert exactly those errors exist (count must
  match, each assertion must find a match).

## Implementation

### SpecRunner

`SpecRunner` accepts an optional `engineName` parameter. At each assertion step,
it calls `step.ExpectedState.ForEngine(engineName)` to get the effective
expectations before running assertions.

```csharp
var runner = new SpecRunner(engine, dataSourceResolver, engineName: "newrecruit");
```

### ConformanceTestBase

The `ConformanceTestBase` xUnit fixture has an abstract `EngineName` property
that subclasses override. It passes this to `SpecRunner` automatically.

### Structural Tests

`SpecLintTests` enforces quality (see `CheckAllErrorAssertionsHaveFrom`):

- All base-level error assertions must include the `from:` field for precise
  matching. Engine overrides are exempt since some engines don't expose
  constraint metadata for all error types.

Engine **names** are open-ended strings (as the Solution section notes) and are
**not** validated against a fixed list. The only engine-related lint is on the
spec-level `engines:` *expectation value*, which must be `pass`, `fail`, or `skip`
(`CheckEngineExpectations`).

## Examples in the Spec Suite

Specs using engine-filtered `expectedState` to document NR behavioral differences:

| Category | Spec | What differs |
|----------|------|-------------|
| constraint | `constraint-entry-link-merged` | Error `from` resolution: shared entry vs link |
| constraint | `constraint-shared-linked` | Error count and entryId |
| constraint | `constraint-min-on-force-linked` | NR reports extra error on selection |
| constraint | `constraint-max-violation` | NR fires at intermediate step |
| constraint | `constraint-percent-value` | NR fires fractional violation |
| scope | `scope-parent` | NR evaluates scope=parent more aggressively |
| selection | `selection-hidden-entry` | Hidden error tagging |
| selection | `hidden-cascade-to-children` | Hidden error tagging |
| modifier | `modifier-set-boolean` | Hidden error tagging |
| modifier | `modifier-field-hidden` | Hidden error tagging |

## Error Location Differences

BattleScribe's Java engine distributes validation errors across the roster tree
based on the constraint's scope (roster/force/category/selection). NewRecruit
consistently attributes errors to the selection that violates the constraint.

The BS BattleScribe adapter includes remapping logic (`RemapRosterErrorsToSelection`,
`RemapForceErrorsToSelection`, `RemapCategoryErrorsToSelection`) that moves
higher-level errors down to selection level where possible, aligning with NR.
After remapping, the following differences remain.

### Missing Errors (NR reports, BS does not)

BS doesn't report these errors at all — fundamental engine behavioral
differences. No adapter can create errors from nothing.

| Spec | NR Error | Why BS doesn't report |
|------|----------|----------------------|
| `constraint-max-violation` | `on: selection se-unit-a` | BS doesn't error at intermediate step |
| `constraint-percent-value` | `on: selection se-1` | BS handles percent constraints differently |
| `scope-parent` | `on: selection se-unit-a` | BS scope resolution differs |
| `modifier-field-hidden` | `on: selection se-1` (hidden) | BS doesn't report hidden as constraint violation |
| `modifier-set-boolean` | `on: selection se-1` (hidden) | BS doesn't report hidden as constraint violation |
| `selection-hidden-entry` | `on: selection se-1` (hidden) | BS doesn't report hidden as constraint violation |
| `hidden-cascade-to-children` | `on: selection se-squad` (hidden) | BS doesn't report hidden as constraint violation |

### Error Count/Attribution Differences

| Spec | BS | NR | Notes |
|------|----|----|-------|
| `constraint-min-on-force-linked` | 1 error on force | 2 errors: force + selection | NR duplicates on both levels |
| `constraint-shared-linked` | 2 errors on category | 1 error on category | NR deduplicates shared min |

### Remaining `from` Difference

`constraint-entry-link-merged`: Both engines report the error `on: selection shared-unit`,
but NR attributes it to the entry link's constraint (`from: link-1/con-link-max`)
while BS attributes it to the shared entry's constraint (`from: shared-unit/con-shared-max`).
This reflects how each engine resolves merged constraint ownership.
