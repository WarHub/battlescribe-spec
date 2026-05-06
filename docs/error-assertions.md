# Error Assertions

Spec YAML files can assert validation errors produced by the engine after each step.
Error assertions verify that the correct constraint violations, cost limit errors,
and hidden-entry errors are reported on the correct roster elements.

## Quick Reference

| Field | Where | Purpose |
|-------|-------|---------|
| `errors:` | `expectedState` | Exact-set match: every assertion must match, no extras allowed |
| `errorsContain:` | `expectedState` | Subset match: listed errors must match, extras are allowed |
| `errorCount:` | `expectedState` | Count-only: asserts the total number of errors |
| `on:` | error assertion item | Identifies the roster element that owns the error |
| `from:` | error assertion item | Identifies the source entry and constraint (required) |
| `messageContains:` | error assertion item | Optional substring match on the error message text |

## Implicit Zero-Errors Default

If a step's `expectedState` does not include any error assertion (`errors:`,
`errorsContain:`, or `errorCount:`), the spec runner automatically asserts that
there are **zero** validation errors. This default is skipped for `dataSource` specs.

To explicitly expect zero errors, use `errors: []`.

## Error Assertion Item Format

Each item in `errors:` or `errorsContain:` has the following fields:

### `on:` (required)

Identifies the roster element that owns the error.

| Format | Example | Matches |
|--------|---------|---------|
| `{ownerType}` | `roster` | Any error on the roster |
| `{ownerType}` | `force` | Any error on a force |
| `{ownerType} {ownerEntryId}` | `category cat-troops` | Error on the specific category |
| `{ownerType} {ownerEntryId}` | `selection se-unit-a` | Error on the specific selection |

Valid owner types: `roster`, `force`, `category`, `selection`.

### `from:` (required)

Identifies the source entry and constraint that caused the error.
Format: `{entryId}/{constraintId}`.

| Format | Example | Meaning |
|--------|---------|---------|
| `{entryId}/{constraintId}` | `se-unit-a/con-min-1` | Error from constraint `con-min-1` on entry `se-unit-a` |
| `costLimits/{costTypeId}` | `costLimits/pts` | Cost limit violation for cost type `pts` |
| `{entryId}/hidden` | `se-unit-a/hidden` | Hidden entry error for entry `se-unit-a` |

### `messageContains:` (optional)

When set, the actual error's message text must contain this substring
(case-insensitive). Useful for distinguishing between errors that share the same
`on`/`from` but have different messages.

```yaml
errors:
  - on: category cat-troops
    from: se-unit-a/con-min-1
    messageContains: "at least 1"
```

## Exact-Set Matching (`errors:`)

The `errors:` field requires an **exact-set match**:
1. Every listed assertion must match exactly one actual error
2. Every actual error must be matched by an assertion (no extras allowed)
3. Matching is **order-independent** — the order of assertions doesn't matter

```yaml
- expectedState:
    errors:
      - on: category cat-troops
        from: se-unit-a/con-min-troops
      - on: category cat-hq
        from: se-hq/con-min-hq
```

An empty list `errors: []` explicitly asserts zero errors.

## Subset Matching (`errorsContain:`)

The `errorsContain:` field requires a **subset match**:
1. Every listed assertion must match at least one actual error
2. Additional actual errors are allowed (not flagged)

This is useful for specs that focus on specific errors without caring about
the full error set.

```yaml
- expectedState:
    errorsContain:
      - on: category cat-troops
        from: se-unit-a/con-min-1
```

**Mutually exclusive** with `errors:` — using both in the same `expectedState`
is a runtime error.

## Count-Only Matching (`errorCount:`)

The `errorCount:` field asserts only the total number of validation errors
without matching specifics. Useful for smoke tests.

```yaml
- expectedState:
    errorCount: 3
```

**Cannot** be combined with `errorsContain:` or `errors:` — all three are mutually exclusive.

## Per-Engine Overrides

Error expectations can vary by engine using the `engines:` override mechanism.
Each engine key provides a partial `expectedState` whose non-null fields replace
the base values for that engine.

```yaml
- expectedState:
    errors:
      - on: category cat-troops
        from: se-unit-a/con-min-1
    engines:
      newrecruit:
        errors:
          - on: selection se-squad
            from: se-squad/hidden
```

In this example, BattleScribe expects a min-constraint error, while NewRecruit
expects a hidden-entry error instead.

## Examples

### Constraint violation

```yaml
- expectedState:
    forces:
      - selectionCount: 0
    errors:
      - on: category cat-troops
        from: se-unit-a/con-min-1
```

### Cost limit exceeded

```yaml
- expectedState:
    costs:
      - typeId: pts
        value: 10
    errors:
      - on: roster
        from: costLimits/pts
```

### Hidden entry error

```yaml
- expectedState:
    forces:
      - selectionCount: 1
        selections:
          - name: Hidden Unit
            hidden: true
    errors:
      - on: selection se-1
        from: se-1/hidden
```

### Multiple errors

```yaml
- expectedState:
    errors:
      - on: category cat-troops
        from: se-unit-a/con-min-troops
      - on: category cat-hq
        from: se-hq/con-min-hq
```
