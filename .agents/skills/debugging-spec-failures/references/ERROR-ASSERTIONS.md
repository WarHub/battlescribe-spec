# Error Assertions Reference

## Error assertion modes

| Mode | YAML field | Behavior |
|------|-----------|----------|
| Exact match | `errors: [...]` | Every listed error must exist, no extras allowed |
| Subset match | `errorsContain: [...]` | Listed errors must exist, extra actual errors OK |
| Count only | `errorCount: N` | Only checks total error count |
| Implicit zero | *(none specified)* | Asserts zero errors (except dataSource specs) |

## Error assertion fields

```yaml
errors:
  - on: category cat-troops        # WHO has the error
    from: se-unit/con-min-1         # WHAT caused the error
    messageContains: "at least"     # Optional: substring match on message
```

### `on` field — error owner

Format: `{ownerType}` or `{ownerType} {ownerEntryId}`

| Example | Meaning |
|---------|---------|
| `roster` | Error on the roster itself |
| `force` | Error on a force (first force if multiple) |
| `category cat-troops` | Error on a category with entryId `cat-troops` |
| `selection se-unit` | Error on a selection with entryId `se-unit` |

The `ownerType` is one of: `roster`, `force`, `category`, `selection`.

### `from` field — error source

Format: `{entryId}/{constraintId}`

| Example | Meaning |
|---------|---------|
| `se-unit/con-min-1` | Constraint `con-min-1` on entry `se-unit` |
| `costLimits/ct-pts` | Cost limit violation for cost type `ct-pts` |
| `se-unit/hidden` | Hidden constraint on entry `se-unit` |

**Pseudo-values:**
- `costLimits/{costTypeId}` — cost limit validation error
- `{entryId}/hidden` — hidden constraint error

### `messageContains` field

Optional substring match against the error message text. Useful when `on`/`from`
matching is ambiguous.

## How SpecRunner matches errors

### Exact match (`errors`)

1. For each expected error, find an actual error where:
   - `on` matches: ownerType and ownerEntryId
   - `from` matches: entryId and constraintId
   - `messageContains` (if specified) is a substring of actual message
2. Each actual error can only match one expected error
3. Any unmatched expected errors → failure
4. Any unmatched actual errors → failure

### Subset match (`errorsContain`)

Same matching as exact, but unmatched actual errors are **ignored**.

## Validation error distribution

BattleScribe distributes validation errors across roster elements:

- **Roster-level:** Cost limit violations, global constraints
- **Force-level:** Force-entry constraints (field=forces)
- **Category-level:** Category min/max constraints
- **Selection-level:** Entry min/max constraints

The `on` field in error assertions targets the roster element that owns the error,
not the element that caused the constraint to be defined.

## Tips

- Start with `errorCount: N` to verify error count, then refine to `errorsContain`
- Use `errorsContain` during development; switch to `errors` for strict specs
- The implicit zero-errors check catches unexpected constraint violations early
- DataSource specs skip the implicit zero check because real-world data has many violations
