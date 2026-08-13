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
  - on: category ${{ steps.add-patrol.categories.cat-troops }}   # WHICH NODE raised it
    from: se-unit/con-min-1         # WHAT caused the error
    messageContains: "at least"     # Optional: substring match on message
```

### `on` field — the raising node

`on` names the roster NODE the engine raised the error on (`raisedOnType` + `raisedOnId`), not
the catalogue entry it was attributed to. Node ids are minted per run on every lane, so they are
always written as `${{ steps.… }}` references.

| Example | Meaning |
|---------|---------|
| `roster` | Raised on the roster — **bare**, `RosterState` has no id to name |
| `group` | Raised on a selectionEntryGroup node — **bare**, no state model carries one |
| `force ${{ steps.add-army.forceId }}` | Raised on that force node |
| `category ${{ steps.add-patrol.categories.cat-troops }}` | Raised on that category node |
| `selection ${{ steps.select-parent.selectionId }}` | Raised on that selection node |

The node kind is one of: `roster`, `force`, `category`, `selection`, `group`.

**The two engines often raise on different nodes** — BattleScribe on the counting container,
NewRecruit on the violating selection — so a spec carries a base assertion plus an `engines:` block
rather than one answer bent to fit both.

**A literal second token (`selection se-unit`) matches nothing** — it names a catalogue entry, which
is a SET of nodes. The linter rejects the spec before it runs.

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
   - `on` matches: raisedOnType and raisedOnId
   - `from` matches: entryId and constraintId
   - `messageContains` (if specified) is a substring of actual message
2. Each actual error can only match one expected error — **consume-once**, and some steps really do
   produce several byte-identical errors that nothing can tell apart
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

NewRecruit does not: it raises a collective over-limit violation on the violating **selection**, and
entry-group constraints on the **group** node. That divergence is asserted, not normalized — see
`docs/error-assertions.md`.

The `on` field targets the node the error was raised ON, not the entry that declared the constraint;
`from:` is what names the constraint's source entry.

## Tips

- Start with `errorCount: N` to verify error count, then refine to `errorsContain`
- Use `errorsContain` during development; switch to `errors` for strict specs
- The implicit zero-errors check catches unexpected constraint violations early
- DataSource specs skip the implicit zero check because real-world data has many violations
